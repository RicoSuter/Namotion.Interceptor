# Single ownership for subject bound hosted services

## Problem

`Namotion.Interceptor.Hosting` offers two ways to bind an `IHostedService` to a subject, and neither
states who owns the start, the stop or the disposal. Four defects follow from that one gap.

### 1a. `AddHostedSubject<T>` starts the subject twice

`HostedSubjectServiceCollectionExtensions.AddHostedSubject<T>` registers the subject as a singleton and
then registers the same instance as a hosted service:

```csharp
services.AddHostedService<T>(serviceProvider => serviceProvider.GetRequiredService<T>());
```

When the subject is constructed with a context configured through `WithHostedServices()`, construction
attaches it to that context, `HostedServiceHandler.HandleLifecycleChange` sees
`change.Subject is IHostedService` (`HostedServiceHandler.cs:30`) and starts it. The DI registration then
starts it again. Measured with a probe that counts `StartAsync` calls: **2**.

On a `BackgroundService` the second call overwrites `_stoppingCts` and assigns a second `_executeTask`,
so the first execution task is orphaned. Once host startup completes, its parent token source is gone
and nothing can cancel it.

### 1b. The context is silently dropped for most real subjects

`AddHostedSubject` passes the context only when `HasContextConstructor<T>()` finds a public constructor
accepting `IInterceptorSubjectContext` (`HostedSubjectServiceCollectionExtensions.cs:36,60-65`). Two
distinct shapes fail that test or defeat it.

**No generated context constructor.** The generator emits one only when
`HasOrWillHaveParameterlessConstructor` (`SubjectCodeGenerator.cs:262`), which
`SubjectMetadataExtractor.cs:807-828` computes from the *first declared* constructor having zero
parameters. Each of the six device subjects declares exactly one parameterised constructor, so none gets
it:

| Type | Declared constructor |
|---|---|
| `ShellyDevice.cs:148` | `(IHttpClientFactory, ILogger<ShellyDevice>)` |
| `MyStromSwitch.cs:129` | `(IHttpClientFactory, ILogger<MyStromSwitch>)` |
| `WallboxCharger.cs:200` | `(IHttpClientFactory, ILogger<WallboxCharger>)` |
| `GpioSubject.cs:111` | `(GpioDriver? driver = null)` |
| `EcowittGateway.cs:171` | `(IHttpClientFactory, ILogger<EcowittGateway>)` |
| `HueBridge.cs:115` | `(ILogger<HueBridge>)` |

The generated `MyStromSwitch` confirms it: the only `IInterceptorSubjectContext` in the file is
`IInterceptorSubject.Context => _context ??= new InterceptorExecutor(this)`. So these six are built with
`ActivatorUtilities.CreateInstance<T>(sp)`, fall back to a private empty context, and the
`contextResolver` argument every `Add*Device` extension faithfully forwards has no effect. The device
runs with no tracking, no registry and no lifecycle.

**A hand written context constructor that never attaches.** The shape `docs/subject-guidelines.md:513`
teaches is `MySubject(IInterceptorSubjectContext? context = null, IMyDriver? driver = null)`. For that
type `HasContextConstructor` is true, so the context is handed to a parameter that nobody uses, and the
subject is still never attached. The documented pattern is broken.

These are not exhaustive partitions of one bug. A subject with a generated context constructor gets a
context and starts twice; a subject with DI constructor parameters gets no context; a subject with a
hand written context parameter gets neither a context nor a double start. All three are the same missing
answer to "who attaches the subject".

### 2. Detaching an attached service stops it but never disposes it

`HostedServiceHandler` calls `StopAsync` and drops the reference (`HostedServiceHandler.cs:146-155`,
`:212-229`). Connectors own more than a cancellation token: `OpcUaSubjectClientSource` holds a
`SessionManager` and a `LifecycleInterceptor.SubjectDetaching` subscription released only through
`_ownership.Dispose()` inside `DisposeAsync` (`OpcUaSubjectClientSource.cs:704,720`,
`Namotion.Interceptor.Connectors/SourceOwnershipManager.cs:52,134`).

`HomeBlaze.OpcUa.OpcUaClient` works around this by hand, with a comment explaining the per cycle leak
(`OpcUaClient.cs:293-296`, dispose block at `:297-307`). `HomeBlaze.OpcUa.OpcUaServer` runs the same
start and stop dance and omits the dispose entirely (`OpcUaServer.cs:295-302`). That omission happens to
be harmless, because `OpcUaSubjectServer` has no `Dispose` of its own and removes its `SubjectDetaching`
subscription in `ExecuteAsync`'s own `finally`, but the wrapper author has no way to know that without
reading the connector. `MqttSubjectClientSource` and `MqttSubjectServer` are both `IAsyncDisposable`, so
the next wrapper walks into the trap.

Two copies of the same pattern already disagree on precisely its subtlest step.

### 3. A detached subject cannot bring its services back

