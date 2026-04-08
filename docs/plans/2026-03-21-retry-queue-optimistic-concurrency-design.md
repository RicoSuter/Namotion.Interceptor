# Retry Queue: Optimistic Concurrency on Reconnection

## Problem

When a client disconnects and reconnects, the `WriteRetryQueue` flushes stale changes directly to the server. These changes were made against the client's pre-disconnection state, which may have diverged from the server's current state. Stale structural writes (Collection, Dictionary, ObjectRef replacements) corrupt the server's graph and cause convergence failures.

Observed in chaos testing at 900 structural mutations/sec: consistent failures at cycle 5-50 where server and one client diverge on structural or value properties after retry queue flush.

## Root Cause

Current flow on reconnection:

1. Client reconnects
2. `SubjectPropertyWriter.LoadInitialStateAndResumeAsync` flushes retry queue to server
3. Server applies stale changes (including structural) and broadcasts to all clients
4. Welcome applies (client gets server state)

The retry queue bypasses the Welcome state — stale changes overwrite the server's newer state.

## Solution: Optimistic Concurrency with Local Re-apply

After Welcome, re-apply retry queue entries **locally** using optimistic concurrency: only apply a change if the property's current value (from Welcome) matches the change's old value. This means "the server hasn't touched this property since the client last saw it."

### Initialization sequence (revised)

The documented initialization sequence (see [connectors.md](../connectors.md)) is Buffer → Flush → Load → Replay. This design replaces the Flush step and adds an optimistic retry re-apply step after CQP creation:

1. **Buffer** — `StartListeningAsync()` buffers inbound updates (unchanged)
2. **Load** — `LoadInitialStateAsync()` fetches complete state / Welcome (unchanged, Flush removed)
3. **Replay** — buffered updates replayed after initial state applied (unchanged)
4. **CQP created** — `ChangeQueueProcessor` subscribes to property change queue
5. **Optimistic retry re-apply** (NEW) — drain retry queue, compare each entry with current value, re-apply locally if non-conflicting

The core subscribe-loadstate-apply pattern is preserved. The retry re-apply is an additive step after the core flow. The old Flush step (which sent stale changes to the server) is removed entirely.

**Why after CQP creation:** The re-applied `SetValue` calls trigger change notifications. CQP must be subscribed before these fire, otherwise the changes are captured but never sent to the server.

**Why Flush is removed:** The old flush-before-load rationale was "no visible state toggle." With optimistic re-apply, stale values are never sent to the server, so the concern disappears.

### Optimistic re-apply algorithm

Walk retry queue entries after Welcome + CQP creation:

- Read property's current value (post-Welcome = server's value)
- Compare with `change.OldValue` (what client saw before making the change)
- If `currentValue == oldValue` → server hasn't changed it → call `SetValue(change.NewValue)` locally
- If `currentValue != oldValue` → server (or another client) changed it → drop (server wins)
- Local `SetValue` calls trigger normal change detection → CQP picks them up → sends to server as fresh changes → server applies and broadcasts

### Value comparison strategy

Per the [subject guidelines](../subject-guidelines.md), collections and dictionaries are always replaced (not mutated in place). This means **reference equality** is sufficient for all property types:

| Property Type | Comparison | Rationale |
|---|---|---|
| Value (int, string, etc.) | `Equals()` | Standard value equality |
| ObjectRef | `==` (reference) | Same CLR instance = same subject |
| Collection/Dictionary | `==` (reference) | Collections are replaced, never mutated. If Welcome replaced it, reference differs → server wins. If unchanged, same reference → safe to apply. |

No deep structural comparison or snapshot diffing needed.

### Why this converges

After the optimistic retry:

- **Client state** = Welcome + accepted retry changes (applied locally)
- **Server state** = server state + accepted retry changes (received via normal CQP broadcast)
- Since Welcome state = server state at snapshot time, both sides converge
- Other clients receive the broadcast → also converge

### Both scenarios handled automatically

