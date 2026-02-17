# OPC UA Client Reconnection — Findings & Fixes

Summary of bugs found and fixes applied during resilience testing of OPC UA client reconnection.

## Goal

**100% pass rate** on connector tester chaos testing (`--launch-profile opcua`).

**Result**: All fixes verified. Current run: **35+ cycles, 100% PASS, HeapMB stable at 79–80 MB**.

## Summary

| Fix | Description | Area | SDK PR | Status |
|-----|-------------|------|--------|--------|
| 1 | Transport kill + stale callback guard + SetReconnecting | Client | | Verified |
| 2 | KillAsync/DisconnectAsync race guards | Client | | Verified |
| 3 | SessionTimeout configuration | Client | | Verified |
| 4 | ~~Skip full state read after SDK reconnection~~ | Client | | Outdated (replaced by Fix 8) |
| 5 | RecordReconnectionSuccess from SDK handler | Client | | Verified |
| 6 | TryDequeue cancellation check | Server | | Verified |
| 7 | Flush task deadlock (NodeManager.Lock) | Server | | Verified |
| 8 | Full state read after ALL reconnections | Client | | Verified |
| 9 | Server-side change loss during restart | Server | | Verified |
| 10 | Client session disposal order (close before kill) | Client | | Verified |
| 11 | Delete subscriptions on close | Client | | Verified |
| 12 | PublishingStopped detection triggers reconnection | Client | | Verified |
| 13 | Server cleanup on force-kill (SDK tasks) | Server | | Verified |
| 14 | Three SDK disposal workarounds (memory leak) | Server | [#3560](https://github.com/OPCFoundation/UA-.NETStandard/pull/3560), [#3561](https://github.com/OPCFoundation/UA-.NETStandard/pull/3561) | Verified (local workaround + TODOs) |
| 15 | HeapMB measurement + LOH compaction | Tester | | Verified |

### Upstream SDK PRs

| PR | Description | Local workaround |
|----|-------------|-----------------|
| [#3559](https://github.com/OPCFoundation/UA-.NETStandard/pull/3559) | DiagnosticsNodeManager copy-paste bug | None needed |
| [#3560](https://github.com/OPCFoundation/UA-.NETStandard/pull/3560) | Socket disposal + CertificateUpdate unsubscribe | 4 TODOs in code |
| [#3561](https://github.com/OPCFoundation/UA-.NETStandard/pull/3561) | StopAsync TCP listener dispose | 1 TODO in code |

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

**Status**: Build + tests pass. Verified by run +15 (Fix 8 alone). Failed at cycle 42/42 — data convergence failure caused by Fix 9's root cause.

### Fix 9: Server-side change loss during OPC UA server restart (ChangeQueueProcessor.cs, OpcUaSubjectServerBackgroundService.cs)

**Root cause**: Mutations to the interceptor subject during the gap between OPC UA node creation (`application.StartAsync`) and `ChangeQueueProcessor` subscription creation were permanently lost from the OPC UA server's perspective. Clients performing full state reads would get stale values with no correction mechanism.

**Evidence** (cycle 42 of run +15):
- Subject 11's `DecimalValue`: server interceptor subject had `25633.08` (timestamp `01:17:33.42`) but both stable-client and flaky-client had `25630.87` (timestamp `01:17:33.01`).
- Both clients performed successful full state syncs (93 nodes each) well after the mutation — but read stale values from the OPC UA nodes.
- The mutation at `01:17:33.42` landed during server restart from a disconnect chaos event at `01:17:32`.

**The gap**:
```csharp
// BEFORE fix — OpcUaSubjectServerBackgroundService:
await application.StartAsync(server);        // 1. Creates OPC UA nodes from subject values
// ← mutations here are LOST
using var changeQueueProcessor = new ChangeQueueProcessor(...);
await changeQueueProcessor.ProcessAsync(...); // 2. Subscribes to change queue
```

Between step 1 and step 2, the `PropertyChangeQueueSubscription` does not exist. Any property changes fire change events, but no subscriber captures them. If node creation reads the pre-mutation value and the mutation happens before the subscription is active, the OPC UA node is permanently stale.

**Fix** (two changes):

1. **Move subscription creation to `ChangeQueueProcessor` constructor**: The `PropertyChangeQueueSubscription` is now created immediately in the constructor instead of at the start of `ProcessAsync`. Changes are captured in the queue from the moment the processor is constructed, even before processing begins.

2. **Create `ChangeQueueProcessor` before `application.StartAsync`**: In `OpcUaSubjectServerBackgroundService`, the processor is now constructed before the OPC UA server starts, ensuring the subscription is active during node creation:

```csharp
// AFTER fix:
using var changeQueueProcessor = new ChangeQueueProcessor(...); // subscription active
await application.StartAsync(server);        // nodes created — changes captured
await changeQueueProcessor.ProcessAsync(...); // processes queued + ongoing changes
```

**Files changed**:
- `ChangeQueueProcessor.cs` — `PropertyChangeQueueSubscription` created in constructor, disposed in `Dispose()`, removed `using var` from `ProcessAsync`
- `OpcUaSubjectServerBackgroundService.cs` — Moved `ChangeQueueProcessor` creation before `CheckApplicationInstanceCertificatesAsync` and `StartAsync`
- `ChangeQueueProcessorTests.cs` — Added `WithPropertyChangeQueue()` to test contexts (now required by constructor)

**Note**: This bug also affects production scenarios — any property mutation during OPC UA server restart (e.g., from MQTT source, another OPC UA client, or application logic) would be lost from the OPC UA server's address space.

**Status**: Committed. Included in run +17 (654/655 pass) but that run also discovered the memory leak (Fix 13), so verification is ongoing with the current run.

### Fix 10: Client session disposal order — close before transport kill (SessionManager.cs)

**Root cause**: In `ClearSessionAsync`, the transport was killed BEFORE sending `CloseSession` to the server. The server never received `CloseSession`, leaving orphaned sessions and subscriptions that disrupted the publish pipeline for ALL connected clients — including clients that were never disrupted by chaos.

**Evidence** (Cycle 655 of extended chaos run — see Investigation section below):
- Profile `client-b-only`: only client-b was killed, yet **client-a** had stale values
- Client-a's subscription showed `PublishingStopped = true` just 3 seconds after client-b was killed
- Client-a received zero notifications for 6+ minutes despite never being disrupted

```
Old flow (broken):
1. KillTransportChannel(session)     → TCP connection dies
2. session.CloseAsync()              → FAILS (transport is dead)
3. Server never gets CloseSession    → orphaned session + subscriptions
4. Server publishes to dead connection → disrupts publish pipeline for ALL clients

New flow (fixed):
1. session.CloseAsync()              → server receives CloseSession
2. Server cleans up session + subs   → no orphaned resources
3. KillTransportChannel(session)     → cleanup of remaining transport state
```

On a healthy connection (Kill chaos), `CloseAsync` completes in milliseconds. On a dead connection (real network failure), it times out after `SessionDisposalTimeout` (5 seconds).

### Fix 11: Delete subscriptions on close during manual reconnection (SessionManager.cs)

**Root cause**: `ConfigureSession` set `DeleteSubscriptionsOnClose = false` (needed for SDK subscription transfer), but this also applied during manual reconnection disposal where subscriptions should be deleted. Orphaned subscriptions lingered on the server for up to 1 hour (`MaxSubscriptionLifetime = 3,600,000ms`).

**Fix**: Set `session.DeleteSubscriptionsOnClose = true` in `DisposeSessionAsync` before `CloseAsync`. `ConfigureSession` still sets it to `false` for SDK automatic reconnection (where subscription transfer IS desired). The override only applies when we're permanently done with a session.

### Fix 12: PublishingStopped detection triggers manual reconnection (SubscriptionManager.cs, OpcUaSubjectClientSource.cs)

**Root cause**: Health check only verified `session.Connected` and `item.Created` / `StatusCode.IsBad` — never checked `PublishingStopped`. A client could receive zero notifications indefinitely without triggering reconnection.

**Fix**: Added `HasStoppedPublishing` property on `SubscriptionManager` that checks if any subscription has `PublishingStopped == true`. The health check loop in `OpcUaSubjectClientSource.ExecuteAsync` triggers manual reconnection when detected. Only runs when `!isReconnecting` to avoid false positives during normal reconnection.

This is defense-in-depth for scenarios where Fix 10 can't help — real network failures where graceful close is impossible (cable pulled, process crash). `PublishingStopped` triggers after `LifetimeCount × PublishingInterval` ≈ 10 seconds of no publish responses.

**Additional**: Fixed `LogRevisedSubscriptionParameters` to log `item.Status.QueueSize` (server-revised) instead of `item.QueueSize` (client-requested).

### Fix 13: Server memory leak on force-kill — SDK tasks not signaled to exit (OpcUaSubjectServerBackgroundService.cs)

**Root cause**: On server force-kill, `application.StopAsync()` was skipped entirely (comment: "can hang"). This meant `OnServerStoppingAsync` never ran, so the SDK's internal `SubscriptionManager.ShutdownAsync()` was never called. Two fire-and-forget tasks started by the SDK (`PublishSubscriptionsAsync` and `ConditionRefreshWorkerAsync`) via `Task.Factory.StartNew(LongRunning)` with no cancellation token were never signaled to exit. These tasks held the entire server object graph alive as GC roots: Task → SubscriptionManager → ServerInternalData → ApplicationConfiguration → entire server infrastructure (~8-16 MB per server restart).

**Evidence** (memory.log from 83-cycle endurance run):
- HeapMB grew linearly from 75.6 MB to 407.7 MB over 83 cycles (~4 MB/cycle average)
- Growth correlated with server restarts: `server-only` profile added +8-16 MB/cycle, while `client-a-only` showed slight heap *decrease*
- The SDK itself has TODO comments in SubscriptionManager: `"// TODO: Ensure shutdown awaits completion and a cancellation token is passed"`

**Fix**: On force-kill, close transport listeners first (clients still see abrupt crash — chaos simulation preserved), then always call `ShutdownServerAsync(application)`. The transport is already dead, so `StopAsync` only cleans up internal SDK state. `ShutdownServerAsync` already has a 10-second timeout as a safety net.

```csharp
// Force-kill path (before):
s.CloseTransportListeners();     // ← clients see crash
// StopAsync SKIPPED             // ← SDK tasks never signaled → memory leak

// Force-kill path (after):
s.CloseTransportListeners();     // ← clients see crash (unchanged)
ShutdownServerAsync(application) // ← SDK tasks signaled to exit + 10s timeout
```

**Files changed**: `OpcUaSubjectServerBackgroundService.cs` — removed force-kill special case that skipped `ShutdownServerAsync`

**Status**: Verified — internal SDK tasks exit properly. However, heap still grew ~2 MB/cycle due to three additional SDK disposal bugs discovered via `dotnet-dump` GC root analysis (see Fix 14).

**Investigation details** (for future debugging if leak persists):

Per-profile heap delta analysis from 83-cycle memory.log:

| Profile | Heap delta/cycle | Notes |
|---------|-----------------|-------|
| server-only | +8–16 MB | Biggest contributor — server kill/restart |
| full-chaos | +6–8 MB | Contains server kill component |
| all-clients | +0.3–1.3 MB | Minimal |
| client-a-only | slight decrease | GC reclaims more than allocated |
| no-chaos | slight decrease | Baseline — no leak without restarts |

SDK code paths examined (`/home/rico/GitHub/UA-.NETStandard`):

- **`ApplicationInstance.cs`** — Lightweight, no IDisposable, no static registrations. Not a leak source.
- **`CertificateValidator.cs`** — No static registrations, no IDisposable. `CertificateUpdate` event subscriber in `StandardServer` never unsubscribed, but validator is local to `ApplicationConfiguration` which goes out of scope — not a GC root.
- **`DefaultTelemetry.cs` / `TelemetryContextBase.cs`** — Static dictionaries for ActivitySource and Assembly cache, but bounded by assembly count. Not a growth source.
- **`CustomNodeManager2.Dispose`** — Properly clears `PredefinedNodes` and disposes all nodes. Not a leak source.
- **`ServerBase.Dispose`** — Disposes transport listeners, service hosts, request queue. Does basic cleanup.
- **`ServerInternalData.Dispose`** — Calls `Utils.SilentDispose` on all managers but doesn't null fields.
- **`SamplingGroup.Dispose`** — Properly waits for sampling task via `GetAwaiter().GetResult()`. Not a leak source.

The critical difference between `StopAsync` and `Dispose`:

```
StopAsync → OnServerStoppingAsync:
  1. SubscriptionManager.ShutdownAsync()  → signals m_shutdownEvent.Set() → publish tasks exit
  2. SessionManager.Shutdown()            → closes sessions
  3. NodeManager.ShutdownAsync()          → shuts down node managers

Dispose (WITHOUT StopAsync):
  1. ServerInternalData.Dispose()         → disposes managers
  2. SubscriptionManager.Dispose()        → disposes m_shutdownEvent WITHOUT signaling it
  3. Publish tasks catch ObjectDisposedException eventually, but hold GC roots while running
```

The SDK's `SubscriptionManager.StartupAsync` (line ~236) starts two fire-and-forget tasks:
```csharp
m_shutdownEvent.Reset();
// TODO: Ensure shutdown awaits completion and a cancellation token is passed
_ = Task.Factory.StartNew(
    () => PublishSubscriptionsAsync(m_publishingResolution),
    default,
    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
    TaskScheduler.Default);
```
These tasks loop on `m_shutdownEvent.WaitOne(timeout)`. Without `Set()`, they only exit when `WaitOne` throws `ObjectDisposedException` after `Dispose()` — but the task itself keeps the SubscriptionManager (and everything it references) alive as a GC root until then.

If the fix doesn't resolve the leak, next steps:
1. Check if `ShutdownServerAsync` times out on force-kill (look for `"OPC UA server shutdown timed out after 10s"` in logs)
2. If it does time out, investigate what blocks `StopAsync` after transport is already closed
3. If heap still grows but no timeout, use `dotnet-dump` / `dotnet-gcdump` to identify actual GC roots

### Fix 14: Server memory leak — three SDK disposal bugs (OpcUaSubjectServer.cs)

**Root cause**: After Fix 13 resolved the fire-and-forget task leak, `dotnet-dump` GC root analysis (`gcroot` on retained `OpcUaSubjectServer` instances) revealed three additional SDK bugs causing ~2 MB/cycle heap growth. All three retention paths originate from lingering TCP sockets held by `SocketAsyncEngine` (the .NET runtime's async socket infrastructure keeps strong references to sockets with pending operations).

**Three GC retention chains discovered**:

1. **`StopAsync` clears listener list before `Dispose` can process it**:
   - `ServerBase.StopAsync()` calls `listeners[ii].Close()` then `listeners.Clear()` (line 573)
   - `ServerBase.Dispose()` iterates `TransportListeners` to dispose them — but the list is empty
   - Result: `TcpTransportListener.Dispose()` never runs → timers, channels, and buffer managers leak

2. **Channel `Dispose()` doesn't close sockets** → lingering sockets retain server via `m_callback`:
   - `TcpTransportListener.Dispose()` calls `Utils.SilentDispose()` on each channel
   - `UaSCUaBinaryChannel.Dispose()` discards tokens/nonces but does NOT close the `Socket` property
   - Only `TcpListenerChannel.ChannelClosed()` (protected) calls `Socket?.Close()` — but it's never called during disposal
   - Lingering sockets retain: Socket → `TcpMessageSocket` → `TcpServerChannel` → `Listener` → `TcpTransportListener` → `m_callback` → `SessionEndpoint` → Server
   - `TcpTransportListener.Dispose()` does NOT null `m_callback`

3. **`StandardServer` subscribes `CertificateUpdate` but never unsubscribes**:
   - `StandardServer.OnServerStarted()` subscribes `CertificateValidator.CertificateUpdate += OnCertificateUpdateAsync` (line 3231)
   - Neither `StandardServer.Dispose()` nor `ServerBase.Dispose()` unsubscribes
   - The `CertificateValidator` is shared (from `ApplicationConfiguration`) and outlives the server
   - Lingering sockets retain: Socket → Channel → `ChannelQuotas` → `CertificateValidator` → `CertificateUpdateEventHandler` → Server

**Fix** (three workarounds for SDK bugs):

1. **Save and manually dispose transport listeners**: `CloseTransportListeners()` saves listener references before `StopAsync` clears the list. `DisposeTransportListeners()` disposes them after shutdown, before `server.Dispose()`.

2. **Null `m_callback` via reflection**: After disposing each listener, null the private `m_callback` field to break the Socket → Listener → Server retention chain. No public API exists (`ChannelClosed()` is protected, `Socket` is `protected internal`, `m_callback` has no setter). This is the only workaround without modifying the SDK.

3. **Unsubscribe `CertificateUpdate` in `Dispose()`**: Override `Dispose(bool)` and call `CertificateValidator.CertificateUpdate -= OnCertificateUpdateAsync`. This is clean — `OnCertificateUpdateAsync` is `protected virtual` on `ServerBase`, so accessible from our subclass. No reflection needed.

**Verification** (`dotnet-dump` analysis):

| Dump | Cycles | Retained `OpcUaSubjectServer` | HeapMB |
|------|--------|-------------------------------|--------|
| Before any fix | ~50 | 16 | growing ~4 MB/cycle |
| After fix #1 only | ~50 | 13 | growing ~7 MB/cycle |
| After fix #1 + #2 | 344 | 295 | growing ~2 MB/cycle |
| After fix #1 + #2 + #3 | 58 | **2** (1 live + 1 pending GC) | **stable ~91 MB** |
| After fix #1 + #3 (no reflection) | 14 | **14** | growing ~12 MB/cycle |

All three fixes are required. Fix #3 had the biggest impact (broke the most numerous retention path), but without fix #2 the `m_callback` chain still retains servers.

**Files changed**: `OpcUaSubjectServer.cs` — Added `TransportListenerCallbackField` (reflection), `CloseTransportListeners()`, `DisposeTransportListeners()`, and `CertificateUpdate` unsubscription in `Dispose()`

**Status**: Verified — HeapMB stable at ~91 MB after 58+ cycles. Only 2 `OpcUaSubjectServer` instances retained (1 live + 1 pending GC).

**Upstream PRs**:
- https://github.com/OPCFoundation/UA-.NETStandard/pull/3560 (fixes #2 socket disposal and #3 CertificateUpdate). Workarounds #2 and #3 can be removed once the SDK NuGet is updated.
- https://github.com/OPCFoundation/UA-.NETStandard/pull/3561 (fixes #1 StopAsync not disposing TCP listeners). Workaround #1 (saved listeners) can be removed once the SDK NuGet is updated.

### Fix 15: HeapMB measurement noise + LOH fragmentation (VerificationEngine.cs)

**Root cause**: Two measurement issues in the ConnectorTester's `AppendMemoryLog`:

1. **Measurement noise**: `GC.GetTotalMemory(forceFullCollection: false)` includes uncollected Gen0 objects, giving noisy readings. Heap dumps at cycle 50 and 58 (with `false`) showed identical Gen2 (39.7 MB) and LOH (5.0 MB), confirming the ~0.3 MB/cycle apparent growth was measurement noise.

2. **LOH fragmentation**: After switching to `forceFullCollection: true`, a slower but persistent ~0.2 MB/cycle growth remained. Heap dumps at cycle 40 and 51 showed massive transient object accumulation mid-cycle (Export XML types: 2 → 36,830 objects, System.Xml types: 480 → 38,836 objects, OpcUaNodeConfiguration: 339 → 2,655) — but ALL sampled objects had **0 GC roots**. These are mid-cycle garbage from NodeSet XML parsing during server restarts. The persistent HeapMB growth is caused by **LOH fragmentation**: large temporary objects (~500-800 KB NodeSet XML strings, serialization buffers) land on the LOH, become garbage, get collected, but leave unfillable holes since LOH doesn't compact by default.

**Fix**:
- Changed `GC.GetTotalMemory(forceFullCollection: false)` → `forceFullCollection: true`
- Added `GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce` before forced GC
- Added `compacting: true` to `GC.Collect()` calls

**Files changed**: `VerificationEngine.cs` — `AppendMemoryLog` method

**Status**: Verified — after removing the server-side socket reflection hack (CloseChannelSockets), the ~0.2 MB/cycle drift is gone. Current run (35+ cycles) shows HeapMB flat at 79–80 MB with no upward trend after warmup. The earlier drift was likely caused by additional object churn from the reflection-based socket closing. LOH compaction remains in place as a precaution.

## Investigation: Stale Value Root Cause (Cycles 565 & 655)

### Background

After Fix 9, an extended chaos run was started with diagnostic logging enabled (subscription parameters, PublishingStopped detection, zero-notification flow detection). The run passed 654 cycles before a failure at cycle 655 revealed the root cause of stale values.

### Cycle 565 — Initial Failure (Pre-Diagnostic Logging)

Client-a had ~20/93 stale property values after chaos testing. Stale timestamps clustered in a ~250ms window (08:59:24.67–08:59:24.91), while the server's final values had timestamps up to 08:59:26.85. Selective staleness within a single subscription was puzzling. This failure motivated adding the diagnostic logging that enabled the Cycle 655 root cause analysis.

### Cycle 655 — Root Cause Found

Profile `client-b-only` — only client-b was killed, yet client-a had stale values. Client-a was never disrupted.

```
13:30:25  Kill on client-b (client-a NOT disrupted)
13:30:28  Client-a subscription 2501304071: PublishingStopped = true, zero notifications
13:31:25  Converge phase starts (mutations stop)
13:32:36  Client-b creates new session + subscription (correct state via state read)
13:32:43  Server session closing (client-b's old session timeout)
13:36:45  FAIL — client-a has stale values, client-b matches server
```

Client-a's subscription stopped publishing 3 seconds after client-b's kill. The server's publish pipeline was disrupted by client-b's orphaned session (transport dead, session never closed). This led to Fix 10 (disposal order), Fix 11 (delete subscriptions), and Fix 12 (PublishingStopped detection).

### Diagnostic Logging Added

Three diagnostic checks in `SubscriptionManager.LogSubscriptionDiagnostics()`, called from health check loop (~5s interval):

1. **Revised subscription parameters** (INFO, one-time after `ApplyChangesAsync`): Subscription ID, revised PublishingInterval, KeepAliveCount, LifetimeCount, SamplingInterval range, QueueSize
2. **PublishingStopped detection** (WARNING, periodic): Logs if any subscription has `PublishingStopped == true`
3. **Zero notification flow** (WARNING, periodic): Logs if zero notifications received since last health check while subscriptions exist

## Known Remaining Issues

### Request timeout loop → Fixed by Fix 6 + Fix 7

**Symptom**: After rapid successive chaos events (multiple server kills + client kills in one cycle), BOTH clients enter a permanent `BadRequestTimeout` (0x80850000) loop.

**Root cause**: Two server-side bugs prevented restart after KillAsync:
1. `TryDequeue` fast path doesn't check cancellation → server's ProcessAsync never exits (Fix 6)
2. Flush task deadlock with `lock(node)` vs `NodeManager.Lock` → server.Dispose() hangs (Fix 7)

**Status**: Fixed. Server restarts reliably after kills.

### Data convergence failure after server kill + subscription transfer → Addressed by Fix 8 + Fix 9

**Symptom**: After server kill → server recover → SDK subscription transfer, some property values remain stale on clients.

**Two distinct root causes identified**:
1. **Client-side** (Fix 8): Subscription notifications alone are insufficient for state sync after reconnection — notifications can be lost due to subscription lifetime expiration, queue overflow, timing gaps, or stuck reconnect handler. Fixed by full state read after ALL reconnections.
2. **Server-side** (Fix 9): Mutations during OPC UA server restart gap are lost — the `ChangeQueueProcessor` subscription didn't exist during node creation, so changes were never written to OPC UA nodes. Clients reading stale OPC UA nodes get stale values regardless of full state reads.

**Status**: Fix 8 addresses client-side. Fix 9 addresses server-side. Both needed for full eventual consistency.

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
| +15 | Fix 8: Full state read after ALL reconnections | **41/42 pass** (data convergence at cycle 42 — server-side gap) |
| +16 | Fix 9: ChangeQueueProcessor before StartAsync | verification run |
| +17 | Fix 10-12: Session disposal order + PublishingStopped detection | **654/655 pass** (memory leak identified) |
| +18 | Fix 13: Server cleanup on force-kill | heap still growing ~2 MB/cycle (SDK disposal bugs) |
| +19 | Fix 14: Three SDK disposal workarounds | **58+ cycles, HeapMB stable ~91 MB, 2 instances** |
| +20 | Fix 15: LOH compaction + accurate measurement | ~0.2 MB/cycle drift remains (Gen2 fragmentation, not object retention). Multi-day run in progress. |

## Environment

- OPC UA SDK: OPCFoundation.NetStandard.Opc.Ua.* 1.5.376.244
- OPC UA SDK source: /home/rico/GitHub/UA-.NETStandard
- .NET 9.0
- Testing: ConnectorTester with `--launch-profile opcua`
- Branch: `feature/resilience-testing`

## Key Code Paths

| File | Method | Relevance |
|------|--------|-----------|
| `SessionManager.cs` | `ClearSessionAsync` | Session disposal (Fix 10: close before transport kill) |
| `SessionManager.cs` | `DisposeSessionAsync` | Session cleanup (Fix 11: DeleteSubscriptionsOnClose) |
| `SessionManager.cs` | `OnReconnectComplete` | SDK reconnection handling |
| `SubscriptionManager.cs` | `HasStoppedPublishing` | Fix 12: PublishingStopped detection |
| `SubscriptionManager.cs` | `LogSubscriptionDiagnostics` | Diagnostic logging (subscription health) |
| `SubscriptionManager.cs` | `OnFastDataChange` | Processes incoming notifications |
| `OpcUaSubjectClientSource.cs` | `ExecuteAsync` | Health check loop, triggers reconnection |
| `OpcUaSubjectServerBackgroundService.cs` | `ExecuteServerLoopAsync` | Server restart loop (Fix 13: always shutdown) |
| `OpcUaSubjectServerBackgroundService.cs` | `ShutdownServerAsync` | Graceful server shutdown with timeout |
| `ChangeQueueProcessor.cs` | constructor | Fix 9: subscription created before server start |
| SDK: `SubscriptionManager.cs` | `StartupAsync` | Fire-and-forget tasks (Fix 13 root cause) |
| SDK: `StandardServer.cs` | `OnServerStoppingAsync` | Critical cleanup skipped on force-kill |
| SDK: `ServerBase.cs` | `StopAsync` | Clears listener list before Dispose (Fix 14) |
| SDK: `TcpTransportListener.cs` | `Dispose` | Doesn't null m_callback (Fix 14) |
| SDK: `UaSCBinaryChannel.cs` | `Dispose` | Doesn't close Socket (Fix 14) |
| SDK: `StandardServer.cs` | `Dispose` / `OnServerStarted` | CertificateUpdate never unsubscribed (Fix 14) |

## Upstream SDK Issues (OPCFoundation/UA-.NETStandard)

### Issue 1: Memory leak — Server not GC'd after `StopAsync`/`Dispose` due to socket and event disposal bugs

**PR submitted**: https://github.com/OPCFoundation/UA-.NETStandard/pull/3560

**Reproduction**: Create a `StandardServer`, start it with connected clients, call `StopAsync()` then `Dispose()`. The server object is never garbage collected (~2 MB leaked per restart cycle).

**Root cause**: Two disposal bugs prevent proper cleanup:

1. **`UaSCUaBinaryChannel.Dispose`** (`UaSCBinaryChannel.cs:~224`) doesn't close the `Socket` property. Only `TcpListenerChannel.ChannelClosed()` (protected) calls `Socket?.Close()`, but this is never triggered during disposal. Lingering sockets in `SocketAsyncEngine` act as strong GC roots retaining the entire chain: Socket → Channel → Listener → m_callback → SessionEndpoint → Server.

2. **`StandardServer.OnServerStarted()`** subscribes `CertificateValidator.CertificateUpdate += OnCertificateUpdateAsync` but neither `StandardServer.Dispose()` nor `ServerBase.Dispose()` unsubscribes. Since the `CertificateValidator` is shared (from `ApplicationConfiguration`) and outlives the server, the delegate retains every disposed server instance.

**Our workarounds** (until SDK NuGet is updated):
- Save listener references before `StopAsync`, manually dispose them after (for the `listeners.Clear()` bug — fixed in PR #3561)
- Null `m_callback` via reflection to break the GC retention chain (redundant once SDK disposes sockets in channel Dispose — fixed in PR #3560)
- Override `Dispose(bool)` and unsubscribe `CertificateUpdate` (redundant once SDK unsubscribes in `StandardServer.Dispose` — fixed in PR #3560)

### Issue 3: `ServerBase.StopAsync` doesn't dispose TCP transport listeners

**PR submitted**: https://github.com/OPCFoundation/UA-.NETStandard/pull/3561

**Root cause**: `ServerBase.StopAsync()` calls `Close()` on each transport listener, then `Clear()` on the list. `ServerBase.Dispose()` later iterates `TransportListeners` to call `Dispose()` on each — but the list is already empty, so `TcpTransportListener.Dispose()` never runs. The bug is TCP-specific: `HttpsTransportListener.Close()` → `Stop()` → `Dispose()` (full cleanup), but `TcpTransportListener.Close()` → `Stop()` only closes listening sockets without calling `Dispose()`. This leaks `m_inactivityDetectionTimer` and all `TcpListenerChannel` instances per server restart cycle.

### Issue 2: Copy-paste bug — `DiagnosticsNodeManager.SetDiagnosticsEnabled(false)` deletes wrong nodes

**PR submitted**: https://github.com/OPCFoundation/UA-.NETStandard/pull/3559

**Root cause**: In `DiagnosticsNodeManager.cs:643-646`, the subscription cleanup loop iterates `m_subscriptions.Count` but indexes `m_sessions[ii].Value.Variable` instead of `m_subscriptions[ii].Value.Variable`. This causes `IndexOutOfRangeException` when subscriptions > sessions (preventing all cleanup), and deletes the wrong nodes (session nodes instead of subscription nodes) otherwise. Subscription diagnostic nodes leak permanently in `PredefinedNodes`.

### Other known SDK issues (lower priority)

- **`SubscriptionManager` fire-and-forget tasks lack cancellation tokens** (`SubscriptionManager.cs:~236`): `PublishSubscriptionsAsync` and `ConditionRefreshWorkerAsync` started via `Task.Factory.StartNew(LongRunning)` with no cancellation token. The SDK has TODO comments acknowledging this. However, this is handled by design: both tasks catch `ObjectDisposedException` and log "Exited Normally (disposed during shutdown)" — this is their intended fallback exit path when `Dispose()` runs. Our Fix 13 ensures `ShutdownAsync` (which signals `m_shutdownEvent.Set()`) is always called before `Dispose`, so tasks typically exit cleanly via the signal. If the signal is missed due to timing, the `ObjectDisposedException` fallback handles it. No upstream fix needed.
- **`CustomNodeManager2.SetDefaultPermissions`**: Accesses `namespaceMetadataValues[0]` without bounds checking. However, `ReadAttributes` called with `params uint[]` always returns one entry per attribute — the empty list case is not reachable. Not a real bug.

## Remaining Work

### 1. Long-running tester verification

Run ConnectorTester with all fixes (Fix 1–15) for 200+ cycles to confirm:
- [ ] Pass rate matches or exceeds previous 654/655 (99.85%)
- [ ] HeapMB remains stable with LOH compaction (no growth trend after initial warmup)
- [ ] No new failure modes introduced by memory leak fixes
- [ ] Fix 15 (LOH compaction) eliminates the ~0.2 MB/cycle drift seen in 51-cycle run

### 2. Clean up diagnostics logging

Review all logging added during investigation. Files to review:
- [ ] `SessionManager.cs` — review log levels, remove any temporary debug logging
- [ ] `SubscriptionManager.cs` — review `LogSubscriptionDiagnostics` (keep health checks, remove investigation-only logging)
- [ ] `OpcUaSubjectClientSource.cs` — review health check logging verbosity
- [ ] `OpcUaSubjectServerBackgroundService.cs` — review server loop logging verbosity
- [ ] General: ensure log levels are appropriate (INFO for operational events, DEBUG for diagnostics, no leftover WARN spam)

### 3. Final review of all changes in branch

Review ALL changed files in `feature/resilience-testing` vs `master` (66 files, ~4000 lines added):

**OPC UA Client** (Fix 1–5, 8, 10–12):
- [ ] `SessionManager.cs` — reconnection handling, transport kill, stale callback guard, SetReconnecting, ConcurrentQueue disposal, AbandonCurrentSession, full state sync, disposal order, DeleteSubscriptionsOnClose
- [ ] `SubscriptionManager.cs` — HasStoppedPublishing, LogSubscriptionDiagnostics, LogRevisedSubscriptionParameters, notification counting, UpdateTransferredSubscriptions cleanup
- [ ] `OpcUaSubjectClientSource.cs` — IFaultInjectable, KillAsync/DisconnectAsync race guards, reconnectCts, PublishingStopped reconnection trigger, full state sync in health loop
- [ ] `OpcUaClientConfiguration.cs` — SessionTimeout/OperationTimeout defaults, validation
- [ ] `PollingManager.cs` — changes review
- [ ] `ReadAfterWriteManager.cs` — unused using removal

**OPC UA Server** (Fix 6, 7, 9, 13, 14):
- [ ] `OpcUaSubjectServer.cs` — transport listener disposal, m_callback reflection workaround, CertificateUpdate unsubscription, NodeManagerLock, session event handlers
- [ ] `OpcUaSubjectServerBackgroundService.cs` — IFaultInjectable, force-kill CTS, NodeManager.Lock instead of lock(node), ChangeQueueProcessor before StartAsync, ShutdownServerAsync always called, transport listener disposal, exponential backoff
- [ ] `CustomNodeManager.cs` — removed redundant lock(variableNode), RemoveSubjectNodes
- [ ] `OpcUaNodeFactory.cs` — minor fix

**Core/Connectors infrastructure**:
- [ ] `ChangeQueueProcessor.cs` — subscription in constructor (Fix 9), disposal
- [ ] `PropertyChangeQueueSubscription.cs` — cancellation check in TryDequeue (Fix 6)
- [ ] `SubjectPropertyWriter.cs` — LoadInitialStateAndResumeAsync rename
- [ ] `IFaultInjectable.cs` — new interface for chaos testing
- [ ] `SubjectSourceBackgroundService.cs` — minor change
- [ ] `PropertyReference.cs` — SetValueFromSource extensions
- [ ] `SubjectChangeContext.cs` — write timestamp atomicity (#190)
- [ ] `IWriteInterceptor.cs` / `WriteInterceptorFactory.cs` — write interceptor changes
- [ ] `SubjectPropertyMetadata.cs` — minor change

**MQTT connector**:
- [ ] `MqttSubjectClientSource.cs` — resilience improvements
- [ ] `MqttSubjectServerBackgroundService.cs` — IFaultInjectable, restart loop, resilience
- [ ] `MqttClientConfiguration.cs` / `MqttServerConfiguration.cs` — configuration changes
- [ ] `MqttConnectionMonitor.cs` — new file
- [ ] `MqttHelper.cs` — minor change

**Tracking**:
- [ ] `DerivedPropertyChangeHandler.cs` — changes review
- [ ] `LifecycleInterceptor.cs` — removal of unused code
- [ ] `PropertyReferenceTimestampExtensions.cs` — removed (moved to core)

**Tests**:
- [ ] `ChangeQueueProcessorTests.cs` — updated for Fix 9
- [ ] `SubjectPropertyWriterTests.cs` — updated for rename
- [ ] `WriteTimestampTests.cs` — new (replaces TimestampTests.cs)
- [ ] `MqttClientConfigurationTests.cs` / `MqttServerConfigurationTests.cs` — new

**ConnectorTester** (new project):
- [ ] Review all tester files for anything that shouldn't be committed (investigation artifacts, hardcoded paths, temp files)
- [ ] `VerificationEngine.cs` — memory logging (added for diagnostics — keep or remove?), Fix 15 LOH compaction

**Other**:
- [ ] `findings.md` — keep as documentation or remove before merge?
- [ ] `docs/` — aspnetcore.md, opcua.md, mqtt.md documentation updates
- [ ] `scripts/benchmark.ps1` — benchmark script changes
- [ ] `.claude/settings.local.json` — should NOT be committed (local settings)

**Build & test verification**:
- [ ] Run `dotnet build src/Namotion.Interceptor.slnx` — no warnings
- [ ] Run `dotnet test src/Namotion.Interceptor.slnx` — all unit tests pass

**Upstream**:
- [x] File upstream SDK issues: [#3560](https://github.com/OPCFoundation/UA-.NETStandard/pull/3560) (socket + certificate), [#3559](https://github.com/OPCFoundation/UA-.NETStandard/pull/3559) (diagnostics copy-paste), [#3561](https://github.com/OPCFoundation/UA-.NETStandard/pull/3561) (StopAsync TCP listener dispose)

### 4. Update PR description

- [ ] Update PR description with summary of all changes in the branch
- [ ] Include: ConnectorTester (new project), OPC UA client resilience (Fix 1–5, 8, 10–12), OPC UA server resilience (Fix 6, 7, 9, 13, 14), MQTT resilience improvements, core infrastructure (IFaultInjectable, write timestamp atomicity), documentation updates
- [ ] Link to findings.md for detailed fix descriptions
