using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting.Tests;

public class SubjectHostTests
{
    [Fact]
    public async Task WhenStarted_ThenServiceRuns()
    {
        // Arrange
        var subject = new ActivatableSubject();

        // Act
        await using var running = await subject.StartAsync();

        // Assert
        Assert.Equal(1, subject.CreateCount);
        var attachment = Assert.Single(subject.GetHostedServiceAttachments());
        Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
    }

    [Fact]
    public async Task WhenStartedWithoutDependencyInjection_ThenNestedAttachmentsStart()
    {
        // Arrange
        var subject = new ActivatableSubject { ServiceFactory = s => new AttachingService(s) };

        // Act
        await using var running = await subject.StartAsync();

        // Assert
        var service = (AttachingService)subject.LastService!;
        await AsyncTestHelpers.WaitUntilAsync(() =>
            service.ChildAttachment?.GetState(out _) == HostedServiceAttachmentState.Running);
    }

    [Fact]
    public async Task WhenRunningHandleIsDisposed_ThenServiceIsDisposedAndSubjectIsDetached()
    {
        // Arrange
        var subject = new ActivatableSubject { ServiceFactory = s => new AttachingService(s) };
        var running = await subject.StartAsync();
        var service = (AttachingService)subject.LastService!;
        await AsyncTestHelpers.WaitUntilAsync(() =>
            service.ChildAttachment?.GetState(out _) == HostedServiceAttachmentState.Running);

        // Act
        await running.DisposeAsync();

        // Assert
        Assert.True(service.Child!.IsDisposed);
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
        Assert.DoesNotContain(subject.GetHostedServiceAttachments(), attachment => attachment.GetState(out _) == HostedServiceAttachmentState.Running);
    }

