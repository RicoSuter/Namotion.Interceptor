# OPC UA Client Reconnection — Findings & Fixes

Summary of bugs found and fixes applied during resilience testing of OPC UA client reconnection.

## Goal

**100% pass rate** on connector tester chaos testing (`--launch-profile opcua`).

**Result**: 8/9 cycles PASS in extended run (1h endurance test). 16/16 in initial verification.

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

### Fix 5: RecordReconnectionSuccess not called from SDK handler path (OpcUaSubjectClientSource.cs, SessionManager.cs)

**Root cause**: `RecordReconnectionSuccess()` (increments `SuccessfulReconnections` counter) was only called from the manual `ReconnectSessionAsync` path. When the SDK's `SessionReconnectHandler` reconnected successfully via `OnReconnectComplete`, the counter was never incremented. This caused the CI integration test `ServerRestart_WithDisconnectionWait_ClientRecovers` to fail with "Should have at least 1 successful reconnection, had 0".

**Fix**:
- Changed `RecordReconnectionSuccess()` from `private` to `internal` in `OpcUaSubjectClientSource.cs`.
- Added `_source.RecordReconnectionSuccess()` calls in `SessionManager.OnReconnectComplete` for both success paths (subscription transfer and preserved session).

**Status**: Not yet committed.

### Additional improvements in SessionManager.cs

- **ConcurrentQueue for session disposal** — Replaces single-slot `_pendingOldSession` to handle multiple sessions queuing for disposal during rapid reconnection.
- **AbandonCurrentSession** — Consolidated session cleanup (kill transport, enqueue for disposal, null session, clear pending reads).
- **DisposeSessionAsync 5s timeout** — Prevents 60s hang when `CloseAsync` sends to a dead connection.
- **Null session handling** — `OnReconnectComplete` with null session now calls `AbandonCurrentSession` instead of just returning.
- **DetachChannel guard** — If preserved session has no transport (SDK called `DetachChannel()`), abandon immediately instead of trying to use it.
- **CreateSessionAsync validation** — Verifies new session is connected with valid transport before accepting.

## Known Remaining Issues

### Request timeout loop (reproduced: 3 times)

**Symptom**: After rapid successive chaos events (multiple server kills + client kills in one cycle), BOTH clients enter a permanent `BadRequestTimeout` (0x80850000) loop. Every reconnection attempt creates a session on the server but `CreateAsync()` never returns — it times out after 30s (OperationTimeout).

**Root cause identified by enhanced diagnostics (run 3, cycle 1)**:

Stack traces from the enhanced exception logging reveal the failure is in **`FetchNamespaceTablesAsync`**, NOT `ActivateSession` as initially hypothesized:

```
Session.OpenAsync
  → Session.FetchNamespaceTablesAsync    ← FAILS HERE
    → SessionClient.ReadAsync
      → UaSCUaBinaryClientChannel.SendRequestAsync
        → ChannelAsyncOperation.EndAsync  [80850000 / 80840000]
```

**The session is successfully created and activated** (server logs confirm "session created"), but the subsequent `ReadAsync` call to fetch the namespace table times out.

**Detailed timeline from run 3 (cycle 1)**:

1. Chaos: kill server → restart → both clients reconnect successfully (step 4/4)
2. Chaos: kill stable-client, disconnect server (= kill server again), kill flaky-client, kill stable-client (rapid succession)
3. Sessions created on the DYING server (355, 412) — CreateSession/ActivateSession succeed but FetchNamespaceTablesAsync times out because server is shutting down
4. Server restarts (3rd time), clients recover
5. Both clients enter permanent loop: create session on NEW server → FetchNamespaceTables times out → retry

**Key findings from enhanced diagnostics**:

1. **Failure is in `FetchNamespaceTablesAsync`** — The session is created and activated. The server logs "session created". But the follow-up `ReadAsync` to fetch namespace tables times out. This means the server's security/session layer works but the service layer (Read/Write/Browse) is unresponsive.