`IsContextAttach` is `isFirstAttach` and `IsContextDetach` is `isLastDetach`
(`LifecycleInterceptor.cs:133,250`), refcounted across parents. A subject that leaves the graph and
re-enters it therefore fires a real context detach followed by a context attach. Today the detach branch
removes every attachment from the subject's data bag (`HostedServiceHandler.cs:47-50`), so the re-attach
starts nothing and the failure is silent.

A plain `BackgroundService` restarts cleanly after a completed `StopAsync`, so restarting the stopped
instance would appear to work. It is not an option, because defect 2 requires the handler to dispose what
it stopped and a disposed connector cannot restart: `OpcUaSubjectClientSource` latches `_disposed`
(`:33,:704`) and has released its ownership manager. Re-attach needs a new instance.

Nothing in the repository performs a move today. HomeBlaze's storage layer has `AddToHierarchy` and
`RemoveFromHierarchy` and no reparent, and the lifecycle diff is set based, so reassigning a folder's
`Children` dictionary leaves unchanged siblings attached and fires no detach
(`LifecycleInterceptor.cs:340,349`).

That is a statement about today, not about demand. An external consumer of the package already needs
attach, detach and re-attach to work, and moving subjects is planned for HomeBlaze, where a move is
exactly a detach followed by an attach. The capability has to exist before either can be built on it, and
the alternative is shipping attach and detach with a silent no-op in the middle.

### 4. Two contexts sharing one `IServiceCollection` silently lose the second handler

`WithHostedServices` registers the handler with `serviceCollection.AddHostedService(sp => …)`
(`InterceptorSubjectContextExtensions.cs:16`), which is `TryAddEnumerable` internally and dedupes on the
implementation type. A second context on the same collection creates its handler and installs it into its
own context, but the hosted service registration is discarded, so that handler never starts and every
subject in that context silently never starts.

Nothing trips this today: `ConnectorTesterHost` builds a fresh `new ServiceCollection()` per participant
(`ConnectorTesterHost.cs:111`). It is a latent trap and one line to close.

## Goals

1. Exactly one component decides when a subject bound hosted service starts and stops.
2. Whatever creates an instance disposes it, and nothing disposes an instance it did not create.
3. A subject that leaves and re-enters the graph gets working hosted services again.
4. Registering a subject supplies its context regardless of the subject's constructor shape.
5. No user code runs while a framework lock is held, structurally rather than by convention.

## Non-goals

- Removing the 50 ms `Task.Delay` before start and stop. See Out of scope.
- Converting the samples to the new registration helper. See Documentation.
- Changing `SubjectSourceBase` or any connector implementation.
- Detecting cycles between targets. See Residual hazards.
- Supporting a subject reachable from two hosting enabled contexts, beyond making it benign.

## Design

### The rule

A hosted service runs exactly while its subject is in the graph. The `HostedServiceHandler` on the
subject's context is the only thing that starts or stops it, and it disposes exactly what it created.

### Ownership

| Kind | Instance created by | Started and stopped by | Disposed by | On re-attach |
|---|---|---|---|---|
| Subject implements `IHostedService` | The caller, or the DI container via `AddSubject` | `HostedServiceHandler` | The DI container, for singletons it created. The handler never disposes a subject | Restarted in place |
| Factory attachment | `HostedServiceHandler`, by invoking the factory | `HostedServiceHandler` | `HostedServiceHandler` | Fresh instance from the factory |

A subject registered through `AddSubject` is constructed by the container, so the container disposes it at
shutdown like any other factory registered singleton. A subject registered with `AddSingleton(instance)`
belongs to the caller and is not disposed by the container. Either way the handler stops it and leaves
disposal alone, which is what makes a graph move non destructive.

### Concurrency: per target serialization

The current implementation drains a single `BufferBlock` from one consumer loop
(`HostedServiceHandler.cs:66-85`). That one queue couples services that have nothing to do with each
other, and it is the direct cause of a self deadlock, a shutdown hang, an ordering dependence on DI
registration, and a create versus detach race. It is replaced by per target serialization.

**A target is one managed thing:** either a subject that implements `IHostedService`, or an attachment.
A target record holds the factory (attachments only), the current instance, the fault from the last
failed transition, the owning handler, and a transition chain. Records live in the subject's `Data` bag.
The handler holds only the set of targets currently running, which is what shutdown iterates.

**A transition is a start or a stop appended to the target's chain:**

```csharp
lock (_sync)
{
    _tail = _tail
        .ContinueWith(_ => RunTransitionAsync(kind), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default)
        .Unwrap();
    return _tail;
}
```

Every element of that call is load bearing and none is optional:

- **The lock.** Without it, `_tail = _tail.ContinueWith(...)` is an unsynchronised read modify write. A
  replica of this design with the unlocked form ran five transitions concurrently on a single target on
  its first attempt. Two appenders race here in practice: `HandleLifecycleChange` appends under
  `lock (_attachedSubjects)` (`LifecycleInterceptor.cs:38,64,302`) while a wrapper's stop path appends
  from a pool thread (`OpcUaClient.cs:207` then `:284`).
- **`TaskScheduler.Default`.** `ContinueWith` otherwise captures `TaskScheduler.Current`, which in the
  same replica was a `ConcurrentExclusiveTaskScheduler`, and in one run the continuation was queued to a
  scheduler the appending task was occupying and never ran.