    [Fact]
    public async Task WhenRunningHandleIsDisposedAndSubjectJoinsAnOptedOutContext_ThenNothingRuns()
    {
        // Arrange
        var subject = new ActivatableSubject();
        var running = await subject.StartAsync();
        await running.DisposeAsync();
        var (host, context) = await HostingTestHost.StartAsync(activateSubjectHostedServices: false);

        try
        {
            // Act
            ((IInterceptorSubject)subject).Context.AddFallbackContext(context);

            // Assert
            Assert.Empty(subject.GetHostedServiceAttachments());
            Assert.Null(subject.TryGetLiveActivation());
            Assert.Equal(1, subject.CreateCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenRunningHandleIsDisposedAndChildJoinsAnOptedOutContext_ThenNothingRuns()
    {
        // Arrange
        var child = new ActivatableSubject();
        var parent = new ActivatableParent { Child = child };
        var running = await parent.StartAsync();
        await AsyncTestHelpers.WaitUntilAsync(() => child.CreateCount == 1);
        await running.DisposeAsync();
        var (host, context) = await HostingTestHost.StartAsync(activateSubjectHostedServices: false);

        try
        {
            // Act
            ((IInterceptorSubject)parent).Context.AddFallbackContext(context);

            // Assert
            Assert.Empty(parent.GetHostedServiceAttachments());
            Assert.Empty(child.GetHostedServiceAttachments());
            Assert.Null(child.TryGetLiveActivation());
            Assert.Equal(1, child.CreateCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenChildActivatedBeforeJoiningTheHostedGraphAndHandleIsDisposed_ThenNothingRunsInAnOptedOutContext()
    {
        // Arrange
        var parent = new ActivatableParent();
        var running = await parent.StartAsync();
        var child = new ActivatableSubject();
        child.ActivateHostedService(EmptyServiceProvider.Instance);
        parent.Child = child;
        await AsyncTestHelpers.WaitUntilAsync(() => child.CreateCount == 1);
        await running.DisposeAsync();
        var (host, context) = await HostingTestHost.StartAsync(activateSubjectHostedServices: false);

        try
        {
            // Act
            ((IInterceptorSubject)parent).Context.AddFallbackContext(context);

            // Assert
            Assert.Empty(child.GetHostedServiceAttachments());
            Assert.Null(child.TryGetLiveActivation());
            Assert.Equal(1, child.CreateCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenSubjectActivatedBeforeStartAndHandleIsDisposed_ThenNothingRunsInAnOptedOutContext()
    {
        // Arrange
        var subject = new ActivatableSubject();
        subject.ActivateHostedService(EmptyServiceProvider.Instance);
        var running = await subject.StartAsync();
        await running.DisposeAsync();
        var (host, context) = await HostingTestHost.StartAsync(activateSubjectHostedServices: false);

        try
        {
            // Act
            ((IInterceptorSubject)subject).Context.AddFallbackContext(context);

            // Assert
            Assert.Empty(subject.GetHostedServiceAttachments());
            Assert.Null(subject.TryGetLiveActivation());
            Assert.Equal(1, subject.CreateCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenChildMovedToAnotherHostWhileHosted_ThenDisposingTheHandleLeavesItRunningThere()
    {
        // Arrange
        var child = new ActivatableSubject();
        var parent = new ActivatableParent { Child = child };
        var running = await parent.StartAsync();
        await AsyncTestHelpers.WaitUntilAsync(() => child.CreateCount == 1);
        var (host, context) = await HostingTestHost.StartAsync();

        try
        {
            var otherParent = new ActivatableParent(context);
            parent.Child = null;
            otherParent.Child = child;
            var attachment = Assert.Single(child.GetHostedServiceAttachments());
            await AsyncTestHelpers.WaitUntilAsync(() =>
                child.CreateCount == 2 && attachment.GetState(out _) == HostedServiceAttachmentState.Running);

            // Act
            await running.DisposeAsync();

            // Assert
            Assert.Same(attachment, Assert.Single(child.GetHostedServiceAttachments()));
            Assert.Same(attachment, child.TryGetLiveActivation());
            Assert.Equal(HostedServiceAttachmentState.Running, attachment.GetState(out _));
            Assert.Equal(2, child.CreateCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenStartedTwice_ThenThrows()
    {
        // Arrange
        var subject = new ActivatableSubject();
        await using var running = await subject.StartAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => subject.StartAsync());
        Assert.Equal(1, subject.CreateCount);
    }

    [Fact]
    public async Task WhenSubjectIsInHostingGraph_ThenStartThrows()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync(activateSubjectHostedServices: false);
        var subject = new ActivatableSubject(context);

        try
        {
            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() => subject.StartAsync());
            Assert.Equal(0, subject.CreateCount);
            Assert.Empty(subject.GetHostedServiceAttachments());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenSubjectIsInATrackedGraphWithoutHosting_ThenStartThrowsAndTheSubjectStaysThere()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var subject = new ActivatableSubject(context);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => subject.StartAsync());
        Assert.Contains("tracked graph", exception.Message);
        Assert.Equal(0, subject.CreateCount);
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
        Assert.Same(context.TryGetService<LifecycleInterceptor>(), ((IInterceptorSubject)subject).Context.TryGetService<LifecycleInterceptor>());
    }

    [Fact]
    public async Task WhenSubjectIsAChildInALifecycleGraphThatPassesNoContextDown_ThenStartThrows()
    {
        // Arrange
        // Lifecycle without context inheritance, so the child's own context never reaches the graph's
        // lifecycle and only the reference count shows that the child is in it.
        var parent = new ActivatableParent(InterceptorSubjectContext.Create().WithLifecycle());
        var child = new ActivatableSubject();
        parent.Child = child;

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => child.StartAsync());
        Assert.Equal(0, child.CreateCount);
        Assert.Null(((IInterceptorSubject)child).Context.TryGetService<HostedServiceHandler>());
    }

    [Fact]
    public async Task WhenServiceStartThrows_ThenStartAsyncThrowsAndSubjectIsDetached()
    {
        // Arrange
        var subject = new ActivatableSubject { ServiceFactory = _ => new ThrowingStartService() };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => subject.StartAsync());
        Assert.Equal("start failed", exception.Message);
        Assert.Empty(subject.GetHostedServiceAttachments());
        Assert.Null(subject.TryGetLiveActivation());
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
    }

    [Fact]
    public async Task WhenSubjectsOwnStartThrowsWhileItsAttachmentRuns_ThenStartAsyncThrowsOnlyOnceTheAttachmentIsStoppedAndDisposed()
    {
        // Arrange - the teardown after a failed start runs on the start's own token, so with one that is
        // never cancelled it has to wait for every stop rather than give up on it. The subject's start
        // fails only once its attachment is running, and the attachment's stop is held, so a teardown
        // that did not wait would return with that instance still up. On the internal host, because
        // the extension binds only to a subject that has a service, and this one is a service itself.
        var instance = new TrackedBackgroundService();
        var subject = new ThrowingHostedSubject { StartHold = () => AsyncTestHelpers.WaitUntilAsync(() => instance.IsStarted) };
        subject.AttachHostedService(() => instance);
        using var attachmentStop = instance.HoldAtStop();
        var host = new SubjectHost(serviceProvider: null);

        // Act
        var starting = host.StartAsync(subject, CancellationToken.None);
        await attachmentStop.WaitUntilReachedAsync();

        // Assert
        Assert.False(starting.IsCompleted, "The start returned while its attachment was still stopping.");

        attachmentStop.Release();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => starting);
        Assert.Equal("start failed", exception.Message);
        Assert.True(instance.IsStopped);
        Assert.True(instance.IsDisposed);
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
    }

    [Fact]
    public async Task WhenStartIsCancelledWhileTheServiceStartNeverReturns_ThenStartAsyncThrowsAndSubjectIsDetached()
    {
        // Arrange
        var service = new ParkedStartService();
        var subject = new ActivatableSubject { ServiceFactory = _ => service };
        using var cancellation = new CancellationTokenSource();
        var starting = subject.StartAsync(cancellation.Token);
        await service.Entered.Task;

        // Act
        cancellation.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.Empty(subject.GetHostedServiceAttachments());
        Assert.Null(subject.TryGetLiveActivation());
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());

        service.Release.SetResult();
        await AsyncTestHelpers.WaitUntilAsync(() => service.IsDisposed);
    }

    [Fact]
    public async Task WhenSubjectIsItselfAHostedService_ThenItsOwnStartAndItsServiceRun()
    {
        // Arrange
        var subject = new HostedActivatableSubject();

        // Act
        await using var running = await subject.StartAsync();

        // Assert
        Assert.Equal(1, subject.StartCount);
        Assert.Equal(1, subject.CreateCount);
        Assert.Equal(HostedServiceAttachmentState.Running,
            Assert.Single(subject.GetHostedServiceAttachments()).GetState(out _));
    }

    [Fact]
    public async Task WhenSubjectsOwnStartThrows_ThenStartAsyncThrowsAndSubjectIsDetached()
    {
        // Arrange
        var subject = new HostedActivatableSubject { StartFault = new InvalidOperationException("own start failed") };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => subject.StartAsync());
        Assert.Equal("own start failed", exception.Message);
        Assert.Empty(subject.GetHostedServiceAttachments());
        Assert.Null(subject.TryGetLiveActivation());
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
    }

    [Fact]
    public async Task WhenStoppedWithAnExpiringTokenWhileAStopHangs_ThenTheSubjectIsDetachedAnyway()
    {
        // Arrange
        GatedStopService? service = null;
        var subject = new ActivatableSubject { ServiceFactory = _ => service = new GatedStopService() };
        var running = await subject.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var stopping = running.StopAsync(cancellation.Token);
        await service!.StopEntered.Task;

        // Act
        cancellation.Cancel();
        await stopping;

        // Assert
        Assert.False(service.IsDisposed);
        Assert.Empty(subject.GetHostedServiceAttachments());
        Assert.Null(subject.TryGetLiveActivation());
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());

        service.StopRelease.SetResult();
        await AsyncTestHelpers.WaitUntilAsync(() => service.IsDisposed);
    }

    [Fact]
    public async Task WhenStoppedThenDisposed_ThenDisposeIsNoOp()
    {
        // Arrange
        var subject = new ActivatableSubject();
        var running = await subject.StartAsync();
        var service = (ScriptedBackgroundService)subject.LastService!;

        // Act
        await running.StopAsync();
        await running.DisposeAsync();

        // Assert
        Assert.Equal(1, service.StopCount);
        Assert.True(service.IsDisposed);
    }

    [Fact]
    public async Task WhenRestartedAfterDispose_ThenFreshServiceRuns()
    {
        // Arrange
        var subject = new ActivatableSubject();
        var first = await subject.StartAsync();
        var firstService = subject.LastService;
        await first.DisposeAsync();

        // Act
        await using var second = await subject.StartAsync();

        // Assert
        Assert.Equal(2, subject.CreateCount);
        Assert.NotSame(firstService, subject.LastService);
        Assert.Equal(HostedServiceAttachmentState.Running,
            Assert.Single(subject.GetHostedServiceAttachments()).GetState(out _));
    }

    [Fact]
    public async Task WhenServiceProviderIsGiven_ThenServiceReceivesIt()
    {
        // Arrange
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var subject = new ActivatableSubject();

        // Act
        await using var running = await subject.StartAsync(provider);

        // Assert
        Assert.Same(provider, subject.LastServiceProvider);
    }

    [Fact]
    public async Task WhenNoServiceProviderIsGiven_ThenServiceReceivesOneThatResolvesNothing()
    {
        // Arrange
        var subject = new ActivatableSubject();

        // Act
        await using var running = await subject.StartAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(subject.LastServiceProvider);
        Assert.Null(subject.LastServiceProvider!.GetService(typeof(IServiceProvider)));
    }

    [Fact]
    public async Task WhenSubjectAddsTheRegistryToItsOwnContext_ThenItIsRegisteredAndItsServiceRuns()
    {
        // Arrange
        var subject = new ContextConfiguratorFactorySubject { AddsRegistry = true };

        // Act
        await using var running = await subject.StartAsync();

        // Assert
        Assert.NotNull(subject.TryGetRegisteredSubject());
        Assert.Equal(1, subject.ConfigureContextCount);
        Assert.False(subject.LastService!.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task WhenSubjectDoesNotConfigureItsOwnContext_ThenItIsNotRegistered()
    {
        // Arrange
        var subject = new ActivatableSubject();

        // Act
        await using var running = await subject.StartAsync();

        // Assert
        Assert.Null(((IInterceptorSubject)subject).TryGetRegisteredSubject());
        Assert.Equal(1, subject.CreateCount);
    }

    [Fact]
    public async Task WhenSubjectConfiguresItsOwnContext_ThenThatRunsBeforeTheSubjectJoinsIt()
    {
        // Arrange
        var subject = new ContextConfiguratorFactorySubject();

        // Act
        await using var running = await subject.StartAsync();

        // Assert
        Assert.False(subject.WasAttachedWhenContextWasConfigured);
    }

    [Fact]
    public async Task WhenSubjectAddsHostingToItsOwnContext_ThenStartThrowsAndTheSubjectStaysDetached()
    {
        // Arrange
        var subject = new ContextConfiguratorFactorySubject { AddsHosting = true };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => subject.StartAsync());
        Assert.Contains("added hosting", exception.Message);
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
        Assert.Null(subject.LastService);
    }

    [Fact]
    public async Task WhenRunningHandleOfASubjectWithARegistryIsDisposed_ThenItIsNoLongerRegistered()
    {
        // Arrange
        var subject = new ContextConfiguratorFactorySubject { AddsRegistry = true };
        var running = await subject.StartAsync();

        // Act
        await running.DisposeAsync();

        // Assert
        Assert.Null(subject.TryGetRegisteredSubject());
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
    }

    /// <summary>A service whose start parks until the test releases it.</summary>
    private sealed class ParkedStartService : IHostedService, IDisposable
    {
        private int _disposeCount;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDisposed => Volatile.Read(ref _disposeCount) > 0;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Entered.SetResult();
            return Release.Task;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
