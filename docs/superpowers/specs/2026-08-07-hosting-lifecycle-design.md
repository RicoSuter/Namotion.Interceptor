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

**Context detach of a subject is one composite transition, not several.** Today's global FIFO gives an
ordering the wrappers depend on: `BackgroundService.StopAsync` awaits `_executeTask`, so the subject's
`ExecuteAsync` has fully unwound before the handler touches its attachments. Independent chains lose
that, and a replica of the `OpcUaClient` detach trace saw the unwind observe its own source already
disposed in 50 of 50 runs, while `StopClientAsync` was still reading it. So the handler appends a single
transition to the *subject's* chain that stops the subject and then stops and disposes each attachment.
This does not reintroduce the deadlock, because that step never appends to its own chain, and by the time
it reaches the attachment chains they are free.

**The gate has three states.** Transitions wait until the handler has started, so nothing runs before host
start. After shutdown has drained, new appends complete immediately as no-ops rather than running or
hanging. `EnsureStartedAsync` is idempotent and is called from `SubjectActivation<T>` *and* from the attach
and detach extension methods, so no caller can hang by being started before the handler.
`ParticipantHostBundle.cs:35-69` is a live example of a hosted service that awaits attach and could be
ordered ahead of the handler.

**What is ordered, and what is not.** Global ordering exists, at append time: every lifecycle event fires
under `lock (_attachedSubjects)` and the handler appends inside it, so appends are globally serialised in
event order. Execution is then per target. That combination is what gives quiescent consistency: each
chain drains in append order, so once events settle every target is in the state its last event demanded.

A globally serialised *executor* would add cross target ordering, a strictly stronger property, at the
cost of the deadlock, the shutdown hang and N times 50 ms of startup. It is needed in exactly one place, a
subject finishing its stop before its own attachments are disposed, and the composite transition below
buys that without serialising unrelated services.

One ordering guarantee is deliberately dropped. `LifecycleInterceptor.DetachFromProperty` invokes a
parent's handlers at `:258` before recursing into children at `:260-268`, so today a parent hosted subject
stops before hosted descendants. Under per target chains they stop concurrently. No dependency between
them exists in this repository, and hosted services at different depths of the graph are independent by
construction, so this is accepted rather than preserved. If a consumer ever needs it, the fix is the same
composite transition applied one level up, not a global executor.

**Records are published safely.** `Data` is a `ConcurrentDictionary`, whose `AddOrUpdate` update delegate
may run more than once with no rollback, so a record is built outside and published with `GetOrAdd`, and
handler ownership is taken with `Interlocked.CompareExchange`. A second handler that loses the exchange
does nothing, which makes the two context case benign instead of a double start. Ownership is released on
context detach, so a subject moved between contexts is not permanently owned by the first.

**Target lifetime.** A target leaves the handler's running set on context detach and rejoins on attach.
The record itself stays in the subject's `Data`, because the factory has to survive for goal 3. Nothing in
the handler roots a detached subject.

### Residual hazards, stated rather than solved

Per target serialization removes the self deadlock through a shared queue. It does not remove cycles.
An attachment whose own `StopAsync` detaches itself deadlocks on its own chain, and two subjects whose
services detach each other's attachments deadlock on each other. Both were reproduced in the replica.
Neither occurs in this repository and neither is detected. They are documented as unsupported rather than
guarded, because detection costs more than the shapes are worth.

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
context to having one, but two behavioural consequences must be in the release notes: they now participate
in tracking and the registry, and `configure` now runs against an attached subject, so its assignments are
intercepted and tracked.

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
| Context detach | Within the subject's composite transition: stop, dispose, clear `Current`. **The attachment stays on the subject** |
| Explicit detach | Stop, dispose, clear `Current`, and remove the attachment from the subject |
| Host shutdown | Every running target is stopped and, if handler created, disposed. Appends after the drain are no-ops |

Keeping the attachment across a context detach is what makes goal 3 work: the factory survives, so the next
context attach produces a fresh instance and no restart contract is needed.

Further details:

- **The factory runs inside the transition**, outside every lock, and reads live state rather than a
  snapshot. Ordering against a detach is guaranteed by the chain, so no liveness re-check is needed.
- **`AttachHostedServiceAsync` is transactional only when its own transition is the first one.** If a
  context attach already appended a create, the caller awaits the second transition. The documentation
  states this rather than pretending otherwise. When the caller's own transition faults, the attachment is
  removed before the exception propagates, so a `catch` is never left owning an invisible attachment. A
  graph driven start that faults keeps the attachment with `Current` null and `Fault` set, so the next
  context attach retries.
