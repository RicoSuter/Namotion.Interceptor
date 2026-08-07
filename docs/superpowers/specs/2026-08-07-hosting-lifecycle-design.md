# Single ownership for subject bound hosted services

## Problem

`Namotion.Interceptor.Hosting` offers two ways to bind an `IHostedService` to a subject, and neither
states who owns the start, the stop or the disposal. Four defects follow from that one gap.

### 1a. `AddHostedSubject<T>` starts the subject twice

`HostedSubjectServiceCollectionExtensions.AddHostedSubject<T>` registers the subject as a singleton
and then registers the same instance as a hosted service:

```csharp
services.AddHostedService<T>(serviceProvider => serviceProvider.GetRequiredService<T>());
```

When the subject is constructed with a context that was configured through `WithHostedServices()`,
construction attaches it to that context, `HostedServiceHandler.HandleLifecycleChange` sees
`change.Subject is IHostedService` (`HostedServiceHandler.cs:30`) and starts it. The DI registration
then starts it again. Measured with a probe that counts `StartAsync` calls: **2**.

On a `BackgroundService` the second call overwrites `_stoppingCts` and assigns a second
`_executeTask`, so the first execution task is orphaned and can no longer be cancelled through the
public API.

### 1b. For every subject with dependency injected constructor parameters, the context is silently dropped

`AddHostedSubject` passes the context only when `HasContextConstructor<T>()` is true, which looks for a
public constructor accepting `IInterceptorSubjectContext`
(`HostedSubjectServiceCollectionExtensions.cs:36,60-65`). The generator emits that constructor only
when the type has or will have a parameterless one:

```csharp
if (metadata.HasOrWillHaveParameterlessConstructor)   // SubjectCodeGenerator.cs:262
{
    builder.AppendLine($"        public {metadata.ClassName}(IInterceptorSubjectContext context) : this()");
```

and `HasOrWillHaveParameterlessConstructor` is false as soon as the type declares any constructor with
parameters (`SubjectMetadataExtractor.cs:807-828`). None of the six device subjects qualifies:

| Type | Declared constructor |
|---|---|
| `ShellyDevice.cs:148` | `(IHttpClientFactory, ILogger<ShellyDevice>)` |
| `MyStromSwitch.cs:129` | `(IHttpClientFactory, ILogger<MyStromSwitch>)` |
| `WallboxCharger.cs:200` | `(IHttpClientFactory, ILogger<WallboxCharger>)` |
| `GpioSubject.cs:111` | `(GpioDriver? driver = null)` |
| `EcowittGateway.cs:171` | `(IHttpClientFactory, ILogger<EcowittGateway>)` |
| `HueBridge.cs:115` | `(ILogger<HueBridge>)` |

The generated `MyStromSwitch` confirms it: the only `IInterceptorSubjectContext` in the file is
`IInterceptorSubject.Context => _context ??= new InterceptorExecutor(this)`, with no context taking
constructor. So `AddHostedSubject` builds these six with `ActivatorUtilities.CreateInstance<T>(sp)`,
they fall back to a private empty context, and the `contextResolver` argument that every
`Add*Device` extension faithfully forwards has no effect whatsoever. The device runs, with no
tracking, no registry and no lifecycle.

The two defects are complementary, and together they cover the whole input space. A subject shaped
like the one `docs/subject-guidelines.md:510` documents gets a context and starts twice. A subject with
dependency injected constructor parameters starts once and gets no context. The second shape is
exactly the shape that needs DI construction in the first place, which is a plausible reason nothing
outside their own unit tests calls those six extensions. HomeBlaze builds devices through
`SubjectFactory.CreateSubject` and attaches them to the graph instead.

### 2. Detaching an attached service stops it but never disposes it

