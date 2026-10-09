# Hosting

The `Namotion.Interceptor.Hosting` package binds `IHostedService` implementations to interceptor subjects and drives them from the .NET Generic Host (`Microsoft.Extensions.Hosting`). It works with any host based application: ASP.NET Core, worker services, console apps.

## The Rule

**A hosted service runs exactly while its subject is in the graph. The `HostedServiceHandler` on the subject's context is the only thing that starts or stops it, and it disposes exactly what it created.**

Everything else on this page follows from that one sentence:

- A subject entering the graph starts its services, and a subject leaving the graph stops them. A subject that re-enters gets them back.
- The handler creates every factory attachment instance, so it disposes every factory attachment instance.
- The handler never creates a subject, so it never disposes one. It starts and stops a subject that implements `IHostedService` and leaves disposal to whoever constructed it: the dependency injection container for a subject registered through `AddSubject<T>()`, you for a subject you constructed yourself. That is what makes moving a subject through the graph non destructive.

Nothing else may start these services. In particular, do not also register a subject the handler manages with `AddHostedService<T>()`, because that is a second owner and a second start.

> **Internal design:** For the concurrency model behind this, the ordering guarantees and the deadlock shapes that are accepted rather than guarded, see [Hosted Service Ownership](design/hosting-service-ownership.md).

## Setup

Configure hosting support on the context and register it with the host:

```csharp
var builder = Host.CreateApplicationBuilder();

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithHostedServices(builder.Services);

var host = builder.Build();
await host.StartAsync();
```

`WithHostedServices()` creates the `HostedServiceHandler` for this context and registers it with the host, so the handler opens for business when the host starts and drains when the host stops. It also enables `WithLifecycle()`, which raises the context attach and detach events the handler listens to. Each context gets its own handler, so two contexts sharing one `IServiceCollection` both work, and a subject reachable from two hosting enabled contexts is still started once.

Hosting below the root needs context inheritance. `WithFullPropertyTracking()` includes `WithContextInheritance()`. If you compose the context by hand, add `WithContextInheritance()` yourself.

Without it, a subject one level below the root still starts, because the graph write that attaches it invokes the handler through the parent's context. Two things break instead, and both are silent:

- **The descent stops at level one.** Inheritance is what gives a child the parent's context, and it is that assignment which walks the child's own children into the graph. Without it nothing below the first level is ever attached, so nothing below the first level is ever started.
- **Attaching to a subject already in the graph resolves no handler.** `AttachHostedService` looks the handler up on `subject.Context`. A child that never inherited the parent's context resolves nothing there, so the factory is stored and no instance is created.