- **Appending never blocks and never runs user code**, so `HandleLifecycleChange` may append while the
  lifecycle holds `_attachedSubjects`. A default `ContinueWith` never executes inline on the appending
  thread, so goal 5 holds structurally rather than by a rule nobody can enforce.

**Transition bodies never throw.** `RunTransitionAsync` catches, logs, records into the target's `Fault`,
and completes successfully. Without this, dropped fire and forget transitions raise
`UnobservedTaskException` (200 of 200 in the replica) and a faulted `_tail` is retained until the target
transitions again, so a failed graph driven start could surface nowhere at all.

**Every append happens at event time, never deferred into another transition.** This is the rule that makes
a graph move work, and getting it wrong is subtle enough to be worth showing. `BackgroundService.StopAsync`
awaits `_executeTask`, so a subject's stop is slow, and its attachments must not be disposed until it has
unwound: a replica of the `OpcUaClient` detach trace saw the unwind observe its own source already disposed
in 50 of 50 runs. The tempting fix is one composite transition on the subject's chain that stops the
subject and then stops its attachments. That is wrong. On `detach` immediately followed by `attach`, the
re-attach's create-and-start lands on the attachment's own chain and runs first, and the composite's stop
is issued afterwards against the *new* instance. A replica of that shape leaves the pre-detach instance
never disposed and the post-re-attach instance stopped and disposed, in 20 of 20 runs, so the subject sits
in the graph with nothing running.

So on context detach, under `lock (_attachedSubjects)`, the handler appends immediately to every affected
chain:

- If the subject is an `IHostedService`, a stop transition on the subject's chain. It signals a
  `subjectStopped` completion in a `finally`, so cancellation and failure release it too.
- For each attachment, a stop transition on that attachment's chain which first awaits `subjectStopped`,
  then stops, disposes, and clears `Current`. The attachment record stays on the subject.

When the subject is not an `IHostedService`, `subjectStopped` is already completed, so a plain subject with
attachments needs no chain of its own. Ordering is preserved because both appends happen under the
lifecycle lock, so any later re-attach queues behind them on the same chains. The wait is acyclic: an
attachment chain waits on the subject's signal, and the subject's chain waits on nothing.

Context attach is the mirror image, also under the lock: a start transition on the subject's chain if it is
an `IHostedService`, and a create-and-start on each attachment's chain. Host shutdown uses the same shape
per owned subject rather than stopping targets independently, because the ordering hazard is identical
there.

**A start transition re-checks liveness before doing anything.** Ordering by the chain covers
lifecycle-driven appends, which are serialised by `lock (_attachedSubjects)`, but a user-driven
`AttachHostedService` appends under the target's own lock only and is unordered against them. So the start
transition first confirms this handler still owns the target; if not, it completes without creating or
starting anything. Ownership doubles as the liveness signal, which also gives the documented behaviour that
attaching to a subject outside a hosting enabled context stores the factory and runs nothing.

**The gate has four states**, and the fourth is not optional. `NotStarted`, `Running`, `Draining`,
`Drained`. Transitions wait for `Running`, so nothing runs before host start. Once shutdown flips the state
to `Draining`, start transitions complete as no-ops while stops still run, which closes a race a replica hit
3 times in 400: with only three states, a target attached during the drain is started after the running set
was snapshotted and is never stopped. After `Drained`, every append completes immediately as a no-op.

`EnsureStartedAsync` moves `NotStarted` to `Running` and is idempotent, so `HostedServiceHandler.StartAsync`
becomes a call to it. Which callers may open the gate is a real decision, because "nothing runs before host
start" and "a caller started before the handler must not hang" pull in opposite directions:

- `SubjectActivation<T>` and the **awaitable** attach and detach overloads call `EnsureStartedAsync`, so
  they open it. Awaiting is an explicit request for the service to be running, and this is what stops a
  hosted service registered ahead of the handler from hanging. `ParticipantHostBundle.cs:35-69` starts a
  participant's hosted services in registration order and awaits each, so that shape is live.
- The **synchronous** overloads and every graph driven append only wait for the gate. They cannot await an
  async method anyway, and this preserves the invariant for `new Car(context)` at configuration time.

**What is ordered, and what is not.** Lifecycle-driven appends are globally serialised in event order,
because every lifecycle event fires under `lock (_attachedSubjects)` and the handler appends inside it.
User-driven appends are not ordered against them, which is why the liveness re-check above exists.
Execution is then per target, and that is what gives quiescent consistency: each chain drains in append
order, so once events settle every target is in the state its last event demanded.

A globally serialised *executor* would add cross target ordering, a strictly stronger property, at the cost
of the deadlock, the shutdown hang and N times 50 ms of startup. The one place it is genuinely needed, a
subject finishing its stop before its own attachments are disposed, is bought by the `subjectStopped`
signal without serialising unrelated services.

