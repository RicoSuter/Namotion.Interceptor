using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

public class HostedServiceHandlerTests
{
    [Fact]
    public async Task WhenSubjectImplementsIHostedService_ThenItIsStartedAndStopped()
    {
        // Arrange
        PersonWithBackgroundService person = null!;

        // Act
        await RunWithAppLifecycleAsync(async context =>
        {
            person = new PersonWithBackgroundService(context);
            await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "John");

            // Assert
            Assert.Equal("John", person.FirstName);
            Assert.Equal("Doe", person.LastName);
        });

        await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "Disposed");
        Assert.Equal("Disposed", person.FirstName);
    }
    
    [Fact]
    public async Task WhenHostedServiceIsAttachedToSubject_ThenHostedServiceIsStarted()
    {
        // Arrange
        Person person = null!;
        await RunWithAppLifecycleAsync(async context =>
        {
            person = new Person(context);
            
            // Act
            var hostedService = new PersonBackgroundService(person);
            person.AttachHostedService(hostedService);

            await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "John");

            // Assert
            Assert.Equal("John", person.FirstName);
            Assert.Equal("Doe", person.LastName);
        });

        await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "Disposed");
        Assert.Equal("Disposed", person.FirstName);
    }
    
    [Fact]
    public async Task WhenHostedServiceIsDetachedFromSubject_ThenHostedServiceIsStopped()
    {
        // Arrange
        Person person;
        await RunWithAppLifecycleAsync(async context =>
        {
            person = new Person(context);
         
            var hostedService = new PersonBackgroundService(person);
            person.AttachHostedService(hostedService);
            var attachedHostedServices = person.GetAttachedHostedServices();

            await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "John");

            Assert.Equal("John", person.FirstName);
            Assert.Equal("Doe", person.LastName);
            Assert.Single(attachedHostedServices);

            // Act
            person.DetachHostedService(hostedService);
            attachedHostedServices = person.GetAttachedHostedServices();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "Disposed");
            Assert.Equal("Disposed", person.FirstName);
            Assert.Empty(attachedHostedServices);
        });
    }

    [Fact]
    public async Task WhenSubjectServiceIsDetached_ThenHostedServiceIsStopped()
    {
        // Arrange
        Person person;
        await RunWithAppLifecycleAsync(async context =>
        {
            person = new Person(context);

            var hostedService = new PersonBackgroundService(person);
            person.AttachHostedService(hostedService);
            var attachedHostedServices = person.GetAttachedHostedServices();

            await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "John");
            Assert.Single(attachedHostedServices);

            // Act
            ((IInterceptorSubject)person).Context.RemoveFallbackContext(context);
            attachedHostedServices = person.GetAttachedHostedServices();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "Disposed");
            Assert.Equal("Disposed", person.FirstName);
            Assert.Empty(attachedHostedServices); // the service has been stopped and
                                                   // removed from list (not allowed to restart again anyway)
        });
    }

    [Fact]
    public async Task WhenHostedServiceIsAttachedAsync_ThenServiceIsStartedAndAwaited()
    {
        // Arrange
        Person person = null!;
        await RunWithAppLifecycleAsync(async context =>
        {
            person = new Person(context);
            var hostedService = new PersonBackgroundService(person);

            // Act - AttachHostedServiceAsync should wait for StartAsync to complete
            await person.AttachHostedServiceAsync(hostedService, CancellationToken.None);

            // Assert - Service should be running immediately after await returns
            Assert.Equal("John", person.FirstName);
            Assert.Equal("Doe", person.LastName);
            Assert.Single(person.GetAttachedHostedServices());
        });

        await AsyncTestHelpers.WaitUntilAsync(() => person!.FirstName == "Disposed");
        Assert.Equal("Disposed", person!.FirstName);
    }

    [Fact]
    public async Task WhenHostedServiceIsDetachedAsync_ThenServiceIsStoppedAndAwaited()
    {
        // Arrange
        Person person = null!;
        await RunWithAppLifecycleAsync(async context =>
        {
            person = new Person(context);
            var hostedService = new PersonBackgroundService(person);

            // Start the service
            await person.AttachHostedServiceAsync(hostedService, CancellationToken.None);
            Assert.Equal("John", person.FirstName);

            // Act - DetachHostedServiceAsync should wait for StopAsync to complete
            await person.DetachHostedServiceAsync(hostedService, CancellationToken.None);

            // Assert - Service should be stopped immediately after await returns
            Assert.Equal("Disposed", person.FirstName);
            Assert.Empty(person.GetAttachedHostedServices());
        });
    }

    [Fact]
    public async Task WhenAttachHostedServiceAsyncCalledTwice_ThenOnlyStartsOnce()
    {
        // Arrange
        await RunWithAppLifecycleAsync(async context =>
        {
            var person = new Person(context);
            var hostedService = new PersonBackgroundService(person);

            // Act - Attach same service twice
            await person.AttachHostedServiceAsync(hostedService, CancellationToken.None);
            await person.AttachHostedServiceAsync(hostedService, CancellationToken.None);

            // Assert - Should only be in the collection once
            Assert.Single(person.GetAttachedHostedServices());
            Assert.Equal("John", person.FirstName);
        });
    }

    [Fact]
    public async Task WhenActionsAreQueued_ThenWaitForPendingActionsCompletesOnlyAfterTheyHaveRun()
    {
        // Arrange
        await RunWithAppLifecycleAsync(async context =>
        {
            var person = new Person(context);
            var hostedService = new PersonBackgroundService(person);

            // Act
            person.AttachHostedService(hostedService);
            await context.WaitForPendingHostedServiceActionsAsync(CancellationToken.None);

            // Assert
            Assert.Equal("John", person.FirstName);
        });
    }

    [Fact]
    public async Task WhenAStopActionIsQueued_ThenWaitForPendingActionsCompletesOnlyAfterItHasRun()
    {
        // Arrange
        await RunWithAppLifecycleAsync(async context =>
        {
            var person = new Person(context);
            var hostedService = new PersonBackgroundService(person);
            person.AttachHostedService(hostedService);
            await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "John");

            // Act - companion to WhenActionsAreQueued_ThenWaitForPendingActionsCompletesOnlyAfterTheyHaveRun
            // above, which only covers the start-action path.
            person.DetachHostedService(hostedService);
            await context.WaitForPendingHostedServiceActionsAsync(CancellationToken.None);

            // Assert
            Assert.Equal("Disposed", person.FirstName);
        });
    }

    [Fact]
    public async Task WhenAnActionIsPostedAfterTheWaitIsCreated_ThenTheWaitDoesNotWaitForIt()
    {
        // Arrange
        await RunWithAppLifecycleAsync(async context =>
        {
            var person = new Person(context);
            var gated = new GatedHostedService();

            // Act - the wait's marker is posted (synchronously, inside this call, before it returns
            // the incomplete task) before the gated service's own start action is posted, so a
            // correct FIFO barrier must not wait for it: the marker runs first and completes the
            // wait regardless of whether the gated service ever finishes starting.
            var waitTask = context.WaitForPendingHostedServiceActionsAsync(CancellationToken.None);
            person.AttachHostedService(gated);

            // Assert
            await waitTask.WaitAsync(TimeSpan.FromSeconds(5));

            gated.Release();
        });
    }

    [Fact]
    public async Task WhenNoHandlerIsConfigured_ThenWaitForPendingActionsCompletesImmediately()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();

        // Act
        var task = context.WaitForPendingHostedServiceActionsAsync(CancellationToken.None);

        // Assert
        Assert.True(task.IsCompletedSuccessfully);
    }

    private static async Task RunWithAppLifecycleAsync(Func<IInterceptorSubjectContext, Task> action)
    {
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithHostedServices(builder.Services);

        var host = builder.Build();
        await host.StartAsync();
        try
        {
            await action(context);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}

/// <summary>A hosted service whose StartAsync blocks until Release is called.</summary>
internal sealed class GatedHostedService : IHostedService
{
    private readonly TaskCompletionSource _started = new();

    public void Release() => _started.TrySetResult();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _started.Task.WaitAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}