`HostedServiceHandler` calls `StopAsync` and drops the reference (`HostedServiceHandler.cs:146-155`,
`:212-229`). Connectors own more than a cancellation token: `OpcUaSubjectClientSource` holds a
`SessionManager` and a `LifecycleInterceptor.SubjectDetaching` subscription that is released only
through `_ownership.Dispose()` inside `DisposeAsync` (`OpcUaSubjectClientSource.cs:704,720`,
`SourceOwnershipManager.cs:52,134`).

`HomeBlaze.OpcUa.OpcUaClient` works around this by hand, with a comment explaining that skipping it
leaks per start and stop cycle (`OpcUaClient.cs:293-296`, dispose block at `:297-307`).
`HomeBlaze.OpcUa.OpcUaServer` runs the same start and stop dance and omits the dispose entirely
(`OpcUaServer.cs:295-302`). That omission is currently harmless, because `OpcUaSubjectServer` is
`IDisposable` only through `BackgroundService` and `StopAsync` has already cancelled its token source,
but the wrapper author has no way to know that without reading the connector.
`MqttSubjectClientSource` and `MqttSubjectServer` are both `IAsyncDisposable`, so the next wrapper
walks into the trap.

Two copies of the same pattern already disagree on precisely its subtlest step.

### 3. A detached subject cannot bring its services back

`IsContextAttach` is `isFirstAttach` and `IsContextDetach` is `isLastDetach`
(`LifecycleInterceptor.cs:133,250`), refcounted across parents. Moving a subject with
`parentA.Child = null` followed by `parentB.Child = child` therefore fires a full context detach and a
context attach. Today the detach branch removes every attachment from the subject's data bag
(`HostedServiceHandler.cs:47-50`), so the re-attach starts nothing and the failure is silent.

A plain `BackgroundService` does restart cleanly after a completed `StopAsync`, so restarting the
stopped instance would appear to work. It is not an option here, because defect 2 requires the handler
to dispose what it stopped, and a disposed connector cannot be restarted: `OpcUaSubjectClientSource`
latches `_disposed` (`:33,:704`) and has released its ownership manager. Re-attach therefore needs a
new instance, not a restarted one.

### 4. Two contexts sharing one `IServiceCollection` silently lose the second handler

`WithHostedServices` registers the handler with `serviceCollection.AddHostedService(sp => …)`
(`InterceptorSubjectContextExtensions.cs:16`), which is `TryAddEnumerable` internally and dedupes on
the implementation type. If two contexts call `WithHostedServices` on the same collection, the second
handler is created and installed into its own context (`TryAddService` succeeds, because that context
has none) but its hosted service registration is discarded. Its action loop never starts, its
`BufferBlock` fills forever, and every subject in that context silently never starts.

Nothing in the repository trips this today. `ConnectorTesterHost` builds a fresh `new ServiceCollection()`
per participant (`ConnectorTesterHost.cs:111`), so each context gets its own collection. It is a latent
trap rather than a live bug, and it is one line to close.

## Goals

1. Exactly one component decides when a subject bound hosted service starts and stops.
2. Whatever creates an instance disposes it, and nothing disposes an instance it did not create.
3. A subject that leaves and re-enters the graph gets working hosted services again.
4. Registering a subject supplies its context regardless of the subject's constructor shape.
5. Behavioural breaking changes surface as compile errors wherever the public surface allows it.

## Non-goals

- Removing the 50 ms `Task.Delay` in `PostStartService` and `PostStopService`. See Out of scope.
- Converting the samples to the new registration helper. See Documentation.
- Changing `SubjectSourceBase` or any connector implementation.
- Supporting a subject reachable from two hosting enabled contexts. See Concurrency and reentrancy.

## Design

### The rule

A hosted service runs exactly while its subject is in the graph. The `HostedServiceHandler` on the
subject's context is the only thing that starts or stops it, and it disposes exactly what it created.

### Ownership

| Kind | Instance created by | Started and stopped by | Disposed by | On re-attach |
|---|---|---|---|---|
| Subject implements `IHostedService` | The caller, or the DI container via `AddSubject` | `HostedServiceHandler` | The DI container, for singletons it created. The handler never disposes a subject | Restarted in place |
| Factory attachment | `HostedServiceHandler`, by invoking the factory | `HostedServiceHandler` | `HostedServiceHandler` | Fresh instance from the factory |