One ordering guarantee is deliberately dropped. `LifecycleInterceptor.DetachFromProperty` invokes a
parent's handlers at `:258` before recursing into children at `:260-268`, so today a parent hosted subject
stops before hosted descendants. Under per target chains they stop concurrently. No dependency between them
exists in this repository, and hosted services at different depths of the graph are independent by
construction, so this is accepted rather than preserved. If a consumer ever needs it, the fix is another
completion signal, not a global executor.

**Records are published safely.** `IInterceptorSubject.Data` is a
`ConcurrentDictionary<(string? property, string key), object?>` (`IInterceptorSubject.cs:20`), and
`GetHostedServiceAttachments` has to enumerate them, so attachments live under one key as an
`ImmutableArray` of records, mutated with `AddOrUpdate` as today (`InterceptorHostingExtensions.cs:40-55`).
`AddOrUpdate`'s update delegate can run more than once with no rollback, so the record is constructed
outside the delegate and the delegate only appends it. Handler ownership is taken with
`Interlocked.CompareExchange`, where an exchange that finds *this* handler already installed counts as
success; only losing to a different handler means do nothing, which makes the two context case benign
instead of a double start.

**Ownership is released on context detach and on drain.** Detach release is what allows a subject moved
between contexts to be picked up by the next handler, and drain release is what allows a second host over
the same subjects, which the HomeBlaze end to end tests do. Without the latter every record stays owned by
a dead handler and nothing ever starts again.

**Publication.** `Current` and `Fault` are written with `Volatile.Write` and read with `Volatile.Read`.
Awaiting a transition gives `WaitForStartAsync` a happens before edge, but the wrappers' diagnostics polls
read `Current` from a different chain with no such edge, so this is required rather than decorative.
`Current` is published after `StartAsync` returns successfully, and a faulted start leaves it null.

**Target lifetime.** A target leaves the handler's running set on context detach and rejoins on attach.
The record itself stays in the subject's `Data`, because the factory has to survive for goal 3. Nothing in
the handler roots a detached subject.

**Stop and dispose are idempotent per record**, because an explicit detach can race a context detach and
both will reach the same instance.

### Residual hazards, stated rather than solved

Per target serialization removes the self deadlock through a shared queue. It does not remove cycles.
An attachment whose own `StopAsync` detaches itself deadlocks on its own chain, and two subjects whose
services detach each other's attachments deadlock on each other. Both were reproduced in the replica.
Neither occurs in this repository and neither is detected. They are documented as unsupported rather than
guarded, because detection costs more than the shapes are worth.

Moving disposal from the wrapper to the handler also puts a constraint on connectors that must be stated,
because nothing enforces it. `SourceOwnershipManager.Dispose()` takes its own lock and then invokes the
`onReleasing` callback (`:136`), while `OnSubjectDetaching` reaches that same lock from inside
`lock (_attachedSubjects)` (`LifecycleInterceptor.cs:194,255`). Today the wrapper disposes, so the two
never interleave; under this design the handler disposes from a transition that can run while a detach
cascade still holds `_attachedSubjects`. It is safe for OPC UA only because its `onReleasing`
(`OpcUaSubjectClientSource.cs:78-87`) touches property data and never writes a subject property. That is
the contract for any connector's dispose path: it must not write subject properties.

### Part 1: `AddSubject<T>` replaces `AddHostedSubject<T>`

```csharp
public static IServiceCollection AddSubject<T>(
    this IServiceCollection services,
    Action<T>? configure = null,
    Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
    where T : class, IInterceptorSubject
```

**Context supply no longer depends on constructor shape.** The context is resolved as today, then applied
unconditionally after construction:

```csharp
var instance = HasContextConstructor<T>() && context is not null
    ? ActivatorUtilities.CreateInstance<T>(serviceProvider, context)
    : ActivatorUtilities.CreateInstance<T>(serviceProvider);

if (context is not null)
{
    instance.Context.AddFallbackContext(context);
}

configure?.Invoke(instance);
```

The constructor branch survives only because `ActivatorUtilities.CreateInstance<T>(sp, context)` throws
when no constructor can consume the extra argument, so the guard is load bearing there. It confers no
attachment advantage: the generated constructor is `public C(IInterceptorSubjectContext context) : this()`
(`SubjectCodeGenerator.cs:264-267`), so `: this()` runs the parameterless constructor to completion and the
attach happens last, exactly like the fallback. `AddFallbackContext` is idempotent
(`InterceptorSubjectContext.cs:125-128`, `InterceptorExecutor.cs:114`), so calling it unconditionally is
safe and fixes the hand written context parameter shape too. `RootManager.cs:85` is the existing precedent
for attaching after construction.

**Start ownership moves to the handler.** Instead of `AddHostedService<T>`, `AddSubject` registers
`SubjectActivation<T>`, whose job is to resolve `T` and hand over:

```csharp
public async Task StartAsync(CancellationToken cancellationToken)
{
    _subject = _serviceProvider.GetRequiredService<T>();

    if (_subject is not IHostedService hostedService)
    {
        return;
    }

    if (TryGetHandler(_subject) is { } handler)
    {
        await handler.EnsureStartedAsync(cancellationToken);
        await handler.WaitForStartAsync(hostedService, cancellationToken);
    }
    else
    {
        await hostedService.StartAsync(cancellationToken);
    }
}
```