| Scenario | Behavior |
|---|---|
| Server mutates heavily | Most properties: `currentValue != oldValue` → retries dropped → server wins |
| Server doesn't mutate (coordinator only) | Most properties: `currentValue == oldValue` → retries applied → client changes survive |

No configuration or "mode" switch needed — the property values decide.

### Edge case: concurrent retry from multiple clients

If two clients both retry conflicting changes on the same property:

1. Client A re-applies locally, CQP sends to server
2. Client B re-applies locally, CQP sends to server
3. Server applies both sequentially (last-write-wins at server level)
4. Server broadcasts final state to all clients → everyone converges

This is the same last-write-wins behavior as normal concurrent mutations.

### Edge case: multiple retry entries for the same property

The retry queue can contain multiple entries for the same property (from separate CQP flush batches during disconnection). Sequential replay handles this correctly: each entry's `oldValue` is checked against the current (possibly already modified) value.

### Edge case: detached subjects

If Welcome replaced a CLR object (e.g., new subject for an ObjectRef), the `PropertyReference` in the retry queue still points to the old detached subject. Reading its value returns the stale pre-Welcome value, which matches `oldValue` — a false positive. The `SetValue` fires on a detached object, but since it's no longer in the graph, CQP won't pick it up. The change is effectively dropped. This is harmless and correct (server wins).

## Implementation

### Changes to `WriteRetryQueue`

Add a method to drain the queue for local re-application:

```csharp
public SubjectPropertyChange[] DrainForLocalReapply()
{
    lock (_lock)
    {
        var changes = _pendingWrites.ToArray();
        _pendingWrites.Clear();
        Volatile.Write(ref _count, 0);
        return changes;
    }
}
```

### Changes to `SubjectPropertyWriter`

Remove the `_flushRetryQueueAsync` field and constructor parameter entirely. The retry logic moves to `SubjectSourceBackgroundService`. The constructor becomes:

```csharp
public SubjectPropertyWriter(ISubjectSource source, ILogger logger)
```

In `LoadInitialStateAndResumeAsync`, remove the flush-before-load block (the `if (_flushRetryQueueAsync is not null)` block). The method now goes directly to `LoadInitialStateAsync`.

### Changes to `SubjectSourceBackgroundService`

**Constructor:** Remove the callback wiring. Replace:
```csharp
_propertyWriter = new SubjectPropertyWriter(source,
    _writeRetryQueue is not null ? ct => _writeRetryQueue.FlushAsync(source, ct) : null, logger);
```
with:
```csharp
_propertyWriter = new SubjectPropertyWriter(source, logger);
```

**ExecuteAsync:** Call `ReapplyRetryQueue()` between CQP creation and `ProcessAsync`:

```csharp
await _propertyWriter.LoadInitialStateAndResumeAsync(stoppingToken);

using var processor = new ChangeQueueProcessor(
    _source, _context,
    property => property.Reference.TryGetSource(out var source) && source == _source,
    WriteChangesAsync, _bufferTime, _logger);

ReapplyRetryQueue();

await processor.ProcessAsync(stoppingToken).ConfigureAwait(false);
```

**New private method** `ReapplyRetryQueue`:

```csharp
private void ReapplyRetryQueue()
{
    var retryChanges = _writeRetryQueue?.DrainForLocalReapply();
    if (retryChanges is null || retryChanges.Length == 0)
        return;

    var applied = 0;
    var dropped = 0;
    foreach (var change in retryChanges)
    {
        var property = change.Property;
        var currentValue = property.Metadata.GetValue?.Invoke(property.Subject);
        var oldValue = change.GetOldValue<object>();

        if (Equals(currentValue, oldValue))
        {
            property.Metadata.SetValue?.Invoke(property.Subject, change.GetNewValue<object>());
            applied++;
        }
        else
        {
            dropped++;
        }
    }

    if (applied > 0 || dropped > 0)
    {
        _logger.LogInformation(
            "Retry queue optimistic re-apply: {Applied} applied, {Dropped} dropped (server wins).",
            applied, dropped);
    }
}
```

