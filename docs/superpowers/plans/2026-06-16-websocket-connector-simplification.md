# WebSocket Connector Simplification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the client to server direction the same delivery guarantee the server to client direction already has (sequence numbers, gap detection, complete-state recovery), then remove the entire structural-hash/shadow layer that was masking the missing guarantee.

**Architecture:** Mirror the proven server to client reliability mechanism in reverse. The client stamps a monotonic sequence on each outbound update and reports its last-sent sequence during idle; the server detects gaps per connection and, on a gap, asks that client to resend its complete owned state (a reverse Welcome). Once both directions are guaranteed, delete `SentStructuralState`, the structural hash, the divergence checks, and the reconnect-on-divergence path. Stage as disable, implement, verify, remove: the hash triggers are already disabled by a spike, so tests prove the new mechanism on its own before any code is deleted.

**Tech Stack:** C# 13 / .NET 9, System.Text.Json WebSocket serialization, xUnit. Validation via the `Namotion.Interceptor.ConnectorTester` chaos and structural profiles.

Spec: `docs/superpowers/specs/2026-06-16-websocket-connector-simplification-design.md`

---

## File Structure

Created:
- `src/Namotion.Interceptor.WebSocket/Server/ConnectionSequenceTracker.cs` - server-side per-connection tracker of the expected next client sequence (mirror of the client's `ClientSequenceTracker`).
- `src/Namotion.Interceptor.WebSocket/Protocol/ResyncPayload.cs` - server to client "resend your complete owned state" control message.
- `src/Namotion.Interceptor.WebSocket/Protocol/ClientHeartbeatPayload.cs` - client to server idle report carrying the client's last-sent sequence.
- `src/Namotion.Interceptor.WebSocket.Tests/Server/ConnectionSequenceTrackerTests.cs` - unit tests for the tracker.

Modified:
- `src/Namotion.Interceptor.WebSocket/Protocol/MessageType.cs` - add `Resync` and `ClientHeartbeat`.
- `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs` - stamp outbound sequence, reset on connect, reply to heartbeats with last-sent sequence, handle `Resync` by sending complete owned state. (Stage D: delete the hash spike and the two hash methods.)
- `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs` - read inbound sequence, detect gaps, send `Resync`; check `ClientHeartbeat` for trailing-idle gaps. (Stage D: stop computing/sending the structural hash.)
- `src/Namotion.Interceptor.WebSocket/Server/WebSocketClientConnection.cs` - own the `ConnectionSequenceTracker`; add `SendResyncAsync`; deserialize inbound as `UpdatePayload`. (Stage D: delete the `SentStructuralState` field and hash methods.)

Deleted (Stage D only):
- `src/Namotion.Interceptor.WebSocket/Internal/SentStructuralState.cs` and `src/Namotion.Interceptor.WebSocket.Tests/Internal/SentStructuralStateTests.cs`.

---

## Phase A: Client to server in-stream sequencing, gap detection, resync recovery

### Task A1: Server-side connection sequence tracker

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket/Server/ConnectionSequenceTracker.cs`
- Test: `src/Namotion.Interceptor.WebSocket.Tests/Server/ConnectionSequenceTrackerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Namotion.Interceptor.WebSocket.Server;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

public class ConnectionSequenceTrackerTests
{
    [Fact]
    public void WhenFirstClientMessageIsSequenceOne_ThenItIsValidAndAdvances()
    {
        // Arrange
        var tracker = new ConnectionSequenceTracker();

        // Act
        var valid = tracker.IsClientUpdateValid(1);

        // Assert
        Assert.True(valid);
        Assert.Equal(2, tracker.ExpectedNextSequence);
    }

    [Fact]
    public void WhenSequenceSkipsAhead_ThenGapIsDetected()
    {
        // Arrange
        var tracker = new ConnectionSequenceTracker();
        tracker.IsClientUpdateValid(1);

        // Act
        var valid = tracker.IsClientUpdateValid(3); // expected 2

        // Assert
        Assert.False(valid);
    }

    [Fact]
    public void WhenClientReportsLastSentBeyondReceived_ThenTrailingGapIsDetected()
    {
        // Arrange
        var tracker = new ConnectionSequenceTracker();
        tracker.IsClientUpdateValid(1); // received through 1, expected 2

        // Act & Assert
        Assert.True(tracker.HasReceivedThrough(1));   // server has everything the client sent
        Assert.False(tracker.HasReceivedThrough(2));  // client sent 2, server never got it
    }

    [Fact]
    public void WhenResetAfterReconnect_ThenExpectsSequenceOneAgain()
    {
        // Arrange
        var tracker = new ConnectionSequenceTracker();
        tracker.IsClientUpdateValid(1);

        // Act
        tracker.Reset();

        // Assert
        Assert.Equal(1, tracker.ExpectedNextSequence);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "FullyQualifiedName~ConnectionSequenceTracker"`
Expected: FAIL to compile (`ConnectionSequenceTracker` does not exist).

- [ ] **Step 3: Implement the tracker**

```csharp
using System.Threading;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Tracks the expected next sequence number from a single client connection and detects gaps.
/// Mirror of the client-side <see cref="Client.ClientSequenceTracker"/> for the client-to-server direction.
/// A gap means the server missed one or more client updates and must request a resync.
/// </summary>
internal sealed class ConnectionSequenceTracker
{
    private long _expectedNextSequence = 1; // client's first message after connect is sequence 1

    public long ExpectedNextSequence => Volatile.Read(ref _expectedNextSequence);

    /// <summary>Resets to expect sequence 1 again (new connection / reconnect).</summary>
    public void Reset() => Volatile.Write(ref _expectedNextSequence, 1);

    /// <summary>
    /// Validates an inbound client update sequence. Returns true and advances when it is the
    /// expected next sequence; false when a gap is detected (server missed earlier messages).
    /// </summary>
    public bool IsClientUpdateValid(long sequence)
    {
        if (sequence != Volatile.Read(ref _expectedNextSequence))
        {
            return false;
        }

        Volatile.Write(ref _expectedNextSequence, sequence + 1);
        return true;
    }

    /// <summary>
    /// Idle check: given the client's reported last-sent sequence, returns true when the server has
    /// already received everything the client sent (mirror of the client's heartbeat sequence check).
    /// </summary>
    public bool HasReceivedThrough(long clientLastSentSequence)
        => clientLastSentSequence < Volatile.Read(ref _expectedNextSequence);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "FullyQualifiedName~ConnectionSequenceTracker"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Server/ConnectionSequenceTracker.cs src/Namotion.Interceptor.WebSocket.Tests/Server/ConnectionSequenceTrackerTests.cs
git commit -m "feat(websocket): add server-side connection sequence tracker"
```

### Task A2: Add Resync message type and payload

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Protocol/MessageType.cs`
- Create: `src/Namotion.Interceptor.WebSocket/Protocol/ResyncPayload.cs`

- [ ] **Step 1: Add the message type**

In `MessageType.cs`, add after `Heartbeat = 4`:

```csharp
    ,
    /// <summary>
    /// Server asks a client to resend its complete owned state after detecting a
    /// gap in that client's update sequence (reverse Welcome).
    /// </summary>
    Resync = 5,

    /// <summary>
    /// Client reports its last-sent sequence to the server during idle so the server
    /// can detect a trailing client-to-server loss that has no following message.
    /// </summary>
    ClientHeartbeat = 6
```

- [ ] **Step 2: Create the Resync payload**

```csharp
namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Payload for the Resync control message. Carries no state: it simply instructs the
/// client to resend a complete update of its owned properties.
/// </summary>
public class ResyncPayload
{
    /// <summary>Optional reason for diagnostics (e.g. "sequence-gap", "idle-trailing-gap").</summary>
    public string? Reason { get; set; }
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.WebSocket/Namotion.Interceptor.WebSocket.csproj -c Release`
Expected: Build succeeded, 0 warnings (warnings are errors in this repo).

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Protocol/MessageType.cs src/Namotion.Interceptor.WebSocket/Protocol/ResyncPayload.cs
git commit -m "feat(websocket): add Resync and ClientHeartbeat message types"
```

### Task A3: Client stamps a monotonic sequence on outbound updates

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs`

Anchors: the send method currently builds a bare `SubjectUpdate` and serializes it (around line 387-392). The reset point is where the client initializes from Welcome (`_sequenceTracker.InitializeFromWelcome`, around line 243). `UpdatePayload : SubjectUpdate` already carries the optional `Sequence`.

- [ ] **Step 1: Add the send-sequence field**

Near the other client fields (with `_sequenceTracker`), add:

```csharp
    // Monotonic per-connection sequence stamped on each client-to-server update.
    // Reset on every (re)connect so the server's per-connection tracker stays aligned.
    private long _clientSendSequence;
```

- [ ] **Step 2: Reset the sequence on connect**

Immediately after the `_sequenceTracker.InitializeFromWelcome(welcome.Sequence);` call, add:

```csharp
            Interlocked.Exchange(ref _clientSendSequence, 0);
```

- [ ] **Step 3: Stamp the sequence on send**

In the send method, replace the bare `SubjectUpdate` serialization with an `UpdatePayload` carrying the next sequence (mirrors the server's `BroadcastUpdateAsync`):

```csharp
            var update = SubjectUpdate.CreatePartialUpdateFromChanges(_subject, changes.Span, _processors);
            var payload = new UpdatePayload
            {
                Root = update.Root,
                Subjects = update.Subjects,
                Sequence = Interlocked.Increment(ref _clientSendSequence)
            };
            _sendBuffer.Clear();
            _serializer.SerializeMessageTo(_sendBuffer, MessageType.Update, payload);
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.WebSocket/Namotion.Interceptor.WebSocket.csproj -c Release`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs
git commit -m "feat(websocket): stamp monotonic sequence on client-to-server updates"
```

### Task A4: Server detects inbound gaps and requests resync; client responds with complete owned state

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketClientConnection.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs`

- [ ] **Step 1: Give each connection a tracker and a SendResyncAsync**

In `WebSocketClientConnection.cs`, add a field and method (next to the existing send helpers):

```csharp
    private readonly ConnectionSequenceTracker _clientSequence = new();

    public ConnectionSequenceTracker ClientSequence => _clientSequence;

    public Task SendResyncAsync(string? reason, CancellationToken cancellationToken)
    {
        var message = _serializer.SerializeMessage(MessageType.Resync, new ResyncPayload { Reason = reason });
        return SendRawAsync(message, cancellationToken);
    }
```

Use whatever existing low-level send helper the other `Send*Async` methods use (for example the same path `SendWelcomeAsync`/`SendHeartbeatAsync` call). If a private raw-send helper does not exist, send through the same `_sendLock` + `WebSocket.SendAsync` pattern those methods already use.

- [ ] **Step 2: Read the inbound sequence and detect gaps in the receive loop**

In `WebSocketSubjectHandler.ReceiveUpdatesAsync`, deserialize the inbound message as `UpdatePayload` (it is a superset of `SubjectUpdate`) so the `Sequence` is available, then check it before applying. Replace the apply block (around line 214-224) with:

```csharp
            try
            {
                if (update is UpdatePayload payload && payload.Sequence is { } clientSequence)
                {
                    if (!connection.ClientSequence.IsClientUpdateValid(clientSequence))
                    {
                        _logger.LogWarning(
                            "Client {ConnectionId}: client-to-server sequence gap (expected {Expected}, received {Received}). Requesting resync.",
                            connection.ConnectionId, connection.ClientSequence.ExpectedNextSequence, clientSequence);
                        await connection.SendResyncAsync("sequence-gap", stoppingToken).ConfigureAwait(false);
                    }
                }

                var factory = _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance;
                lock (_applyUpdateLock)
                {
                    using (SubjectChangeContext.WithSource(connection))
                    {
                        _subject.ApplySubjectUpdate(update, factory);
                    }
                }
            }
```

Ensure `connection.ReceiveUpdateAsync` returns `UpdatePayload` (deserialize as `UpdatePayload` instead of `SubjectUpdate`). `UpdatePayload : SubjectUpdate`, so the existing apply call and null checks are unaffected. Apply the update even on a gap: the requested resync is complete-owned-state and supersedes anything missing.

- [ ] **Step 3: Client builds and sends its complete owned state on Resync**

In `WebSocketSubjectClientSource.cs`, add a builder that emits the current value of every owned property (the owned set comes from `_ownership.Properties`; reuse `CreatePartialUpdateFromChanges`):

```csharp
    private SubjectUpdate BuildOwnedStateUpdate()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var owned = _ownership.Properties; // IReadOnlyCollection<PropertyReference>
        var changes = new List<SubjectPropertyChange>(owned.Count);
        foreach (var property in owned)
        {
            var current = property.GetValue(); // current value as object (verify accessor name)
            changes.Add(SubjectPropertyChange.Create(property, this, timestamp, null, current, current));
        }

        return SubjectUpdate.CreatePartialUpdateFromChanges(
            _subject, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(changes), _processors);
    }
```

Add a send method that stamps the next sequence (same shape as the normal send):

```csharp
    private async Task SendOwnedStateResyncAsync(System.Net.WebSockets.WebSocket webSocket, CancellationToken cancellationToken)
    {
        var update = BuildOwnedStateUpdate();
        var payload = new UpdatePayload
        {
            Root = update.Root,
            Subjects = update.Subjects,
            Sequence = Interlocked.Increment(ref _clientSendSequence)
        };
        _sendBuffer.Clear();
        _serializer.SerializeMessageTo(_sendBuffer, MessageType.Update, payload);
        await webSocket.SendAsync(_sendBuffer.WrittenMemory, System.Net.WebSockets.WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }
```

In the receive loop `switch (messageType)`, add a case:

```csharp
                            case MessageType.Resync:
                                _logger.LogInformation("Server requested resync; resending complete owned state.");
                                await SendOwnedStateResyncAsync(webSocket, cancellationToken).ConfigureAwait(false);
                                break;
```

- [ ] **Step 4: Build and run the WebSocket integration suite**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests`
Expected: PASS (the existing integration tests still pass with sequencing wired in).

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Server/WebSocketClientConnection.cs src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs
git commit -m "feat(websocket): detect client-to-server gaps and recover via owned-state resync"
```

## Phase B: Trailing-idle gap detection

The in-stream check in Phase A only fires when a later client message arrives. The cycle-21 failure was the last write before idle, so we also need the client to report its last-sent sequence during idle and the server to act on it.

### Task B1: Client replies to server heartbeats with its last-sent sequence

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket/Protocol/ClientHeartbeatPayload.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs`

- [ ] **Step 1: Create the payload**

```csharp
namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Client-to-server idle report carrying the client's last-sent update sequence,
/// letting the server detect a trailing loss that has no following update.
/// </summary>
public class ClientHeartbeatPayload
{
    public long LastSentSequence { get; set; }
}
```

- [ ] **Step 2: Send it in response to a server heartbeat**

In the receive loop's `case MessageType.Heartbeat:` block (after the existing `IsHeartbeatInSync` check), add:

```csharp
                                var clientHeartbeat = new ClientHeartbeatPayload
                                {
                                    LastSentSequence = Interlocked.Read(ref _clientSendSequence)
                                };
                                _sendBuffer.Clear();
                                _serializer.SerializeMessageTo(_sendBuffer, MessageType.ClientHeartbeat, clientHeartbeat);
                                await webSocket.SendAsync(_sendBuffer.WrittenMemory, System.Net.WebSockets.WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.WebSocket/Namotion.Interceptor.WebSocket.csproj -c Release`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Protocol/ClientHeartbeatPayload.cs src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs
git commit -m "feat(websocket): client reports last-sent sequence on idle heartbeat"
```

### Task B2: Server handles ClientHeartbeat and requests resync on a trailing gap

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketClientConnection.cs`

- [ ] **Step 1: Receive and route the ClientHeartbeat**

The server receive path currently expects only updates. Extend `WebSocketClientConnection.ReceiveUpdateAsync` (or the envelope read in `ReceiveUpdatesAsync`) to inspect the message type. When it is `ClientHeartbeat`, deserialize `ClientHeartbeatPayload` and check it:

```csharp
            // inside ReceiveUpdatesAsync, after reading the message type/envelope
            if (messageType == MessageType.ClientHeartbeat)
            {
                var heartbeat = _serializer.Deserialize<ClientHeartbeatPayload>(payloadBytes);
                if (!connection.ClientSequence.HasReceivedThrough(heartbeat.LastSentSequence))
                {
                    _logger.LogWarning(
                        "Client {ConnectionId}: trailing client-to-server gap (client sent through {Sent}, server received through {Received}). Requesting resync.",
                        connection.ConnectionId, heartbeat.LastSentSequence, connection.ClientSequence.ExpectedNextSequence - 1);
                    await connection.SendResyncAsync("idle-trailing-gap", stoppingToken).ConfigureAwait(false);
                }
                continue; // not a state update
            }
```

If the current `ReceiveUpdateAsync` hides the message type, add a method that returns the envelope (type + payload bytes) so the loop can branch. Keep the update path unchanged.

- [ ] **Step 2: Build and run the WebSocket integration suite**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs src/Namotion.Interceptor.WebSocket/Server/WebSocketClientConnection.cs
git commit -m "feat(websocket): server detects trailing client gaps via client heartbeat"
```

## Phase C: Validation (hash still disabled)

The hash triggers stay disabled (the existing spike) so these runs prove the new mechanism alone.

### Task C1: Re-run the experiments that exposed the problems

- [ ] **Step 1: Full build**

Run: `dotnet build src/Namotion.Interceptor.slnx -c Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 2: No-hash chaos run must now pass past cycle 21**

Run: `dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile websocket-chaos --configuration Release`
Expected: cycles keep passing well beyond 21, including `full-chaos` rounds and `no-chaos` cycles. The previous failure was a no-chaos cycle-21 client-to-server value loss; with sequencing it must be detected and recovered. Watch `logs/<run>/cycles.csv` for any `Fail`. Stop after at least 40 cycles: `pkill -TERM -f Namotion.Interceptor.ConnectorTester`.

- [ ] **Step 3: Structural-no-chaos heap must stay flat**

Run: `dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile websocket-structural-nochaos --configuration Release`
Expected: the per-30s live `HeapMB` in `logs/<run>/performance-server.csv` stays flat over the 20-minute mutate phase (no shadow to leak; contrast the earlier 56 MB to 2.5 GB climb). Stop after one full cycle.

- [ ] **Step 4: Full WebSocket integration suite**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests`
Expected: 143 tests pass.

- [ ] **Step 5: Commit a checkpoint note (no code change)**

If any run fails, do not proceed to Phase D. Root-cause the failure (the new mechanism has a gap) before deleting the hash. Only when all three are green, continue.

## Phase D: Remove the hash/shadow layer

Only after Phase C is green. This converts the temporary spike into the real removal.

### Task D1: Delete the client hash checks and the spike

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs`

- [ ] **Step 1: Remove the methods and call sites**

Delete `HasStructuralHashMismatch` and `HasRegistryDivergence` (including the spike `return false;` and the `#pragma warning disable CS0162`). Remove their call sites in the receive loop: the `if (!HandleUpdate(update)) return;` hash-mismatch branch becomes `HandleUpdate(update);` (still apply), and remove the heartbeat-path `HasStructuralHashMismatch`/`HasRegistryDivergence` calls. Remove `_clientState` (`SentStructuralState`), `_lastUpdateReceivedTicks`, and the `_clientState.UpdateFromBroadcast(update)` call in `HandleUpdate`. `HandleUpdate` returns void (or is inlined).

- [ ] **Step 2: Build**

Run: `dotnet build src/Namotion.Interceptor.WebSocket/Namotion.Interceptor.WebSocket.csproj -c Release`
Expected: Build succeeded (unreachable-code pragma is gone with the methods).

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs
git commit -m "refactor(websocket): remove client structural-hash and divergence checks"
```

### Task D2: Delete the server hash computation and the shadow class

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketClientConnection.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Protocol/UpdatePayload.cs`, `Protocol/HeartbeatPayload.cs`
- Delete: `src/Namotion.Interceptor.WebSocket/Internal/SentStructuralState.cs`, `src/Namotion.Interceptor.WebSocket.Tests/Internal/SentStructuralStateTests.cs`

- [ ] **Step 1: Remove hash usage on the server**

In `WebSocketSubjectHandler.CreateUpdateWithSequence` remove `connection.UpdateSentState(update)` and `connection.ComputeSentStateHash()`; broadcasts no longer set `StructuralHash`. In `BroadcastHeartbeatAsync` remove the `StateHash`. In `WebSocketClientConnection` remove the `SentStructuralState` field, `UpdateSentState`, `ComputeSentStateHash`, and `InitializeSentState`; remove the `InitializeSentState` call in the Welcome path. Remove `StructuralHash` from `UpdatePayload` and `StateHash` from `HeartbeatPayload`.

- [ ] **Step 2: Delete the shadow class and its tests**

```bash
git rm src/Namotion.Interceptor.WebSocket/Internal/SentStructuralState.cs src/Namotion.Interceptor.WebSocket.Tests/Internal/SentStructuralStateTests.cs
```

- [ ] **Step 3: Build and run the suite**

Run: `dotnet build src/Namotion.Interceptor.slnx -c Release && dotnet test src/Namotion.Interceptor.WebSocket.Tests`
Expected: Build succeeded; tests pass.

- [ ] **Step 4: Commit**

```bash
git add -A src/Namotion.Interceptor.WebSocket
git commit -m "refactor(websocket): remove SentStructuralState shadow and structural hash"
```

### Task D3: Remove the dead idle-divergence config and ChangeQueueProcessor.IsIdle

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Client/WebSocketClientConfiguration.cs`
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`

- [ ] **Step 1: Remove `IdleDivergenceCheckDelay`**

Delete the `IdleDivergenceCheckDelay` property and any references (it gated only the deleted divergence check).

- [ ] **Step 2: Remove the dead `IsIdle()` and `_lastFlushWithChangesTicks`**

These have no callers (confirmed during investigation). Remove them from `ChangeQueueProcessor`.

- [ ] **Step 3: Build and run the broader suite**

Run: `dotnet build src/Namotion.Interceptor.slnx -c Release && dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: Build succeeded; unit tests pass.

- [ ] **Step 4: Re-run validation (Phase C) to confirm removal changed nothing**

Repeat Task C1 steps 2-4. Expected: same green results (the removal is behavior-neutral now that the new mechanism carries correctness).

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Client/WebSocketClientConfiguration.cs src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs
git commit -m "refactor: remove dead idle-divergence config and ChangeQueueProcessor.IsIdle"
```

## Phase E: Documentation and scaffolding cleanup

### Task E1: Update connector docs and remove experiment scaffolding and working docs

- [ ] **Step 1: Update `docs/connectors-websocket.md`** to describe the symmetric sequence-based reliability (both directions) and remove any structural-hash description. Keep it compact; this is the canonical location.

- [ ] **Step 2: Remove the experiment scaffolding**

```bash
git rm src/Namotion.Interceptor.ConnectorTester/appsettings.websocket-structural-nochaos.json
```

Revert the added `websocket-structural-nochaos` profile in `src/Namotion.Interceptor.ConnectorTester/Properties/launchSettings.json` (unless keeping it as a documented tester profile is desired; if kept, document it in `docs/connector-tester.md` instead of removing).

- [ ] **Step 3: Remove the working spec and plan docs** (per the agreed lifecycle: keep through implementation, remove at the end)

```bash
git rm docs/superpowers/specs/2026-06-16-websocket-connector-simplification-design.md docs/superpowers/plans/2026-06-16-websocket-connector-simplification.md
```

- [ ] **Step 4: Final full validation**

Run: `dotnet build src/Namotion.Interceptor.slnx -c Release && dotnet test src/Namotion.Interceptor.WebSocket.Tests`
Plus one more `websocket-chaos` long run to confirm sustained convergence and flat post-GC heap in `cycles.csv`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "docs(websocket): document symmetric reliability; remove working specs and experiment scaffolding"
```

---

## Notes for the implementer

- Apply the message-type `enum` addition carefully: `MessageType` currently ends at `Heartbeat = 4` with no trailing comma. Add the comma and the two new members.
- The server's `ReceiveUpdateAsync`/receive loop must branch on message type once clients send `ClientHeartbeat` as well as `Update`. If the current code assumes every inbound message is an update, refactor it to read the envelope (type + payload range) first, like the client receive loop already does.
- `PropertyReference.GetValue()` is the assumed current-value accessor in `BuildOwnedStateUpdate`; confirm the exact accessor name against `PropertyReference` and adjust if it differs.
- Keep the per-subject apply lock and all data-path fixes (12-22) untouched. Only the hash-snapshot use of the apply lock is obsolete, and it disappears naturally when the hash computation under the lock is removed in D2.
- Commit messages must not include AI attribution (repo rule).
