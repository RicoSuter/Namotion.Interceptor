# OPC UA Client Reconnection — Findings & Fixes

Summary of bugs found and fixes applied during resilience testing of OPC UA client reconnection.

## Goal

**100% pass rate** on connector tester chaos testing (`--launch-profile opcua`).

**Result**: **18/18 cycles PASS** with Fix 6 + Fix 7. Endurance test completed successfully.

## Fixes Applied

### Fix 1: Transport kill + stale callback guard + SetReconnecting (SessionManager.cs)

**Root cause**: Multiple race conditions during reconnection:

1. **SourceWriteLock contention** — In-flight `session.WriteAsync` on a dead session holds SourceWriteLock for the full OperationTimeout (60s), blocking `LoadInitialStateAndResumeAsync`. The new session expires on the server while waiting.
2. **Stale reconnect callback** — After `ClearSessionAsync` or `TryForceResetIfStalled` replaces the reconnect handler, the old handler's `async void` callback (already queued on the thread pool) fires and corrupts session state.
3. **Keep-alive interference** — During manual `ReconnectSessionAsync`, keep-alive on the newly created session fires immediately, triggering `OnKeepAlive` → `BeginReconnect` → `OnReconnectComplete` → `AbandonCurrentSession`, destroying the session mid-setup.

**Fix**:
- Kill old session transport immediately (`session.NullableTransportChannel?.Dispose()`) in `CreateSessionAsync`, `AbandonCurrentSession`, and `ClearSessionAsync`. This makes in-flight operations fail fast and release SourceWriteLock.
- Added stale callback guard in `OnReconnectComplete`: `ReferenceEquals(sender, _reconnectHandler)` ignores callbacks from disposed/replaced handlers.
- Added `SetReconnecting(bool)` flag. `ReconnectSessionAsync` sets it before starting and clears in finally. `OnKeepAlive` checks it and returns early.

**Impact**: Improved from ~3/20 cycles passing to ~19/20.

### Fix 2: KillAsync / DisconnectAsync race guards (OpcUaSubjectClientSource.cs)

**Root cause**: Chaos events (`KillAsync`, `DisconnectAsync`) can fire during `ReconnectSessionAsync`, destroying the session that was just created.

**Fix**:
- `KillAsync`: When `_reconnectCts` is not null (manual reconnection in progress), only cancel the CTS instead of calling `ClearSessionAsync`.
- `DisconnectAsync`: When `sessionManager.IsReconnecting`, skip the disconnect entirely.

### Fix 3: SessionTimeout configuration (OpcUaClientConfiguration.cs)

- Changed `SessionTimeout` default from 60s to 120s.
- Changed `OperationTimeout` default from 60s to 30s (faster failure detection).
- Fixed `DefaultSessionTimeout` to use `SessionTimeout` property instead of hardcoded 60000.
- Added validation: `SessionTimeout > 2 * OperationTimeout`.

### Fix 4: Skip full state read after SDK subscription transfer (SessionManager.cs, SubjectPropertyWriter.cs) — OUTDATED by Fix 8

**Root cause**: The `subscribe+collect/load/reapply` pattern was designed for initial startup. Master incorrectly reused it after SDK reconnection by setting `_needsInitialization = 1` in `OnReconnectComplete`, triggering a full server state read after subscription transfer.

This breaks eventual consistency: the full state read returns values at time T1, but the transferred subscription delivers pending notifications from before T1. Since `SetValueFromSource` applies unconditionally (no timestamp comparison), stale notifications overwrite fresh read values.

**SDK design**: The subscription transfer mechanism is self-sufficient for state sync:
- **Subscription transfer**: Server delivers queued notifications from the disconnect period.
- **Session recreation** (server restart): SDK creates new subscriptions via `RecreateAsync`, triggering initial data change notifications for ALL monitored items.
- **Preserved session**: Pending notifications cover changes during the brief disconnect.

**Fix**:
- Added `StopBuffering()` to `SubjectPropertyWriter` — replays buffered updates then resumes immediate mode (no full state read).
- In `OnReconnectComplete`, both subscription transfer and preserved session paths call `_propertyWriter.StopBuffering()` instead of setting `_needsInitialization = 1`.
- The full state read (`LoadInitialStateAndResumeAsync`) remains only for `ReconnectSessionAsync` (manual reconnection with new subscriptions).
- Removed the now-dead `_needsInitialization` / `NeedsInitialization` / `ClearInitializationFlag()` code.

**Note**: This bug also exists in master.

