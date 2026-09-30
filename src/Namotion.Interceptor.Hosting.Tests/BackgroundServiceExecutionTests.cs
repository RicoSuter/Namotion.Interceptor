using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Hosting.Tests;

/// <summary>
/// What the handler does with a <c>BackgroundService</c> once its <c>StartAsync</c> has returned.
/// <c>BackgroundService.StartAsync</c> schedules the execution and returns at once, so nothing the
/// execution does is part of the start: a fault in it is observed through the execute task instead,
/// and the token a caller hands an attach must never reach the start, or a cancelled one would leave
/// the execution never entered.
/// </summary>
public class BackgroundServiceExecutionTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAnAttachedExecutionFaultsAtOnce_ThenTheAttachmentSettlesToFaultedWithTheInstanceStoppedAndDisposed(
        bool attachIsAwaited)
    {
        // Arrange - the fault is raised before the execution's first await. BackgroundService.StartAsync
        // returns before the execution runs, so the attachment reads Running first, the fault is
        // observed afterwards, and the awaited attach returns rather than throws.
        await HostingTestHost.RunAsync(async context =>
        {
            var person = new Person(context);
            var exception = new InvalidOperationException("execution failed");
            ScriptedBackgroundService? instance = null;

            ScriptedBackgroundService Factory()
            {
                instance = new ScriptedBackgroundService(_ => throw exception);
                return instance;
            }

            // Act
            var attachment = attachIsAwaited
                ? await person.AttachHostedServiceAsync(Factory, CancellationToken.None)
                : person.AttachHostedService(Factory);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => attachment.GetState(out _) is HostedServiceAttachmentState.Faulted,
                message: "The execution fault was never observed: the attachment did not settle to Faulted.");

            Assert.Same(exception, attachment.Fault);
            Assert.Null(attachment.Current);
            Assert.Single(person.GetHostedServiceAttachments());
            Assert.Equal(1, instance!.StopCount);
            Assert.True(instance.IsDisposed);
        });
    }

    [Fact]
    public async Task WhenAnExecutionIsCancelledByItsOwnCode_ThenTheAttachmentSettlesToFaulted()
    {
        // Arrange - a cancellation no stop asked for, such as an HttpClient timeout escaping the
        // execution, leaves the execute task Canceled rather than Faulted, and the service dead.
        await HostingTestHost.RunAsync(async context =>
        {
            var person = new Person(context);
            var exception = new TaskCanceledException("request timed out");

            // Act
            var attachment = person.AttachHostedService(() => new ScriptedBackgroundService(async _ =>
            {
                await Task.Yield();
                throw exception;
            }));

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => attachment.GetState(out _) is HostedServiceAttachmentState.Faulted,
                message: "The cancelled execution was never observed: the attachment did not settle to Faulted.");

            Assert.Same(exception, attachment.Fault);
            Assert.Null(attachment.Current);
        });
    }

    [Fact]
    public async Task WhenAHostedSubjectsRunFaulted_ThenWaitingForItsStartReturnsFalseWithoutThrowing()
    {
        // Arrange - the start itself succeeded, so a wait for it must not rethrow the run's fault
        await HostingTestHost.RunAsync(async context =>
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var parent = new ScriptedHostedParent(context);
            var subject = new ScriptedHostedSubject();
            subject.Run = _ => throw new InvalidOperationException("execution failed");

            parent.Child = subject;
            var target = ((IInterceptorSubject)subject).TryGetSubjectTarget()!;

            await AsyncTestHelpers.WaitUntilAsync(
                () => target.GetState(out _) is HostedServiceAttachmentState.Faulted,
                message: "The execution fault was never observed: the subject did not settle to Faulted.");
            await target.DrainAsync();

            // Act
            var started = await handler.WaitForStartAsync(subject, CancellationToken.None);

            // Assert
            Assert.False(started);
        });
    }

    [Fact]
    public async Task WhenAnExecutionFaultsWhileItsStopTearsItDown_ThenTheFaultIsLoggedAtDebugAndNotRecorded()
    {
        // Arrange - the run fails on its way out of a stop the handler made, so the fault is the stop's
        // side effect rather than a reason to settle to Faulted, and it is still logged.
        var logs = new CapturingLoggerProvider();
        var builder = HostingTestHost.CreateBuilder();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        var context = HostingTestHost.CreateContext(builder);

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var person = new Person(context);
            var exception = new InvalidOperationException("teardown failed");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var attachment = person.AttachHostedService(() => new ScriptedBackgroundService(async stoppingToken =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    throw exception;
                }
            }));
            await entered.Task.WaitAsync(WaitTimeout);

            // Act
            await person.DetachHostedServiceAsync(attachment, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => logs.Entries.Any(entry => entry.Level == LogLevel.Debug && ReferenceEquals(entry.Exception, exception)),
                message: "The fault the stop's teardown raised was never logged.");

            Assert.Null(attachment.Fault);
            Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAHostedSubjectsExecutionFaultsAtOnce_ThenTheSubjectIsStoppedAndTheNextAttachRestartsIt()
    {
        // Arrange - a subject is restarted in place on the same instance, so the fault of one run and
        // the retry that clears it are both on the one target.
        await HostingTestHost.RunAsync(async context =>
        {
            var parent = new ScriptedHostedParent(context);
            var subject = new ScriptedHostedSubject();
            var exception = new InvalidOperationException("execution failed");
            subject.Run = _ => throw exception;

            // Act
            parent.Child = subject;
            var target = ((IInterceptorSubject)subject).TryGetSubjectTarget()!;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => target.GetState(out _) is HostedServiceAttachmentState.Faulted,
                message: "The execution fault was never observed: the subject did not settle to Faulted.");

            Assert.Same(exception, target.Fault);
            Assert.Null(target.Current);
            Assert.Equal(1, subject.StopCount);

            // Act - the next context attach retries, and this run parks instead of faulting
            subject.Run = null;
            parent.Child = null;
            parent.Child = subject;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => target.Current is not null);
            Assert.Null(target.Fault);
            Assert.Equal(HostedServiceAttachmentState.Running, target.GetState(out _));
            Assert.Equal(1, subject.StopCount);
        });
    }

    [Fact]
    public async Task WhenAnEarlierRunsFaultIsObservedAfterTheSubjectWasRestarted_ThenTheNewRunIsLeftAlone()
    {
        // Arrange - the first run faults on the test's signal rather than on cancellation, so its
        // fault can be timed to land once the restart is already queued. Holding the chain queues the
        // stop and the restart, then the fault is raised, and the transition its observer appends lands
        // behind the restart: the subject instance is the same, only the execute task tells the runs apart.
        await HostingTestHost.RunAsync(async context =>
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var parent = new ScriptedHostedParent(context);
            var subject = new ScriptedHostedSubject();
            var firstRunEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var faultTrigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            subject.Run = async _ =>
            {
                firstRunEntered.TrySetResult();
                await faultTrigger.Task;
                throw new InvalidOperationException("first run failed");
            };

            parent.Child = subject;
            await firstRunEntered.Task.WaitAsync(WaitTimeout);

            // The execution can be entered before StartAsync has returned and assigned ExecuteTask, and
            // the start still counts as in flight until its transition has finished, which the count
            // below would otherwise take for the fault transition.
            var target = ((IInterceptorSubject)subject).TryGetSubjectTarget()!;
            await target.DrainAsync();
            var firstExecuteTask = subject.ExecuteTask!;

            // The second run parks until it is cancelled.
            subject.Run = null;

            // Act
            using var transitions = target.HoldAtTransition();
            parent.Child = null;
            parent.Child = subject;

            faultTrigger.SetResult();
            await AsyncTestHelpers.WaitUntilAsync(
                () => handler.InFlightTransitionCount == 3,
                message: "The fault observer did not append its transition behind the stop and the restart.");

            transitions.Release();
            await target.DrainAsync();

            // Assert
            Assert.Same(subject, target.Current);
            Assert.Null(target.Fault);
            Assert.Equal(HostedServiceAttachmentState.Running, target.GetState(out _));
            Assert.Equal(1, subject.StopCount);
            Assert.NotSame(firstExecuteTask, subject.ExecuteTask);
            Assert.True(firstExecuteTask.IsFaulted);
            Assert.False(subject.ExecuteTask!.IsCompleted);
        });
    }

    [Fact]
    public async Task WhenTheAttachingCallersTokenIsAlreadyCancelled_ThenTheCallerIsCancelledAndTheExecutionStillRuns()
    {
        // Arrange - the token bounds the caller's wait and nothing else. Handed to the start instead,
        // it would make BackgroundService.StartAsync schedule an execution that is cancelled before it
        // is entered, and the start would still record the instance as running. The startup scope parks
        // the start, so the wait is cancelled deterministically rather than by racing the transition.
        await HostingTestHost.RunAsync(async context =>
        {
            var person = new Person(context);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            // Act
            using (context.DeferHostedServiceStartup())
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => person.AttachHostedServiceAsync(
                    () => new ScriptedBackgroundService(async stoppingToken =>
                    {
                        entered.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
                    }),
                    cancellation.Token));
            }

            // Assert
            await entered.Task.WaitAsync(WaitTimeout);

            var attachment = Assert.Single(person.GetHostedServiceAttachments());
            await AsyncTestHelpers.WaitUntilAsync(() => attachment.Current is not null);
            Assert.Null(attachment.Fault);
        });
    }
}