- **The cancellation token bounds the wait, not the work.** A cancelled await leaves the transition running
  and holding its chain slot, matching today's `WaitAsync` behaviour at
  `InterceptorHostingExtensions.cs:172,190`.
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
| `Namotion.Interceptor.Hosting/HostedServiceHandler.cs` | Loop and `HashSet` replaced by target set, composite detach, gate, `EnsureStartedAsync`, `WaitForStartAsync`, disposal |
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

The attach moves **out** of `ExecuteAsync` and into the enable path, and the factory reads live
configuration when invoked instead of capturing a snapshot:

```csharp
_attachment = this.AttachHostedService(() =>
{
    var root = new OpcUaDynamicSubject(RootSegmentName);
    Root = root;
    return root.CreateOpcUaClientSource(BuildConfiguration(), _logger);
});
```

This is required, not stylistic. If the attach stayed in `ExecuteAsync` while the attachment survives a
context detach, a re-attach would re-invoke the surviving factory *and* the restarted `ExecuteAsync` would
create a second one. With the attach outside, `ExecuteAsync` keeps only the ten second diagnostics poll,
`IsEnabled` means "the attachment exists", the Start and Stop operations attach and detach, and
`UpdateDiagnostics` reads `_attachment?.Current`. The manual dispose block and its comment are deleted.

`OpcUaServer` gets the same treatment, with one real difference to carry into the plan: its
`targetSubject` comes from `_pathResolver.ResolveSubject(Path, …)` (`OpcUaServer.cs:239`), a lookup into
the existing graph rather than a construction, so its factory must re-resolve the path on each invocation
or it will bind a server to a subject that has since been replaced.

## Documentation

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

**Registration and activation**

1. `AddSubject` on a subject with a generated context constructor starts it exactly once with hosting
   enabled. Currently returns 2.
2. `AddSubject` on a subject with DI constructor parameters and no generated context constructor still
   receives the context and is started by the handler. Currently fails.
3. `AddSubject` on a subject with a hand written `IInterceptorSubjectContext` parameter that ignores it is
   still attached.
4. `AddSubject` with no hosting handler starts and stops the subject itself.
5. `AddSubject` on a plain subject constructs and attaches it at host start.
6. `AddSubject` called twice registers one activation.
7. A subject whose `StartAsync` throws aborts host startup.
8. `AddSubject<T>` registered **before** `WithHostedServices` does not hang.
9. Two contexts on one `IServiceCollection` both get a running handler.

**Concurrency and ordering**

10. Concurrent appends to one target never overlap. This is the test that would have caught the
    unsynchronised chain, and must drive appends from two threads with the head transition stalled.
11. On context detach the subject's `StopAsync` completes before its attachment is disposed.
12. A factory that constructs a subject, re-entering the lifecycle lock, does not deadlock.
13. Stopping the host while a wrapper's `ExecuteAsync` is unwinding completes rather than hanging, and the
    attachment is stopped and disposed.
14. A transition appended after the shutdown drain completes as a no-op.

**Attachment lifecycle**

15. Attach, context detach, context re-attach produces a different instance, and the first was disposed.
16. Context detach disposes exactly once.
17. Explicit detach removes the attachment, so a later context attach starts nothing.
18. A factory that throws leaves `Current` null, sets `Fault`, is logged, and does not fault the chain.
19. A dropped fire and forget faulted transition raises no `UnobservedTaskException`.
20. An instance whose `StartAsync` faults is not left in the running set.
21. Host shutdown disposes handler created instances. Asserted as "the handler did not dispose the
    subject", not "the subject was not disposed", since the container disposes `AddSubject` singletons.

**Subject as hosted service**

22. A subject implementing `IHostedService` is restarted on re-attach.
23. Re-parenting in add then remove order, where the reference count never reaches zero, neither stops nor
    restarts anything.

## Out of scope

The 50 ms `Task.Delay` before start and before stop (`HostedServiceHandler.cs:199,218`), carrying the
comment "Fix small delay to let sync property assignments/deserialization complete".

It covers a real hazard, though not the one the old spec claimed. The generated context constructor
attaches last, so the hazard is caller side: `new Car(context) { Name = "x" }`, deserialization, and
`configure?.Invoke` all assign after the attach has already fired. Removing the delay needs a "subject
fully constructed" signal, which is a separate design problem touching the generator.

The delay stays, in both the start and stop paths, and moves inside each target's transition. Two
consequences are worth stating rather than discovering. It no longer serialises across targets, so N
subjects cost 50 ms rather than N times 50 ms, which is a real improvement to host startup now that
`SubjectActivation<T>` awaits the start. But it also protects strictly less than before: under the old
global loop targets woke at 50 ms intervals, and now they all wake at T plus 50 ms together. And because
the start transition no longer receives a cancellable token, shutdown waits it out per target.
