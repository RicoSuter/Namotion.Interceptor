# Hosting

The `Namotion.Interceptor.Hosting` package integrates interceptor subjects with the .NET Generic Host lifecycle (`Microsoft.Extensions.Hosting`). This works with any host-based application: ASP.NET Core, worker services, console apps, etc. Subjects can either be hosted services themselves (extending `BackgroundService`) or have hosted services attached to them that start and stop dynamically.

## Setup

Configure hosting support in your interceptor context and register it with the host:

```csharp
var builder = Host.CreateApplicationBuilder();

var context = InterceptorSubjectContext
    .Create()
    .WithLifecycle()
    .WithHostedServices(builder.Services);

var host = builder.Build();
await host.StartAsync();
```

The `WithHostedServices()` method:
- Registers a `HostedServiceHandler` that manages service lifecycles
- Automatically enables `WithLifecycle()` for subject attach/detach tracking
- Integrates with the .NET hosting pipeline

## Subject as Hosted Service

A subject can directly extend `BackgroundService` to become a hosted service. When the subject is attached to a context with hosting support, the service automatically starts. When the subject is detached from the context (or the host stops), the service stops.

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
// Service starts automatically when attached to context
// Service stops when detached from context or host stops
```

This pattern is useful when the subject's entire purpose is to run a background task that updates its properties.

## Attaching Hosted Services

For more flexibility, you can attach and detach hosted services from subjects at runtime. This allows dynamic background tasks that can be started and stopped independently of the subject's lifecycle.

### Fire-and-Forget Attachment

```csharp
var person = new Person(context);
var backgroundService = new DataSyncService(person);

// Attach - service starts asynchronously
person.AttachHostedService(backgroundService);

// Check attached services
var services = person.GetAttachedHostedServices();

// Detach - service stops asynchronously
person.DetachHostedService(backgroundService);
```

### Awaitable Attachment

When you need to ensure the service has started or stopped before continuing:

```csharp
// Wait for service to start
await person.AttachHostedServiceAsync(backgroundService, cancellationToken);
// Service is now running

// Wait for service to stop
await person.DetachHostedServiceAsync(backgroundService, cancellationToken);
// Service has stopped
```

### Automatic Cleanup

When a subject is detached from its context (e.g., removed from the object graph), all attached hosted services are automatically stopped and removed:

```csharp
var parent = new Parent(context);
var child = new Child();
child.AttachHostedService(new ChildMonitorService(child));

parent.Child = child;  // child attached to context, service running
parent.Child = null;   // child detached, service automatically stopped
```

## Example: Background Service for a Subject

A common pattern is creating a dedicated background service that operates on a subject:

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
        // Initialize
        _person.FirstName = "John";
        _person.LastName = "Doe";

        // Run until cancelled
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // Cleanup
        _person.FirstName = "Stopped";
        return base.StopAsync(cancellationToken);
    }
}

// Usage
var person = new Person(context);
await person.AttachHostedServiceAsync(
    new PersonBackgroundService(person),
    cancellationToken);
```

## For Library Authors

If you're building a library that provides hosted subjects, see [Subject Guidelines - Implementing Hosted Subjects for DI](subject-guidelines.md#implementing-hosted-subjects-for-di) for the recommended pattern using `AddHostedSubject<T>()`.
