# Registry-Independent Pipeline Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement the two high-priority follow-ups: (1) CQP filter PathProvider inclusion cache via `subject.Data`, and (2) hash-in-update with idle-only heartbeat replacing quiet-only comparison.

**Architecture:** The server CQP filter caches PathProvider decisions in `subject.Data` on first registered encounter, then uses the cached decision when the subject is momentarily unregistered. The structural hash is embedded in every update broadcast and compared by the client after each apply. The heartbeat becomes idle-only (fires only when no update was broadcast for 10s).

**Tech Stack:** C# 13, .NET 9, System.Security.Cryptography (SHA256), System.Text.Json

**Design doc:** `docs/plans/2026-04-06-registry-independent-pipeline-design.md`

---

### Task 1: CQP filter — PathProvider inclusion cache via `subject.Data`

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs:387-395`

**Step 1: Implement the cached inclusion filter**

Replace the current `CreateChangeQueueProcessor` method (lines 387-395):

```csharp
public ChangeQueueProcessor CreateChangeQueueProcessor(ILogger logger) =>
    new(source: this, Context,
        // TODO: Accept unregistered properties to avoid CQP drops during concurrent
        // structural mutations. ProcessPropertyChange handles serialization fallback.
        // Revisit: add PathProvider-aware filtering that doesn't drop unregistered subjects.
        propertyFilter: propertyReference =>
            propertyReference.TryGetRegisteredProperty() is not { } property ||
            (_configuration.PathProvider?.IsPropertyIncluded(property) ?? true),
        writeHandler: BroadcastChangesAsync, BufferTime, logger);
```

With:

```csharp
public ChangeQueueProcessor CreateChangeQueueProcessor(ILogger logger) =>
    new(source: this, Context,
        propertyFilter: CreatePropertyFilter(),
        writeHandler: BroadcastChangesAsync, BufferTime, logger);

private Func<PropertyReference, bool> CreatePropertyFilter()
{
    var pathProvider = _configuration.PathProvider;
    if (pathProvider is null)
        return static _ => true;

    return propertyReference =>
    {
        var property = propertyReference.TryGetRegisteredProperty();
        var cacheKey = ("ws:included", propertyReference.Name);

        if (property is not null)
        {
            var included = pathProvider.IsPropertyIncluded(property);
            propertyReference.Subject.Data.GetOrAdd(cacheKey, included);
            return included;
        }

        // Unregistered: use cached PathProvider decision from subject.Data
        return propertyReference.Subject.Data.TryGetValue(cacheKey, out var cached)
            && (bool)cached;
    };
}
```

**Step 2: Verify build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeds

**Step 3: Run unit tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: All tests pass

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs
git commit -m "fix: CQP filter caches PathProvider decisions in subject.Data

Replaces the blind accept of unregistered properties with a cached
inclusion filter. PathProvider decisions are cached in subject.Data
on first registered encounter and reused when the subject is
momentarily unregistered during structural mutations. No PathProvider
bypass, no memory leak (cache lives on subject, same lifecycle)."
```

---

### Task 2: Add `StructuralHash` field to `UpdatePayload`

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Protocol/UpdatePayload.cs`

**Step 1: Add the hash field**

Add a `StructuralHash` property to `UpdatePayload`:

```csharp
using System.Text.Json.Serialization;
using Namotion.Interceptor.Connectors.Updates;

namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Update message payload. Inherits SubjectUpdate and adds an optional sequence number.
/// Server-to-client messages set Sequence; client-to-server messages leave it null.
/// </summary>
public class UpdatePayload : SubjectUpdate
{
    [JsonPropertyName("sequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Sequence { get; set; }

    /// <summary>
    /// Structural hash of the server's graph at the time this update was created.
    /// Clients compare against their own hash after applying to detect divergence.
    /// Null when sent by clients or when hashing is not supported.
    /// </summary>
    [JsonPropertyName("structuralHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StructuralHash { get; set; }
}
```

**Step 2: Verify build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Protocol/UpdatePayload.cs
git commit -m "feat: Add StructuralHash field to UpdatePayload"
```

---

### Task 3: Server — include structural hash in every broadcast

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs`

**Step 1: Add `_lastBroadcastTicks` field for idle detection**

Add field after `_applyUpdateLock`:

```csharp
private long _lastBroadcastTicks = Environment.TickCount64;
```

**Step 2: Modify `CreateUpdateWithSequence` to compute and return hash**

Replace the current method:

```csharp
private (SubjectUpdate Update, long Sequence, string? StructuralHash) CreateUpdateWithSequence(ReadOnlySpan<SubjectPropertyChange> changes)
{
    lock (_applyUpdateLock)
    {
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(_subject, changes, _processors);
        var sequence = Interlocked.Increment(ref _sequence);

        string? hash = null;
        try { hash = Internal.StateHashComputer.ComputeStructuralHash(_subject); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to compute structural hash"); }

        return (update, sequence, hash);
    }
}
```

The hash is computed inline under `_applyUpdateLock` — no caching, no dirty flag, no extra fields. If performance becomes an issue, lazy caching can be added as a follow-up.

**Step 3: Update `BroadcastChangesAsync` to thread hash through**

```csharp
public async ValueTask BroadcastChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
{
    if (changes.Length == 0 || _connections.IsEmpty) return;

    var batchSize = _configuration.WriteBatchSize;
    if (batchSize <= 0 || changes.Length <= batchSize)
    {
        var (update, sequence, hash) = CreateUpdateWithSequence(changes.Span);
        await BroadcastUpdateAsync(update, sequence, hash, cancellationToken).ConfigureAwait(false);
    }
    else
    {
        for (var i = 0; i < changes.Length; i += batchSize)
        {
            var currentBatchSize = Math.Min(batchSize, changes.Length - i);
            var batch = changes.Slice(i, currentBatchSize);
            var (update, sequence, hash) = CreateUpdateWithSequence(batch.Span);
            await BroadcastUpdateAsync(update, sequence, hash, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

**Step 4: Update `BroadcastUpdateAsync` to include hash and stamp broadcast time**

```csharp
private async Task BroadcastUpdateAsync(SubjectUpdate update, long sequence, string? structuralHash, CancellationToken cancellationToken)
{
    Volatile.Write(ref _lastBroadcastTicks, Environment.TickCount64);
    if (_connections.IsEmpty) return;

    var updatePayload = new UpdatePayload
    {
        Root = update.Root,
        Subjects = update.Subjects,
        Sequence = sequence,
        StructuralHash = structuralHash
    };

    var serializedMessage = _serializer.SerializeMessage(MessageType.Update, updatePayload);

    await BroadcastToAllAsync(
        connection => connection.SendUpdateAsync(serializedMessage, sequence, cancellationToken),
        cancellationToken).ConfigureAwait(false);
}
```

**Step 5: Verify build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeds

**Step 6: Run unit tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: All tests pass

**Step 7: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs
git commit -m "feat: Include structural hash in every broadcast

Hash computed inline under _applyUpdateLock. No caching — simple
and correct. Tracks _lastBroadcastTicks for idle heartbeat."
```

---

### Task 4: Server — idle-only heartbeat

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs`

**Step 1: Change heartbeat to idle-only**

Replace `BroadcastHeartbeatAsync` to only send when idle:

```csharp
private async Task BroadcastHeartbeatAsync(CancellationToken cancellationToken)
{
    if (_connections.IsEmpty) return;

    // Only send heartbeat if no update was broadcast recently (idle detection)
    var timeSinceLastBroadcast = Environment.TickCount64 - Volatile.Read(ref _lastBroadcastTicks);
    if (timeSinceLastBroadcast < _configuration.HeartbeatInterval.TotalMilliseconds)
        return;

    string? stateHash;
    long sequence;
    lock (_applyUpdateLock)
    {
        sequence = Volatile.Read(ref _sequence);
        try { stateHash = Internal.StateHashComputer.ComputeStructuralHash(_subject); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to compute structural hash for heartbeat");
            stateHash = null;
        }
    }

    var heartbeat = new HeartbeatPayload
    {
        Sequence = sequence,
        StateHash = stateHash
    };

    var serializedMessage = _serializer.SerializeMessage(MessageType.Heartbeat, heartbeat);

    await BroadcastToAllAsync(
        connection => connection.SendHeartbeatAsync(serializedMessage, cancellationToken),
        cancellationToken).ConfigureAwait(false);
}
```

Key changes:
- Early return if a broadcast happened recently (idle detection)
- Hash computed inline under `_applyUpdateLock` for consistency with sequence
- No dirty flag, no cached field — same simple approach as Task 3

**Step 2: Verify build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs
git commit -m "feat: Heartbeat only fires during idle periods

Heartbeat is suppressed when updates were broadcast recently.
During active operation, updates carry the hash. Heartbeat only
serves as 'nothing happened, state is still X' signal."
```

---

### Task 5: Client — compare hash on every update, remove quiet-only logic

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs`

**Step 1: Remove `_lastUpdateAppliedTicks` field**

Remove this field (line 48):

```csharp
private long _lastUpdateAppliedTicks;
```

And remove the write in `HandleUpdate` (line 553):

```csharp
Volatile.Write(ref _lastUpdateAppliedTicks, Environment.TickCount64);
```

**Step 2: Add hash comparison after applying updates**

In the `HandleUpdate` method, after applying the update, compare the hash if present. The `HandleUpdate` method currently looks like:

```csharp
private void HandleUpdate(SubjectUpdate update)
{
    var propertyWriter = _propertyWriter;
    if (propertyWriter is null) return;

    propertyWriter.Write(
        (update, subject: _subject, source: this, factory: _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance),
        static (state, _) =>
        {
            state.subject.ApplySubjectUpdate(state.update, state.factory, (property, propertyUpdate) =>
            {
                // ...
            });
        });
}
```

Change the return type and add hash comparison. Since `HandleUpdate` is called from the receive loop, it should return a bool indicating whether reconnection is needed:

```csharp
/// <summary>
/// Returns true if the update was applied successfully and hashes match (or no hash present).
/// Returns false if a structural hash mismatch was detected (caller should trigger reconnection).
/// </summary>
private bool HandleUpdate(UpdatePayload update)
{
    var propertyWriter = _propertyWriter;
    if (propertyWriter is null) return true;

    propertyWriter.Write(
        (update, subject: _subject, source: this, factory: _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance),
        static (state, _) =>
        {
            state.subject.ApplySubjectUpdate(state.update, state.factory, (property, propertyUpdate) =>
            {
                // ... existing transform logic unchanged
            });
        });

    // Compare structural hash if present in the update
    if (update.StructuralHash is not null)
    {
        try
        {
            var applyLock = _subject.GetApplyLock();
            string? clientHash;
            lock (applyLock)
            {
                clientHash = Internal.StateHashComputer.ComputeStructuralHash(_subject);
            }
            if (clientHash is not null && clientHash != update.StructuralHash)
            {
                _logger.LogWarning(
                    "Structural hash mismatch after update: server={ServerHash}, client={ClientHash}. Triggering reconnection.",
                    update.StructuralHash[..Math.Min(8, update.StructuralHash.Length)],
                    clientHash[..Math.Min(8, clientHash.Length)]);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to compute client state hash after update");
        }
    }

    return true;
}
```

**Step 3: Update the call site in the receive loop**

In the receive loop (around line 454), change:

```csharp
case MessageType.Update:
    var update = _serializer.Deserialize<UpdatePayload>(payloadBytes);
    if (update.Sequence is not null && !_sequenceTracker.IsUpdateValid(update.Sequence.Value))
    {
        _logger.LogWarning(
            "Sequence gap detected: expected {Expected}, received {Received}. Triggering reconnection.",
            _sequenceTracker.ExpectedNextSequence, update.Sequence);
        return; // Exit receive loop -> triggers reconnection
    }
    if (!HandleUpdate(update))
    {
        return; // Hash mismatch -> triggers reconnection
    }
    break;
```

**Step 4: Simplify heartbeat handler — remove quiet-only hash comparison**

Replace the heartbeat handling (lines 457-496) with just sequence checking:

```csharp
case MessageType.Heartbeat:
    var heartbeat = _serializer.Deserialize<HeartbeatPayload>(payloadBytes);
    if (!_sequenceTracker.IsHeartbeatInSync(heartbeat.Sequence))
    {
        _logger.LogWarning(
            "Heartbeat sequence gap: server at {ServerSequence}, client expects {Expected}. Triggering reconnection.",
            heartbeat.Sequence, _sequenceTracker.ExpectedNextSequence);
        return; // Exit receive loop -> triggers reconnection
    }

    // Idle heartbeat: compare structural hash (server only sends
    // heartbeat when no updates were broadcast recently)
    if (heartbeat.StateHash is not null)
    {
        try
        {
            var applyLock = _subject.GetApplyLock();
            string? clientHash;
            lock (applyLock)
            {
                clientHash = Internal.StateHashComputer.ComputeStructuralHash(_subject);
            }
            if (clientHash is not null && clientHash != heartbeat.StateHash)
            {
                _logger.LogWarning(
                    "Structural hash mismatch (idle heartbeat): server={ServerHash}, client={ClientHash}. Triggering reconnection.",
                    heartbeat.StateHash[..Math.Min(8, heartbeat.StateHash.Length)],
                    clientHash[..Math.Min(8, clientHash.Length)]);
                return; // Exit receive loop -> triggers reconnection
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to compute client state hash for heartbeat");
        }
    }
    break;
```

**Step 5: Verify build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeds

**Step 6: Run unit tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: All tests pass

**Step 7: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs
git commit -m "feat: Client compares structural hash on every update

Removes quiet-only hash comparison. Hash is now compared after
every applied update (embedded in UpdatePayload) and on idle
heartbeats. _lastUpdateAppliedTicks field removed."
```

---

### Task 6: Update documentation

**Files:**
- Modify: `docs/plans/fixes.md`
- Modify: `docs/plans/followups.md`

**Step 1: Add Fix 23 entry to fixes.md**

Append to the end of `docs/plans/fixes.md`:

```markdown
---

## Fix 23: CQP filter PathProvider inclusion cache + hash-in-update

**Files changed:** `WebSocketSubjectHandler.cs`, `UpdatePayload.cs`, `WebSocketSubjectClientSource.cs`

**Cause (CQP filter):** Fix 19 relaxed the server CQP filter to accept changes for momentarily unregistered subjects, bypassing PathProvider. PathProvider-excluded property values could leak to clients during the microsecond unregistration window.

**Cause (hash):** Fix 21's heartbeat structural hash only compared when the system was "quiet" (no updates for 10+ seconds). If value mutations never stopped, structural divergence in one part of the graph was never detected.

**Fix (CQP filter):** PathProvider decisions are cached in `subject.Data` on first registered encounter via `Data.GetOrAdd(("ws:included", propertyName), included)`. When the property is later encountered while unregistered, the cached decision is used. Properties never seen while registered are dropped (safe default). No PathProvider bypass, no memory leak.

**Fix (hash-in-update):** Server computes the structural hash inline under `_applyUpdateLock` on every broadcast. Every broadcast includes the hash in the `UpdatePayload.StructuralHash` field. Client compares after each apply. Heartbeat becomes idle-only (suppressed when broadcasts happened recently). Hash comparison on every update replaces quiet-only heartbeat comparison.

**Design:** See [registry-independent pipeline design](2026-04-06-registry-independent-pipeline-design.md).

**Status:** Applied.

### Summary update

Add to the summary section:

### Fix 23: CQP filter cache + hash-in-update
**Files:** `WebSocketSubjectHandler.cs`, `UpdatePayload.cs`, `WebSocketSubjectClientSource.cs`

CQP filter caches PathProvider decisions in `subject.Data` — no PathProvider bypass during unregistration. Structural hash computed inline and embedded in every broadcast — client compares after each apply. Heartbeat only fires during idle periods (no updates for 10s). Replaces Fix 21's quiet-only hash comparison.
```

**Step 2: Mark follow-ups as implemented in followups.md**

Update the first two entries in `docs/plans/followups.md` to mark them as implemented:

For "CQP filter: PathProvider inclusion cache via `subject.Data`":
- Change `**Priority:** High (security)` to `**Priority:** High (security) — **IMPLEMENTED** (Fix 23)`

For "Hash-in-update: embed structural hash in every broadcast":
- Change `**Priority:** High (reliability)` to `**Priority:** High (reliability) — **IMPLEMENTED** (Fix 23)`

For "Structural hash: lazy recomputation instead of per-heartbeat graph walk":
- Keep as-is — NOT implemented yet. Hash is computed inline on every broadcast. Lazy caching is a future optimization if perf testing shows it's needed.

**Step 3: Commit**

```bash
git add docs/plans/fixes.md docs/plans/followups.md
git commit -m "docs: Document Fix 23 and mark follow-ups as implemented"
```

---

### Task 7: Build and run ConnectorTester

**Step 1: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeds with zero errors

**Step 2: Run unit tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: All tests pass

**Step 3: Run ConnectorTester for initial validation**

Run: `dotnet run --project src/Namotion.Interceptor.ConnectorTester -- --ConnectorTester:Connector=websocket --ConnectorTester:Cycles=50`
Expected: 50 cycles without convergence failure. Watch for:
- Zero `Structural hash mismatch after update` warnings during active mutation phase
- Hash mismatches detected and resolved if structural divergence occurs
- Idle heartbeats sent during convergence check (when mutations stop)

**Step 4: Run longer test in background if initial validation passes**

Run: `dotnet run --project src/Namotion.Interceptor.ConnectorTester -- --ConnectorTester:Connector=websocket --ConnectorTester:Cycles=1000`
Expected: 1000 cycles without failure. This validates the full design under sustained load.
