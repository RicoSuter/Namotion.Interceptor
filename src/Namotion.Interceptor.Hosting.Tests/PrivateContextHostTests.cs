using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting.Tests;

public class PrivateContextHostTests
{
    [Fact]
    public async Task WhenStarted_ThenSubjectRunsInItsOwnContext()
    {
        // Arrange
        var subject = new CountingHostedSubject();
        var host = CreateHost();

        try
        {
            // Act
            await host.StartAsync(subject, CancellationToken.None);

            // Assert
            Assert.Equal(1, subject.StartCount);
            Assert.NotNull(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenStarted_ThenAttachmentsTheSubjectMakesStart()
    {
        // Arrange
        var subject = new SubjectOwningAnAttachment();
        var host = CreateHost();

        try
        {
            // Act
            await host.StartAsync(subject, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => subject.Instance?.IsStarted == true);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenDisposed_ThenAttachedServiceIsDisposedAndSubjectIsDetached()
    {
        // Arrange
        var subject = new SubjectOwningAnAttachment();
        var host = CreateHost();
        await host.StartAsync(subject, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => subject.Instance?.IsStarted == true);

        // Act
        await host.DisposeAsync();

        // Assert
        Assert.True(subject.Instance!.IsStopped);
        Assert.True(subject.Instance.IsDisposed);
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
    }

    [Fact]
    public async Task WhenStartedTwice_ThenTheSecondStartThrows()
    {
        // Arrange
        var subject = new CountingHostedSubject();
        var first = CreateHost();
        await first.StartAsync(subject, CancellationToken.None);
        var second = CreateHost();

        try
        {
            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync(subject, CancellationToken.None));
            Assert.Equal(1, subject.StartCount);
            Assert.Single(((IInterceptorSubject)subject).Context.GetServices<HostedServiceHandler>());
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenSubjectIsInHostingGraph_ThenStartThrows()
    {
        // Arrange
        var (hostingHost, context) = await HostingTestHost.StartAsync();
        var subject = new CountingHostedSubject(context);
        var host = CreateHost();

        try
        {
            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(subject, CancellationToken.None));
            var handler = Assert.Single(((IInterceptorSubject)subject).Context.GetServices<HostedServiceHandler>());
            Assert.Same(context.TryGetService<HostedServiceHandler>(), handler);
        }
        finally
        {
            await hostingHost.StopAsync();
        }
    }

    [Fact]
    public async Task WhenSubjectIsInATrackedGraphWithoutHosting_ThenStartThrowsAndTheSubjectStaysThere()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var subject = new CountingHostedSubject(context);
        var host = CreateHost();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(subject, CancellationToken.None));
        Assert.Contains("tracked graph", exception.Message);
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
        Assert.Same(context.TryGetService<LifecycleInterceptor>(), ((IInterceptorSubject)subject).Context.TryGetService<LifecycleInterceptor>());
    }

    [Fact]
    public async Task WhenSubjectIsAChildInALifecycleGraphThatPassesNoContextDown_ThenStartThrows()
    {
        // Arrange
        // Lifecycle without context inheritance, so the child's own context never reaches the graph's
        // lifecycle and only the reference count shows that the child is in it.
        var parent = new HostedContainer(InterceptorSubjectContext.Create().WithLifecycle());
        var child = new CountingHostedSubject();
        parent.Child = child;
        var host = CreateHost();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(child, CancellationToken.None));
        Assert.Null(((IInterceptorSubject)child).Context.TryGetService<HostedServiceHandler>());
    }

    [Fact]
    public async Task WhenSubjectsOwnStartThrows_ThenStartAsyncThrowsAndSubjectIsDetached()
    {
        // Arrange
        var subject = new ThrowingHostedSubject();
        var host = CreateHost();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(subject, CancellationToken.None));
        Assert.Equal("start failed", exception.Message);
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
    }

    [Fact]
    public async Task WhenSubjectsOwnStartThrowsWhileItsAttachmentRuns_ThenStartAsyncThrowsOnlyOnceTheAttachmentIsStoppedAndDisposed()
    {
        // Arrange - the teardown after a failed start runs on the start's own token, so with one that is
        // never cancelled it has to wait for every stop rather than give up on it. The subject's start
        // fails only once its attachment is running, and the attachment's stop is held, so a teardown
        // that did not wait would return with that instance still up.
        var instance = new TrackedBackgroundService();
        var subject = new ThrowingHostedSubject { StartHold = () => AsyncTestHelpers.WaitUntilAsync(() => instance.IsStarted) };
        subject.AttachHostedService(() => instance);
        using var attachmentStop = instance.HoldAtStop();
        var host = CreateHost();

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
    public async Task WhenStartIsCancelledWhileTheSubjectsStartNeverReturns_ThenStartAsyncThrowsAndSubjectIsDetached()
    {
        // Arrange
        var subject = new ParkedStartHostedSubject();
        var host = CreateHost();
        using var cancellation = new CancellationTokenSource();
        var starting = host.StartAsync(subject, cancellation.Token);
        await subject.Entered.Task;

        // Act
        cancellation.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());

        subject.Release.SetResult();
        await AsyncTestHelpers.WaitUntilAsync(() => subject.StopCount == 1);
    }

    [Fact]
    public async Task WhenStoppedWithAnExpiringTokenWhileAStopHangs_ThenTheSubjectIsDetachedAnyway()
    {
        // Arrange
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subject = new CountingHostedSubject
        {
            StopHold = () =>
            {
                stopEntered.TrySetResult();
                return stopRelease.Task;
            }
        };
        var host = CreateHost();
        await host.StartAsync(subject, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var stopping = host.StopAsync(cancellation.Token);
        await stopEntered.Task;

        // Act
        cancellation.Cancel();
        await stopping;

        // Assert
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());

        stopRelease.SetResult();
    }

    [Fact]
    public async Task WhenStoppedThenDisposed_ThenDisposeIsNoOp()
    {
        // Arrange
        var subject = new CountingHostedSubject();
        var host = CreateHost();
        await host.StartAsync(subject, CancellationToken.None);

        // Act
        await host.StopAsync();
        await host.DisposeAsync();

        // Assert
        Assert.Equal(1, subject.StopCount);
    }

    [Fact]
    public async Task WhenStoppedConcurrently_ThenOneTeardownRuns()
    {
        // Arrange
        var subject = new CountingHostedSubject();
        var host = CreateHost();
        await host.StartAsync(subject, CancellationToken.None);

        // Act
        var first = host.StopAsync();
        var second = host.StopAsync();
        await Task.WhenAll(first, second);

        // Assert
        Assert.Same(first, second);
        Assert.Equal(1, subject.StopCount);
    }

    [Fact]
    public async Task WhenStartedAgainWithANewHostAfterStop_ThenSubjectStartsAgain()
    {
        // Arrange
        var subject = new CountingHostedSubject();
        var first = CreateHost();
        await first.StartAsync(subject, CancellationToken.None);
        await first.DisposeAsync();
        var second = CreateHost();

        try
        {
            // Act
            await second.StartAsync(subject, CancellationToken.None);

            // Assert
            Assert.Equal(2, subject.StartCount);
            Assert.Equal(1, subject.StopCount);
            Assert.NotNull(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenSubjectConfiguresItsOwnContext_ThenItIsRegisteredAndThatRunsBeforeTheSubjectJoinsIt()
    {
        // Arrange
        var subject = new ContextConfiguratorHostedSubject();
        var host = CreateHost();

        try
        {
            // Act
            await host.StartAsync(subject, CancellationToken.None);

            // Assert
            Assert.NotNull(subject.TryGetRegisteredSubject());
            Assert.Equal(1, subject.ConfigureContextCount);
            Assert.False(subject.WasAttachedWhenContextWasConfigured);
            Assert.Equal(1, subject.StartCount);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenSubjectDoesNotConfigureItsOwnContext_ThenItIsNotRegistered()
    {
        // Arrange
        var subject = new CountingHostedSubject();
        var host = CreateHost();

        try
        {
            // Act
            await host.StartAsync(subject, CancellationToken.None);

            // Assert
            Assert.Null(((IInterceptorSubject)subject).TryGetRegisteredSubject());
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenSubjectAddsHostingToItsOwnContext_ThenStartThrowsAndTheSubjectStaysDetached()
    {
        // Arrange
        var subject = new ContextConfiguratorHostedSubject { AddsHosting = true };
        var host = CreateHost();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(subject, CancellationToken.None));
        Assert.Contains("added hosting", exception.Message);
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
        Assert.Equal(0, subject.StartCount);
    }

    [Fact]
    public async Task WhenHostOfASubjectWithARegistryIsDisposed_ThenItIsNoLongerRegistered()
    {
        // Arrange
        var subject = new ContextConfiguratorHostedSubject();
        var host = CreateHost();
        await host.StartAsync(subject, CancellationToken.None);

        // Act
        await host.DisposeAsync();

        // Assert
        Assert.Null(subject.TryGetRegisteredSubject());
        Assert.Null(((IInterceptorSubject)subject).Context.TryGetService<HostedServiceHandler>());
    }

    private static PrivateContextHost CreateHost() => new(NullServiceProvider.Instance);

    /// <summary>Resolves nothing, so the host runs without a logger, as it does outside a container.</summary>
    private sealed class NullServiceProvider : IServiceProvider
    {
        public static NullServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
