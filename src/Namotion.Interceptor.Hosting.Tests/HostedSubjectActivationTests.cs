using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Hosting.Tests;

public class HostedSubjectActivationTests
{
    [Fact]
    public async Task WhenSubjectIsAttachedToOptedOutContext_ThenNoServiceIsCreated()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync(activateSubjectHostedServices: false);

        try
        {
            // Act
            var subject = new ActivatableSubject(context);

            // Assert
            Assert.Equal(0, subject.CreateCount);
            Assert.Empty(subject.GetHostedServiceAttachments());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenSubjectIsAttachedToActivatingContext_ThenOneServiceRuns()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync();

        try
        {
            // Act
            var subject = new ActivatableSubject(context);
            var attachment = Assert.Single(subject.GetHostedServiceAttachments());
            await attachment.DrainAsync();

            // Assert
            Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
            Assert.Equal(1, subject.CreateCount);
            Assert.Same(attachment, subject.ActivateHostedService(host.Services));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenSubjectIsActivatedAutomatically_ThenTheFactoryGetsTheHostsProvider()
    {
        // Arrange - the subject attaches while the context is being configured, before any provider
        // exists, which is the documented "construct and register directly" shape.
        var builder = HostingTestHost.CreateBuilder();
        var context = HostingTestHost.CreateContext(builder);
        var subject = new ActivatableSubject(context);
        var attachment = Assert.Single(subject.GetHostedServiceAttachments());

        var host = builder.Build();
        try
        {
            // Act
            await host.StartAsync();
            await attachment.DrainAsync();

            // Assert
            Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
            Assert.NotNull(subject.LastServiceProvider);
            Assert.NotNull(subject.LastServiceProvider.GetService<ILoggerFactory>());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenActivatedSubjectIsAttachedToOptedOutContext_ThenServiceRuns()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync(activateSubjectHostedServices: false);
        var subject = new ActivatableSubject();

        try
        {
            // Act
            var attachment = subject.ActivateHostedService(host.Services)!;
            ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
            await attachment.DrainAsync();

            // Assert
            Assert.Equal(1, subject.CreateCount);
            Assert.Same(host.Services, subject.LastServiceProvider);
            Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenActivatedExplicitlyBeforeAttachingToActivatingContext_ThenOneServiceRuns()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync();
        var subject = new ActivatableSubject();

        try
        {
            // Act
            var attachment = subject.ActivateHostedService(host.Services)!;
            ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
            await attachment.DrainAsync();

            // Assert
            Assert.Same(attachment, Assert.Single(subject.GetHostedServiceAttachments()));
            Assert.Equal(1, subject.CreateCount);
            Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenActivatedExplicitlyAfterAttachingToActivatingContext_ThenOneServiceRuns()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync();
        var subject = new ActivatableSubject(context);

        try
        {
            // Act
            var attachment = subject.ActivateHostedService(host.Services)!;
            await attachment.DrainAsync();

            // Assert
            Assert.Same(attachment, Assert.Single(subject.GetHostedServiceAttachments()));
            Assert.Equal(1, subject.CreateCount);
            Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenActivatedTwice_ThenTheSameAttachmentIsReturned()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync(activateSubjectHostedServices: false);
        var subject = new ActivatableSubject();

        try
        {
            // Act
            var first = subject.ActivateHostedService(host.Services);
            var second = subject.ActivateHostedService(host.Services);
            ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
            await first!.DrainAsync();

            // Assert
            Assert.Same(first, second);
            Assert.Single(subject.GetHostedServiceAttachments());
            Assert.Equal(1, subject.CreateCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenActivatedConcurrently_ThenOneServiceRuns()
    {
        // Arrange - attached first, so both activations also race their starts.
        var (host, context) = await HostingTestHost.StartAsync(activateSubjectHostedServices: false);
        var subject = new ActivatableSubject(context);

        try
        {
            // Act
            using var barrier = new Barrier(2);
            var activations = await Task.WhenAll(
                Task.Run(() => Activate(subject, host.Services, barrier)),
                Task.Run(() => Activate(subject, host.Services, barrier)));

            await activations[0].DrainAsync();

            // Assert
            Assert.Same(activations[0], activations[1]);
            Assert.Same(activations[0], Assert.Single(subject.GetHostedServiceAttachments()));
            Assert.Equal(1, subject.CreateCount);
            Assert.Equal(HostedServiceAttachmentState.Running, activations[0].GetState(out _));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IHostedServiceAttachment Activate(IInterceptorSubject subject, IServiceProvider serviceProvider, Barrier barrier)
    {
        barrier.SignalAndWait(TimeSpan.FromSeconds(30));
        return subject.ActivateHostedService(serviceProvider)!;
    }

    [Fact]
    public void WhenServiceProviderIsNull_ThenActivateThrows()
    {
        // Arrange
        var subject = new ActivatableSubject();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => subject.ActivateHostedService(null!));
        Assert.Empty(subject.GetHostedServiceAttachments());
    }

    [Fact]
    public void WhenSubjectIsNotAFactory_ThenActivateReturnsNull()
    {
        // Arrange
        var subject = new Person();

        // Act
        var attachment = subject.ActivateHostedService(EmptyServiceProvider.Instance);

        // Assert
        Assert.Null(attachment);
        Assert.Empty(subject.GetHostedServiceAttachments());
    }

    [Fact]
    public async Task WhenActivatedSubjectIsReattached_ThenFreshServiceRuns()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync(activateSubjectHostedServices: false);
        var subject = new ActivatableSubject();
        var attachment = subject.ActivateHostedService(host.Services)!;
        ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
        await attachment.DrainAsync();
        var firstService = (ScriptedBackgroundService)subject.LastService!;

        try
        {
            // Act
            ((IInterceptorSubject)subject).Context.RemoveFallbackContext(context);
            await attachment.DrainAsync();
            ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
            await attachment.DrainAsync();

            // Assert
            Assert.Equal(2, subject.CreateCount);
            Assert.NotSame(firstService, subject.LastService);
            Assert.True(firstService.IsDisposed);
            Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAutomaticallyActivatedSubjectIsReattached_ThenTheSameActivationRunsAFreshService()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync();
        var subject = new ActivatableSubject(context);
        var attachment = Assert.Single(subject.GetHostedServiceAttachments());
        await attachment.DrainAsync();
        var firstService = (ScriptedBackgroundService)subject.LastService!;

        try
        {
            // Act
            ((IInterceptorSubject)subject).Context.RemoveFallbackContext(context);
            await attachment.DrainAsync();
            ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
            await attachment.DrainAsync();

            // Assert
            Assert.Same(attachment, Assert.Single(subject.GetHostedServiceAttachments()));
            Assert.Equal(2, subject.CreateCount);
            Assert.NotSame(firstService, subject.LastService);
            Assert.True(firstService.IsDisposed);
            Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenActivationWasDetached_ThenActivateAttachesAgain()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync(activateSubjectHostedServices: false);
        var subject = new ActivatableSubject();
        var first = subject.ActivateHostedService(host.Services)!;
        ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
        await first.DrainAsync();
        var firstService = (ScriptedBackgroundService)subject.LastService!;

        try
        {
            // Act
            await subject.DetachHostedServiceAsync(first, CancellationToken.None);
            var second = subject.ActivateHostedService(host.Services)!;
            await second.DrainAsync();

            // Assert
            Assert.NotSame(first, second);
            Assert.Same(second, Assert.Single(subject.GetHostedServiceAttachments()));
            Assert.Equal(2, subject.CreateCount);
            Assert.True(firstService.IsDisposed);
            Assert.Equal(HostedServiceAttachmentState.Running, second.GetState(out _));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenActivationIsDetachedWithoutAwaitingTheStop_ThenActivateAttachesAgain()
    {
        // Arrange - the first attachment still reads Running here, because its stop has only been
        // queued, so an activation that went by the state would hand back the detached one.
        var (host, context) = await HostingTestHost.StartAsync();
        var subject = new ActivatableSubject(context);
        var first = Assert.Single(subject.GetHostedServiceAttachments());
        await first.DrainAsync();
        var firstService = (ScriptedBackgroundService)subject.LastService!;

        try
        {
            // Act
            subject.DetachHostedService(first);
            var second = subject.ActivateHostedService(host.Services)!;
            await first.DrainAsync();
            await second.DrainAsync();

            // Assert
            Assert.NotSame(first, second);
            Assert.Same(second, Assert.Single(subject.GetHostedServiceAttachments()));
            Assert.Equal(2, subject.CreateCount);
            Assert.True(firstService.IsDisposed);
            Assert.Equal(HostedServiceAttachmentState.Removed, first.GetState(out _));
            Assert.Equal(HostedServiceAttachmentState.Running, second.GetState(out _));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenActivationIsAwaited_ThenItReturnsOnceTheServiceHasStarted()
    {
        // Arrange - the start is parked on a start deferral, so a wait that returned early is observable.
        var (host, context) = await HostingTestHost.StartAsync();
        var handler = context.TryGetService<HostedServiceHandler>()!;

        try
        {
            Task activating;
            IHostedServiceAttachment attachment;
            ActivatableSubject subject;
            using (context.DeferHostedServiceStarts())
            {
                subject = new ActivatableSubject(context);
                attachment = Assert.Single(subject.GetHostedServiceAttachments());

                // Act
                activating = subject.ActivateHostedServiceAsync(host.Services, handler, CancellationToken.None);

                Assert.False(activating.IsCompleted);
                Assert.Equal(HostedServiceAttachmentState.Stopped, attachment.GetState(out _));
            }

            await activating;

            // Assert
            Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
            Assert.Equal(1, subject.CreateCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenActivationIsAwaitedOnAHandlerThatDoesNotOwnIt_ThenItReturnsWithoutWaiting()
    {
        // Arrange - the start is parked on the owning context's scope, so a wait on it would not return.
        var (host, first, second) = await HostingTestHost.StartWithTwoContextsAsync();
        var otherHandler = second.TryGetService<HostedServiceHandler>()!;

        try
        {
            using (first.DeferHostedServiceStarts())
            {
                var subject = new ActivatableSubject(first);
                var attachment = Assert.Single(subject.GetHostedServiceAttachments());

                // Act
                var activating = subject.ActivateHostedServiceAsync(host.Services, otherHandler, CancellationToken.None);

                // Assert
                Assert.True(activating.IsCompletedSuccessfully);
                Assert.Same(attachment, Assert.Single(subject.GetHostedServiceAttachments()));
                Assert.Equal(HostedServiceAttachmentState.Stopped, attachment.GetState(out _));
            }
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAwaitedActivationFaults_ThenItThrowsAndRemovesTheActivation()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync();
        var handler = context.TryGetService<HostedServiceHandler>()!;
        var subject = new ActivatableSubject { ServiceFactory = _ => new ThrowingStartService() };
        ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
        var faulted = Assert.Single(subject.GetHostedServiceAttachments());

        try
        {
            // Act
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => subject.ActivateHostedServiceAsync(host.Services, handler, CancellationToken.None));

            // Assert
            Assert.Equal("start failed", exception.Message);
            Assert.Empty(subject.GetHostedServiceAttachments());
            await faulted.DrainAsync();
            Assert.Equal(HostedServiceAttachmentState.Removed, faulted.GetState(out _));

            subject.ServiceFactory = _ => new ScriptedBackgroundService(token => Task.Delay(Timeout.Infinite, token));
            var fresh = subject.ActivateHostedService(host.Services)!;
            await fresh.DrainAsync();

            Assert.NotSame(faulted, fresh);
            Assert.Same(fresh, Assert.Single(subject.GetHostedServiceAttachments()));
            Assert.Equal(HostedServiceAttachmentState.Running, fresh.GetState(out _));
            Assert.Equal(2, subject.CreateCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAwaitedActivationIsCancelledWhileTheStartIsParked_ThenTheActivationStays()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync();
        var handler = context.TryGetService<HostedServiceHandler>()!;

        try
        {
            IHostedServiceAttachment attachment;
            using (context.DeferHostedServiceStarts())
            {
                var subject = new ActivatableSubject(context);
                attachment = Assert.Single(subject.GetHostedServiceAttachments());
                using var cancellation = new CancellationTokenSource();

                // Act
                var activating = subject.ActivateHostedServiceAsync(host.Services, handler, cancellation.Token);
                cancellation.Cancel();

                // Assert
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activating);
                Assert.Same(attachment, Assert.Single(subject.GetHostedServiceAttachments()));
                Assert.Same(attachment, subject.ActivateHostedService(host.Services));
            }

            await attachment.DrainAsync();
            Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAutomaticallyActivatedSubjectMovesToASecondHost_ThenTheFactoryGetsTheSecondHostsProvider()
    {
        // Arrange - the first host is disposed before the move, so a provider captured from it would
        // throw on resolution rather than merely be the wrong one.
        var (firstHost, firstContext) = await HostingTestHost.StartAsync();
        var (secondHost, secondContext) = await HostingTestHost.StartAsync();
        var firstLoggerFactory = firstHost.Services.GetRequiredService<ILoggerFactory>();

        var subject = new ActivatableSubject(firstContext);
        var attachment = Assert.Single(subject.GetHostedServiceAttachments());
        await attachment.DrainAsync();
        Assert.Same(firstLoggerFactory, subject.LastServiceProvider!.GetRequiredService<ILoggerFactory>());

        try
        {
            // Act
            ((IInterceptorSubject)subject).Context.RemoveFallbackContext(firstContext);
            await attachment.DrainAsync();
            await firstHost.StopAsync();
            firstHost.Dispose();

            ((IInterceptorSubject)subject).Context.AddFallbackContext(secondContext);
            await attachment.DrainAsync();

            // Assert
            Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
            Assert.Equal(2, subject.CreateCount);
            Assert.Same(
                secondHost.Services.GetRequiredService<ILoggerFactory>(),
                subject.LastServiceProvider!.GetRequiredService<ILoggerFactory>());
        }
        finally
        {
            await secondHost.StopAsync();
        }
    }

    [Fact]
    public async Task WhenTheFirstActivationIsAwaited_ThenItReturnsOnceTheServiceHasStarted()
    {
        // Arrange - an opted-out context, so the awaited call is the one that publishes and starts.
        var (host, context) = await HostingTestHost.StartAsync(activateSubjectHostedServices: false);
        var handler = context.TryGetService<HostedServiceHandler>()!;
        var subject = new ActivatableSubject(context);

        try
        {
            Task activating;
            IHostedServiceAttachment attachment;
            using (context.DeferHostedServiceStarts())
            {
                // Act
                activating = subject.ActivateHostedServiceAsync(host.Services, handler, CancellationToken.None);
                attachment = Assert.Single(subject.GetHostedServiceAttachments());

                Assert.False(activating.IsCompleted);
                Assert.Equal(HostedServiceAttachmentState.Stopped, attachment.GetState(out _));
            }

            await activating;

            // Assert
            Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
            Assert.Equal(1, subject.CreateCount);
            Assert.Same(host.Services, subject.LastServiceProvider);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenTheFirstActivationIsAwaitedAndItsStartFaults_ThenItThrowsAndRemovesTheActivation()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync(activateSubjectHostedServices: false);
        var handler = context.TryGetService<HostedServiceHandler>()!;
        var subject = new ActivatableSubject(context) { ServiceFactory = _ => new ThrowingStartService() };

        try
        {
            // Act
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => subject.ActivateHostedServiceAsync(host.Services, handler, CancellationToken.None));

            // Assert
            Assert.Equal("start failed", exception.Message);
            Assert.Empty(subject.GetHostedServiceAttachments());
            Assert.Equal(1, subject.CreateCount);

            subject.ServiceFactory = _ => new ScriptedBackgroundService(token => Task.Delay(Timeout.Infinite, token));
            var fresh = subject.ActivateHostedService(host.Services)!;
            await fresh.DrainAsync();

            Assert.Same(fresh, Assert.Single(subject.GetHostedServiceAttachments()));
            Assert.Equal(HostedServiceAttachmentState.Running, fresh.GetState(out _));
            Assert.Equal(2, subject.CreateCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenTheFirstActivationIsAwaitedAndCancelledWhileTheStartIsParked_ThenTheActivationStays()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync(activateSubjectHostedServices: false);
        var handler = context.TryGetService<HostedServiceHandler>()!;
        var subject = new ActivatableSubject(context);

        try
        {
            IHostedServiceAttachment attachment;
            using (context.DeferHostedServiceStarts())
            {
                using var cancellation = new CancellationTokenSource();

                // Act
                var activating = subject.ActivateHostedServiceAsync(host.Services, handler, cancellation.Token);
                attachment = Assert.Single(subject.GetHostedServiceAttachments());
                cancellation.Cancel();

                // Assert
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activating);
                Assert.Same(attachment, Assert.Single(subject.GetHostedServiceAttachments()));
                Assert.Same(attachment, subject.ActivateHostedService(host.Services));
            }

            await attachment.DrainAsync();
            Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
            Assert.Equal(1, subject.CreateCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAddSubjectRunsItInAPrivateContext_ThenTheServiceRunsWithTheApplicationsProvider()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddSubject<ActivatableSubject>();
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();
            var subject = host.Services.GetRequiredService<ActivatableSubject>();

            // Assert
            Assert.Equal(1, subject.CreateCount);
            Assert.Same(host.Services.GetRequiredService<ILoggerFactory>(), subject.LastServiceProvider!.GetRequiredService<ILoggerFactory>());
            var attachment = Assert.Single(subject.GetHostedServiceAttachments());
            Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
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
            var attachment = Assert.Single(subject.GetHostedServiceAttachments());
            Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenHostStops_ThenTheServiceAddSubjectActivatedIsStoppedAndDisposed()
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
    }

    [Fact]
    public async Task WhenTheServiceAddSubjectActivatedFailsToStart_ThenHostStartThrows()
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
    public async Task WhenAddSubjectRunsASubjectThatIsAndHasAHostedService_ThenBothStartOnce()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.AddSubject<HostedActivatableSubject>();
        var host = builder.Build();

        try
        {
            // Act
            await host.StartAsync();
            var subject = host.Services.GetRequiredService<HostedActivatableSubject>();

            // Assert
            Assert.Equal(1, subject.StartCount);
            Assert.Equal(1, subject.CreateCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