**OUTDATED**: Fix 8 reverses this approach. Relying solely on subscription notifications proved unreliable — notifications can be lost due to subscription lifetime expiration, queue overflow, or timing gaps. Fix 8 re-introduces the full state read after ALL reconnections.

### Fix 5: RecordReconnectionSuccess not called from SDK handler path (OpcUaSubjectClientSource.cs, SessionManager.cs) — partially OUTDATED by Fix 8

**Root cause**: `RecordReconnectionSuccess()` (increments `SuccessfulReconnections` counter) was only called from the manual `ReconnectSessionAsync` path. When the SDK's `SessionReconnectHandler` reconnected successfully via `OnReconnectComplete`, the counter was never incremented. This caused the CI integration test `ServerRestart_WithDisconnectionWait_ClientRecovers` to fail with "Should have at least 1 successful reconnection, had 0".

**Fix**:
- Changed `RecordReconnectionSuccess()` from `private` to `internal` in `OpcUaSubjectClientSource.cs`.
- Added `_source.RecordReconnectionSuccess()` calls in `SessionManager.OnReconnectComplete` for both success paths (subscription transfer and preserved session).

**Status**: Committed. Fix 8 removed the preserved session success path (always abandons now), so `RecordReconnectionSuccess` is only called from the transfer success path in `OnReconnectComplete`.

### Additional improvements in SessionManager.cs

- **ConcurrentQueue for session disposal** — Replaces single-slot `_pendingOldSession` to handle multiple sessions queuing for disposal during rapid reconnection.
- **AbandonCurrentSession** — Consolidated session cleanup (kill transport, enqueue for disposal, null session, clear pending reads).
- **DisposeSessionAsync 5s timeout** — Prevents 60s hang when `CloseAsync` sends to a dead connection.
- **Null session handling** — `OnReconnectComplete` with null session now calls `AbandonCurrentSession` instead of just returning.
- **DetachChannel guard** — If preserved session has no transport (SDK called `DetachChannel()`), abandon immediately instead of trying to use it.
- **CreateSessionAsync validation** — Verifies new session is connected with valid transport before accepting.

### Fix 6: Server never restarts after KillAsync — TryDequeue cancellation bug (PropertyChangeQueueSubscription.cs)

**Root cause**: `PropertyChangeQueueSubscription.TryDequeue` fast path dequeues items without checking `CancellationToken`. When producers (mutation engine, other sources) continuously feed the queue, the cancellation token is never observed, and `ChangeQueueProcessor.ProcessAsync` never exits. This prevents the server from restarting after `KillAsync()`.

**Evidence** (server-side diagnostic logging in run +13):
- `[ServerLoop]` shutdown/restart messages never appear after initial startup — server never restarts
- `[Server ReadAsync]` never appears — Read requests never reach the server override
- Server is in a zombie state: CTS cancelled but ProcessAsync still spinning in TryDequeue fast path

**The timeout loop** was a consequence: the zombie server's transport listener stays open, so clients connect to the old instance. CreateSession/ActivateSession succeed (session-layer), but Read requests (FetchNamespaceTablesAsync) time out because the server's service layer is non-functional in the zombie state.

**Fix**: Added `cancellationToken.IsCancellationRequested` check at the top of the `TryDequeue` while loop, before the fast path dequeue. This ensures kill/shutdown signals are observed within one iteration even when the queue is continuously fed.

```csharp
// PropertyChangeQueueSubscription.TryDequeue - before fix:
while (true)
{
    if (_queue.TryDequeue(out item))  // ← never checks cancellation!
        return true;
    // ... _signal.Wait(cancellationToken) only reached when queue is empty
}

// After fix:
while (true)
{
    if (cancellationToken.IsCancellationRequested)  // ← added
    {
        item = default!;
        return false;
    }
    if (_queue.TryDequeue(out item))
        return true;
    // ...
}
```

**Impact**: Fixes the permanent BadRequestTimeout loop (0x80850000) that occurred after rapid successive chaos events (2+ server kills in one cycle).

**Note**: This bug also affects production scenarios — any source that continuously writes properties (MQTT, another OPC UA client) would prevent server restart after KillAsync.

**Status**: Verified — server restarts correctly after kills.

### Fix 7: Flush task deadlock blocks server restart (OpcUaSubjectServerBackgroundService.cs, CustomNodeManager.cs)

**Root cause**: After Fix 6 resolved the TryDequeue issue, a second server restart blocker was uncovered. The `ChangeQueueProcessor`'s flush task would hang during server shutdown, blocking `ProcessAsync` from returning and preventing server disposal/restart.

**Evidence**: The flush task was stuck inside `WriteChangesAsync`, and `server.Dispose()` also hung — a classic ABBA deadlock.