Starts and stops queued before the host starts run once it does, or once an [awaited attach or detach](#factory-attachment) opens the handler earlier. Each managed service has its own queue, so its own starts and stops never overlap, while unrelated services run concurrently. The one ordering guarantee across services is the one that matters for cleanup: when a subject leaves the graph, its own stop runs before the stops of the services attached to it, and the activated service of an `ISubjectHostedServiceFactory` subject stops before the other services attached to that subject. The second holds only while the activation is attached: once it has been detached explicitly, the other services no longer wait for it.

## Which Pattern When

| You have | Use |
|---|---|
| A subject with no constructor dependencies, and you want the instance during configuration | Construct it and register the instance |
| A subject whose constructor dependencies only exist after `builder.Build()`, or a device or other subject with a service to run from dependency injection, in a private context by default | `services.AddSubject<T>()` |
| A service that should run for as long as a subject is in the graph | Factory attachment |
| A subject whose own purpose is a background loop | Let the subject implement `BackgroundService` |
| A subject whose service should run only where it is the real one, not where it is mirrored | Implement `ISubjectHostedServiceFactory` (see [A Subject With a Service](#a-subject-with-a-service)) |

### Construct and register directly

When a subject needs nothing from the container beyond its context, construct it during configuration. This is what the connector samples do, because they need the instance itself before the host is built:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithHostedServices(builder.Services);

var root = Root.CreateWithPersons(context);
context.AddService(root);

builder.Services.AddSingleton(root);
```

Constructing with the context attaches the subject there and then, so the handler already owns whatever that subject brings with it and starts it when the host starts.

The container does not dispose an instance registered with `AddSingleton(instance)`. Disposal stays with you.

### `AddSubject<T>()`

Use `AddSubject<T>()` when the subject's constructor needs services that only exist after `builder.Build()`, such as `IHttpClientFactory` or `ILogger<T>`:

```csharp
using Namotion.Interceptor.Hosting;

builder.Services.AddSubject<WeatherStation>(station =>
{
    station.PollingInterval = TimeSpan.FromSeconds(5);
});
```

It registers `T` as a singleton, forces its construction at host start and runs it: a subject that implements `IHostedService` is started, and a subject that implements `ISubjectHostedServiceFactory` is [activated](#a-subject-with-a-service) with the application's service provider. Host startup waits for those starts and fails if one throws, the way `AddHostedService<T>` does. A plain subject is only constructed and attached. Where the subject runs depends on `contextResolver`:

- **Without a resolver** it runs in a private context, with property tracking, lifecycle, hosting and whatever the subject adds with [`IPrivateContextConfigurator`](#running-one-without-a-host), and ignores any context registered in the container. Host shutdown stops it and detaches it from that context.
- **With a resolver** it joins the resolved context, whose handler runs it. Host startup throws when that context has no hosting, because `WithHostedServices()` was never called on it, while the subject has something to run.

A subject in a private context belongs to that context alone. Do not place it in another tracked or hosting graph, for example by assigning it to a property of a subject in the application's context. The assignment does not throw, but the subject then reaches two lifecycle interceptors and two hosting handlers, so every single-service lookup on it and its children, such as `AttachHostedService`, throws far from the cause. What does throw is an instance that is already in a tracked graph when it joins its private context: for an instance `AddSubject` constructs, that is when it is first resolved, and for one you registered yourself, at host startup. To share the application's context instead, as `AddHostedSubject<T>()` did, pass `contextResolver: serviceProvider => serviceProvider.GetRequiredService<IInterceptorSubjectContext>()`.

Either way the context is applied after construction whether or not `T` declares a constructor taking an `IInterceptorSubjectContext`, so a subject with only injected dependencies is attached just the same.

`AddKeyedSubject<T>(key)` registers one of several instances of a type as a keyed singleton, with the same two modes. Without a resolver each key runs in a private context:

```csharp
builder.Services.AddKeyedSubject<WeatherStation>("roof", station => station.PollingInterval = TimeSpan.FromSeconds(5));
builder.Services.AddKeyedSubject<WeatherStation>("garden");
```

Two sharp edges:

- One registration per type, or per type and key. A second registration of the same `T` and key throws, because its `configure` and `contextResolver` could not take effect.
- If you already registered `T` yourself, `AddSubject<T>()` applies neither the context nor `configure` to that instance. The hosting graph the instance is already in runs it. Otherwise, without a resolver, it runs in a private context when it is in no graph, and host startup throws when it is in a tracked graph. With a resolver, host startup throws when it is a hosted service or an `ISubjectHostedServiceFactory` and leaves a plain subject alone.

`configure` always runs before the attach `AddSubject` performs, so the subject is fully configured before anything can start it. Without a resolver the subject is constructed and configured before it joins any context, so on every constructor shape the assignments in `configure` are not intercepted and not tracked. With a resolver, construction and `configure` both run inside a [start deferral](#configuration-before-startup) on the resolved context, and what differs between the shapes is whether those assignments are intercepted:

- **`T` has no constructor taking a context**, or it declares the documented `MySubject(IInterceptorSubjectContext? context = null)` parameter and never attaches with it. Nothing is attached while `configure` runs, so its assignments are not intercepted and not tracked.
- **Construction attaches the subject**, which is what the generated context constructor does. `configure` runs against an attached subject, so its assignments are intercepted and tracked.

#### What it costs at host startup

`AddSubject<T>()` registers one hosted activation per registration, and when `T` is a hosted service or has one, that activation waits for the start before host startup moves on. The generic host starts hosted services one after another by default, so those waits do not overlap and the cost is linear in the number of such registrations, at whatever each start takes. A registered type that is a plain subject waits for no start and adds nothing.

Let the host start its services concurrently to get the waits overlapping:

```csharp
builder.Services.Configure<HostOptions>(options => options.ServicesStartConcurrently = true);
```

`AddSubject<T>()` deliberately does not set this for you. The option is host wide, so it changes the startup of every hosted service in the application, including ones registered by libraries that know nothing about this package, and that decision belongs to whoever owns the host.

Subjects that reach the graph as part of an object tree do not pay this cost at all. Their starts are queued independently, one queue per service, and run concurrently, so 50 subjects attached together cost about as much as one.

### A service bound to a subject

When a service is not the subject itself but should run for as long as a subject is in the graph, attach a factory to that subject. See [Factory Attachment](#factory-attachment) below.

### A subject that is its own background loop

When the subject's whole purpose is a background loop over its own properties, let it extend `BackgroundService`. See [Subject as Hosted Service](#subject-as-hosted-service) below.

### A subject that has a service

When the same subject type can be the real thing in one place and a copy mirrored from elsewhere in another, give it a service instead of making it one. See [A Subject With a Service](#a-subject-with-a-service) below.

## Factory Attachment

Attach a factory to a subject and the handler runs an instance of it for exactly as long as the subject is in the graph:

```csharp
public class PersonBackgroundService : BackgroundService
{
    private readonly Person _person;

    public PersonBackgroundService(Person person)
    {
        _person = person;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _person.FirstName = "John";
        _person.LastName = "Doe";

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }
}

// Usage
var person = new Person(context);
var attachment = person.AttachHostedService(() => new PersonBackgroundService(person));
```

`AttachHostedService` stores the factory on the subject and returns a handle. When the subject is already inside a hosting enabled graph, the handler takes ownership and queues a create and start. When it is not, the factory is stored and nothing runs until the subject enters one. Each call yields its own attachment, so attaching the same factory twice gives two independently managed instances.

### The factory must construct

`() => existingInstance` is the one shape that defeats the design. The handler [stops and disposes what it created](#detach-stops-and-disposes-and-keeps-the-attachment) when the subject leaves the graph and invokes the factory again when the subject comes back, so a factory that hands out a captured instance would restart something it has already stopped.

```csharp
// Correct: a fresh instance every time the handler needs one.
subject.AttachHostedService(() => new DataSyncService(subject));

// Wrong: refused on the second start. The handler stopped this instance on detach.
var service = new DataSyncService(subject);
subject.AttachHostedService(() => service);
```

The second shape is caught rather than left to fail obscurely. The handler compares each instance the factory produces against the previous one and, on a repeat, refuses the start before calling `StartAsync`: `Current` stays null and an `InvalidOperationException` explaining the rule lands on `attachment.Fault`.

The refusal is wider than the damage, and deliberately so. A repeat is refused whatever the instance is, including a hosted service that implements neither disposable interface and was therefore never disposed and would in fact restart cleanly. Constructing on every call is a rule about the attachment; a rule that held for some service types and not others would be worse than one that fails closed.

Only the repeat is refused, so the first start of `() => service` succeeds and a subject that never leaves and re-enters the graph is never told anything is wrong. The check is also one reference comparison against the last instance, which catches the immediate repeat and nothing more. A pooling factory that alternates between two instances still hands back a stopped one, and that is not detected.

The factory runs inside the handler's transition, outside every lock, so it can read live state rather than a snapshot taken at attach time. It is deliberately narrow: `Func<T>`, no cancellation token, no service provider, not async.

### Do not detach from your own stop path

**A hosted service must not detach an attachment from inside its own stop path.** That includes anything reached through its `StopAsync`, and for a `BackgroundService` it includes the tail of `ExecuteAsync` as it unwinds.

When a subject leaves the graph, the handler stops the subject first, then its activated service, and holds each of its other attachments' stops behind that, so an attachment is never disposed underneath a subject or service that is still unwinding. A stop that waits for a detach of one of those attachments therefore waits for itself.

Nothing resolves that. The wedged queue never drains, the instance is never stopped and never disposed, and every later start or stop for the same service queues behind it for the rest of the process. Shutdown is the one thing the wedge cannot hold: the handler stops waiting for its outstanding stops when the host's `ShutdownTimeout` expires, so `StopAsync` returns even though the wedged service is still sitting there. That bounds the process, not the damage.

Detaching from an operation, from a configuration change, or from any path not reached through the service's own stop is fine, with two exceptions. A subject, or its activated service, must not await a detach of an attachment on that subject from its own `StartAsync`. If shutdown begins while that start is still running, the detach queues behind shutdown's stop for the attachment, which waits for the stop of the subject or of its activated service, which waits for that start. The same holds for a `BackgroundService`, the subject itself or its activated service, whose `ExecuteAsync` awaits a detach of such an attachment with `CancellationToken.None` as shutdown begins: its stop waits for `ExecuteAsync`, so the detach parks until the host's shutdown deadline. Pass the stopping token to that detach, which the stop cancels first, or use the synchronous `DetachHostedService`. Nothing detects the bad shape, so it is a rule rather than a guard. Both OPC UA wrappers in this repository had it and were changed, so it is a shape that gets written rather than a hypothetical one. `HostedServiceHandlerTests.WhenASubjectOwningAnAttachmentIsStoppedByTheHost_ThenShutdownCompletesWellInsideTheTimeout` is the regression guard.

### Keep the dispose path out of the lifecycle lock

The handler disposes what the factory built, from a transition that can run while a detach cascade still holds the lifecycle lock. A service disposed this way must therefore obey two rules:

- its dispose path must not enter the lifecycle lock, directly or transitively
- it must not block on a lock that its own `SubjectDetaching` handler acquires

Writing a scalar property from a dispose path is safe. Writing a property whose type can contain subjects takes the lifecycle lock and is not safe; attaching or detaching a subject enters the same lock without being a property write at all; and so does attaching a hosted service, which takes it to settle whether the subject is still in the graph. That last one is the likeliest of the three to appear on a hosted service's own teardown path. Nothing enforces this and no test covers it, which is exactly why it is written down here. The lock order that makes it a deadlock rather than a slow path is in [Disposal from a handler transition](design/hosting-service-ownership.md#disposal-from-a-handler-transition).

### Reading the outcome

The handle carries the state of the attachment:

- `Current` is the running instance, or null when nothing is running: before the first start, after a stop, and after a start that failed. The awaiting overload can return this way too, with no fault, when there was no start to wait for: no handler on the context, the subject not in the graph, the attachment already detached, or the host shutting down.
- `Fault` is the exception from the last failed transition, or null. Only a start clears it, and only once it has got past its own guards, so that a start skipped by a shutdown does not drop a fault nobody has read yet. A stop never clears it. A start that failed followed by a clean stop therefore leaves `Fault` set with `Current` null, which is the shape of "this should be running and is not". An execution fault ends in the same shape: a `BackgroundService` whose `ExecuteAsync` faults after its `StartAsync` returned has the fault recorded, is stopped and, when the handler created it, disposed. An `OperationCanceledException` escaping `ExecuteAsync` that no stop caused, such as an `HttpClient` timeout, counts as a fault too, because the service is just as dead. `BackgroundService.StartAsync` schedules the execution and returns at once, so an `ExecuteAsync` that throws before its first await is such a run fault rather than a failed start, and an awaited attach returns the running attachment, which settles to `Faulted` afterwards. That holds unless the service's `StartAsync` runs `ExecuteAsync` inline, as `SubjectConnectorBase` does: the part before the first await is then part of the start, so a throw there is a start fault, and an awaited attach throws and removes the attachment. An execution that runs to completion is not a fault and leaves the attachment `Running`.
- `GetState(out var current)` says what the attachment is doing, and hands back the instance that reading was derived from. The state is what tells apart the situations a null `Current` covers: `Stopped` (nothing is running), `Starting` (the handler is creating and starting an instance), `Running`, `Stopping` (the instance has left `Current` and is still stopping or being disposed), `Removed` (detached, or an awaited attach faulted, so no start enqueued after that point is accepted) and `Faulted` (the last attempt failed, and the next one may still succeed). `Stopped` against `Removed` is the distinction to act on: recoverable against terminal. The state is not exposed on its own, so that a caller cannot pair a state with an instance read a moment apart from it. Neither member is a promise about the next start, and no state is the state of a declined one. A start the handler declined, whether because the host is shutting down, because the subject is no longer in the graph, because another handler owns it or because an instance is already running, leaves the attachment reading exactly what it read before, because every one of those refusals returns before the start clears the fault. A start already queued when a detach marked the attachment still runs and is then stopped, so `Removed` can be followed by a brief `Starting`, `Running` and `Stopping` before it settles back.

**A factory with observable side effects must not assume `Current` becomes non null before anything else can observe them.** The factory runs inside the transition and the instance is recorded only after it has returned and started, so whatever the factory publishes into the subject is already visible while `Current` is still null. That is what `GetState` is for: a consumer reconciling its own view against the handle reads `Starting` beside a null instance and leaves the fresh state alone, where `Current is null` on its own would tell it to discard what the factory has just published.

```csharp
if (attachment.Fault is { } fault)
{
    logger.LogError(fault, "The attached service is not running.");
}

// One reading rather than State and Current separately, so the two cannot disagree.
var state = attachment.GetState(out var service);

if (service is not null)
{
    // Running.
}
else if (state is HostedServiceAttachmentState.Starting)
{
    // An instance is on its way, and the factory may already have published into the subject,
    // so whatever is there is fresh rather than stale.
}
else
{
    // Nothing is running and nothing is being created.
}
```

Read the fault first and the state after it, and take the state immediately before the decision it informs, not once at the top of a longer method. A start clears the fault before it enters its start window, so a state read taken above the fault can be settled for a fault that is already being retried. Anything between the reading and the decision, an intercepted property write above all, is long enough for a start to publish a whole tree into the gap, and a poll has no synchronization with the start it races.

`AttachHostedService` and `DetachHostedService` return once the transition has been queued rather than run, so neither result means "started" or "stopped". Queueing is not instant on the attach side: it takes the lifecycle lock to record liveness, so it blocks for as long as any graph move already holding that lock takes. Do not call it while holding a lock that a lifecycle handler could need, and do not call it from a hosted service's own dispose path. `Current`, `Fault` and `GetState` are how the outcome is observed. `DetachHostedService` returns false when the attachment was not on the subject, which is what a second detach of the same handle gets. The awaitable overloads wait for the transition instead:

```csharp
var attachment = await person.AttachHostedServiceAsync(
    () => new PersonBackgroundService(person), cancellationToken);
// The instance is running, or this call threw, or nothing was started and Current is null.

await person.DetachHostedServiceAsync(attachment, cancellationToken);
// The instance has stopped, and has been disposed when it is disposable.
```

`AttachHostedServiceAsync` is transactional for its own transition: when that start faults, the attachment is removed before the exception propagates, so a `catch` block is never left owning an invisible attachment. That holds only for a caller that was still waiting: a wait cancelled before the start faulted throws on the token, and a start that faults afterwards leaves the attachment on the subject with `Current` null and `Fault` set. When a context attach had already queued a create for the same attachment, the caller awaits the second transition rather than the first. A graph driven start that faults keeps the attachment with `Current` null and `Fault` set, so the next context attach retries it. So does an execution that faults after its start returned, whichever attach path started it: only the start's own outcome makes the awaited attach throw.

Both awaited overloads open the handler if the host has not started yet, since awaiting is an explicit request for the service to be running, and that releases every start already queued on the context, not only the one awaited. The fire and forget overloads and graph driven starts wait for the host.

The two overloads treat the cancellation token differently. On the attach side it bounds the wait, not the work: a cancelled await leaves the start running to completion, so a caller that gives up waiting still ends with a started instance rather than a half started one. On the detach side it reaches the work: the token is handed to the instance's own `StopAsync`, as `IHostedService` defines it, so a cancelled token cuts the graceful stop short, and the instance is disposed either way.

`GetHostedServiceAttachments()` returns an immutable snapshot of a subject's attachments.

### Detach stops and disposes, and keeps the attachment

Two different things are called detaching, and they differ in exactly one respect:

- **The subject leaves the graph.** The handler takes the instance out of `Current`, stops it and disposes it, and **keeps the attachment on the subject**. The factory survives, so the next time the subject enters a hosting enabled graph the handler invokes it again and a fresh instance runs. This is what makes moving a subject through the graph work.
- **`DetachHostedService` or `DetachHostedServiceAsync`.** The same stop and dispose, and the attachment is removed from the subject as well, so a later context attach starts nothing.

Disposal prefers `IAsyncDisposable` and falls back to `IDisposable`, and a service that implements neither is simply dropped after its stop. A dispose that throws is logged and never propagated, because the disposal can run from inside a property write that has nothing to do with the service.

```csharp
var parent = new Parent(context);
var child = new Child();
child.AttachHostedService(() => new ChildMonitorService(child));

parent.Child = child;   // child enters the graph, an instance is created and started
parent.Child = null;    // that instance is stopped and disposed, the attachment stays
parent.Child = child;   // the factory runs again, a different instance is now running
```

Host shutdown does the same thing as a context detach for everything the handler owns, with the host's stopping token. The token bounds two things: each instance's own `StopAsync` receives it, and the handler stops waiting for its outstanding stops once it expires. So shutdown returns at `ShutdownTimeout` whatever the services do. It does not bound the services themselves. One that ignores its token keeps running after the host has stopped, is never disposed, and, if it was created by a factory attachment, is unreachable by then.

## Subject as Hosted Service

A subject can implement `IHostedService` itself, usually by extending `BackgroundService`. The handler starts it on the first context attach, stops it on the last context detach, and never disposes it.

```csharp
[InterceptorSubject]
public partial class SensorMonitor : BackgroundService
{
    public partial double Temperature { get; set; }
    public partial double Humidity { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Temperature = ReadTemperatureSensor();
            Humidity = ReadHumiditySensor();

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}

// Usage
var monitor = new SensorMonitor(context);
// Starts when the subject is in the graph, stops when it leaves or the host stops.
```

This pattern fits when the subject's entire purpose is to run a background task that updates its own properties. Two contract requirements come with it:

**`ExecuteAsync` must tolerate being run more than once.** A subject that leaves the graph and re-enters it is restarted in place, on the same instance, because the handler does not dispose subjects. A plain `BackgroundService` handles this. Anything that latches a "done" or "disposed" flag does not, so reset per run state at the top of `ExecuteAsync` rather than in the constructor.

**A hand written `IHostedService` must honour `StopAsync`.** The handler passes `CancellationToken.None` to `StartAsync`, so a service that captured the `StartAsync` token as its only stop signal will never be cancelled. `BackgroundService` is unaffected, because it cancels its own execution token in `StopAsync`.

A run that faults, or that is cancelled by anything but its stop, stops the subject. The handler records the exception, stops the subject and leaves it stopped until the next context attach, which restarts it in place and clears the fault. This differs from the generic host, which stops the whole application when a `BackgroundService` it hosts faults.

## A Subject With a Service

A subject that is a hosted service runs wherever it is attached to a hosting context. That is wrong for a subject that can also be a copy. An application that mirrors devices from another instance through a connector holds the same device types, and a mirrored device that starts polling the real hardware is a second writer fighting the connector. Such a subject has a hosted service instead of being one, by implementing `ISubjectHostedServiceFactory`:

```csharp
[InterceptorSubject]
public partial class Thermostat : ISubjectHostedServiceFactory
{
    public partial string? HostAddress { get; set; }

    public partial decimal? Temperature { get; internal set; }

    public Thermostat()
    {
        HostAddress = null;
        Temperature = null;
    }

    IHostedService ISubjectHostedServiceFactory.CreateHostedService(IServiceProvider serviceProvider)
        => new ThermostatPoller(this, serviceProvider.GetService<ILogger<ThermostatPoller>>());
}
```

The service can be any `IHostedService`: a poller, a service that receives pushed updates, or a connector source whose root is the subject itself, such as `this.CreateModbusClientSource(configuration, logger)`. A source reads its configuration once when it is created, so a configuration change applies on its next creation, and it does not write the subject's own status properties; when either matters, return a small service that owns the source and recreates it.

`CreateHostedService` must construct a new service on every call, as [any factory must](#the-factory-must-construct). The provider it receives is the activator's, and may resolve nothing.

Activating the subject attaches that service to it as a [factory attachment](#factory-attachment), so from then on [the rule](#the-rule) applies: the service runs while the subject is in a hosting graph, is stopped and disposed when the subject leaves, and a fresh one runs when it returns. Activation is idempotent: every call returns the one activation attachment until that attachment is detached.

Who activates:

- A hosting context, as each such subject attaches to it, with the host's service provider. A context created with `WithHostedServices(services, activateSubjectHostedServices: false)` opts out, and in it attaching a subject runs nothing.
- `AddSubject<T>()` and `AddKeyedSubject<T>()`, in both modes, with the application's service provider.
- `subject.StartAsync()`, [below](#running-one-without-a-host).
- In a context that opted out, any code that creates the subject as the real one, by calling `subject.ActivateHostedService(serviceProvider)` once the subject is configured. That includes a service that creates child subjects which themselves have a service: it activates each one, because nothing else will.

Who must not: code that creates the subject as a copy, such as a connector building a local subject to mirror a remote one. A context that holds such copies must opt out, so that they stay data whose values the connector writes.

### Running one without a host

```csharp
var thermostat = new Thermostat { HostAddress = "192.168.1.20" };

await using var running = await thermostat.StartAsync(cancellationToken);
```

`StartAsync` runs the subject in a private context, with property tracking, lifecycle and hosting, waits for its service to start and returns a `PrivateContextHost`. A subject that needs more in that context, for example the registry, implements `IPrivateContextConfigurator`, whose `ConfigureContext` runs before the subject joins it, here and for `AddSubject<T>()` without a resolver; it is never called for a shared context. Child subjects that have a service run too while it does. The overload `StartAsync(serviceProvider, cancellationToken)` hands that provider to `CreateHostedService`; without it the service gets one that resolves nothing.

- A start that throws or is cancelled stops what it started before it rethrows, so no handle to a half started subject is returned.
- Starting a subject that is already started, or already in a tracked graph, a context with lifecycle or hosting or a graph that holds a reference to it, throws `InvalidOperationException`. Let that graph's hosting context run it instead, or remove it from the graph first.
- `StopAsync(cancellationToken)` stops and disposes the services the host ran, including everything they attached, then detaches the subject from the private context and removes every activation the host ran. The subject keeps its last values and is plain data again. The token bounds the wait: a service still stopping when it expires keeps running unobserved.
- `DisposeAsync` does the same without a deadline, so a service whose stop never returns blocks it. Call `StopAsync` with a token first to bound it, which makes the disposal a no-op.
- A stopped subject can be started again, with a fresh service.

## Configuration Before Startup

A subject that takes the context in its constructor is attached during construction, which queues its service start. Object initializers, property assignments and deserializers all run afterwards, so the service can start against a subject that is not configured yet.

Either build the subject detached, configure it and attach it once it is ready, or keep the context-taking constructor and wrap the work in a start deferral:

```csharp
using (context.DeferHostedServiceStarts())
{
    var person = new Person(context) { FirstName = "John", LastName = "Doe" };
    person.AttachHostedService(() => new PersonBackgroundService(person));
}
```

Attaching still takes effect immediately, so the subject joins the graph and is visible to the registry and to sources. Only the start waits for the block to exit, and leaving the block releases it even when configuration throws: the deferral says when a subject is ready, never whether it is fit to run. Validating configuration stays with the service and its caller.

Four rules have consequences:

- Do not await a captured service's start, or its detach, inside its own block. Both wait for that start, which cannot run until the block exits.
- Do not start the host inside a block on a context that an `AddSubject<T>()` registration with a `contextResolver` joins. Such a registration opens a start deferral of its own inside yours, so its subject, and its activated service, wait for yours, and host startup waits for them. Set `HostOptions.StartupTimeout` if you want that to fail rather than hang. A registration without a resolver uses a private context and opens no scope, so your scope neither delays nor blocks it.
- A deferral nobody disposes holds its starts until the host shuts down.
- `DeferHostedServiceStarts()` returns null on a context without hosting support, and `using` accepts that.

`AddSubject<T>()` already wraps its own construction this way with a resolver, and without one configures the subject before it joins any context, so nothing extra is needed there. The exact contract, including nesting, disposal order and what happens to a start still waiting when its subject leaves the graph, is in [Start Deferrals](design/hosting-service-ownership.md#start-deferrals).

## Queued Starts and Startup Completion

Attaching a hosted service queues its `StartAsync` without waiting for it. Any subsystem that treats "the graph has finished starting" as a completion point would otherwise pass that point while a queued start is still on its way in.

A subsystem says so by implementing `IStartupCompletion` and registering it on the context. Before queueing a start, the hosting layer defers startup completion on every `IStartupCompletion` reachable from the subject's context, and releases those completion deferrals once the start has run, including when the start is skipped because the host is shutting down and when it throws. This applies to every start the handler queues, whether it came from an explicit attach or from a subject entering the graph, and to the awaiting and fire and forget attach paths alike: awaiting the start blocks the caller, but it does not block whatever else is deciding that startup is finished, so the gap still needs holding open.

Completion deferrals are counted, so nested attaches compose: a service that attaches children during its own `StartAsync` defers startup completion for them before its own completion deferrals are released.

A completion deferral covers `StartAsync` returning and nothing after it. `BackgroundService.StartAsync` schedules `ExecuteAsync` and returns at once, so a service that attaches children or registers sources for startup completion must do so in `StartAsync`, ahead of the base call.

`SourceMonitor` is the one implementation in this repository. It is what makes an attached source count towards source registration from the moment it is attached rather than from the moment it finally starts, so a synchronization wait cannot complete against a tree whose sources have not registered yet. See [Applications That Create Sources at Runtime](connectors-monitoring.md#applications-that-create-sources-at-runtime).

A startup completion runs inside the lifecycle lock, so neither `Defer` nor the `Dispose` of the handle it returns may block on anything that needs that lock to make progress, and a lock of the startup completion's own is allowed only where its order against the lifecycle lock is already fixed. The full constraint is on `IStartupCompletion`, and an implementation that follows it cannot take part in the deadlock: see [A startup completion that takes a lock of its own](design/hosting-service-ownership.md#4-a-startup-completion-that-takes-a-lock-of-its-own).

## For Library Authors

If you're building a library that provides hosted subjects, see [Subject Guidelines - Implementing Hosted Subjects for DI](subject-guidelines.md#implementing-hosted-subjects-for-di) for the device pattern: a data subject with an internal service, and an `AddX` and `AddKeyedX` pair over `AddSubject<T>()` and `AddKeyedSubject<T>()`.
