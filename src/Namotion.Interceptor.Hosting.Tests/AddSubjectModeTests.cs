using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

public class AddSubjectModeTests
{
    [Fact]
    public async Task WhenAddSubjectWithoutResolver_ThenSubjectGetsPrivateContextIgnoringDependencyInjectionContext()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        var sharedContext = HostingTestHost.CreateContext(builder);
        builder.Services.AddSingleton(sharedContext);
        builder.Services.AddSubject<ActivatableSubject>();
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();
            var subject = host.Services.GetRequiredService<ActivatableSubject>();

            // Assert
            Assert.Equal(1, subject.CreateCount);
            Assert.NotSame(
                sharedContext.TryGetService<HostedServiceHandler>(),
                ((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAddSubjectWithResolver_ThenServiceRunsInSharedContext()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        var sharedContext = HostingTestHost.CreateContext(builder);
        builder.Services.AddSubject<ActivatableSubject>(contextResolver: _ => sharedContext);
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();
            var subject = host.Services.GetRequiredService<ActivatableSubject>();

            // Assert
            Assert.Equal(1, subject.CreateCount);
            Assert.Same(
                sharedContext.TryGetService<HostedServiceHandler>(),
                ((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenSharedContextOptedOutOfActivation_ThenAddSubjectStillActivatesTheService()
    {
        // Arrange - the context does not activate on attach, so the registration's explicit
        // activation is the only thing that can run the service.
        var builder = HostingTestHost.CreateBuilder();
        var sharedContext = HostingTestHost.CreateContext(builder, activateSubjectHostedServices: false);
        builder.Services.AddSubject<ActivatableSubject>(contextResolver: _ => sharedContext);
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();
            var subject = host.Services.GetRequiredService<ActivatableSubject>();

            // Assert
            Assert.Equal(1, subject.CreateCount);
            var service = Assert.IsType<ScriptedBackgroundService>(subject.LastService);
            Assert.False(service.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenSharedContextHasNoHostingHandler_ThenStartThrows()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        var sharedContext = InterceptorSubjectContext.Create().WithLifecycle();
        builder.Services.AddSubject<ActivatableSubject>(contextResolver: _ => sharedContext);
        var host = builder.Build();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("WithHostedServices", exception.Message);
    }

    [Fact]
    public async Task WhenSharedContextHasNoHostingHandlerAndSubjectHasNothingToRun_ThenStartSucceeds()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        var sharedContext = InterceptorSubjectContext.Create().WithLifecycle();
        builder.Services.AddSubject<Person>(contextResolver: _ => sharedContext);
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();

            // Assert
            Assert.NotNull(host.Services.GetRequiredService<Person>());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenHostStops_ThenSelfContainedServiceIsStoppedAndDisposed()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddSubject<ActivatableSubject>();
        var host = builder.Build();
        await host.StartAsync();
        var subject = host.Services.GetRequiredService<ActivatableSubject>();
        var service = (ScriptedBackgroundService)subject.LastService!;

        // Act
        await host.StopAsync();

        // Assert
        Assert.Equal(1, service.StopCount);
        Assert.True(service.IsDisposed);
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
        Assert.Null(((IInterceptorSubject)subject).TryGetLiveActivation());
    }

    [Fact]
    public async Task WhenSelfContainedServiceStartThrows_ThenHostStartThrows()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddSubject<ActivatableSubject>(subject =>
            subject.ServiceFactory = _ => new ThrowingStartService());
        var host = builder.Build();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Equal("start failed", exception.Message);
    }

    [Fact]
    public async Task WhenSelfContainedSubjectIsConfigured_ThenServiceSeesTheConfiguration()
    {
        // Arrange
        string? nameAtCreation = null;
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddSubject<ActivatableSubject>(subject =>
        {
            subject.Name = "configured";
            subject.ServiceFactory = s =>
            {
                nameAtCreation = s.Name;
                return new ScriptedBackgroundService(token => Task.Delay(Timeout.Infinite, token));
            };
        });
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();

            // Assert
            Assert.Equal("configured", nameAtCreation);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenSelfContainedSubjectIsItselfAHostedService_ThenItStartsOnce()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddSubject<CountingHostedSubject>();
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();

            // Assert
            Assert.Equal(1, host.Services.GetRequiredService<CountingHostedSubject>().StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenCallerRegisteredTheInstanceOutsideAnyContext_ThenItRunsAndStopsWithTheHost()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        var subject = new ActivatableSubject();
        builder.Services.AddSingleton(subject);
        builder.Services.AddSubject<ActivatableSubject>();
        var host = builder.Build();
        await host.StartAsync();
        var service = Assert.IsType<ScriptedBackgroundService>(subject.LastService);
        var wasRunning = !service.ExecuteTask!.IsCompleted;

        // Act
        await host.StopAsync();

        // Assert
        Assert.True(wasRunning);
        Assert.Equal(1, subject.CreateCount);
        Assert.Equal(1, service.StopCount);
        Assert.True(service.IsDisposed);
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
    }

    [Fact]
    public async Task WhenSelfContainedPlainSubjectHasAnAttachment_ThenTheAttachmentRunsAndStopsWithTheHost()
    {
        // Arrange - the subject hosts nothing itself, so only its private host can run the attachment.
        TrackedBackgroundService? instance = null;
        IHostedServiceAttachment? attachment = null;
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddSubject<Person>(person =>
            attachment = person.AttachHostedService(() => instance = new TrackedBackgroundService()));
        var host = builder.Build();
        await host.StartAsync();
        await attachment!.DrainAsync();
        var wasStarted = instance?.IsStarted;

        // Act
        await host.StopAsync();

        // Assert
        Assert.True(wasStarted);
        Assert.True(instance!.IsStopped);
        Assert.True(instance.IsDisposed);
        Assert.Null(((IInterceptorSubject)host.Services.GetRequiredService<Person>()).Context.TryGetService<HostedServiceHandler>());
    }

    [Fact]
    public async Task WhenCallerRegisteredPlainSubjectWithAnAttachmentOutsideAnyContext_ThenTheAttachmentRunsAndStopsWithTheHost()
    {
        // Arrange
        TrackedBackgroundService? instance = null;
        var subject = new Person();
        var attachment = subject.AttachHostedService(() => instance = new TrackedBackgroundService());
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddSingleton(subject);
        builder.Services.AddSubject<Person>();
        var host = builder.Build();
        await host.StartAsync();
        await attachment.DrainAsync();
        var wasStarted = instance?.IsStarted;

        // Act
        await host.StopAsync();

        // Assert
        Assert.True(wasStarted);
        Assert.True(instance!.IsStopped);
        Assert.True(instance.IsDisposed);
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
    }

    [Fact]
    public async Task WhenCallerRegisteredTheInstanceInAHostingContext_ThenThatContextRunsItWithoutASecondHost()
    {
        // Arrange - the context does not activate on attach, so only the registration's activation
        // through that context's handler can create the service.
        var builder = HostingTestHost.CreateBuilder();
        var sharedContext = HostingTestHost.CreateContext(builder, activateSubjectHostedServices: false);
        var subject = new ActivatableSubject(sharedContext);
        builder.Services.AddSingleton(subject);
        builder.Services.AddSubject<ActivatableSubject>();
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();

            // Assert
            Assert.Equal(1, subject.CreateCount);
            var handler = Assert.Single(((IInterceptorSubject)subject).Context.GetServices<HostedServiceHandler>());
            Assert.Same(sharedContext.TryGetService<HostedServiceHandler>(), handler);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenSelfContainedSubjectTakesTheContextAndIgnoresIt_ThenItRunsOnceInItsPrivateContextAfterConfigure()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        var sharedContext = HostingTestHost.CreateContext(builder);
        builder.Services.AddSingleton(sharedContext);
        builder.Services.AddSubject<SubjectIgnoringContextParameter>(subject => subject.Name = "configured");
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();
            var subject = host.Services.GetRequiredService<SubjectIgnoringContextParameter>();

            // Assert
            var handler = ((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>();
            Assert.NotNull(handler);
            Assert.NotSame(sharedContext.TryGetService<HostedServiceHandler>(), handler);
            Assert.Equal(1, subject.StartCount);
            Assert.Equal("configured", subject.NameAtStart);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenSelfContainedSubjectHasDependencyInjectedConstructor_ThenItRunsOnceInItsPrivateContextAfterConfigure()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        var sharedContext = HostingTestHost.CreateContext(builder);
        builder.Services.AddSingleton(sharedContext);
        builder.Services.AddSubject<SubjectWithDependencies>(subject => subject.Name = "configured");
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();
            var subject = host.Services.GetRequiredService<SubjectWithDependencies>();

            // Assert
            var handler = ((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>();
            Assert.NotNull(handler);
            Assert.NotSame(sharedContext.TryGetService<HostedServiceHandler>(), handler);
            Assert.Equal(1, subject.StartCount);
            Assert.Equal("configured", subject.NameAtStart);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenTwoProvidersAreBuiltFromOneCollection_ThenEachSubjectRunsInItsOwnHost()
    {
        // Arrange
        var services = new ServiceCollection().AddLogging();
        services.AddSubject<ActivatableSubject>();
        await using var first = services.BuildServiceProvider();
        await using var second = services.BuildServiceProvider();
        var firstActivation = Assert.Single(first.GetServices<IHostedService>());
        var secondActivation = Assert.Single(second.GetServices<IHostedService>());
        await firstActivation.StartAsync(CancellationToken.None);
        await secondActivation.StartAsync(CancellationToken.None);
        var firstSubject = first.GetRequiredService<ActivatableSubject>();
        var secondSubject = second.GetRequiredService<ActivatableSubject>();
        var firstService = Assert.IsType<ScriptedBackgroundService>(firstSubject.LastService);
        var secondService = Assert.IsType<ScriptedBackgroundService>(secondSubject.LastService);

        // Act
        await firstActivation.StopAsync(CancellationToken.None);

        // Assert
        Assert.NotSame(firstSubject, secondSubject);
        Assert.Equal(1, firstSubject.CreateCount);
        Assert.Equal(1, secondSubject.CreateCount);
        Assert.Equal(1, firstService.StopCount);
        Assert.Equal(0, secondService.StopCount);
        Assert.False(secondService.ExecuteTask!.IsCompleted);
        Assert.NotNull(((IInterceptorSubject)secondSubject).Context.TryGetService<HostedServiceHandler>());
    }

    [Fact]
    public async Task WhenALaterHostedServiceFailsHostStart_ThenDisposingTheHostStopsTheSelfContainedService()
    {
        // Arrange - the generic host disposes without stopping when a start fails, so disposal is the
        // only thing that can stop a subject host that had already started.
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddSubject<ActivatableSubject>();
        builder.Services.AddHostedService<ThrowingStartService>();
        var host = builder.Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        var service = Assert.IsType<ScriptedBackgroundService>(
            host.Services.GetRequiredService<ActivatableSubject>().LastService);
        var stopCountBeforeDispose = service.StopCount;

        // Act
        await ((IAsyncDisposable)host).DisposeAsync();

        // Assert
        Assert.Equal(0, stopCountBeforeDispose);
        Assert.Equal(1, service.StopCount);
        Assert.True(service.IsDisposed);
    }

    [Fact]
    public async Task WhenAnAwaitedAttachOpenedThePrivateHostBeforeAnEarlierServiceFailedHostStart_ThenDisposingTheHostStopsIt()
    {
        // Arrange - the awaited attach opens the private handler ahead of host start, and the service
        // failing ahead of the activation means the activation's own start never runs.
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddHostedService<ThrowingStartService>();
        builder.Services.AddSubject<CountingHostedSubject>();
        var host = builder.Build();
        var subject = host.Services.GetRequiredService<CountingHostedSubject>();
        var instance = new TrackedBackgroundService();
        await subject.AttachHostedServiceAsync(() => instance, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        var stopCountBeforeDispose = subject.StopCount;

        // Act
        await ((IAsyncDisposable)host).DisposeAsync();

        // Assert
        Assert.True(instance.IsStarted);
        Assert.Equal(0, stopCountBeforeDispose);
        Assert.Equal(1, subject.StopCount);
        Assert.True(instance.IsStopped);
        Assert.True(instance.IsDisposed);
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
    }

    [Fact]
    public async Task WhenCallerRegisteredInstanceIsInNoHostingGraphWithAResolver_ThenStartThrows()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        var sharedContext = HostingTestHost.CreateContext(builder);
        builder.Services.AddSingleton(new ActivatableSubject());
        builder.Services.AddSubject<ActivatableSubject>(contextResolver: _ => sharedContext);
        var host = builder.Build();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("not constructed by AddSubject", exception.Message);
    }

    [Fact]
    public async Task WhenSelfContainedSubjectAddsTheRegistryToItsOwnContext_ThenItIsRegisteredAndItsServiceRuns()
    {
        // Arrange - configure sets the flag ConfigureContext reads, so this also shows configure runs first.
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddSubject<ContextConfiguratorFactorySubject>(subject => subject.AddsRegistry = true);
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();
            var subject = host.Services.GetRequiredService<ContextConfiguratorFactorySubject>();

            // Assert
            Assert.NotNull(subject.TryGetRegisteredSubject());
            Assert.Equal(1, subject.ConfigureContextCount);
            Assert.False(subject.LastService!.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenSelfContainedSubjectIsConstructedWithAContext_ThenItsContextIsConfiguredBeforeItJoinsIt()
    {
        // Arrange - the generated context constructor attaches during construction, so only a
        // construction context without lifecycle keeps the subject out of a graph until then.
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddSubject<ContextConfiguratorFactorySubject>();
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();
            var subject = host.Services.GetRequiredService<ContextConfiguratorFactorySubject>();

            // Assert
            Assert.False(subject.WasAttachedWhenContextWasConfigured);
            Assert.Null(subject.TryGetRegisteredSubject());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAddSubjectWithResolver_ThenTheSubjectDoesNotConfigureTheSharedContext()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        var sharedContext = HostingTestHost.CreateContext(builder);
        builder.Services.AddSubject<ContextConfiguratorFactorySubject>(
            subject => subject.AddsRegistry = true,
            contextResolver: _ => sharedContext);
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();
            var subject = host.Services.GetRequiredService<ContextConfiguratorFactorySubject>();

            // Assert
            Assert.Equal(0, subject.ConfigureContextCount);
            Assert.Null(subject.TryGetRegisteredSubject());
            Assert.False(subject.LastService!.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenSelfContainedSubjectIsItselfAHostedServiceAndConfiguresItsOwnContext_ThenItIsRegisteredAndStarts()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddSubject<ContextConfiguratorHostedSubject>();
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();
            var subject = host.Services.GetRequiredService<ContextConfiguratorHostedSubject>();

            // Assert
            Assert.Equal(1, subject.ConfigureContextCount);
            Assert.NotNull(subject.TryGetRegisteredSubject());
            Assert.Equal(1, subject.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
