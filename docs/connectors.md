# Connectors

The `Namotion.Interceptor.Connectors` package provides infrastructure for bridging your subject graph to external systems, syncing property values in and out over protocols like OPC UA, MQTT, or WebSocket. Every connector falls into one of two categories, defined by **data ownership**:

| Type                   | Data Owner      | Typical Role                            | Base                                                 |
|------------------------|-----------------|-----------------------------------------|------------------------------------------------------|
| **Source** (Client)    | External system | Client connecting to an external system | `SubjectSourceBase` (`ISubjectSource`)               |
| **Connector** (Server) | Local model     | Exposing subjects to external clients   | `BackgroundService` (optionally `ISubjectConnector`) |

In practice, sources act as network clients and servers act as network servers, but this is a convention, not a requirement. The defining distinction is which side owns the data.

### Protocol-specific documentation

- [WebSocket](connectors-websocket.md) - Bidirectional WebSocket protocol for real-time synchronization
- [MQTT](connectors-mqtt.md) - MQTT client/server integration for IoT scenarios
- [OPC UA](connectors-opcua.md) - OPC UA client/server integration for industrial automation ([Client](connectors-opcua-client.md) | [Server](connectors-opcua-server.md) | [Mapping](connectors-opcua-mapping.md))
- [Subject Updates](connectors-subject-updates.md) - Wire format for serializing subject state
- [Source Monitoring](connectors-monitoring.md) - Synchronization state, waits, and the source event stream

## Sources

A **source** represents an external authoritative system where the data originates. The local model is a **replica** that synchronizes with this external source of truth.

**Examples**: OPC UA client connecting to a PLC, MQTT client subscribing to a broker, database client, REST API consumer

**Single-owner rule**: Each property can be associated with at most one source. Sources are responsible for claiming and releasing ownership of the properties they manage. This happens initially by scanning the subject graph during startup, and dynamically when the model changes structurally (subjects attached or detached via lifecycle events). Dynamic ownership changes require the external system to support adding and removing subscriptions at runtime. You can retrieve the source that currently owns a property with `TryGetSource()`, for example to check connection status or access protocol-specific features.

### Data Flow

#### Inbound (External → Subject)

When the external system sends new values:

1. External system sends update
2. Source receives the update
3. Source calls `propertyWriter.Write()`
4. Subject property is updated

#### Outbound (Subject → External)

When you change a property value in code, the **local model is updated immediately**. These are regular C# property setters. The change is then picked up by the change queue and written to the attached source **asynchronously** in the background:

1. Property setter writes the new value to the backing field (immediate)
2. Change notification is enqueued
3. Background service dequeues the change and calls `WriteChangesAsync()` on the source
4. Source sends the update to the external system

This means the local model and the external system can be temporarily inconsistent. If the source write fails (network error, validation on the remote system), the local model already has the new value. The write retry queue handles transient failures by buffering and retrying, but the local model is always ahead of the external system.

For **servers**, the pattern is similar: local writes are applied immediately, then eventually pushed to connected clients.

#### Source-First Writes with Transactions

