using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Hosting.Tests;

public class HostedServiceStartDeferralTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenADeferralIsOpen_ThenIndependentFlowCanStartAndStopServices(bool alreadyStarted)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var subject = new Person(fixture.Context);
        var deferred = new ProbeService(() => "deferred");
        var independent = new ProbeService(() => "independent");
        if (alreadyStarted)
        {
            await subject.AttachHostedServiceAsync(independent, CancellationToken.None);
        }
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var independentFlow = Task.Run(async () =>
        {
            await proceed.Task;
            if (!alreadyStarted)
            {
                await subject.AttachHostedServiceAsync(independent, CancellationToken.None);
            }
            await subject.DetachHostedServiceAsync(independent, CancellationToken.None);
        });

        // Act
        using (var deferral = fixture.Context.DeferHostedServiceStarts())
        {
            subject.AttachHostedService(deferred);
            proceed.SetResult();
            await independentFlow.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.False(deferred.Started.Task.IsCompleted);
        }
        await deferred.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenANestedDeferralIsDisposed_ThenStartWaitsForTheOuterDeferral()
    {
        // Arrange
        await using var fixture = new Fixture();
        var configuration = "uninitialized";
        var service = new ProbeService(() => configuration);
        var subject = new Person(fixture.Context);

        // Act
        using (var outer = fixture.Context.DeferHostedServiceStarts())
        {
            using (var inner = fixture.Context.DeferHostedServiceStarts())
            {
                subject.AttachHostedService(service);
            }
            await fixture.Handler.StartAsync(CancellationToken.None);
            Assert.False(service.Started.Task.IsCompleted);
            configuration = "configured";
        }

        // Assert
        Assert.Equal("configured", await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task WhenHandlerStopsDuringAnOpenDeferral_ThenAllStartsAreCanceledAndCompletionDeferralsAreReleased(int count)
    {
        // Arrange
        await using var fixture = new Fixture();
        var services = Enumerable.Range(0, count).Select(_ => new ProbeService(() => "started")).ToArray();
        var subject = new Person(fixture.Context);
        using var deferral = fixture.Context.DeferHostedServiceStarts();
        var attachments = services.Select(service => subject.AttachHostedServiceAsync(service, CancellationToken.None)).ToArray();
        Assert.Equal(count, fixture.CompletionDeferred);
        Assert.Equal(0, fixture.CompletionDeferralReleased);

        // Act
        await fixture.Handler.StartAsync(CancellationToken.None);
        await fixture.Handler.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        foreach (var attachment in attachments)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attachment.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        Assert.Equal(count, fixture.CompletionDeferralReleased);
        Assert.All(services, service => Assert.False(service.Started.Task.IsCompleted));
    }

    [Fact]
    public async Task WhenShutdownCancellationDetachesADeferredService_ThenShutdownStillStopsIt()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var subject = new Person(fixture.Context);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new ProbeService(() => "deferred", () => stopped.TrySetResult());
        var blocking = new ProbeService(() => "blocking", startup: releaseStart.Task,
            starting: token => token.Register(() => subject.DetachHostedService(deferred)));
        subject.AttachHostedService(blocking);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var deferral = fixture.Context.DeferHostedServiceStarts();
        subject.AttachHostedService(deferred);
        Task? stopping = null;

        try
        {
            // Act
            stopping = fixture.Handler.StopAsync(CancellationToken.None);

            // Assert
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(deferred.Started.Task.IsCompleted);
        }
        finally
        {
            releaseStart.TrySetResult();
            await (stopping ?? fixture.Handler.StopAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(2, fixture.CompletionDeferralReleased);
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("Dispose")]
    [InlineData("Cancellation")]
    public async Task WhenShutdownBeginsWhileAStartIsBlocked_ThenReattachmentDefersNoStartupCompletion(string shutdown)
    {
        // Arrange
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await fixture.Handler.StartAsync(cancellation.Token);
        var subject = new Person(fixture.Context);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = new ProbeService(() => "blocking", startup: releaseStart.Task);
        subject.AttachHostedService(blocking);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var deferral = fixture.Context.DeferHostedServiceStarts();
        var deferred = new ProbeService(() => "deferred", () => stopped.TrySetResult());
        var attachment = subject.AttachHostedServiceAsync(deferred, CancellationToken.None);
        Task? stopping = null;

        try
        {
            // Act
            if (shutdown == "Stop")
            {
                stopping = fixture.Handler.StopAsync(CancellationToken.None);
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else if (shutdown == "Dispose")
            {
                ((IDisposable)fixture.Handler).Dispose();
            }
            else
            {
                await cancellation.CancelAsync();
            }
            subject.DetachHostedService(deferred);
            var exception = Record.Exception(() => subject.AttachHostedService(deferred));

            // Assert
            Assert.Null(exception);
            Assert.Equal(2, fixture.CompletionDeferred);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subject.AttachHostedServiceAsync(
                new ProbeService(() => "late"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(2, fixture.CompletionDeferred);
        }
        finally
        {
            releaseStart.TrySetResult();
            await (stopping ?? fixture.Handler.StopAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attachment.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, fixture.CompletionDeferralReleased);
        Assert.False(deferred.Started.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenDeferredServiceIsDetached_ThenItsStartIsCanceledBeforeDeferralRelease(bool reattach)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var events = new List<string>();
        var service = new ProbeService(() => { events.Add("start"); return "started"; }, () => events.Add("stop"));
        var subject = new Person(fixture.Context);
        Task? secondAttachment = null;

        // Act
        using (var deferral = fixture.Context.DeferHostedServiceStarts())
        {
            var firstAttachment = subject.AttachHostedServiceAsync(service, CancellationToken.None);
            var detachment = subject.DetachHostedServiceAsync(service, CancellationToken.None);
            if (reattach)
            {
                secondAttachment = subject.AttachHostedServiceAsync(service, CancellationToken.None);
            }
            await detachment.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstAttachment.WaitAsync(TimeSpan.FromSeconds(10)));
            await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref fixture.CompletionDeferralReleased) == 1);
            Assert.False(service.Started.Task.IsCompleted);
        }
        if (secondAttachment is not null)
        {
            await secondAttachment.WaitAsync(TimeSpan.FromSeconds(10));
        }
        // A queued independent start is a barrier for all previously eligible actions.
        await subject.AttachHostedServiceAsync(new ProbeService(() => "barrier"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(reattach ? new[] { "stop", "start" } : new[] { "stop" }, events);
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref fixture.CompletionDeferralReleased) == fixture.CompletionDeferred);
        Assert.Equal(fixture.CompletionDeferred, fixture.CompletionDeferralReleased);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenADeferralReleasesSeveralStarts_ThenTheyRunInAttachmentOrder(bool nested)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var events = new List<string>();
        var subject = new Person(fixture.Context);
        var attachments = new List<Task>();

        // Act
        using (var outer = fixture.Context.DeferHostedServiceStarts())
        {
            using (var inner = nested ? fixture.Context.DeferHostedServiceStarts() : null)
            {
                attachments.Add(subject.AttachHostedServiceAsync(new ProbeService(() => { events.Add("first"); return "first"; }), CancellationToken.None));
            }
            attachments.Add(subject.AttachHostedServiceAsync(new ProbeService(() => { events.Add("second"); return "second"; }), CancellationToken.None));
            Assert.Equal(2, fixture.CompletionDeferred);
            Assert.Equal(0, fixture.CompletionDeferralReleased);
        }
        await Task.WhenAll(attachments).WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.AllCompletionDeferralsReleased.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(new[] { "first", "second" }, events);
        Assert.Equal(2, fixture.CompletionDeferralReleased);
    }

    [Fact]
    public async Task WhenHandlerStartsInsideAnOpenDeferral_ThenTheActionLoopDoesNotInheritIt()
    {
        // Arrange
        await using var fixture = new Fixture();
        var subject = new Person(fixture.Context);
        var child = new ProbeService(() => "child");
        var parent = new ProbeService(() =>
        {
            subject.AttachHostedService(child);
            return "parent";
        });

        // Attached from a flow created before the deferral below, so the deferral never captures this
        // attach and the loop's own flow is the only thing that can defer the child.
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var independentFlow = Task.Run(async () =>
        {
            await proceed.Task;
            await subject.AttachHostedServiceAsync(parent, CancellationToken.None);
        });

        // Act - the deferral stays open across the start, so a loop that inherited the flow it was
        // started in would capture the child that parent attaches inside its own StartAsync and
        // hold it until this deferral is disposed.
        using (fixture.Context.DeferHostedServiceStarts())
        {
            await fixture.Handler.StartAsync(CancellationToken.None);
            proceed.SetResult();
            await independentFlow.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.Equal("child", await child.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Theory]
    [InlineData("OutOfOrder")]
    [InlineData("OtherFlow")]
    [InlineData("Twice")]
    public async Task WhenADeferralIsDisposedIrregularly_ThenCapturedAndLaterServicesCanStart(string disposal)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var subject = new Person(fixture.Context);
        var captured = new ProbeService(() => "captured");
        var outer = fixture.Context.DeferHostedServiceStarts();
        var inner = fixture.Context.DeferHostedServiceStarts();
        subject.AttachHostedService(captured);

        // Act
        var irregular = Record.Exception(() =>
        {
            switch (disposal)
            {
                case "OutOfOrder": outer!.Dispose(); inner!.Dispose(); break;
                case "OtherFlow": Task.Run(() => inner!.Dispose()).GetAwaiter().GetResult(); inner!.Dispose(); outer!.Dispose(); break;
                default: inner!.Dispose(); inner.Dispose(); outer!.Dispose(); outer.Dispose(); break;
            }
        });

        // Assert
        Assert.Null(irregular);
        Assert.Equal("captured", await captured.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var later = new ProbeService(() => "later");
        await subject.AttachHostedServiceAsync(later, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenAStopIsStillQueuedAtShutdown_ThenTheDetachedServiceIsStillStopped()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var subject = new Person(fixture.Context);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detached = new ProbeService(() => "detached", () => stopped.TrySetResult());
        await subject.AttachHostedServiceAsync(detached, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Occupies the loop, so the stop queued below is still waiting when shutdown begins.
        var blocking = new ProbeService(() => "blocking", startup: releaseStart.Task);
        subject.AttachHostedService(blocking);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        subject.DetachHostedService(detached);

        // Act
        var stopping = fixture.Handler.StopAsync(CancellationToken.None);
        releaseStart.SetResult();

        // Assert
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenAnAwaitedAttachResumes_ThenItDoesNotOccupyTheActionLoop()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var subject = new Person(fixture.Context);
        var next = new ProbeService(() => "next");

        // Act - the continuation attaches another service and waits for it, so it can only finish
        // if the loop is free to run that start rather than being inside this continuation.
        var attaching = Task.Run(async () =>
        {
            await subject.AttachHostedServiceAsync(new ProbeService(() => "first"), CancellationToken.None);
            subject.AttachHostedService(next);
            return next.Started.Task.Wait(TimeSpan.FromSeconds(5));
        });

        // Assert
        Assert.True(await attaching.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task WhenReleasingOneCompletionDeferralThrows_ThenTheOtherCompletionDeferralsAreStillReleased()
    {
        // Arrange
        await using var fixture = new Fixture();
        var lastReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Context.AddService<IStartupCompletion>(new ReleaseThrowingCompletion());
        fixture.Context.AddService<IStartupCompletion>(new ReleaseSignalingCompletion(lastReleased));
        await fixture.Handler.StartAsync(CancellationToken.None);
        var subject = new Person(fixture.Context);

        // Act
        await subject.AttachHostedServiceAsync(new ProbeService(() => "started"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await fixture.AllCompletionDeferralsReleased.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await lastReleased.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenDeferringStartupCompletionFails_ThenTheServiceIsNotLeftAttachedAndAReattachStartsIt(bool awaitAttach)
    {
        // Arrange
        await using var fixture = new Fixture();
        var deferFailing = new DeferFailingCompletion { IsFailing = true };
        fixture.Context.AddService<IStartupCompletion>(deferFailing);
        await fixture.Handler.StartAsync(CancellationToken.None);
        var subject = new Person(fixture.Context);
        var service = new ProbeService(() => "started");

        Task AttachAsync()
        {
            if (awaitAttach)
            {
                return subject.AttachHostedServiceAsync(service, CancellationToken.None);
            }

            subject.AttachHostedService(service);
            return Task.CompletedTask;
        }

        // Act
        var failure = await Record.ExceptionAsync(AttachAsync);
        var attachedAfterFailure = subject.GetAttachedHostedServices();
        deferFailing.IsFailing = false;
        await AttachAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Empty(attachedAfterFailure);
        Assert.Equal("started", await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await AsyncTestHelpers.WaitUntilAsync(() =>
            Volatile.Read(ref fixture.CompletionDeferralReleased) == Volatile.Read(ref fixture.CompletionDeferred));
    }

    private sealed class DeferFailingCompletion : IStartupCompletion
    {
        public volatile bool IsFailing;

        public IDisposable Defer() => IsFailing
            ? throw new InvalidOperationException("Defer failed.")
            : new CompletionDeferral(() => { });
    }

    private sealed class ReleaseThrowingCompletion : IStartupCompletion
    {
        public IDisposable Defer() => new CompletionDeferral(() => throw new InvalidOperationException("Release failed."));
    }

    private sealed class ReleaseSignalingCompletion(TaskCompletionSource released) : IStartupCompletion
    {
        public IDisposable Defer() => new CompletionDeferral(() => released.TrySetResult());
    }

    private sealed class Fixture : IAsyncDisposable, IStartupCompletion
    {
        private readonly ServiceProvider _provider;
        public IInterceptorSubjectContext Context { get; }
        public IHostedService Handler { get; }
        public int CompletionDeferred;
        public int CompletionDeferralReleased;
        public TaskCompletionSource AllCompletionDeferralsReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture()
        {
            var services = new ServiceCollection().AddLogging();
            Context = InterceptorSubjectContext.Create().WithHostedServices(services);
            Context.AddService<IStartupCompletion>(this);
            _provider = services.BuildServiceProvider();
            Handler = Assert.Single(_provider.GetServices<IHostedService>());
        }

        public IDisposable Defer()
        {
            Interlocked.Increment(ref CompletionDeferred);
            return new CompletionDeferral(() =>
            {
                if (Interlocked.Increment(ref CompletionDeferralReleased) == Volatile.Read(ref CompletionDeferred))
                {
                    AllCompletionDeferralsReleased.TrySetResult();
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            await Handler.StopAsync(CancellationToken.None);
            await _provider.DisposeAsync();
        }
    }

    private sealed class CompletionDeferral(Action released) : IDisposable
    {
        public void Dispose() => released();
    }

    private sealed class ProbeService(Func<string> readConfiguration, Action? stopped = null, Task? startup = null, Action<CancellationToken>? starting = null) : IHostedService
    {
        public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken cancellationToken)
        {
            starting?.Invoke(cancellationToken);
            Started.TrySetResult(readConfiguration());
            return startup ?? Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            stopped?.Invoke();
            return Task.CompletedTask;
        }
    }
}
