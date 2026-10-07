# SubjectServerBase design

## Goal

Every server subscribes to property changes before it accepts clients, enforced by a template base class rather than by convention. A client's snapshot plus the changes delivered after it must converge on the model's current values; a change made between a client's snapshot and the subscription would otherwise never reach that client.

Sources already get this from `SubjectSourceBase`, which seals `RunAsync` and owns its subscription. Servers have no equivalent, and the MQTT server currently starts its broker before subscribing.

## Scope

- New `SubjectServerBase` in `Namotion.Interceptor.Connectors`.
- Migrate `OpcUaSubjectServer`, `MqttSubjectServer` and `WebSocketSubjectServer` onto it.
- `SubjectConnectorBase` stays open (constructor remains `protected`, not obsolete). Its XML docs and `docs/connectors.md` steer authors to `SubjectSourceBase` or `SubjectServerBase`; direct derivation remains supported for connectors that are neither.
- Out of scope: the embedded `WebSocketSubjectChangeProcessor` (a plain `BackgroundService`, already subscribes in `StartAsync`), and gating server start on source synchronization (possible follow-up).

## API

```csharp
public abstract class SubjectServerBase : SubjectConnectorBase
{
    protected SubjectServerBase(ConnectorMetrics metrics, ILogger logger);

    protected int ConsecutiveFailures { get; }

    protected virtual Task<IAsyncDisposable?> InitializeAsync(CancellationToken stoppingToken);

    protected abstract ChangeQueueProcessor CreateChangeQueueProcessor(Action<long> dropHandler);

    protected abstract Task<IAsyncDisposable?> StartServerAsync(ConnectorRunAttempt attempt);

    protected virtual TimeSpan GetRestartDelay(int consecutiveFailures);

    protected sealed override Task RunAsync(CancellationToken stoppingToken);
}
```

- `InitializeAsync`: once per run, around the restart loop. For state that outlives a restart. Must not accept clients. Its result is disposed after the loop ends. A throw ends the connector (no retry); the base records it through the existing `ExecuteAsync` error path. Default returns `null`.
- `CreateChangeQueueProcessor`: called by the base only, at the start of each attempt and before `StartServerAsync`. The `dropHandler` is the outbound drop reporter and must be passed to the processor. A factory rather than abstract filter/write/rule members, because the WebSocket server builds its processor through `WebSocketSubjectHandler` (the handler is the echo source, shared with embedded mode), and each server's existing factory is pinned by delivery-rule tests.
- `StartServerAsync`: per attempt, called once the processor is subscribed; returns once clients can connect. Receives the attempt so a server can tie request handling to `attempt.Token`, read `WasForceKilled` in its teardown, and call `attempt.CancelAsync()` to end the attempt. Disposing the result tears the attempt down. If it throws, the server releases whatever it acquired before rethrowing; the base has nothing to dispose.
- `GetRestartDelay`: delay after a failed attempt, `consecutiveFailures` starting at 1. Default is exponential `min(2^(n-1), 30)` seconds plus 0 to 2 s random jitter. Virtual for external servers; no built-in server overrides it.
- `ConsecutiveFailures`: failures since the last successful start. Exposed so a server's diagnostics can report it (`OpcUaServerDiagnostics.ConsecutiveFailures`).

## Run loop

`RunAsync` (sealed):

1. `await using` the result of `InitializeAsync(stoppingToken)`.
2. Reset `ConsecutiveFailures` to 0.
3. While not stopping, run one attempt under `RunAttemptAsync`:
   1. `CreateChangeQueueProcessor(Metrics.OutboundChanges.CreateDropReporter())`.
   2. `Metrics.OutboundChanges.Register(() => processor.QueueDepth, capacity: null)`, declared after the processor so it is released first.
   3. `StartServerAsync(attempt)`.
   4. Reset `ConsecutiveFailures`, `Metrics.MarkOperational()`.
   5. `processor.ProcessAsync(attempt.Token)`.
   6. `finally`: `Metrics.MarkNotOperational()`, dispose the server teardown, release the registration, dispose the processor.
4. Outcome of the attempt, decided inside the attempt body (filters must read `WasForceKilled` before the attempt is disposed):
   - Stopping token cancelled: return without reporting, regardless of the exception type, because a stop can tear a server down with arbitrary exceptions.
   - Force-killed: log a warning, restart without delay, no error reported.
   - Any other exception, or `ProcessAsync` completing while neither stopping nor killed: increment `ConsecutiveFailures`, `Metrics.ReportError`, log an error, restart after `GetRestartDelay(ConsecutiveFailures)`. A completion without exception is reported as an `InvalidOperationException` wrapping any captured cancellation.