**Deadlock chain**:
```
Thread A (flush task):     lock(node) → ClearChangeMasks → OnMonitoredNodeChanged → tries lock(NodeManager.Lock) — BLOCKED
Thread B (server.Dispose): StandardServer.Dispose → lock(NodeManager.Lock) → node cleanup → tries lock(node) — DEADLOCK
```

The original `lock(node)` in `WriteChangesAsync` was the wrong lock — it's a custom lock that the SDK doesn't use. The SDK coordinates all node access via `NodeManager.Lock` (= `CustomNodeManager2.Lock`). Our `lock(node)` created a second lock in the chain, enabling the deadlock with SDK operations.

**Fix**: **Use `NodeManager.Lock` instead of `lock(node)`** in `WriteChangesAsync`. This matches the SDK's own locking pattern. `ClearChangeMasks` → `OnMonitoredNodeChanged` → `lock(NodeManager.Lock)` is reentrant on the same thread (C# Monitor allows re-entry), so no deadlock. `StandardServer.Dispose` simply waits for the lock to be released, then proceeds normally.

```csharp
// Before: lock(node) { Value; Timestamp; ClearChangeMasks; } — deadlock with SDK
// After:
var nodeManagerLock = server?.NodeManagerLock;
lock (nodeManagerLock)
{
    node.Value = convertedValue;
    node.Timestamp = change.ChangedTimestamp.UtcDateTime;
    node.ClearChangeMasks(currentInstance.DefaultSystemContext, false);
}
```

**Files changed**:
- `OpcUaSubjectServer.cs` — Added `NodeManagerLock` property exposing `CustomNodeManager2.Lock`
- `OpcUaSubjectServerBackgroundService.cs` — `WriteChangesAsync` uses `NodeManager.Lock` instead of `lock(node)`
- `CustomNodeManager.cs` — Removed redundant `lock(variableNode)` from StateChanged handler (now always called under NodeManager.Lock)

**Status**: **18/18 cycles PASS**. Endurance test completed. Code review confirmed correct lock ordering and no remaining deadlock potential.

### Fix 8: Full state read after ALL reconnections + abandon preserved sessions (SessionManager.cs, OpcUaSubjectClientSource.cs)

**Root cause**: Fix 4 removed the full state read after SDK reconnection, relying solely on subscription notifications for state sync. This proved unreliable — chaos testing revealed data convergence failures where client property values remained permanently stale after server kills (cycle 6 of run +11, cycle 18 of parallel run). Both stable-client and flaky-client showed the same stale values, pointing to subscription transfer itself as the source.

**Why subscription notifications alone are insufficient**:
1. **Subscription lifetime expiration**: With `SubscriptionLifetimeCount × RevisedPublishingInterval`, the server silently deletes subscriptions during prolonged client disconnects. The transferred subscription appears valid but is dead.
2. **Notification queue overflow**: Server-side notification queues have finite depth. During disconnect, if mutations continue, older notifications are lost.
3. **Timing gaps**: Between server address space creation and `ChangeQueueProcessor` subscription, mutations can occur that never become notifications.
4. **Stuck reconnect handler**: If `OnReconnectComplete` never fires (observed in cycle-018 log), the client stays in buffering mode permanently.

**Fix** (two changes):

1. **Always perform full state read after SDK subscription transfer**:
   - `OnReconnectComplete` transfer success path: sets `NeedsInitialization = 1` instead of calling `ReplayBufferAndResume()`.
   - Removed `StartBuffering()` from `OnKeepAlive` — no buffering during SDK reconnection itself.
   - Health check loop detects `NeedsInitialization` on healthy session: `StartBuffering()` → `LoadInitialStateAndResumeAsync()` → `ClearInitializationFlag()`.
   - Key insight: buffering starts AFTER SDK reconnection completes (session is healthy), not during the disconnect. This avoids Fix 4's race condition where disconnect-period notifications overwrite read values.

2. **Abandon preserved sessions unconditionally**:
   - `OnReconnectComplete` preserved session path: calls `AbandonCurrentSession()` instead of accepting.
   - Preserved sessions are unreliable: subscriptions may be silently dead (lifetime expired) with no mechanism for future updates.
   - Health check sees dead session → triggers `ReconnectSessionAsync` with fresh subscriptions + full state read.

**Design**: SessionManager no longer stores `_propertyWriter` as a field — it only passes it through to PollingManager/SubscriptionManager constructors. The `_needsInitialization` flag (Interlocked) is the coordination mechanism between `OnReconnectComplete` (sets) and the health check loop (reads/clears). Flag is cleared in `AbandonCurrentSession()` and `ClearSessionAsync()` to prevent stale flags from triggering state reads on wrong sessions.