Note: `change.Property` is a `PropertyReference` (not `RegisteredSubjectProperty`), so we use `property.Metadata.GetValue/SetValue` directly. These call the generated property setter which goes through the interceptor chain — CQP captures the change notifications.

### No changes needed to

- `ChangeQueueProcessor` — re-applied changes go through normal change detection
- `SubjectUpdateFactory` — changes are normal property changes
- `SubjectUpdateApplier` — server applies received changes normally
- `WebSocketSubjectHandler` — broadcast works as before

### Documentation updates

Update connectors.md initialization sequence section to reflect the revised flow (remove Flush, add optimistic retry re-apply step).

## Testing

All tests in `Namotion.Interceptor.Connectors.Tests`. Follow existing patterns: `Person` model, `Mock<ISubjectSource>`, `InterceptorSubjectContext` with `.WithRegistry()` and `.WithPropertyChangeQueue()`.

### WriteRetryQueue tests (new method only)

Add to existing `WriteRetryQueueTests.cs`:

**`WhenDrainForLocalReapply_ThenReturnsAllItemsAndClearsQueue`**
- Enqueue 5 changes
- Call `DrainForLocalReapply()`
- Assert: returns array of 5, `IsEmpty == true`, `PendingWriteCount == 0`

**`WhenDrainForLocalReapplyOnEmptyQueue_ThenReturnsEmptyArray`**
- Call `DrainForLocalReapply()` on empty queue
- Assert: returns empty array (not null)

### SubjectPropertyWriter tests (flush removal)

Update existing `SubjectPropertyWriterTests.cs`:

- Update all constructor calls from `new SubjectPropertyWriter(source, null, logger)` to `new SubjectPropertyWriter(source, logger)` (2-arg constructor).
- Replace the ordering test `WhenCallbackProvided_ThenOrderIsFlushThenInitialStateThenBuffered` with `WhenInitialStateProvided_ThenOrderIsInitialStateThenBuffered` — verifies that without a flush callback, the order is InitialState → BufferedUpdate (2 steps, not 3).

### SubjectSourceBackgroundService tests (optimistic re-apply)

Add to existing `SubjectSourceBackgroundServiceTests.cs`. These are the core tests for the new behavior.

#### Setup pattern for all re-apply tests

```csharp
// 1. Create context with full tracking + registry
var context = InterceptorSubjectContext.Create()
    .WithFullPropertyTracking()
    .WithRegistry();
var subject = new Person(context) { FirstName = "Original" };

// 2. Create source mock
var sourceMock = new Mock<ISubjectSource>();
sourceMock.Setup(s => s.StartListeningAsync(...)).ReturnsAsync((IDisposable?)null);

// 3. Configure LoadInitialStateAsync to simulate Welcome
// IMPORTANT: Wrap in WithSource so CQP ignores Welcome changes (loop prevention)
sourceMock.Setup(s => s.LoadInitialStateAsync(It.IsAny<CancellationToken>()))
    .ReturnsAsync(() =>
    {
        using (SubjectChangeContext.WithSource(sourceMock.Object))
        {
            subject.FirstName = "ServerValue";
        }
    });

// 4. Claim source ownership for properties under test
new PropertyReference(subject, nameof(Person.FirstName)).SetSource(sourceMock.Object);

// 5. Capture writes to verify which changes CQP sends to server
var writtenChanges = new ConcurrentBag<SubjectPropertyChange>();
var writeTcs = new TaskCompletionSource();
sourceMock.Setup(s => s.WriteChangesAsync(...))
    .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
    {
        foreach (var c in changes.ToArray()) writtenChanges.Add(c);
        writeTcs.TrySetResult();
        return new ValueTask<WriteResult>(WriteResult.Success);
    });

// 6. Create service, pre-fill retry queue via reflection, start service
// Use reflection to access _writeRetryQueue field and call Enqueue()
```

#### Test cases