2. **First error is `BadSecureChannelIdInvalid` (0x80840000)** after 14273ms — NOT a timeout but an active server rejection. The secure channel became invalid between ActivateSession and FetchNamespaceTablesAsync. This likely happens because the server was killed mid-connection. Subsequent errors are `BadRequestTimeout` (0x80850000) after exactly 30000ms.

3. **Active sessions drop to 1** but loop continues — rules out zombie session accumulation as root cause.

4. **Thread pool is fine** — 32740+/32767 workers available throughout. Workers slowly leak (~1 per 2 attempts) but not starvation.

5. **Loop is permanent** — 30+ consecutive failures with zero self-recovery.

6. **Server creates sessions on the new server instance** — After the server restarts, new sessions are created (Active sessions: 1, 2, etc.) but FetchNamespaceTablesAsync still times out. The server's service layer is non-functional despite session management working.

7. **Multiple server kills in one cycle** is the trigger — The loop always starts after 2+ rapid server kills with interleaved client kills. Single server kills never cause this issue.

**Root cause hypothesis** (updated):

The server successfully handles CreateSession/ActivateSession (session-layer operations) but fails to respond to Read requests (service-layer operations). This suggests the server's service request processing pipeline is blocked or deadlocked after rapid kill/restart cycles.

Remaining hypotheses:
1. **Server service layer deadlock** — The `MasterNodeManager` or `StandardServer.ProcessRequest` may hold a lock from the previous server instance that prevents the new instance from processing Read requests. Shared static state between server instances could cause this.
2. **Custom node manager initialization blocking** — After server kill/restart, our `CustomNodeManager` may not be fully initialized, and incoming Read requests for namespace 0 nodes might be queued behind a blocked node manager initialization.
3. **TCP socket/channel corruption** — The rapid kill/restart cycle may leave orphaned TCP connections or half-open channels that interfere with new connections to the same port.

**Next diagnostics needed**:
1. **Server-side Read request logging** — Override `Read` in the server or add middleware to confirm whether Read requests reach the server. If they don't, the issue is in the transport/channel layer. If they do, the issue is in the service processing pipeline.
2. **Test with a third fresh client** — After the loop starts, try connecting a brand new client. If it succeeds, the issue is on the stuck clients. If it fails, the issue is on the server.

### Data convergence failure after server kill + subscription transfer

**Symptom**: After server kill → server recover → SDK subscription transfer, some property values remain stale on clients. Reproduced at cycle 6 of run +11.

**Details**:
- Subject 6's `DecimalValue`: server had 691.1 but both stable-client and flaky-client had 688.83 (stale value with older timestamp).
- Both clients had the same stale value, suggesting the issue is in subscription transfer behavior.
- The server's value had a newer timestamp than the clients' value, confirming it was a missed update.

**Root cause hypothesis**: After server kill, the SDK's subscription transfer mechanism (`RecreateAsync`) creates new subscriptions and delivers initial data change notifications. However, if the server was killed mid-mutation, the notification for the last write may be lost — the write was applied to the server's in-memory model but the data change notification was never sent (or was sent but not acknowledged before the transport died). On recreation, the subscription's initial notification delivers the current server value, but if the mutation engine wrote a new value between server recovery and subscription recreation, that intermediate value may not trigger a new notification if the monitored item's sampling interval hasn't elapsed.

**Frequency**: Rare (1 occurrence in ~50 cycles across all runs). Lower priority than the BadRequestTimeout loop.

**Status**: Under observation. May be related to timing of mutations vs subscription recreation.

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

## Environment

- OPC UA SDK: OPCFoundation.NetStandard.Opc.Ua.* 1.5.376.244
- OPC UA SDK source: C:\Users\rsute\GitHub\UA-.NETStandard
- .NET 9.0
- Testing: ConnectorTester with `--launch-profile opcua`
- Branch: `feature/resilience-testing`