Resolving the singleton constructs it, construction attaches it to the context, and the handler appends
the start. `WaitForStartAsync` awaits that target's transition and rethrows its recorded fault, preserving
the `AddHostedService` guarantee that a failing subject aborts host startup and that `ApplicationStarted`
implies the subject is running. Construction reaches `HandleLifecycleChange` synchronously, so the
transition is already appended when the activator awaits it.

`StopAsync` mirrors it and stops the subject only in the no handler case.

**Defect 4 fix.** `WithHostedServices` registers the handler with
`serviceCollection.AddSingleton<IHostedService>(sp => …)`, a plain add with no implementation type dedupe.
Nothing resolves `HostedServiceHandler` from the provider, since all lookups go through
`subject.Context.TryGetService<HostedServiceHandler>()` (`InterceptorHostingExtensions.cs:59,90,125,159`).
The same registration shape is already used by every connector in the repository, for example
`OpcUaSubjectExtensions.cs:191`, `MqttSubjectExtensions.cs:64`, `WebSocketSubjectExtensions.cs:55` and
`SubjectGraphQLExtensions.cs:25`.

**Idempotency.** `AddHostedService<SubjectActivation<T>>` goes through `TryAddEnumerable`, which dedupes on
the closed generic implementation type, so registering the same subject twice is a no-op. Note the sharp
edge, which the documentation must state: a second `AddSubject<T>` with a different `configure` or
`contextResolver` silently drops them, and if the caller already registered `T` themselves,
`AddSubject<T>` applies neither the context nor `configure`.

**Constraint change.** From `IHostedService` to `IInterceptorSubject`, so `AddSubject` also serves plain
subjects that only need to exist and be attached at startup.

**The rename is not cosmetic.** Keeping the old name would change start ownership and context supply under
an unchanged signature. A rename turns that into a build failure. This is only partly reachable: the six
`Add*Device` extensions keep their signatures and forward to the new method, so their consumers get
changed behaviour with no compile error. The change is corrective, since those subjects go from having no
context to having one, but three consequences must be in the release notes: those subjects now participate
in tracking and the registry, `configure` now runs against an attached subject so its assignments are
intercepted and tracked, and the constraint change from `IHostedService` to `IInterceptorSubject` means
`AddSubject` can no longer register a plain hosted service that is not a subject, which
`AddHostedSubject<T> where T : class, IHostedService` allowed.

### Part 2: attachment becomes factory only

```csharp
public interface IHostedServiceAttachment
{
    IHostedService? Current { get; }
    Exception? Fault { get; }
}

public interface IHostedServiceAttachment<out T> : IHostedServiceAttachment
    where T : class, IHostedService
{
    new T? Current { get; }
}
```

```csharp
IHostedServiceAttachment<T> AttachHostedService<T>(
    this IInterceptorSubject subject, Func<T> factory)
    where T : class, IHostedService;

Task<IHostedServiceAttachment<T>> AttachHostedServiceAsync<T>(
    this IInterceptorSubject subject, Func<T> factory, CancellationToken cancellationToken)
    where T : class, IHostedService;

bool DetachHostedService(
    this IInterceptorSubject subject, IHostedServiceAttachment attachment);

Task<bool> DetachHostedServiceAsync(
    this IInterceptorSubject subject, IHostedServiceAttachment attachment,
    CancellationToken cancellationToken);

ImmutableArray<IHostedServiceAttachment> GetHostedServiceAttachments(
    this IInterceptorSubject subject);
```

The instance based overloads are removed. The handle replaces the instance as the detach key, because a
factory delegate cannot identify itself, and it carries the live instance, which
`OpcUaClient.UpdateDiagnostics` needs on its ten second poll (`OpcUaClient.cs:195-201,216-229`). The `out T`
variance is what makes `IHostedServiceAttachment<OpcUaSubjectClientSource>` usable as
`IHostedServiceAttachment<IHostedService>`; the `class` constraint is what makes that variance meaningful,
since a struct argument admits no variance conversion.

| Event | Effect |
|---|---|
| Attach while the subject is outside a hosting enabled context | The factory is stored on the subject. Nothing runs. `Current` is null |
| Attach while the subject is inside one | A create and start transition is appended |
| Context attach | For each stored attachment, append a create and start transition |
| Context detach | A stop transition is appended at event time; it awaits `subjectStopped`, then stops, disposes, clears `Current`. **The attachment stays on the subject** |
| Explicit detach | Stop, dispose, clear `Current`, and remove the attachment from the subject |
| Host shutdown | Same shape as context detach, per owned subject, with the host's stopping token. Starts appended while draining are no-ops, and everything after `Drained` is a no-op |

Keeping the attachment across a context detach is what makes goal 3 work: the factory survives, so the next
context attach produces a fresh instance and no restart contract is needed.

Further details:

- **The factory runs inside the transition**, outside every lock, and reads live state rather than a
  snapshot. It runs only after the liveness re-check confirms this handler still owns the target.
