# Single ownership for subject bound hosted services

## Problem

`Namotion.Interceptor.Hosting` offers two ways to bind an `IHostedService` to a subject, and neither
states who owns the start, the stop or the disposal. Three defects follow from that one gap.

### 1. `AddHostedSubject<T>` starts the subject twice

`HostedSubjectServiceCollectionExtensions.AddHostedSubject<T>` registers the subject as a singleton
and then registers the same instance as a hosted service:

```csharp
services.AddHostedService<T>(serviceProvider => serviceProvider.GetRequiredService<T>());
```

When `T` has a constructor accepting `IInterceptorSubjectContext` and that context was configured
with `WithHostedServices()`, construction attaches the subject to the context,
`HostedServiceHandler.HandleLifecycleChange` sees `change.Subject is IHostedService`
(`HostedServiceHandler.cs:30`) and starts it. The DI registration then starts it again. Measured with
a probe that counts `StartAsync` calls: **2**.

On a `BackgroundService` the second call overwrites `_stoppingCts` and assigns a second
`_executeTask`, so the first execution task is orphaned and can no longer be cancelled through the
public API.

Every `Add*Device` extension in `src/HomeBlaze/Namotion.Devices.*` forwards to `AddHostedSubject`, all
six device subjects extend `BackgroundService`, and HomeBlaze's own context calls `WithHostedServices`
(`SubjectContextFactory.cs:38`). The defect is latent only because nothing calls those extensions
outside their own unit tests. HomeBlaze builds devices through `SubjectFactory.CreateSubject` and
attaches them to the graph instead.

### 2. Detaching an attached service stops it but never disposes it

`HostedServiceHandler` calls `StopAsync` and drops the reference. Connectors own more than a
cancellation token: `OpcUaSubjectClientSource` holds a `SemaphoreSlim`, a session manager and a
lifecycle subscription released only in `DisposeAsync`.

`HomeBlaze.OpcUa.OpcUaClient` works around this by hand, with a six line comment explaining that
skipping it leaks one semaphore and one subscription per start and stop cycle
(`OpcUaClient.cs:292-307`). `HomeBlaze.OpcUa.OpcUaServer` runs the same start and stop dance and omits
the dispose entirely (`OpcUaServer.cs:295-302`). That omission is currently harmless, because
`OpcUaSubjectServer` is `IDisposable` only through `BackgroundService` and `StopAsync` has already
cancelled its token source, but the wrapper author has no way to know that without reading the
connector. `MqttSubjectClientSource` and `MqttSubjectServer` are both `IAsyncDisposable`, so the next
wrapper walks into the trap.

Two copies of the same pattern already disagree on precisely its subtlest step.

### 3. A detached subject cannot bring its services back

`IsContextAttach` is `isFirstAttach` and `IsContextDetach` is `isLastDetach`
(`LifecycleInterceptor.cs:133,250`), refcounted across parents. Moving a subject with
`parentA.Child = null` followed by `parentB.Child = child` therefore fires a full context detach and a
context attach. Today the detach branch removes every attachment from the subject's data bag, so the
re-attach starts nothing and the failure is silent.

This cannot be fixed by restarting the stopped instance. `IHostedService` is single use by contract,
and `OpcUaSubjectClientSource` in particular cannot restart: it guards disposal with an `Interlocked`
flag and builds its session manager once per listen cycle.

## Goals

1. Exactly one component decides when a subject bound hosted service starts and stops.
2. Whatever creates an instance disposes it, and nothing disposes an instance it did not create.
3. A subject that leaves and re-enters the graph gets working hosted services again.
4. Subjects with DI injected constructor dependencies can be registered and activated without the
   caller having to reason about start ownership.
5. Breaking changes surface as compile errors, never as changed runtime behaviour under an unchanged
   signature.

## Non-goals

- Removing the 50 ms `Task.Delay` in `PostStartService` and `PostStopService`. See Out of scope.
- Converting the samples to the new registration helper. See Documentation.
- Changing `SubjectSourceBase` or any connector implementation.

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
at shutdown like any other singleton. A subject constructed by the caller belongs to the caller. Either
way the handler stops it and leaves disposal alone, which is what makes a graph move non destructive.

### Part 1: `AddSubject<T>` replaces `AddHostedSubject<T>`