A subject registered through `AddSubject` is constructed by the container, so the container disposes it
at shutdown like any other factory registered singleton. A subject constructed by the caller and
registered with `AddSingleton(instance)` belongs to the caller and the container does not dispose it.
Either way the handler stops it and leaves disposal alone, which is what makes a graph move non
destructive.

### Concurrency and reentrancy

Three rules, all of which the current implementation violates or would violate under this design.

**Lock order is `_attachedSubjects` then `_hostedServices`, and user code runs under neither.**
`HostedServiceHandler.HandleLifecycleChange` is already invoked from inside `lock (_attachedSubjects)`
(`LifecycleInterceptor.cs:38-48`), and it then takes `lock (_hostedServices)`
(`HostedServiceHandler.cs:137`). Adding factory invocation and disposal makes this dangerous, because a
factory that constructs a subject re-enters `AttachSubjectToContext` and therefore `_attachedSubjects`.
The handler must take `_hostedServices` only to mutate its tracking state, release it, and only then
call a factory, `StartAsync`, `StopAsync` or a dispose method. This also repairs an existing instance
of the same hazard: `HostedServiceHandler.StopAsync:101-123` materialises its stop tasks with
`.ToArray()` inside the lock, so every `StopAsync` runs synchronously up to its first await while the
lock is held.

**Detach invoked from the handler's own loop thread executes inline.** The action loop is a single
sequential consumer (`HostedServiceHandler.cs:70-85`) and `DetachHostedServiceAsync` awaits a
`TaskCompletionSource` only that loop can complete (`:175-191`). Keeping attachments on the subject
across a context detach, which is what makes goal 3 work, would otherwise deadlock the following real
trace:

1. The loop dequeues the stop action for a `BackgroundService` subject and awaits `StopAsync`.
2. `OpcUaClient.ExecuteAsync` unwinds into `await StopClientAsync(CancellationToken.None)`
   (`OpcUaClient.cs:207`).
3. `StopClientAsync` awaits `DetachHostedServiceAsync(..., CancellationToken.None)` (`:284`).
4. That posts a stop action and awaits it, on the thread that is the only consumer of the queue, with
   no cancellation token to break the wait.

Today this cannot happen only because the detach branch removes attachments from the data bag
synchronously first, so step 3 finds nothing to remove and returns immediately. `OpcUaServer.cs:288`
has the identical shape. The handler therefore records the managed thread id of its loop, and when
attach or detach is called from it, performs the work inline instead of posting and awaiting.

**A posted create action re-checks liveness before invoking the factory.** Otherwise a context attach
queues a create, an explicit detach removes the attachment while `Current` is still null, and the
create then runs and produces an instance attached to nothing that nobody will ever stop or dispose.

### Part 1: `AddSubject<T>` replaces `AddHostedSubject<T>`

```csharp
public static IServiceCollection AddSubject<T>(
    this IServiceCollection services,
    Action<T>? configure = null,
    Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
    where T : class, IInterceptorSubject
```

**Context supply no longer depends on constructor shape.** This is the fix for defect 1b. The context
is resolved as today, from `contextResolver` when given and from DI otherwise, and then applied by
whichever of two paths the type supports:

```csharp
var instance = HasContextConstructor<T>() && context is not null
    ? ActivatorUtilities.CreateInstance<T>(serviceProvider, context)
    : ActivatorUtilities.CreateInstance<T>(serviceProvider);

if (context is not null && !HasContextConstructor<T>())
{
    ((IInterceptorSubject)instance).Context.AddFallbackContext(context);
}

configure?.Invoke(instance);
```

The constructor path is kept where it exists, because it attaches before the constructor body runs and
therefore intercepts property assignments made there. The fallback path attaches immediately after
construction, which is exactly what `SubjectFactory.CreateSubject` already does for every subject
HomeBlaze builds, so ctor assigned values are seeded on attach and only the change events for them are
missed.