- **A faulted start disposes the instance it created.** `Current` stays null and the exception goes to
  `Fault`. Leaving a half started connector undisposed would be defect 2 with extra steps:
  `OpcUaSubjectClientSource` holds a `SemaphoreSlim`, a `SessionManager` and a lifecycle subscription
  released only through `DisposeAsync` (`:31,702-722`).
- **`AttachHostedServiceAsync` is transactional only when its own transition is the first one.** If a
  context attach already appended a create, the caller awaits the second transition. The documentation
  states this rather than pretending otherwise. When the caller's own transition faults, the attachment is
  removed before the exception propagates, so a `catch` is never left owning an invisible attachment. A
  graph driven start that faults keeps the attachment with `Current` null and `Fault` set, so the next
  context attach retries.
- **The cancellation token bounds the wait, not the work.** A cancelled await leaves the transition running
  and holding its chain slot, matching today's `WaitAsync` behaviour at
  `HostedServiceHandler.cs:172,190`.
- **Stop transitions take the host's stopping token during shutdown** so a wedged connector cannot hold the
  process past `ShutdownTimeout`, and `CancellationToken.None` for graph driven detaches, which have no
  deadline to inherit.
- **The synchronous overloads return once the transition is appended**, so their `bool` means "accepted",
  not "started". `Fault` and `Current` are how a caller observes the outcome.
- **Dispose policy.** `IAsyncDisposable` preferred, `IDisposable` fallback, otherwise dropped. Dispose
  failures are logged and recorded in `Fault`, never thrown.
- **Each attach call yields a distinct attachment.** Attaching the same factory twice produces two
  independently managed instances.
- **`() => existingInstance` is the one shape that defeats the design**, because a re-attach would start an
  instance the handler already stopped and disposed. Removing the instance overloads makes every old call
  site a compile error and this closure is the tempting way to silence it, so `docs/hosting.md` calls it
  out. The factory must construct.
- **`Func<T>` is deliberately narrow.** No cancellation token, no service provider, not async. Every
  connector factory in the repository is a synchronous call over resolved state, and widening it later is
  source compatible.

### Part 3: subject as hosted service

Started on first context attach, stopped on last context detach, never disposed by the handler, restarted
on re-attach. `ExecuteAsync` must tolerate being run more than once, which a plain `BackgroundService`
does.

`StartAsync` now receives `CancellationToken.None` rather than a long lived loop token. Today the loop
token is passed at `HostedServiceHandler.cs:202` and cancelled at `:98`, so it doubles as a shutdown
signal; with an explicit stop transition per target that backstop is no longer needed. This removes the
registration that kept each restart's `CancellationTokenSource` rooted for the host's lifetime.
`BackgroundService.StartAsync` still allocates one per restart and never disposes it, which is unavoidable
without framework cooperation. One requirement follows and must be documented: a hand written
`IHostedService` that captured the `StartAsync` token as its only stop signal will no longer be cancelled,
and must honour `StopAsync`. Every attachable service in the repository is ultimately a `BackgroundService`
and already does.

## Public API changes

Removed from `Namotion.Interceptor.Hosting`: `AddHostedSubject<T>`, the four instance based attach and
detach methods, and `GetAttachedHostedServices`.

Added: `AddSubject<T>`, `IHostedServiceAttachment`, `IHostedServiceAttachment<T>`, the four factory based
attach and detach methods, and `GetHostedServiceAttachments`.

A `VerifyChecksTests.PublicApi` snapshot test is added for the package. This needs infrastructure the test
project lacks: a `PublicApiGenerator` reference in `Namotion.Interceptor.Hosting.Tests.csproj` and a
`PublicApi()` method in its `VerifyTests.cs`, both modelled on `Namotion.Interceptor.Tracking.Tests`.

## Migration

Single pull request. Splitting was considered and rejected: `WaitForStartAsync` needs the same per target
state the attachment work introduces, so a first PR would build scaffolding a second immediately replaces.

| File | Change |
|---|---|
| `Namotion.Interceptor.Hosting/HostedSubjectServiceCollectionExtensions.cs` | Renamed `SubjectServiceCollectionExtensions.cs`. `AddSubject`, unconditional `AddFallbackContext`, registers `SubjectActivation<T>` |
| `Namotion.Interceptor.Hosting/SubjectActivation.cs` | New |
| `Namotion.Interceptor.Hosting/HostedServiceTarget.cs` | New. Record, chain, transitions |
| `Namotion.Interceptor.Hosting/HostedServiceHandler.cs` | Loop and `HashSet` replaced by target set, event time appends with the `subjectStopped` signal, four state gate, `EnsureStartedAsync`, `WaitForStartAsync`, ownership, disposal. `Dispose` no longer cancels a loop token; it is a no-op after `StopAsync` |
| `Namotion.Interceptor.Hosting/InterceptorHostingExtensions.cs` | Factory based API, attachment handles |
| `Namotion.Interceptor.Hosting/InterceptorSubjectContextExtensions.cs` | `AddSingleton<IHostedService>`, defect 4 |
| `HomeBlaze/Namotion.Devices.*/6 × *ServiceCollectionExtensions.cs` | One identifier each |
| `HomeBlaze/HomeBlaze.OpcUa/OpcUaClient.cs`, `OpcUaServer.cs` | See below |
| `Namotion.Interceptor.Hosting.Tests/*` | Rewritten, plus `PublicApiGenerator` wiring |
| `docs/hosting.md`, `docs/subject-guidelines.md` | Rewritten sections |
| `.claude/commands/create-homeblaze-library.md`, `migrate-homeblaze-library.md` | Follow the rename |