5. The restart delay runs after `RunAttemptAsync` returns, so the teardown has freed the port. A stop during the delay leaves the loop normally.

## Server migration

### OPC UA

- `InitializeAsync`: `_context.WithRegistry()`, subscribe `SubjectDetaching`; teardown unsubscribes.
- `CreateChangeQueueProcessor`: the existing factory, taking the drop handler.
- `StartServerAsync`: create the application instance, clean the certificate store if configured, create `OpcUaStandardServer`, set `_server`, check certificates, start. Teardown: clear `_server` and its property data, close transport listeners if force-killed, `ShutdownServerAsync`, dispose the server. The same teardown runs when the start throws.
- `ConsecutiveFailures`, `RecordConsecutiveFailure` and `ResetConsecutiveFailures` on the server are removed; `OpcUaServerDiagnostics` reads the base value through an internal accessor on the server.

### MQTT

- `InitializeAsync`: build the broker options and broker, create the per-run state (shutdown token, client counter, initial-state tasks, publish lease), attach the broker handlers and `SubjectDetaching`, set `_mqttServer` and `_currentClientCounter`. Teardown is the existing `CleanupRunAsync`.
- `CreateChangeQueueProcessor`: the existing factory, taking the drop handler.
- `StartServerAsync`: `server.StartAsync()`, log the port. On failure, stop a half-started broker (MQTTnet latches its started flag before binding) and rethrow. Teardown stops the broker unless the connector is stopping, in which case `CleanupRunAsync` stops it after the initial-state tasks have drained.
- The `_disposed` check in the run loop's catch is dropped: `DisposeAsync` stops the connector first, so the stopping token covers it.

### WebSocket

- No `InitializeAsync`.
- `CreateChangeQueueProcessor`: `_handler.CreateChangeQueueProcessor(_logger, dropHandler)`.
- `StartServerAsync`: build the web application bound to `attempt.Token`, start it, start `_handler.RunHeartbeatLoopAsync(attempt.Token)`. A heartbeat that completes while the attempt is live calls `attempt.CancelAsync()`, which ends processing and restarts the attempt. Teardown: cancel the attempt, await the heartbeat, `CloseAllConnectionsAsync`, stop the app with the 2 s bound, dispose it (claimed atomically against `DisposeAsync`).

## Tests

`SubjectServerBaseTests` in `Namotion.Interceptor.Connectors.Tests`, with a test server:

- `WhenStartServerAsyncWritesAProperty_ThenTheChangeIsDelivered`
- `WhenTheServerRestarts_ThenInitializeAsyncRunsOnceAndItsTeardownRunsOnceAfterStop`
- `WhenStartServerAsyncFails_ThenTheErrorIsReportedAndTheRestartWaitsForTheDelay` (asserts increasing `consecutiveFailures`)
- `WhenAStartSucceedsAfterFailures_ThenConsecutiveFailuresIsReset`
- `WhenForceKilled_ThenRestartsWithoutDelayOrError`
- `WhenProcessingCompletesUnexpectedly_ThenItIsReportedAndRestarted`
- `WhenInitializeAsyncThrows_ThenTheConnectorFaultsWithTheError`
- `WhenStartServerAsyncThrows_ThenTheProcessorIsDisposed`
- `WhenAnAttemptEnds_ThenTheServerTeardownRunsBeforeTheProcessorIsDisposed`
- `WhenGetRestartDelayUsesTheDefault_ThenItGrowsAndIsCapped`

Existing server tests are adapted where they use the removed OPC UA failure counter. Public API snapshots for Connectors, Mqtt and WebSocket are accepted.

## Documentation

- `docs/connectors.md` server section rewritten around `SubjectServerBase` as the canonical description: hooks, the subscription guarantee, the restart policy, and the steer for custom connectors.
- `docs/connectors-opcua-server.md` backoff paragraph replaced by a link to that section.
- `SubjectConnectorBase` XML docs point at the two templates.
- Docs and comments describe current behavior only; changes relative to the previous behavior go in the PR description.

## Verification

- `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`.
- Integration tests: `Namotion.Interceptor.OpcUa.Tests`, `Namotion.Interceptor.Mqtt.Tests`, `Namotion.Interceptor.WebSocket.Tests`.
- Connector Tester: `opcua-chaos`, `mqtt-chaos` and `websocket-chaos`, at least 100 cycles each, before the PR is finalized. Load profiles are not run; the processing hot path is unchanged.

## PR description notes

Behavior changes to list: MQTT subscribes before the broker accepts clients; MQTT and WebSocket use exponential backoff instead of a fixed 5 s; a WebSocket processing loop that completes unexpectedly now restarts after the backoff; failure log messages come from the base class.