**Start ownership moves to the handler.** Instead of `AddHostedService<T>`, `AddSubject` registers
`SubjectActivation<T>`, an internal hosted service whose job is to resolve `T` and then hand
responsibility over:

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
        await handler.WaitForStartAsync(hostedService, cancellationToken);
    }
    else
    {
        await hostedService.StartAsync(cancellationToken);
    }
}
```

Resolving the singleton constructs it, construction attaches it to the context, and the context's
handler starts it. Start ownership is therefore single: the handler.

`SubjectActivation<T>` **awaits** that start rather than returning once the subject is resolved. Without
this, `AddSubject` would silently change host startup semantics: `AddHostedService<T>` makes the host
await `T.StartAsync`, so a throw there aborts host startup and `ApplicationStarted` implies the subject
is running. `WaitForStartAsync` is the handler side hook that waits for the already queued start,
rather than starting a second time, and rethrows a start failure so it still aborts host startup.

`StopAsync` mirrors it and stops the subject only in the no handler case. Where a handler exists, the
handler's own `StopAsync` is responsible.

**No handler at all.** When `TryGetHandler` returns null, either because the caller never called
`WithHostedServices()` or because `contextResolver` deliberately returned null, the activator starts
and stops the subject itself. This keeps plain DI working and makes the no hosting case behave rather
than fail silently.

**Defect 4 fix.** `WithHostedServices` changes its handler registration from
`serviceCollection.AddHostedService(sp => …)` to `serviceCollection.AddSingleton<IHostedService>(sp => …)`,
a plain add with no implementation type dedupe, so two contexts sharing one collection each get a
running loop. Nothing resolves `HostedServiceHandler` from the provider, since all four lookups go
through `subject.Context.TryGetService<HostedServiceHandler>()`
(`InterceptorHostingExtensions.cs:59,90,125,159`), so the registration exists only to start the loop and
supply a logger. The same registration shape is already used by every connector in the repository, for
example `OpcUaSubjectExtensions.cs:191`, `MqttSubjectExtensions.cs:64`, `WebSocketSubjectExtensions.cs:55`
and `SubjectGraphQLExtensions.cs:25`.

**Idempotency.** `AddHostedService<SubjectActivation<T>>` goes through `TryAddEnumerable`, which dedupes
on the implementation type, so registering the same subject twice stays a no-op. This matches the
existing `TryAddSingleton<T>` behaviour. Unlike defect 4, the dedupe is wanted here, because
`SubjectActivation<T>` is closed over the type rather than over an instance.

**Shutdown is not a function of registration order.** The handler's `StopAsync` stops and disposes
everything it tracks directly rather than through the action loop, which it must, because it cancels
`_stoppingCts` before doing so (`HostedServiceHandler.cs:98`) and anything posted after that point never
runs. Correctness therefore does not depend on `WithHostedServices` being called before `AddSubject`,
nor on `HostOptions.ServicesStopConcurrently` being false. An activator that stops after its handler
finds the subject already stopped, which is a no-op.

**Constraint change.** The generic constraint moves from `IHostedService` to `IInterceptorSubject`, so
`AddSubject` also serves plain subjects that only need to exist and be attached at startup.

**The rename is not cosmetic.** Keeping `AddHostedSubject` with the new semantics would change start
ownership and context supply under an unchanged signature, which no caller can see. A rename turns that
into a build failure. Goal 5 is only partly reachable, see Known limitation below.

**Why this belongs in the hosting package** despite the registration half being pure DI: the
registration half is inert on its own. `ShellyDevice(IHttpClientFactory httpClientFactory, ILogger<ShellyDevice> logger)`
(`ShellyDevice.cs:148`) cannot be constructed before `builder.Build()`, so construction must be deferred
into the container, and something must then force it. `IHostedService` is not literally the only way
(a bare `host.Services.GetRequiredService<T>()` after `Build()` works too) but it is the only one that
composes into a library extension method the caller invokes once.

**Known limitation against goal 5.** The six `Add*Device` extensions keep their signatures and forward
to the new method, so their consumers get changed behaviour with no compile error. The change is
strictly corrective, since those subjects go from having no context to having one, but it is a
behavioural change under an unchanged signature and the release notes must say so.

### Part 2: attachment becomes factory only

```csharp
public interface IHostedServiceAttachment
{
    IHostedService? Current { get; }
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

The instance based overloads are removed. The attachment handle replaces the instance as the detach
key, because a factory delegate cannot identify itself, and it carries the live instance, which is a
requirement rather than a convenience: `OpcUaClient.UpdateDiagnostics` polls `source.Diagnostics` every
ten seconds (`OpcUaClient.cs:195-201,216-229`) and needs whatever instance is running now.

The `class` constraint is required, not stylistic: the implementation stores `Current` in a
`volatile T?` field, and `volatile` on an unconstrained `T?` is `error CS0677`. It also makes the `out T`
variance meaningful, since a struct type argument admits no variance conversion.

Semantics:

| Event | Effect |
|---|---|
| Attach while the subject is outside a hosting enabled context | The factory is stored on the subject. Nothing runs. `Current` is null |
| Attach while the subject is inside one | The handler invokes the factory and starts the instance |
| Context attach | For each stored attachment, invoke the factory and start the instance |
| Context detach | For each attachment, stop the instance, dispose it, set `Current` to null. **The attachment stays on the subject** |
| Explicit detach | Stop, dispose, set `Current` to null, and remove the attachment from the subject |
| Host shutdown | `HostedServiceHandler.StopAsync` stops and disposes every instance it created, directly rather than through the loop |

Keeping the attachment on the subject across a context detach is the change that makes goal 3 work.
The factory survives, so the next context attach produces a fresh instance and no restart contract is
needed.

Further details:

- **Where the factory runs.** Inside the posted start action on the handler's action loop, not at
  attach time, and outside every lock. The action first re-checks that the attachment is still live.
- **`AttachHostedServiceAsync` awaits creation and start only when there is a handler.** With no
  handler there is nothing to await, so it returns as soon as the factory is stored and `Current` stays
  null. This resolves the apparent contradiction with the first row of the table, and it matches the
  existing behaviour at `InterceptorHostingExtensions.cs:126-129`. Unlike `SubjectActivation<T>`, the
  attach path deliberately does not start the service itself in that case, because an attachment has no
  meaningful lifetime without a context to bound it.
- **Dispose policy.** `IAsyncDisposable` is preferred, `IDisposable` is the fallback, and an instance
  that is neither is simply dropped.
- **Failures are logged, never thrown.** A factory that throws leaves `Current` null; the async overload
  additionally propagates through its `TaskCompletionSource`. A dispose that throws is logged only.
  Context detach runs inside a property write, so an exception there would surface at an unrelated
  assignment. The attachment exposes no faulted state, so for the synchronous overload the log is the
  contract; this is a deliberate choice to keep the handle minimal and can be revisited if it bites.
- **Each attach call yields a distinct attachment.** There is no instance based deduplication to
  preserve, so attaching the same factory twice produces two independently managed instances.
- **Detach returns true** when the attachment belonged to the subject and was removed, false otherwise,
  including when the attachment belongs to a different subject.
- **`() => existingInstance` is the one shape that defeats the design**, because a re-attach would start
  an instance the handler has already stopped and disposed. Removing the instance based overloads makes
  every old call site a compile error, and this closure is the tempting way to silence it, so
  `docs/hosting.md` calls it out explicitly. The factory must construct.
- **`Func<T>` is deliberately narrow.** No cancellation token, no service provider, not async. Every
  connector factory in the repository is a synchronous call over already resolved state, and widening
  it later is a source compatible change.
- **Storage.** Attachments live in the subject's `Data` bag under the existing key, as
  `ImmutableArray<IHostedServiceAttachment>`. `Current` is published with a volatile write by the
  handler, outside `_hostedServices`.

`HostedServiceHandler` replaces its flat `HashSet<IHostedService>` with state that distinguishes handler
created instances from subjects, because the disposal policy differs between them.

### Part 3: subject as hosted service

Unchanged in shape, sharpened in contract. A subject implementing `IHostedService` is started on first
context attach, stopped on last context detach, never disposed by the handler because the handler did
not create it, and restarted on re-attach. `ExecuteAsync` must therefore tolerate being run more than
once. This is the documented contract for such subjects, and it is satisfied by a plain
`BackgroundService`, which restarts cleanly after a completed `StopAsync`.

The handler keeps passing its loop token to `StartAsync` (`HostedServiceHandler.cs:202`). That is load
bearing: `HostedServiceHandler.StopAsync` cancels `_stoppingCts` at `:98`, which through the linked token
cancels every attached `BackgroundService`'s `ExecuteAsync` before their `StopAsync` is even called.
The cost is that `BackgroundService.StartAsync` overwrites `_stoppingCts` without disposing the previous
one, so each restart leaks one `CancellationTokenSource` and one registration on the loop token that
lives until the host stops. This is accepted deliberately: a restart happens only on a graph move, and
trading the shutdown behaviour for the allocation would be a worse deal.

A subject reachable from two hosting enabled contexts is unsupported.
`InterceptorSubjectContext.TryGetService<T>` throws when more than one service of a type resolves
(`InterceptorSubjectContext.cs:230`), so this surfaces loudly rather than silently, which is the right
failure mode. `IsContextAttach` fires only on first attach in any case, so the second handler would
never see the subject.

## Public API changes

Removed from `Namotion.Interceptor.Hosting`:

- `HostedSubjectServiceCollectionExtensions.AddHostedSubject<T>`
- `InterceptorHostingExtensions.AttachHostedService(IInterceptorSubject, IHostedService)`
- `InterceptorHostingExtensions.AttachHostedServiceAsync(IInterceptorSubject, IHostedService, CancellationToken)`
- `InterceptorHostingExtensions.DetachHostedService(IInterceptorSubject, IHostedService)`
- `InterceptorHostingExtensions.DetachHostedServiceAsync(IInterceptorSubject, IHostedService, CancellationToken)`
- `InterceptorHostingExtensions.GetAttachedHostedServices`

Added: `AddSubject<T>`, `IHostedServiceAttachment`, `IHostedServiceAttachment<T>`, the four factory based
attach and detach methods, and `GetHostedServiceAttachments`.

A `VerifyChecksTests.PublicApi` snapshot test is added for `Namotion.Interceptor.Hosting` in PR 1, so the
surface is tracked from the first change onward. This needs infrastructure the package's test project
does not have yet: a `PublicApiGenerator` package reference in
`Namotion.Interceptor.Hosting.Tests.csproj` and a `PublicApi()` method in its `VerifyTests.cs`, both
modelled on `Namotion.Interceptor.Tracking.Tests`.

## Migration

| File | PR | Change |
|---|---|---|
| `src/Namotion.Interceptor.Hosting/HostedSubjectServiceCollectionExtensions.cs` | 1 | Renamed to `SubjectServiceCollectionExtensions.cs`. `AddHostedSubject` becomes `AddSubject`, context applied by constructor or fallback, registers `SubjectActivation<T>` |
| `src/Namotion.Interceptor.Hosting/SubjectActivation.cs` | 1 | New |
| `src/Namotion.Interceptor.Hosting/InterceptorSubjectContextExtensions.cs` | 1 | Handler registered with `AddSingleton<IHostedService>`, defect 4 |
| `src/Namotion.Interceptor.Hosting/HostedServiceHandler.cs` | 1, 2 | PR 1 adds `WaitForStartAsync`. PR 2 adds ownership tracking, factory invocation, disposal, the loop thread check and the lock discipline |
| `src/Namotion.Interceptor.Hosting/InterceptorHostingExtensions.cs` | 2 | Factory based API, attachment handles |
| `src/HomeBlaze/Namotion.Devices.{Shelly,MyStrom,Wallbox,Gpio,Ecowitt,Philips.Hue}/*ServiceCollectionExtensions.cs` | 1 | One identifier each |
| `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaClient.cs` | 2 | See below |
| `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaServer.cs` | 2 | See below |
| `src/Namotion.Interceptor.Hosting.Tests/*` | 1, 2 | `PublicApiGenerator` wiring and `AddSubject` cases in PR 1, attachment cases in PR 2 |
| `docs/hosting.md` | 1, 2 | PR 1 corrects the stale `AddHostedSubject` reference at `:145`. PR 2 restructures |
| `docs/subject-guidelines.md` | 1 | Rename, context supply no longer constructor dependent, restart contract |
| `.claude/commands/create-homeblaze-library.md`, `.claude/commands/migrate-homeblaze-library.md` | 1 | Follow the rename |

`Namotion.Devices.Gpio.Tests` and `Namotion.Devices.MyStrom.Tests` need no change. They call `AddGpio`
and `AddMyStromSwitch` and assert configured property values; neither mentions `AddHostedSubject`.

### The two HomeBlaze wrappers

The naive migration does not work and the plan must be explicit about why. `OpcUaClient.StartClientAsync`
builds a fresh `OpcUaDynamicSubject root`, assigns it to the `Root` property, builds a `configuration`,
and creates the source from that root (`OpcUaClient.cs:246-263`). A factory closing over those
(`() => root.CreateOpcUaClientSource(configuration, _logger)`) captures one particular start.

The wrapper is both a hosted subject and an attachment owner, and both are re-driven on a context
re-attach. What makes it work is that the wrapper keeps managing its attachment inside its own start and
stop path:

- `StartClientAsync` builds root and configuration, then attaches a factory closing over that pair.
- `StopClientAsync` detaches explicitly, which removes the attachment from the subject, and the manual
  dispose block plus its comment are deleted because the handler now disposes.
- On context detach the handler stops the wrapper. Its `ExecuteAsync` unwinds into `StopClientAsync`,
  whose detach now runs inline under the loop thread rule, so no attachment survives the detach.
- On re-attach the handler restarts the wrapper, `ExecuteAsync` runs `StartClientAsync` again, and a new
  root, configuration, factory and attachment are created together.

The stale factory and the double attachment both disappear, because the wrapper's attachment never
outlives one start and stop cycle. `OpcUaServer.cs:267,288` gets the same treatment with `targetSubject`
in place of `root`. The context detach rule that keeps attachments alive still applies, just not to
these two, which is the intended split: attach once and let the graph drive it, or manage the attachment
yourself.

## Documentation

`docs/hosting.md` is restructured around the ownership rule rather than around the API surface. It gains
a "which pattern when" section, which is the piece whose absence caused this investigation:

- **Construct and register directly** (`var car = new Car(context); services.AddSingleton(car);`) when
  the subject needs no DI injected constructor dependencies. This hands you the instance at
  configuration time, which the connector samples need for `context.AddService(root)`. Note that the
  container does not dispose an instance registered this way.
- **`AddSubject<T>()`** when the subject has constructor dependencies that only exist after
  `builder.Build()`.
- **Factory attachment** when a service should run for as long as a subject is in the graph, including
  the `() => existingInstance` warning.
- **Subject implements `BackgroundService`** when the subject's own purpose is to run a background loop,
  including the restart contract.

The samples are deliberately left unconverted. All six connector samples call `context.AddService(root)`,
which needs the instance at configuration time and is incompatible with construction deferred to host
start. Four `Program.cs` files build 20,000 children through `Root.CreateWithPersons(context)`, relying on
its default count. The sample subjects take only the context, so constructing them up front is the better
pattern and the docs now say so.

## Testing

New or rewritten cases in `Namotion.Interceptor.Hosting.Tests`:

**Part 1**

1. `AddSubject` on a `BackgroundService` subject whose generated context constructor exists starts it
   exactly once with hosting enabled. This is the probe that currently returns 2.
2. `AddSubject` on a subject with dependency injected constructor parameters and no generated context
   constructor still receives the context, and the handler starts it. This is defect 1b, and it fails
   today.
3. `AddSubject` on a `BackgroundService` subject with no hosting handler still starts and stops it.
4. `AddSubject` on a plain subject constructs and attaches it at host start.
5. `AddSubject` called twice for the same type registers one activation.
6. A subject whose `StartAsync` throws aborts host startup, preserving `AddHostedService` semantics.
7. Two contexts calling `WithHostedServices` on one `IServiceCollection` both get a running handler, and
   subjects in each start. This is defect 4.

**Part 2**

8. Attach, context detach, context re-attach produces a **different** instance, and the first is
   disposed.
9. Context detach disposes the instance exactly once.
10. Explicit detach stops, disposes, and removes the attachment, so a later context attach starts
    nothing.
11. Explicit detach called from inside the service's own stop path, on the handler loop thread,
    completes rather than deadlocking. This is the trace in Concurrency and reentrancy, and it must be
    written as a timed test that fails rather than hangs.
12. Explicit detach racing a queued create leaves no orphaned instance.
13. A factory that constructs a subject, and therefore re-enters the lifecycle lock, does not deadlock.
14. Host shutdown disposes factory created instances, and the handler does not dispose subjects. Asserted
    as "the handler did not dispose it", not "it was not disposed", because the container disposes
    `AddSubject` created singletons.
15. A factory that throws is logged, leaves `Current` null, and does not fault the handler loop.
16. An instance whose `StartAsync` faults is not left in the tracked set as if running.

**Part 3**

17. A subject implementing `IHostedService` is restarted on re-attach.
18. Re-parenting in add then remove order, where the reference count never reaches zero, neither stops
    nor restarts anything.

## Staging

Two pull requests, each release safe on its own and each carrying its own documentation updates.

**PR 1: registration and activation.** Replaces `AddHostedSubject` with `AddSubject`, adds
`SubjectActivation<T>` and `WaitForStartAsync`, fixes context supply for DI constructed subjects, fixes
defect 4, updates the six device extensions, adds the public API snapshot infrastructure and test,
updates `docs/subject-guidelines.md`, corrects the stale reference in `docs/hosting.md`, and updates the
two command files. Touches neither the attach and detach API nor the handler's tracking state, so it
stands alone as the fix for defects 1a, 1b and 4.

**PR 2: attachment and disposal.** Replaces the instance based attach and detach API, adds ownership
tracking, disposal, the loop thread rule and the lock discipline to `HostedServiceHandler`, migrates the
two HomeBlaze wrappers and deletes the manual dispose workaround, restructures `docs/hosting.md`. Fixes
defects 2 and 3.

## Out of scope

The 50 ms `Task.Delay` in `PostStartService` and `PostStopService` (`HostedServiceHandler.cs:199,218`),
carrying the comment "Fix small delay to let sync property assignments/deserialization complete".

It hides a real ordering hazard rather than being arbitrary. `new Car(context)` attaches the subject to
the context inside the generated constructor, before the constructor body assigns properties, so a
service started immediately can observe half initialised state. Removing the delay needs a "subject fully
constructed" signal, which is a separate design problem touching the generator.

The delay and its TODO stay. This spec does not claim to have addressed it. Two consequences are worth
stating rather than discovering later. Every attach and detach is serialised through one action loop at
50 ms each, so N subjects started through `AddSubject` add roughly N times 50 ms to host startup, now
awaited rather than fire and forget. And the inline detach path introduced above skips the delay
entirely, since it is already running behind one.