`Namotion.Devices.Gpio.Tests` and `Namotion.Devices.MyStrom.Tests` need no change; they call `AddGpio` and
`AddMyStromSwitch` and assert property values against a bare `BuildServiceProvider()`. Worth stating in the
plan that they therefore provide zero regression coverage for defects 1a and 1b and would pass unchanged if
the fix were reverted.

While editing `docs/subject-guidelines.md`, fix the unrelated error at `:448`, which claims `HueBridge`
injects `IHttpClientFactory`; `HueBridge.cs:115` takes only a logger.

### The two HomeBlaze wrappers

The structure stays. `StartClientAsync` keeps its place in `ExecuteAsync`, awaits the attach, and keeps
setting `Status` and `StatusMessage` from the outcome. Three changes:

```csharp
if (_attachment is null)                                    // 1. guard
{
    _attachment = await this.AttachHostedServiceAsync(() =>  // 2. factory, live state
    {
        var root = new OpcUaDynamicSubject(RootSegmentName);
        Root = root;
        return root.CreateOpcUaClientSource(BuildConfiguration(), _logger);
    }, cancellationToken);
}
```

and the manual dispose block with its comment is deleted, because the handler now disposes.

The guard is what makes a re-attach correct. The attachment survives a context detach, so on re-attach the
handler re-invokes the factory; a restarted `ExecuteAsync` that attached unconditionally would create a
second source alongside it. With the guard it sees the surviving attachment and does nothing, and the
factory produces exactly one new instance with a fresh root and current configuration.

An earlier draft moved the attach out of `ExecuteAsync` into an enable path. That was wrong on three
counts, all of which the guard avoids. `IConfigurable.ApplyConfigurationAsync` runs only on configuration
edits (`JsonSubjectSynchronizer.cs:98`), never at load, so `ExecuteAsync`'s `if (IsEnabled)` is the only
auto start hook there is and a saved, enabled client would never start. `OpcUaServer.StartServerAsync`
awaits `while (!_rootManager.IsLoaded)` before resolving its path (`:227-230,239`), which a synchronous
`Func<T>` cannot express but an awaited call inside `ExecuteAsync` can. And an awaited attach is what lets
both wrappers report a real `Status`, where a synchronous attach would return "accepted" and leave a
failure to surface on a 10 or 60 second poll (`OpcUaClient.cs:199`, `OpcUaServer.cs:196`).

`OpcUaServer` takes the same three changes. Its factory closes over the `targetSubject` resolved at
`:239`, which is a lookup into the existing graph rather than a construction, so the factory re-resolves
the path on each invocation rather than capturing the result.

`UpdateDiagnostics` reads `_attachment?.Current` in both, and `StopClientAsync` and `StopServerAsync`
detach and null the field.

## Documentation

Three existing passages invert under the new rule and must be rewritten rather than merely re-read.
`docs/hosting.md:91-102` says attached services are "stopped and removed" on detach, which becomes stopped,
disposed and kept. `docs/hosting.md:145` links to a `subject-guidelines.md` anchor that the rename changes.
And `Namotion.Interceptor.SampleWeb/Program.cs:64` carries a commented explanation of `WithHostedServices`
auto start. The existing assertion at `HostedServiceHandlerTests.cs:108`, `Assert.Empty` after a context
detach, inverts for the same reason; the test file is rewritten anyway.

`docs/hosting.md` is restructured around the ownership rule rather than the API surface, and gains the
"which pattern when" section whose absence caused this investigation:

- **Construct and register directly** when the subject needs no DI constructor dependencies. This hands you
  the instance at configuration time, which the connector samples need for `context.AddService(root)`. The
  container does not dispose an instance registered this way.
- **`AddSubject<T>()`** when the subject has constructor dependencies that only exist after
  `builder.Build()`, including the second registration and pre-registration sharp edges.
- **Factory attachment** when a service should run for as long as a subject is in the graph, including the
  `() => existingInstance` warning and the unsupported self detach.
- **Subject implements `BackgroundService`** when the subject's own purpose is a background loop, including
  the restart contract and the `StopAsync` requirement from Part 3.

The samples are left unconverted. All six connector samples call `context.AddService(root)`, which needs the
instance at configuration time and is incompatible with construction deferred to host start, and four
`Program.cs` files build 20,000 children through `Root.CreateWithPersons(context)`. Their subjects take only
the context, so constructing them up front is the better pattern and the docs now say so.

## Testing

`Namotion.Interceptor.Hosting.Tests` currently has no concurrency or ordering test at all.