```csharp
public static IServiceCollection AddSubject<T>(
    this IServiceCollection services,
    Action<T>? configure = null,
    Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
    where T : class, IInterceptorSubject
```

Registration is unchanged from `AddHostedSubject`: `TryAddSingleton` through
`ActivatorUtilities.CreateInstance`, the context passed to the constructor when one accepts
`IInterceptorSubjectContext`, `configure` applied to the created instance.

What changes is the second half. Instead of `AddHostedService<T>`, `AddSubject` registers
`SubjectActivation<T>`, an internal hosted service whose only job is to resolve `T`:

```csharp
internal sealed class SubjectActivation<T> : IHostedService
    where T : class, IInterceptorSubject
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subject = _serviceProvider.GetRequiredService<T>();
        return _subject is IHostedService hostedService && !HasHandler(_subject)
            ? hostedService.StartAsync(cancellationToken)
            : Task.CompletedTask;
    }
}
```

Resolving the singleton constructs it, construction attaches it to the context, and the context's
handler starts it if it is an `IHostedService`. Start ownership is therefore single: the handler.

`HasHandler(subject)` is `subject.Context.TryGetService<HostedServiceHandler>() is not null`. When
there is no handler, either because the caller never called `WithHostedServices()` or because
`contextResolver` deliberately returned null, the activator starts and stops the subject itself. This
keeps plain DI working and, more importantly, makes the no-hosting case behave rather than fail
silently. `StopAsync` mirrors `StartAsync` and stops the subject only in that same no-handler case.

Supporting details:

- **Idempotency.** `AddHostedService<SubjectActivation<T>>` goes through `TryAddEnumerable`, which
  dedupes on the implementation type, so registering the same subject twice stays a no-op. This
  matches the existing `TryAddSingleton<T>` behaviour.
- **Shutdown order.** `WithHostedServices(services)` registers the handler eagerly during context
  construction, before any `AddSubject` call. The host stops hosted services in reverse registration
  order, so activators stop before the handler, and the handler stops everything it owns last.
- **Startup order does not matter.** `HostedServiceHandler` posts start actions to a `BufferBlock`
  that drains once its own `StartAsync` has run, so a subject constructed before the handler starts
  still gets started.
- **Constraint change.** The generic constraint moves from `IHostedService` to `IInterceptorSubject`,
  so `AddSubject` also serves plain subjects that only need to exist and be attached at startup.

The rename is not cosmetic. Keeping `AddHostedSubject` with the new semantics would change start
ownership under an unchanged signature, which no caller can see. A rename turns that into a build
failure.

Why this belongs in the hosting package despite the registration half being pure DI: the registration
half is inert on its own. `ShellyDevice(IHttpClientFactory httpClientFactory, ILogger<ShellyDevice> logger)`
(`ShellyDevice.cs:148`) cannot be constructed before `builder.Build()`, so construction must be
deferred into the container, and the only hook the generic host offers for forcing that construction
at startup is `IHostedService`.

### Part 2: attachment becomes factory only

```csharp
public interface IHostedServiceAttachment
{
    IHostedService? Current { get; }
}

public interface IHostedServiceAttachment<out T> : IHostedServiceAttachment
    where T : IHostedService
{
    new T? Current { get; }
}
```