If you need the external source to accept the change *before* updating the local model, use source transactions. This inverts the write order so the source confirms before the local model changes. See [Write Consistency Guarantees](#write-consistency-guarantees) for a comparison of both approaches and [Transactions](tracking-transactions.md) for full details.

### Initialization

Sources use a buffer-load-replay pattern during initialization and reconnection:

1. **Buffer**: During source startup (the base calls `StartListeningAsync` on the source after `StartBuffering`), inbound updates are buffered
2. **Load**: `LoadInitialStateAsync()` fetches complete state from external system
3. **Replay**: Buffered updates are replayed in order after initial state is applied
4. **Reconcile queued writes**: Writes parked while connecting are decided by commit order and sent, unless a later local write superseded them (see [Write Retry Queue](#write-retry-queue))

This ensures:
- Updates received during initialization are not lost
- Updates are applied in the correct order relative to the initial state
- Queued writes are reconciled by commit order rather than discarded

Writes made while the source is connecting are captured, not lost. The outbound subscription is created once for the whole source lifetime, before the retry loop, so a connect-window or reconnect-delay write cannot fall into a gap.

**What gets parked**, by change origin (see [Change notification source semantics](#change-notification-source-semantics)):

- Writes to properties this source owns, whatever produced them.
- Not this source's own inbound applies, so a value it sent is never echoed back. The one exception is a transaction confirmation on a property a connector has written out, which has to reach the source to repair it.

**What happens when draining starts.** Each parked write is decided by commit order, the same rule as [Change Batching and Merging](#change-batching-and-merging):

- Superseded by a later local write: dropped, because that later write is delivered in its place.
- Otherwise: sent, restored locally first if the initial-state load moved the model off it.

**A local write that has already committed wins over the value the load brought in.**

### Why the source does not always win

The source is authoritative for a property it owns, and normally it does win: an inbound value with no local write pending is applied and produces no outbound write. Two cases break that, and they share a cause. A value arriving from the source cannot be ordered against a local write that has already committed, because it is stamped when we apply it, not when the source produced it (issue #373):

- **An echo.** The source reporting back a value we just wrote is an acknowledgement, not newer truth. Letting it win discards the write that followed it.
- **The initial-state load.** It carries the source's state as of the connect, which says nothing about whether it precedes or follows a write made moments earlier.

In both, "source wins" resolves an ambiguity by discarding a write that already committed locally, with no error and no way for the caller to know. So a committed local write wins instead, and the source converges to it.

**What keeps the two ends in sync** is not the conflict rule but two properties of the delivery path: the newest local commit is never dropped, so the source always receives the model's settled value; and the source's notifications carry its own value back, so the model converges to whatever the source actually holds. A source that neither reports values back nor answers reads is outside that guarantee, which is what the last limitation below covers.

**Servers are the opposite case.** A client's write to a server is not a value produced before it saw ours, it is the newer write, so the ordering ambiguity above does not exist and it does win over an older local commit. Delivering the older one instead would leave every client on a value the model has moved past. The three servers select this with `ChangeSupersessionRule.SourceValuesAreSettled`, whose documentation gives the precondition to check before adding a fourth.

### Write Consistency Guarantees

Property writes to sources follow a **local-first** model: the local property is updated immediately, and the change is sent to the source asynchronously. This means the local model and the source can be temporarily out of sync.

| Scenario | Local Model | Source | Outcome |
|---|---|---|---|
| Write succeeds | Updated immediately | Updated via async write | In sync |
| Write fails, retry succeeds | Updated immediately | Updated on retry | Eventually in sync |
| Disconnect + reconnect, source unchanged | Initial state restores source state, retry re-applies change | Receives change via fresh write | In sync |
| Disconnect + reconnect, source changed | Queued write restored locally | Receives the queued write | In sync (local write wins) |
| Write during connect, source leaves the property alone | Local write kept | Sent as a fresh write once draining starts | In sync |
| Write during connect, initial state overwrites the property | Local write restored | Receives the local write | In sync (local write wins) |
| Queued write superseded by a later local write | Later write kept | Receives only the later write | In sync |

In all cases the local model and the source converge. A write that has already committed locally is never discarded, so in the reconnect rows the source ends up with the local write rather than the value it held during the outage. A write is only dropped when a *later local* write supersedes it, and that later write is delivered in its place. Source-wins still applies wherever no local write is pending: an inbound value is accepted and produces no outbound write.

The table describes a connector talking to a remote source. A server also drops a write superseded by a client's write, since that write is the newer one; see [Why the source does not always win](#why-the-source-does-not-always-win). Convergence is unaffected, because the superseding value is the one the clients already have.

#### Confirmed writes with transactions

If you need the source to accept a change **before** updating the local model, use [source transactions](tracking-transactions.md). With transactions, the source is written first during commit. The local model is only updated if the source accepts the change. If the source is unreachable, the commit fails and the local model remains unchanged.

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithTransactions()
    .WithSourceTransactions();

using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
sensor.Temperature = 42.0m; // Captured, NOT applied locally yet
await tx.CommitAsync(ct);   // Writes to source first, then applies locally
                            // If source rejects → local model unchanged
```

| Approach                 | Local update          | Source guarantee             | On disconnect                    |
|--------------------------|-----------------------|------------------------------|----------------------------------|
| Without transactions     | Immediate             | Eventual (async + retry)     | Optimistic re-apply or snap-back |
| With source transactions | After source confirms | Confirmed before local apply | Commit fails, local unchanged    |

Choose based on your consistency requirements: local-first for responsiveness, transactions for confirmed delivery.

#### Change notification source semantics

The `Origin` of a change notification is typed (`ChangeOrigin`): `FromSource` when an inbound update stored exactly the value the source sent, `Confirmed` when a source transaction commit stored the value the source acknowledged, and `Local` for everything else. A change carries a source only when its stored value is exactly the value that source sent or confirmed. The outbound change queue skips changes whose origin source is the target source itself, so a committed value is written to its source exactly once, by the commit.

Origin is stamped per write at the apply call (`SetValueFromSource`, `ApplySubjectUpdate` with a `FromSource` origin, transaction commit replay). Nothing inherits it: hook cascades, `INotifyPropertyChanged` handler write-backs, derived property recalculations, and lifecycle handler writes are all `Local` and therefore flow to bound sources like any local write. When an `OnChanging` hook or a write interceptor changes the incoming value during a stamped write, the stored value no longer equals the sent value and the write publishes as `Local`, so corrections flow back to the source. Transforms must be projections (idempotent, like clamping); reference-typed values must be reassigned, not mutated in place, to be detected.

Provenance-aware validators receive the origin via `PropertyValidationContext` and can treat source values as authoritative while strictly validating local input.

A write's origin moves through a lifecycle: it starts as a pending stamp set by the apply call (`SetValueFromSource`, `ApplySubjectUpdate`), becomes the attempted origin carried by the write while interceptors and validators run (this is what `PropertyValidationContext.Origin` exposes), and is finalized at the actual write, where a stamped origin whose stored value does not equal the sent value is demoted to `Local`; published changes always carry the finalized origin.

### Change Batching and Merging

A source with a `bufferTime` above zero batches outbound changes and collapses each flush to one change per property, so `WriteChangesAsync` sees at most one entry per property per flush.

Per property, the surviving old value comes from the change with the *lowest* `SubjectPropertyChange.Revision` in that batch and the new value from the one with the *highest*, so the survivor spans the batch even when changes were enqueued in the opposite order they committed. Enqueuing happens after the commit and outside the subject lock, so under concurrent writers that inversion is real rather than theoretical. The survivor's `Revision`, `Origin`, `ChangedTimestamp` and `ReceivedTimestamp` all come from that same highest-revision change, so a handler that keys off `Origin.Source`, for example to suppress echoes, sees the origin of the newest commit. Emit order is the arrival order of each property's last occurrence.

Revisions are monotonic per subject and are not comparable across subjects; see [Delivery Guarantees](tracking.md#delivery-guarantees) for the full contract, including what the old value does and does not promise. A change carrying revision 0 orders against nothing, so a property with one in its batch collapses by arrival position instead, which is what a source saw before revisions existed. The write path always stamps a revision, so this only arises for changes built through the public factory.

Only a property's settled state is ever delivered: a flush drops any survivor that a later commit has superseded. Every committed write stamps its revision on the property it wrote, so this is decided by commit order rather than by comparing values, which means it also holds for derived and runtime-registered properties, whose getters need not return what a write stored.

Dropping is safe only because a later commit carries the settled value in the dropped one's place, and one case fails that: a commit that came **from the source** is skipped as an echo when that source's queue is drained, so nothing would be left to deliver. Source-originated commits therefore never count as superseding. Without that exclusion a source echoing back a value we had just written would suppress the next write to the same property, losing it permanently and settling both ends on the old value. Issue #373 covers why an echo cannot be ranked against local writes at all: its revision is stamped when it is applied here, not when the source produced it.

This is the delivery contract: **buffered delivery coalesces every change, and immediate delivery (`bufferTime` at zero) coalesces only what was queued before processing started.** A source that needs every intermediate value must run without buffering. The asymmetry is deliberate: a change queued before processing starts was captured while the source was connecting and a superseded one is stale state, whereas a change arriving afterwards is stream data, where an intermediate is a value rather than staleness.

[Source transactions](tracking-transactions.md) write to the source themselves and then apply locally, and that local apply arrives here as a confirmation. Normally it is not sent on, because the source already has it. The exception is when a connector has also written that property itself: such a write can reach the source after the transaction's and leave it holding an older commit than the subject, so the confirmation is sent out to restore it.

That "has been written out" mark is sticky and lives in the subject's property data. Nothing observable on this side can prove that an earlier write of ours did not land on the source after a transaction's direct write, so clearing it on any inbound event would be a bet against an ordering the client cannot see, and losing it silently strands a committed transaction value. It is deliberately not kept per source: the mark only decides whether a confirmation is written back, and a confirmation carries the current value, so the worst a foreign connector's mark can cost is one redundant write of the value the source is owed anyway. A property written only through transactions never sets it.

If that repair write fails, the source keeps the older value and the subject keeps the confirmed one, so local and remote stay out of sync. Sources are built with a [write retry queue](#write-retry-queue) by default, which queues the change and retries it, but that queue is a ring buffer that drops its oldest entries when full, so a pending repair can be evicted before it is retried when writes fail across many properties at once. With the queue disabled the change is logged and dropped immediately. There is no active reconciliation in either case: the divergence lasts until the property is written again or the source reloads its initial state on reconnect.

### Write Retry Queue

`SubjectSourceBase` provides a write retry queue that buffers writes during disconnection. Each connector exposes the queue size through its own configuration (for example, `OpcUaClientConfiguration.WriteRetryQueueSize`); when implementing a custom source, pass `writeRetryQueueSize` to the `SubjectSourceBase` constructor (default: 1000, pass 0 to disable).

**Behavior:**
- Ring buffer semantics: oldest writes dropped when capacity reached
- Memory while disconnected is bounded per connection attempt: the change subscription is drained into this queue before each attempt and again after the initial state is applied, so repeated failed attempts do not compound. Between those two points, which covers the retry delay and all of `StartListeningAsync` plus `LoadInitialStateAsync`, captured changes accumulate in the subscription without a bound, so peak memory follows the write rate times the length of one attempt
- Automatic retry when `WriteChangesAsync` fails during normal operation
- Re-apply on reconnection by commit order: after loading initial state, each queued change is kept unless a later *local* write superseded it, in which case that later write is delivered instead. A kept change is sent as a fresh write, restored locally first if the load moved the model off it. Values the load brought in do not supersede a write that already committed, because the load cannot be ranked against it.
- In-memory only: queued writes are lost on process restart

### Monitoring Synchronization State

Every source reports whether it is connecting, synchronized, or stopped, through a per-tree registry, a typed event stream, and an awaitable wait. Add `WithSourceMonitoring()` to the context recipe to enable it:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithSourceMonitoring(builder.Services);
```

See [Source Monitoring](connectors-monitoring.md) for waiting on synchronization, reading per-property state, and the event stream.

### Inbound Update Error Handling

When applying inbound updates (writing data from the external system to the local subject model), if an individual update fails (the action throws an exception), the error is logged and **the update is dropped**. There is no retry mechanism for inbound updates.

This is by design:
- Individual update failures don't block other updates from being applied
- Monitor logs for `Failed to apply subject update` errors to detect issues
- Write failures to internal models are treated as non-transient because property writes are deterministic: they either succeed or fail consistently, so retrying would not help (this includes custom validation failures)

This differs from outbound changes (writing from local model to external system), which use a retry queue to handle transient failures.

### Implementing a Source

All sources inherit from `SubjectSourceBase`, a `BackgroundService` that owns the full pump lifecycle. You override three hooks:

| Hook                    | Data Flow          | Description                                                                             |
|-------------------------|--------------------|-----------------------------------------------------------------------------------------|
| `StartListeningAsync`   | External → Subject | Connect to the external system and start receiving changes via `propertyWriter.Write()` |
| `LoadInitialStateAsync` | External → Subject | Fetch complete state snapshot for initialization                                        |
| `WriteChangesAsync`     | Subject → External | Send local property changes to the external system                                      |

The base class handles everything else: retry loop with backoff, buffering during initialization, change queue processing, write batching, and the write retry queue.

#### Pump Lifecycle

Each iteration of the sealed `ExecuteAsync` runs the following sequence. On failure, the base disposes the listen lifetime, waits `retryTime` (default 10s), and restarts from the top. Only `OperationCanceledException` when the host stopping token is cancelled exits the loop. All other exceptions (including internal protocol timeouts) trigger a retry.

```
ExecuteAsync
 ├── create source-lifetime subscription  ← captures local writes continuously (no gap across reconnects)
 └── retry loop (per connection attempt)
      ├── Task.Delay(retryTime)            ← retries only; the subscription keeps capturing during the wait
      ├── drain owned writes → retry queue ← park writes captured since the last attempt (caps memory)
      ├── StartBuffering()
      ├── StartListeningAsync()            ← your hook: connect + spawn monitor
      ├── LoadInitialStateAndResume()      ← calls your LoadInitialStateAsync, then replays buffer
      ├── drain owned writes → retry queue ← park connect-window writes
      ├── ReconcileRetryQueueAsync()       ← restore / send / drop queued writes vs current state
      ├── new ChangeQueueProcessor()       ← connected phase; reuses the source-lifetime subscription
      └── ProcessAsync()                   ← drains changes, calls your WriteChangesAsync
```

"Owned writes" are `Local`-origin changes to properties bound to this source; the source's own `FromSource` and `Confirmed` applies are skipped at drain and in the connected phase, so inbound values are not echoed back (see [Change notification source semantics](#change-notification-source-semantics)).

#### ISubjectSource Interface

`SubjectSourceBase` implements `ISubjectSource`; its abstract `WriteChangesAsync` and `LoadInitialStateAsync` satisfy the interface members directly.

```csharp
public interface ISubjectSource : ISubjectConnector
{
    int WriteBatchSize { get; }
    ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken);
    Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken);

    SourceState State { get; }
    DateTimeOffset? LastSynchronizedAt { get; }
    int PendingWriteCount { get; }
    event EventHandler<SourceEvent>? StateChanged;
}
```

Direct interface implementation without the base class is supported for advanced scenarios, but the implementer is then responsible for its own listening loop, buffering, and outbound dispatch, as well as the four synchronization-state members. See the XML docs on `ISubjectSource` for their exact contract, including the lock-free requirement on `State`/`LastSynchronizedAt`/`RootSubject` and the obligation to register with every reachable `SourceMonitor`.

#### Custom Source Example

Derive from `SubjectSourceBase` and override the three hooks. The base owns the pump lifecycle (buffer, listen, load initial state, run change queue, retry on failure) and satisfies `ISubjectSource` directly through its public abstract members.

```csharp
public sealed class DatabaseSource : SubjectSourceBase
{
    private readonly IInterceptorSubject _root;

    public DatabaseSource(
        IInterceptorSubject root,
        IInterceptorSubjectContext context,
        ILogger<DatabaseSource> logger)
        : base(context, logger)
    {
        _root = root;
    }

    public override IInterceptorSubject RootSubject => _root;

    public override int WriteBatchSize => 100;

    protected override async Task<IAsyncDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter,
        CancellationToken cancellationToken)
    {
        var connection = await OpenDatabaseConnectionAsync(cancellationToken);

        return BackgroundTaskLifetime.Start(
            cancellationToken,
            _logger,
            ct => ListenForChangesAsync(propertyWriter, connection, ct),
            () => connection.DisposeAsync());
    }

    public override async Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        var data = await LoadFromDatabaseAsync(cancellationToken);
        return () => ApplyToSubject(_root, data);
    }

    public override async ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteToDatabaseAsync(changes, cancellationToken);
            return WriteResult.Success;
        }
        catch (Exception ex)
        {
            return WriteResult.Failure(changes, ex);
        }
    }
}
```

**Constructor parameters**: `bufferTime` (default 8ms) controls the change queue batching window. Changes within this window are coalesced into a single `WriteChangesAsync` call. `retryTime` (default 10s) controls the delay between retry attempts when `StartListeningAsync` or the pump loop fails.

**WriteResult**: Return `WriteResult.Success` when all changes were written. Return `WriteResult.Failure(changes, exception)` when all changes failed, or `WriteResult.PartialFailure(changes, exception)` when some succeeded. The failed changes list everything not confirmed written; unlisted changes count as written, and an error with an empty list is treated as the whole batch having failed. The base class enqueues the failed changes into the write retry queue automatically.

#### Registering a Source

Sources are `BackgroundService` implementations, so they need to be registered both as a singleton and as `IHostedService` so the host starts them. The recommended pattern is to provide extension methods that encapsulate this. Use a convenience overload that resolves the subject by type, and a flexible overload with a `subjectSelector` for custom resolution:

```csharp
// Convenience: resolves subject by type from DI
public static IServiceCollection AddDatabaseSource<TSubject>(
    this IServiceCollection services,
    string connectionString)
    where TSubject : IInterceptorSubject
{
    return services.AddDatabaseSource(
        sp => sp.GetRequiredService<TSubject>(),
        connectionString);
}

// Flexible: caller controls subject resolution
public static IServiceCollection AddDatabaseSource(
    this IServiceCollection services,
    Func<IServiceProvider, IInterceptorSubject> subjectSelector,
    string connectionString)
{
    var key = Guid.NewGuid().ToString();
    services.AddKeyedSingleton(key, (sp, _) => new DatabaseSource(
        subjectSelector(sp),
        sp.GetRequiredService<IInterceptorSubjectContext>(),
        sp.GetRequiredService<ILogger<DatabaseSource>>()));
    services.AddSingleton<IHostedService>(sp =>
        sp.GetRequiredKeyedService<DatabaseSource>(key));
    return services;
}
```

Using a unique keyed registration internally allows multiple sources of the same type to be registered independently (e.g., two `DatabaseSource` instances pointing at different databases for different subject trees).

The built-in connectors follow the same pattern:

```csharp
// OPC UA Client Source
builder.Services.AddOpcUaSubjectClientSource<Sensor>("opc.tcp://localhost:4840", "opc", rootPath: ["Root"]);

// MQTT Client Source
builder.Services.AddMqttSubjectClientSource<Sensor>(
    brokerHost: "localhost",
    connectorName: "mqtt");
```

#### BackgroundTaskLifetime

`BackgroundTaskLifetime` manages a background task tied to the listen lifetime. It creates a linked `CancellationTokenSource`, spawns the task, and on disposal cancels the token, awaits the task, and then invokes an optional cleanup callback. All built-in sources (OPC UA, MQTT, WebSocket) use it for their monitor/health-check loops.

```csharp
return BackgroundTaskLifetime.Start(
    cancellationToken,               // parent token from StartListeningAsync
    _logger,
    ct => RunMyBackgroundLoop(ct),   // the task body
    () => connection.DisposeAsync()); // optional: cleanup when disposed
```

Return the `BackgroundTaskLifetime` from `StartListeningAsync`. The base class disposes it automatically on retry or shutdown.

#### Write Retry Queue Configuration

Pass `writeRetryQueueSize` to the `SubjectSourceBase` constructor to configure the queue capacity:

```csharp
public sealed class DatabaseSource : SubjectSourceBase
{
    public DatabaseSource(
        IInterceptorSubjectContext context,
        ILogger<DatabaseSource> logger)
        : base(
            context,
            logger,
            bufferTime: TimeSpan.FromMilliseconds(8),
            retryTime: TimeSpan.FromSeconds(10),
            writeRetryQueueSize: 1000) // Enable write retry queue (0 to disable)
    {
    }

    // ... overrides
}
```

#### SourceOwnershipManager

Sources claim ownership of properties in two phases: initially inside `StartListeningAsync` by scanning the subject graph (e.g., using a path provider to determine which properties to include), and dynamically at runtime when subjects are attached to or detached from the object graph. The `SourceOwnershipManager` class simplifies this by handling:
- Property ownership tracking (which properties this source is responsible for)
- Automatic cleanup when subjects are detached from the object graph
- Safe ownership claims that prevent conflicts with other sources

```csharp
public sealed class DatabaseSource : SubjectSourceBase
{
    private readonly IInterceptorSubject _root;
    private readonly SourceOwnershipManager _ownership;

    public DatabaseSource(
        IInterceptorSubject root,
        IInterceptorSubjectContext context,
        ILogger<DatabaseSource> logger)
        : base(context, logger)
    {
        _root = root;
        // SourceOwnershipManager requires WithLifecycle() on context - throws if not configured
        _ownership = new SourceOwnershipManager(
            this,
            onReleasing: property =>
            {
                // Called before a property is released - clean up protocol-specific data
                property.RemovePropertyData("DatabaseRowId");
            },
            onSubjectDetaching: subject =>
            {
                // Called when a subject is detached from the object graph
                // Use this to clean up caches or subscriptions for the subject
                CleanupCachesForSubject(subject);
            });
    }

    public override IInterceptorSubject RootSubject => _root;

    protected override async Task<IAsyncDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter,
        CancellationToken cancellationToken)
    {
        var registeredSubject = _root.TryGetRegisteredSubject();
        if (registeredSubject is null) return null;

        foreach (var property in registeredSubject.GetAllProperties())
        {
            // ClaimSource returns false if already owned by another source
            if (!_ownership.ClaimSource(property.Reference))
            {
                _logger.LogWarning(
                    "Property {Name} already owned by another source, skipping.",
                    property.Name);
                continue;
            }

            // Set up database subscription for this property...
            property.Reference.SetPropertyData("DatabaseRowId", rowId);
        }

        return subscription;
    }

    public override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        => Task.FromResult<Action?>(null);

    public override ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
        => new(WriteResult.Success);

    public override void Dispose()
    {
        // Releases all owned properties and unsubscribes from lifecycle events
        _ownership.Dispose();
        base.Dispose();
    }
}
```

**Ownership methods:**

| Method                    | Description                                                                         |
|---------------------------|-------------------------------------------------------------------------------------|
| `ClaimSource(property)`   | Returns `true` if ownership was claimed, `false` if already owned by another source |
| `ReleaseSource(property)` | Releases ownership of a single property                                             |
| `Properties`              | Read-only collection of currently owned properties                                  |
| `Dispose()`               | Releases all owned properties and unsubscribes from events                          |

**Lifecycle integration:** The `SourceOwnershipManager` constructor automatically subscribes to lifecycle events from the source's context. When subjects are detached from the object graph, owned properties are automatically released. This prevents memory leaks and stale subscriptions. The context must have lifecycle tracking configured via `WithLifecycle()`. If not configured, the constructor throws `InvalidOperationException`.

#### Low-Level Ownership API

For advanced scenarios, you can use the extension methods directly. These operations are thread-safe and atomic:

```csharp
// Set source ownership (returns false if already owned by different source)
bool claimed = property.SetSource(mySource);

// Remove source (only if it matches expected source)
bool removed = property.RemoveSource(expectedSource);

// Check current source
if (property.TryGetSource(out var source))
{
    // Property has a source
}
```

## Servers

A **server** exposes subject properties to external clients. Unlike sources, the local model is the source of truth. The server publishes outward rather than syncing inward.

**Examples**: `OpcUaSubjectServer` exposes subjects as OPC UA nodes, `MqttSubjectServer` publishes changes to MQTT topics, `WebSocketSubjectServer` streams updates over WebSocket connections.

There is no `SubjectServerBase`. Servers are implemented as `BackgroundService` classes that optionally implement `ISubjectConnector`. The infrastructure provides building blocks, but the server implementation is up to you.

### Responsibilities

A server implementation typically handles:

- **Starting the protocol server**: bind to a port, accept connections, restart on failure
- **Publishing property changes**: observe changes via `ChangeQueueProcessor` and push them to connected clients using the protocol's wire format. Servers create the processor before the protocol server starts, so changes made during startup are captured; of those, changes a later local commit superseded are collapsed rather than published in sequence, so clients see the settled value instead of every intermediate one
- **Handling inbound writes**: receive write requests from external clients and apply them to the local model (typically via `SetValueFromSource()` to prevent echo loops)
- **Lifecycle cleanup**: release caches and subscriptions when subjects are detached from the object graph

### Pattern

All built-in servers (OPC UA, MQTT, WebSocket) follow the same structure:

1. Extend `BackgroundService` for hosting lifecycle
2. Implement `ISubjectConnector` for type consistency and connector enumeration
3. Create a `ChangeQueueProcessor` in `ExecuteAsync` to subscribe to property changes before the protocol server starts accepting clients
4. Accept incoming client connections and route write requests to the local model via `SetValueFromSource()`
5. Use a retry/restart loop in `ExecuteAsync` to recover from protocol failures

The built-in server implementations serve as reference for building custom servers. See the protocol-specific documentation for details:
- [OPC UA Server](connectors-opcua-server.md)
- [MQTT](connectors-mqtt.md)
- [WebSocket](connectors-websocket.md)

### Registering a Server

Servers follow the same registration pattern as sources: register as singleton + `IHostedService`, typically via an extension method. The built-in connectors provide these:

```csharp
// OPC UA Server
builder.Services.AddOpcUaSubjectServer<Sensor>(connectorName: "opc", rootName: "Devices");

// MQTT Server
builder.Services.AddMqttSubjectServer<Sensor>(connectorName: "mqtt", brokerPort: 1883);

// WebSocket Server (standalone)
builder.Services.AddWebSocketSubjectServer<Sensor>(configuration =>
{
    configuration.Port = 8080;
    configuration.Path = "/ws";
    configuration.PathProvider = new AttributeBasedPathProvider("ws");
});
```

## Shared Infrastructure

### ISubjectConnector

Minimal marker interface for components that connect subjects to external systems:

```csharp
public interface ISubjectConnector
{
    /// <summary>
    /// The root subject being connected to an external system.
    /// </summary>
    IInterceptorSubject RootSubject { get; }
}
```

This interface is:
- **Required** for sources (`ISubjectSource : ISubjectConnector`)
- **Optional** for servers (they can implement it for type consistency)

> **Note**: Path providers are implementation details. A source/server may use a path provider internally to decide which properties to include and how to map them, or it may not use one at all.

### Property Mappers

Connectors translate subject properties to external-system representations through the `IPropertyMapper<TMapping>` abstraction, defined in `Namotion.Interceptor.Connectors.Mapping`. Each connector defines its own `TMapping` record (e.g., `MqttPropertyMapping`, `OpcUaPropertyMapping`) that carries protocol-specific metadata such as topics, QoS levels, or OPC UA browse names.

#### Core Interfaces

```csharp
public interface IPropertyMapper<TMapping>
{
    bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out TMapping? mapping);
}

public interface IReversePropertyMapper<TMapping, in TKey> : IPropertyMapper<TMapping>
{
    ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        TKey key,
        RegisteredSubject subject,
        CancellationToken cancellationToken);
}
```

`IPropertyMapper<TMapping>` maps a property to its external representation (forward direction). `IReversePropertyMapper<TMapping, TKey>` adds reverse lookup of a property from an external key, which connectors that receive inbound data need (e.g., finding the property for a received MQTT topic or OPC UA node reference).

#### Built-in Generic Implementations

| Class                                          | Description                                                                                       |
|------------------------------------------------|---------------------------------------------------------------------------------------------------|
| `ReverseCompositeMapper<TMapping, TKey>` | Combines multiple reverse-capable mappers with "last wins" merge semantics via `IPropertyMapping<TMapping>.Merge` |

`ReverseCompositeMapper<TMapping, TKey>` requires the mapping record to implement `IPropertyMapping<TMapping>`, which provides the static `Merge` method for combining partial configurations. Each connector exposes a thin subclass (for example `MqttCompositeMapper` and `OpcUaCompositeMapper`) for type safety and naming; consumers normally use those rather than the generic base.

#### Default Composition

Each connector defaults its `Mapper` to a composite that chains a path-provider adapter with a protocol-specific attribute mapper. For example, the MQTT client defaults to:

```csharp
Mapper = new MqttCompositeMapper(
    new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
    new MqttAttributeMapper("mqtt"))
```

The OPC UA client defaults to:

```csharp
Mapper = new OpcUaCompositeMapper(
    new OpcUaPathProviderMapper(new AttributeBasedPathProvider("opc")),
    new OpcUaAttributeMapper())
```

#### Connector-Specific Wrappers

Each connector ships thin wrappers that adapt generic infrastructure to protocol-specific types:

| Connector | Mapper Wrappers                                                                                               |
|-----------|---------------------------------------------------------------------------------------------------------------|
| MQTT      | `MqttPathProviderMapper`, `MqttAttributeMapper`, `MqttFluentMapper` (built by `MqttFluentMapperBuilder<TRoot>`), `MqttCompositeMapper` |
| WebSocket | Uses `PathProviderBase` directly (no mapper abstraction)                                              |
| OPC UA    | `OpcUaPathProviderMapper`, `OpcUaAttributeMapper`, `OpcUaFluentMapper` (built by `OpcUaFluentMapperBuilder<TRoot>`), `OpcUaCompositeMapper` |

See the protocol-specific documentation for details on each connector's mapping types and configuration.

### Path Providers

Path providers map between subject property paths and external system paths. They are defined in `Namotion.Interceptor.Registry.Paths`.

#### IPathProvider Interface

```csharp
public interface IPathProvider
{
    /// <summary>
    /// Should this property be included in paths?
    /// </summary>
    bool IsPropertyIncluded(RegisteredSubjectProperty property);

    /// <summary>
    /// Get the path segment for a property.
    /// Returns null if no explicit mapping exists.
    /// </summary>
    string? TryGetPropertySegment(RegisteredSubjectProperty property);

    /// <summary>
    /// Find a property by its path segment.
    /// </summary>
    RegisteredSubjectProperty? TryGetPropertyFromSegment(RegisteredSubject subject, string segment);
}
```

#### Built-in Providers

- **DefaultPathProvider** - Uses property names exactly as defined
- **CamelCasePathProvider** - Converts property names to camelCase for JSON APIs
- **AttributeBasedPathProvider** - Uses `[Path]` attributes for custom mapping

#### [Path] Attribute

Use `[Path]` attributes to map properties to custom external paths:

```csharp
[InterceptorSubject]
public partial class Sensor
{
    [Path("temp")]
    public partial decimal Temperature { get; set; }

    [Path("hum")]
    public partial decimal Humidity { get; set; }
}
```

#### [InlinePaths] Attribute

Marks a dictionary property as a transparent container for path resolution:

```csharp
[InterceptorSubject]
public partial class ProductionLine
{
    public partial string Name { get; set; }

    [InlinePaths]
    public partial Dictionary<string, Machine> Machines { get; set; }
}

[InterceptorSubject]
public partial class Machine
{
    public partial string Status { get; set; }
    public partial decimal Temperature { get; set; }
}
```

With `[InlinePaths]`:
- Path `Line.CNC01.Status` resolves to `Line.Machines["CNC01"].Status`
- Direct properties take precedence over child keys. If a subject has both a direct property and a dictionary key with the same name, the property wins and the key is unreachable via that segment
- Only one property per class may be marked with `[InlinePaths]`; multiple properties throws `InvalidOperationException`
- Works with `AttributeBasedPathProvider` without requiring `[Path]` attribute on the dictionary
- Built into `PathProviderBase.TryGetPropertyFromSegment`

### Updates

The `Namotion.Interceptor.Connectors.Updates` namespace contains serialization infrastructure for subject state:

- **SubjectUpdate** - Serializable representation of a subject's state
- **SubjectPropertyUpdate** - Serializable representation of a property change
- **ISubjectUpdateProcessor** - Filter/transform updates before serialization

These are used by both sources and servers (e.g., ASP.NET Core controllers, SignalR hubs).

For details on the update format, collection synchronization, and apply logic, see [Subject Updates](connectors-subject-updates.md).

### Thread Safety

Properties can receive concurrent writes from multiple origins:
- **Source**: Inbound updates from the external system
- **Servers**: Background services exposing the property
- **Local code**: Application services, UI handlers, etc.

Individual property updates are atomic and thread-safe without requiring additional synchronization.

When overriding `StartListeningAsync`, use the provided `SubjectPropertyWriter` to write inbound updates. This handles buffering during initialization and ensures correct ordering.

## Known Limitations

Cases where the local model and the external system can end up disagreeing, or where a write is lost without an error. Everything else about the write path converges and is described above. The reasoning behind the delivery rules lives in [docs/design/connector-delivery.md](design/connector-delivery.md).

**A failed write leaves the two ends diverged until the property is written again.** The [write retry queue](#write-retry-queue) retries it, but it is a bounded ring buffer that drops its oldest entries when full, and with it disabled the change is logged and dropped immediately. Nothing actively reconciles the difference. Tracked as [#342](https://github.com/RicoSuter/Namotion.Interceptor/issues/342).

**Disabling the retry queue discards connect-window writes silently.** With `writeRetryQueueSize: 0` there is no queue to park them in, so the drain empties the subscription and returns, and the queue's own "buffering is disabled" warning never fires because there is no queue to emit it.

**Writes to properties a source has not claimed yet are discarded.** Ownership is established inside `StartListeningAsync`, and the drain must empty the subscription to keep it bounded, so a write it cannot attribute is dropped without an error. First connection only, since ownership persists across reconnects.

**Connector-internal reconnects skip the reconcile.** Transport-level reconnects handled inside a connector (the OPC UA health loop, the MQTT and WebSocket monitors) reload initial state without running the connect-window reconciliation, so a queued write is flushed afterwards without the supersession check. The two paths agree on which value wins and differ only in whether superseded intermediates are filtered. Tracked as [#362](https://github.com/RicoSuter/Namotion.Interceptor/issues/362).

**A property with no setter cannot be restored.** If the load moves the model off a parked write for a derived or getter-only property, there is nothing to write back locally, so the change is dropped and logged by name rather than silently counted as restored.

**A source that neither answers reads nor echoes writes is unobservable.** If it clamps or rejects a value internally and sends no notification, nothing local can reveal the difference and the two ends stay diverged. This is the assumption convergence rests on. See [#373](https://github.com/RicoSuter/Namotion.Interceptor/issues/373).

**Ordering across different properties is not preserved by reconciliation.** Writes sent as already-current are flushed before restored writes travel through the change queue, so two properties written in one order locally can reach the source in the other. Ordering within a single property is preserved.

Use [source transactions](tracking-transactions.md) when a write must be confirmed by the source before the local model accepts it.