Two conventions apply throughout. "Attached to the context" must be asserted through an observable, either
registry membership or the subject's own start, never by inspecting handler internals, or the test passes
vacuously. And the context used by these tests must include `WithContextInheritance`, which
`WithHostedServices` does not pull in (`InterceptorSubjectContextExtensions.cs:24-25`): without it a child
subject's `Context` never resolves the handler, so every child scenario is unreachable. The existing
harness at `HostedServiceHandlerTests.cs:182-185` has this gap.

**Registration and activation**

1. `AddSubject` on a subject with a generated context constructor starts it exactly once with hosting
   enabled. This is the probe that returns 2 today.
2. `AddSubject` on a subject with DI constructor parameters and no generated context constructor is
   attached and started by the handler. Fails today.
3. `AddSubject` on a subject with a hand written `IInterceptorSubjectContext` parameter that ignores it is
   still attached. Fails today.
4. `AddSubject` with no hosting handler starts and stops the subject itself.
5. `AddSubject` on a plain subject constructs and attaches it at host start, and its attachments are not
   awaited by host startup.
6. `AddSubject` called twice registers one activation, and the second `configure` is dropped.
7. A hand written `IHostedService` whose `StartAsync` throws aborts host startup. Not a `BackgroundService`,
   which does not surface `ExecuteAsync` failures through `StartAsync`. Also asserts what happens to
   already started targets when startup aborts.
8. `AddSubject<T>` registered **before** `WithHostedServices` does not hang. This test is the specification
   for `EnsureStartedAsync` opening the gate.
9. Two contexts on one `IServiceCollection` both get a running handler, and separately, one subject
   reachable from two hosting enabled contexts is started once.

**Concurrency and ordering**

10. Concurrent appends to one target never overlap. Drives an attach and an explicit detach of the same
    attachment from two threads with the head transition stalled, since distinct attach calls produce
    distinct targets and cannot contend. Needs a test seam to stall a transition.
11. On context detach the subject's `StopAsync` completes before its attachment is disposed.
12. **Context detach immediately followed by re-attach, with no quiescing in between**, leaves exactly one
    running instance, the pre-detach instance disposed, and the post-attach instance alive. The test must
    forbid a `WaitUntilAsync` between the two events, or it passes while the move is broken. This is the
    20 of 20 failure.
13. A factory that constructs a subject, re-entering the lifecycle lock, does not deadlock.
14. Stopping the host while a wrapper's `ExecuteAsync` is unwinding completes rather than hanging, and the
    attachment is stopped and disposed in that order.
15. A target attached **during** the drain is not left running. This is the 3 in 400 race.
16. A transition appended after the drain completes as a no-op.

**Attachment lifecycle**

17. Attach, context detach, context re-attach produces a different instance, and the first was disposed.
18. Context detach disposes exactly once, including when an explicit detach races it.
19. Explicit detach removes the attachment, so a later context attach starts nothing.
20. An attachment added while a detach is in flight is not left running.
21. A factory that throws leaves `Current` null, sets `Fault`, is logged, and does not fault the chain.
22. An instance whose `StartAsync` faults is disposed, and `Current` stays null.
23. `AttachHostedServiceAsync` removes the attachment before propagating its own start fault.
24. A cancelled `AttachHostedServiceAsync` await leaves the transition running to completion.
25. A dropped fire and forget faulted transition raises no `UnobservedTaskException`. Needs `GC.Collect`
    plus `WaitForPendingFinalizers`, a non parallel collection so another test's dropped task cannot
    pollute the handler, and a positive control proving the assertion can fail.
26. Host shutdown disposes handler created instances and releases ownership, so a second host over the same
    subjects starts them again. Disposal of the subject is asserted as "the handler did not dispose it",
    since the container disposes `AddSubject` singletons.
27. A type implementing both `IAsyncDisposable` and `IDisposable` is disposed through the async path.

**Subject as hosted service**

28. A subject implementing `IHostedService` is restarted on re-attach.
29. Re-parenting in add then remove order, where the reference count never reaches zero, neither stops nor
    restarts anything.

## Out of scope

The 50 ms `Task.Delay` before start and before stop (`HostedServiceHandler.cs:199,218`), carrying the
comment "Fix small delay to let sync property assignments/deserialization complete".

It covers a real hazard, though not the one the old spec claimed. The generated context constructor
attaches last, so the hazard is caller side: `new Car(context) { Name = "x" }`, deserialization, and
`configure?.Invoke` all assign after the attach has already fired. Removing the delay needs a "subject
fully constructed" signal, which is a separate design problem touching the generator.

The delay stays, in both the start and stop paths, and moves inside each target's transition. It no longer
serialises across targets, so N subjects cost 50 ms rather than N times 50 ms, which is a real improvement
to host startup now that `SubjectActivation<T>` awaits the start. What protects against the hazard is the
gap between a target's own attach event and its start, and that is 50 ms either way; the old loop's
staggering was a side effect of serialisation, not a guarantee, so nothing is lost. Because
the start transition no longer receives a cancellable token, shutdown waits it out per target.
