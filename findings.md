# OPC UA Client Reconnection — Findings & Fixes

Summary of bugs found and fixes applied during resilience testing of OPC UA client reconnection.

## Goal

**100% pass rate** on connector tester chaos testing (`--launch-profile opcua`).

**Result**: 16/16 cycles PASS (verified).

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

### Fix 4: Skip full state read after SDK subscription transfer (SessionManager.cs, SubjectPropertyWriter.cs)

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

### Additional improvements in SessionManager.cs

- **ConcurrentQueue for session disposal** — Replaces single-slot `_pendingOldSession` to handle multiple sessions queuing for disposal during rapid reconnection.
- **AbandonCurrentSession** — Consolidated session cleanup (kill transport, enqueue for disposal, null session, clear pending reads).
- **DisposeSessionAsync 5s timeout** — Prevents 60s hang when `CloseAsync` sends to a dead connection.
- **Null session handling** — `OnReconnectComplete` with null session now calls `AbandonCurrentSession` instead of just returning.
- **DetachChannel guard** — If preserved session has no transport (SDK called `DetachChannel()`), abandon immediately instead of trying to use it.
- **CreateSessionAsync validation** — Verifies new session is connected with valid transport before accepting.

## Known Remaining Issues

### Request timeout loop (not reproduced)

During early testing (~10-20% of cycles), clients entered a permanent `BadRequestTimeout` (0x80850000) loop where `FetchNamespaceTablesAsync` timed out on every retry. Not reproduced in the final 16/16 test run. May have been incidentally fixed by the transport kill and timeout improvements.

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

## Environment

- OPC UA SDK: OPCFoundation.NetStandard.Opc.Ua.* 1.5.376.244
- .NET 9.0
- Testing: ConnectorTester with `--launch-profile opcua`
- Branch: `feature/resilience-testing`