**Error handling**: If `LoadInitialStateAndResumeAsync` fails during NeedsInitialization handling, `ClearSessionAsync` is called. The writer stays in buffering mode (accumulating notifications). `ReconnectSessionAsync` calls `StartBuffering()` (idempotent) → buffer replays on next successful load.

**Files changed**:
- `SessionManager.cs` — Removed `_propertyWriter` field, added `_needsInitialization` mechanism, simplified `OnReconnectComplete` to two outcomes (transfer → NeedsInitialization, everything else → Abandon)
- `OpcUaSubjectClientSource.cs` — Added NeedsInitialization handling in health check loop

**Status**: Build + tests pass. Needs chaos testing verification.

## Known Remaining Issues

### Request timeout loop → Fixed by Fix 6 + Fix 7

**Symptom**: After rapid successive chaos events (multiple server kills + client kills in one cycle), BOTH clients enter a permanent `BadRequestTimeout` (0x80850000) loop.

**Root cause**: Two server-side bugs prevented restart after KillAsync:
1. `TryDequeue` fast path doesn't check cancellation → server's ProcessAsync never exits (Fix 6)
2. Flush task deadlock with `lock(node)` vs `NodeManager.Lock` → server.Dispose() hangs (Fix 7)

**Status**: Fixed. Server restarts reliably after kills.

### Data convergence failure after server kill + subscription transfer → Addressed by Fix 8

**Symptom**: After server kill → server recover → SDK subscription transfer, some property values remain stale on clients. Reproduced at cycle 6 of run +11 and cycle 18 of parallel run.

**Details**:
- Subject 6's `DecimalValue`: server had 691.1 but both stable-client and flaky-client had 688.83 (stale value with older timestamp).
- Both clients had the same stale value, suggesting the issue is in subscription transfer behavior.
- The server's value had a newer timestamp than the clients' value, confirming it was a missed update.
- Cycle-018 log: flaky-client's SDK reconnect handler never completed after disconnect, leaving client stuck in buffering mode permanently.

**Root cause**: Subscription notifications alone are insufficient for state sync after reconnection. Notifications can be lost due to subscription lifetime expiration, queue overflow, timing gaps, or stuck reconnect handler.

**Status**: Addressed by Fix 8 — full state read after ALL reconnections ensures eventual consistency regardless of subscription notification reliability.

### SDK Bug: ArgumentOutOfRangeException in SetDefaultPermissions (non-blocking)

SDK's `CustomNodeManager2.SetDefaultPermissions` accesses `namespaceMetadataValues[0]` without bounds checking. `ReadAttributes()` returns empty list during server restart. Non-blocking — client handles the failed Read gracefully. Could be reported upstream.

## Test Results History

| Run | Change | Result |
|-----|--------|--------|
| Initial | Transport kill in CreateSessionAsync | 6/7 pass |
| +1 | Transport kill in ClearSessionAsync | 4/5 pass |
| +2 | Always kill transport + dispose | 19/20 pass |
| +3 | + preserved session validation | 4/5 pass (keep-alive race) |
| +4 | + SetReconnecting + stale callback guard | 5/5 pass |
| +5 | + KillAsync/DisconnectAsync race guards | 8/9, 3/4 pass |
| +6 | + StartBuffering in OnReconnectComplete | 0/1 (wrong approach) |
| +7 | + 15min converge window | 7/8 pass (data mismatch) |
| +8 | Skip full state read (Fix 4) + cleanup | **16/16 pass** |
| +9 | 1h endurance test (cleanup + rename) | **8/9 pass** (timeout loop) |
| +10 | + diagnostic step logging | **15/16 pass** (timeout loop at cycle 16) |
| +11 | + exception/threadpool/stopwatch diagnostics | **5/6 pass** (data convergence at cycle 6) |
| +12 | same diagnostics (re-run) | **0/1 fail** (timeout loop at cycle 1 — FetchNamespaceTables identified) |
| +13 | Fix 6: TryDequeue cancellation check | server restarts but flush task hangs |
| +14 | Fix 7: NodeManager.Lock + cancellation check | **18/18 pass** |

## Environment

- OPC UA SDK: OPCFoundation.NetStandard.Opc.Ua.* 1.5.376.244
- OPC UA SDK source: C:\Users\rsute\GitHub\UA-.NETStandard
- .NET 9.0
- Testing: ConnectorTester with `--launch-profile opcua`
- Branch: `feature/resilience-testing`