```csharp
IHostedServiceAttachment<T> AttachHostedService<T>(
    this IInterceptorSubject subject, Func<T> factory)
    where T : IHostedService;

Task<IHostedServiceAttachment<T>> AttachHostedServiceAsync<T>(
    this IInterceptorSubject subject, Func<T> factory, CancellationToken cancellationToken)
    where T : IHostedService;

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
requirement rather than a convenience: `OpcUaClient.UpdateDiagnostics` polls `source.Diagnostics`
every ten seconds and needs whatever instance is running now.

Semantics:

| Event | Effect |
|---|---|
| Attach while the subject is outside a hosting enabled context | The factory is stored on the subject. Nothing runs. `Current` is null |
| Attach while the subject is inside one | The handler invokes the factory and starts the instance |
| Context attach | For each stored attachment, invoke the factory and start the instance |
| Context detach | For each attachment, stop the instance, dispose it, set `Current` to null. **The attachment stays on the subject** |
| Explicit detach | Stop, dispose, set `Current` to null, and remove the attachment from the subject |
| Host shutdown | `HostedServiceHandler.StopAsync` stops and disposes every instance it created |

Keeping the attachment on the subject across a context detach is the change that makes goal 3 work.
The factory survives, so the next context attach produces a fresh instance and no restart contract is
needed.

Further details:

- **Where the factory runs.** Inside the posted start action on the handler's action loop, not at
  attach time. `AttachHostedServiceAsync` therefore awaits creation and start together, and `Current`
  is non null once it returns. A factory that throws is logged and leaves `Current` null; the async
  overload propagates the exception through its `TaskCompletionSource`.
- **Dispose policy.** `IAsyncDisposable` is preferred, `IDisposable` is the fallback, and an instance
  that is neither is simply dropped.
- **Dispose errors are logged, never thrown.** Context detach runs inside a property write, so an
  exception there would surface at an unrelated assignment.
- **Each attach call yields a distinct attachment.** There is no instance based deduplication to
  preserve, so attaching the same factory twice produces two independently managed instances.
- **Detach returns true** when the attachment belonged to the subject and was removed, false
  otherwise, including when the attachment belongs to a different subject.
- **`() => existingInstance` is the one shape that defeats the design**, because a re-attach would
  restart an instance the handler has already stopped and disposed. Removing the instance based
  overloads makes every old call site a compile error, and this closure is the tempting way to silence
  it, so `docs/hosting.md` calls it out explicitly. The factory must construct.
- **Storage.** Attachments live in the subject's `Data` bag under the existing key, as
  `ImmutableArray<IHostedServiceAttachment>`. `Current` is mutated by the handler under its lock and
  published for readers with a volatile write.

`HostedServiceHandler` replaces its flat `HashSet<IHostedService>` with state that distinguishes
handler created instances from subjects, because the disposal policy differs between them.

### Part 3: subject as hosted service

Unchanged in shape, sharpened in contract. A subject implementing `IHostedService` is started on
first context attach, stopped on last context detach, never disposed because the handler did not
create it, and restarted on re-attach. `ExecuteAsync` must therefore tolerate being run more than
once. This is the documented contract for such subjects.

Implementation note: `BackgroundService.StartAsync` overwrites `_stoppingCts` without disposing the
previous one, so a restart leaks a linked token registration against whatever token is passed in.
The handler passes `CancellationToken.None` to `StartAsync`, which registers nothing because
`CancellationToken.None` cannot be cancelled. The handler's own loop token continues to govern the
posted action.

## Public API changes

Removed from `Namotion.Interceptor.Hosting`:

- `HostedSubjectServiceCollectionExtensions.AddHostedSubject<T>`
- `InterceptorHostingExtensions.AttachHostedService(IInterceptorSubject, IHostedService)`
- `InterceptorHostingExtensions.AttachHostedServiceAsync(IInterceptorSubject, IHostedService, CancellationToken)`
- `InterceptorHostingExtensions.DetachHostedService(IInterceptorSubject, IHostedService)`
- `InterceptorHostingExtensions.DetachHostedServiceAsync(IInterceptorSubject, IHostedService, CancellationToken)`
- `InterceptorHostingExtensions.GetAttachedHostedServices`

Added: `AddSubject<T>`, `IHostedServiceAttachment`, `IHostedServiceAttachment<T>`, the four factory
based attach and detach methods, and `GetHostedServiceAttachments`.

A `VerifyChecksTests.PublicApi` snapshot test is added for `Namotion.Interceptor.Hosting` in PR 1, so
the surface is tracked from the first change onward.

## Migration

| File | Change |
|---|---|
| `src/Namotion.Interceptor.Hosting/HostedSubjectServiceCollectionExtensions.cs` | `AddHostedSubject` becomes `AddSubject`, registers `SubjectActivation<T>` |
| `src/Namotion.Interceptor.Hosting/SubjectActivation.cs` | New |
| `src/Namotion.Interceptor.Hosting/InterceptorHostingExtensions.cs` | Factory based API, attachment handles |
| `src/Namotion.Interceptor.Hosting/HostedServiceHandler.cs` | Ownership tracking, factory invocation, disposal |
| `src/HomeBlaze/Namotion.Devices.{Shelly,MyStrom,Wallbox,Gpio,Ecowitt,Philips.Hue}/*ServiceCollectionExtensions.cs` | One identifier each |
| `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaClient.cs` | Factory attach, attachment handle replaces `_clientSource`, manual dispose block deleted |
| `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaServer.cs` | Factory attach, attachment handle replaces `_serverService` |
| `src/Namotion.Interceptor.Hosting.Tests/*` | Rewritten against the new API, plus new cases below |
| `src/HomeBlaze/Namotion.Devices.{Gpio,MyStrom}.Tests/*ServiceCollectionExtensionsTests.cs` | Follow the rename |
| `docs/hosting.md`, `docs/subject-guidelines.md` | Rewritten sections |
| `.claude/commands/create-homeblaze-library.md`, `.claude/commands/migrate-homeblaze-library.md` | Follow the rename |

## Documentation

`docs/hosting.md` is restructured around the ownership rule rather than around the API surface. It
gains a "which pattern when" section, which is the piece whose absence caused this investigation:

- **Construct and register directly** (`var car = new Car(context); services.AddSingleton(car);`) when
  the subject needs no DI injected constructor dependencies. This hands you the instance at
  configuration time, which the connector samples need for `context.AddService(root)`.
- **`AddSubject<T>()`** when the subject has constructor dependencies that only exist after
  `builder.Build()`.
- **Factory attachment** when a service should run for as long as a subject is in the graph.
- **Subject implements `BackgroundService`** when the subject's own purpose is to run a background
  loop.

The samples are deliberately left unconverted. All six connector samples call `context.AddService(root)`,
which needs the instance at configuration time and is incompatible with construction deferred to host
start, and the three server samples build 20,000 children through the static factory
`Root.CreateWithPersons(context, 20_000)`. Their subjects take only the context, so constructing them
up front is the better pattern and the docs now say so.

`docs/subject-guidelines.md` updates its "Implementing Hosted Subjects for DI" section for the rename
and states the restart contract for subjects that implement `IHostedService`.

## Testing

New or rewritten cases in `Namotion.Interceptor.Hosting.Tests`:

1. `AddSubject` on a `BackgroundService` subject with hosting enabled starts it exactly once. This is
   the probe that currently returns 2.
2. `AddSubject` on a `BackgroundService` subject with no hosting handler still starts and stops it.
3. `AddSubject` on a plain subject constructs and attaches it at host start.
4. `AddSubject` called twice for the same type registers one activation.
5. Attach, context detach, context re-attach produces a **different** instance, and the first is
   disposed.
6. Context detach disposes the instance exactly once.
7. Explicit detach stops, disposes, and removes the attachment, so a later context attach starts
   nothing.
8. Host shutdown disposes factory created instances and leaves subjects undisposed.
9. A subject implementing `IHostedService` is restarted on re-attach.
10. Re-parenting in add then remove order, where the reference count never reaches zero, neither
    stops nor restarts anything.
11. A factory that throws is logged, leaves `Current` null, and does not fault the handler loop.

## Staging

Two pull requests, each release safe on its own and each carrying its own documentation updates.

**PR 1: `AddSubject` and the activator.** Replaces `AddHostedSubject`, adds `SubjectActivation<T>`,
updates the six device extensions and their tests, adds the public API snapshot test for the package,
updates `docs/subject-guidelines.md` and the two skill files. Stands alone as the double start fix.

**PR 2: factory attachment and disposal.** Replaces the instance based attach and detach API, adds
ownership tracking and disposal to `HostedServiceHandler`, migrates the two HomeBlaze wrappers and
deletes the manual dispose workaround, rewrites `docs/hosting.md`.

## Out of scope

The 50 ms `Task.Delay` in `PostStartService` and `PostStopService`
(`HostedServiceHandler.cs:199,218`), carrying the comment "Fix small delay to let sync property
assignments/deserialization complete".

It hides a real ordering hazard rather than being arbitrary. `new Car(context)` attaches the subject
to the context inside the generated constructor, before the constructor body assigns properties, so a
service started immediately can observe half initialised state. Removing the delay needs a "subject
fully constructed" signal, which is a separate design problem touching the generator.

The delay and its TODO stay. This spec does not claim to have addressed it, and the cost is that every
attach and detach is serialised through one action loop at 50 ms each.