**`WhenRetryQueueHasNonConflictingValueChange_ThenChangeIsReappliedAndSentToServer`**
- Subject starts with `FirstName = "Original"`
- Enqueue retry change: `oldValue = "Original"`, `newValue = "ClientChange"`
- Welcome sets `FirstName = "Original"` (server didn't change it)
- Assert: after initialization, `subject.FirstName == "ClientChange"`
- Assert: `writtenChanges` contains a change for FirstName with newValue "ClientChange"

**`WhenRetryQueueHasConflictingValueChange_ThenChangeIsDropped`**
- Subject starts with `FirstName = "Original"`
- Enqueue retry change: `oldValue = "Original"`, `newValue = "ClientChange"`
- Welcome sets `FirstName = "ServerChanged"` (server DID change it)
- Assert: after initialization, `subject.FirstName == "ServerChanged"`
- Assert: `writtenChanges` does NOT contain FirstName

**`WhenRetryQueueHasNonConflictingCollectionChange_ThenChangeIsReapplied`**
- Subject starts with `Children = listA` (a specific List instance)
- Enqueue retry change: `oldValue = listA`, `newValue = listB` (different List instance)
- Welcome sets `Children = listA` (same reference — server didn't replace it)
- Assert: after initialization, `subject.Children` is `listB` (reference equals)
- Assert: `writtenChanges` contains the collection change

**`WhenRetryQueueHasConflictingCollectionChange_ThenChangeIsDropped`**
- Subject starts with `Children = listA`
- Enqueue retry change: `oldValue = listA`, `newValue = listB`
- Welcome sets `Children = listC` (different reference — server replaced it)
- Assert: after initialization, `subject.Children` is `listC`
- Assert: `writtenChanges` does NOT contain the collection change

**`WhenRetryQueueHasNonConflictingObjectRefChange_ThenChangeIsReapplied`**
- Subject starts with `Father = personA`
- Enqueue retry change: `oldValue = personA`, `newValue = personB`
- Welcome sets `Father = personA` (same reference)
- Assert: after initialization, `subject.Father` is `personB`

**`WhenRetryQueueHasConflictingObjectRefChange_ThenChangeIsDropped`**
- Subject starts with `Father = personA`
- Enqueue retry change: `oldValue = personA`, `newValue = personB`
- Welcome sets `Father = personC` (different reference)
- Assert: after initialization, `subject.Father` is `personC`

**`WhenRetryQueueHasMixedChanges_ThenOnlyNonConflictingAreReapplied`**
- Enqueue changes for FirstName (will conflict) and LastName (won't conflict)
- Welcome changes FirstName but not LastName
- Assert: LastName re-applied and sent to server, FirstName dropped

**`WhenRetryQueueIsEmpty_ThenInitializationSucceedsNormally`**
- No changes enqueued
- Welcome applies
- Assert: service starts normally, no errors

**`WhenAllRetryChangesConflict_ThenAllDroppedAndServiceRunsNormally`**
- Enqueue 3 changes, Welcome changes all 3 properties
- Assert: no changes sent to server via CQP, service runs normally

### Negative / regression tests

**`WhenRetryQueueDisabled_ThenInitializationSucceedsWithoutReapply`** (writeRetryQueueSize = 0)
- Create service with `writeRetryQueueSize: 0`
- Welcome applies
- Assert: service starts normally, `_writeRetryQueue` is null, no exception

**`WhenWriteFailsDuringNormalOperation_ThenChangesStillQueuedForRetry`**
- Verify the existing normal-operation retry path (`FlushAsync` in `WriteChangesAsync`) still works after our changes. This ensures removing `_flushRetryQueueAsync` from SubjectPropertyWriter didn't break the separate normal-operation flush path.

### Integration test

**ConnectorTester full-chaos 1000+ cycles**
- Run `appsettings.websocket.json` with full-chaos profile (server + client-a + client-b all mutating + disconnecting)
- Verify convergence after every cycle
- Target: 1000+ cycles without failure at 900 structural mutations/sec
