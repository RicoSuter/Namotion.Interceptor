using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

/// <summary>
/// The ordering and race guarantees of the handler. Every test here drives the interleaving through a
/// seam (<c>HostedServiceTarget.TransitionGate</c>, <c>HostedServiceHandler.DrainGate</c> or the
/// startup gate) rather than through delays, so the interleaving under test provably happens.
/// </summary>
public class HostedServiceHandlerRaceTests
{
    [Fact]
    public async Task WhenAReAttachLandsWhileTheSubjectStopIsHeld_ThenAFreshInstanceRunsAndTheOldOneIsDisposed()
    {
        // Arrange - holding the subject's stop is what makes the re-attach provably land mid-stop.
        // Without the hold the test passes while the move is broken.
        await RunWithAppLifecycleAsync(async context =>
        {
            var parent = new HostedParent(context);
            var child = new CountingHostedSubject();
            var created = new ConcurrentQueue<TrackedBackgroundService>();

            child.AttachHostedService(() =>
            {
                var instance = new TrackedBackgroundService();
                created.Enqueue(instance);
                return instance;
            });

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(
                () => child.StartCount == 1 && created.ToArray() is [{ IsStarted: true }]);

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ((IInterceptorSubject)child).TryGetSubjectTarget()!.TransitionGate = () => release.Task;

            // Act - both graph moves are made while the subject's stop is held, so the re-attach's
            // create-and-start is queued behind the detach's stop on the attachment's chain.
            parent.Child = null;
            parent.Child = child;
            release.SetResult();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => created.ToArray() is [_, { IsStarted: true }],
                message: "The re-attach did not create a second instance.");

            await AsyncTestHelpers.WaitUntilAsync(
                () => created.ToArray()[0].IsDisposed,
                message: "The pre-detach instance was never disposed.");

            var instances = created.ToArray();
            Assert.False(instances[1].IsDisposed);
            Assert.Equal(2, child.StartCount);
        });
    }

    [Fact]
    public async Task WhenAnAttachmentIsDetachedBeforeItsStartIsAppended_ThenNothingIsStarted()
    {
        // Arrange - the window between publishing the attachment and appending its start. A detach
        // that lands inside it removes the attachment from the subject, so the start it leaves
        // running is reachable from nothing: a later context detach enumerates no attachment for it
        // and never stops it. Taking a startup hold is the only user code the attach path runs inside
        // that window, so the deferrer drives the interleaving rather than a delay.
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var detacher = new CallbackStartupDeferrer();
        context.AddService<IStartupCompletionDeferrer>(detacher);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            var parent = new Parent(context);
            var child = new Person();
            parent.Child = child;

            var created = 0;
            detacher.OnDefer = () =>
            {
                foreach (var published in child.GetHostedServiceAttachments())
                {
                    child.DetachHostedService(published);
                }
            };

            // Act
            var attachment = child.AttachHostedService(() =>
            {
                Interlocked.Increment(ref created);
                return new TrackedBackgroundService();
            });

            // Assert - an empty transition on the target's chain drains whatever the attach appended,
            // so the count is read after any start would have run rather than after a delay.
            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            await target.AppendAsync(_ => Task.CompletedTask, CancellationToken.None);

            Assert.Equal(1, detacher.Taken);
            Assert.Empty(child.GetHostedServiceAttachments());
            Assert.Equal(0, Volatile.Read(ref created));
            Assert.Null(attachment.Current);
            Assert.Null(target.Owner);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAnAwaitedAttachmentIsDetachedBeforeItsStartIsAppended_ThenNothingIsStarted()
    {
        // Arrange - the same window on the awaiting overload, which appends through the same call.
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var detacher = new CallbackStartupDeferrer();
        context.AddService<IStartupCompletionDeferrer>(detacher);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            var parent = new Parent(context);
            var child = new Person();
            parent.Child = child;

            var created = 0;
            detacher.OnDefer = () =>
            {
                foreach (var published in child.GetHostedServiceAttachments())
                {
                    child.DetachHostedService(published);
                }
            };

            // Act
            var attachment = await child.AttachHostedServiceAsync(() =>
            {
                Interlocked.Increment(ref created);
                return new TrackedBackgroundService();
            }, CancellationToken.None);

            // Assert
            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            await target.AppendAsync(_ => Task.CompletedTask, CancellationToken.None);

            Assert.Equal(1, detacher.Taken);
            Assert.Empty(child.GetHostedServiceAttachments());
            Assert.Equal(0, Volatile.Read(ref created));
            Assert.Null(attachment.Current);
            Assert.Null(target.Owner);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAnAttachmentIsDetachedByTheAwaitingOverloadBeforeItsStartIsAppended_ThenNothingIsStarted()
    {
        // Arrange - the same window, reached through the awaiting detach overload. Both overloads mark
        // the target before appending their stop, and each mark has to be pinned separately: the two
        // tests above drive the window through the synchronous overload only, so deleting the mark from
        // DetachHostedServiceAsync alone leaves them green.
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var detacher = new CallbackStartupDeferrer();
        context.AddService<IStartupCompletionDeferrer>(detacher);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            var parent = new Parent(context);
            var child = new Person();
            parent.Child = child;

            var created = 0;
            var detaches = new ConcurrentQueue<Task<bool>>();
            detacher.OnDefer = () =>
            {
                foreach (var published in child.GetHostedServiceAttachments())
                {
                    // Not awaited here: the detach runs synchronously up to and past its append, which
                    // is the whole window, and awaiting it from inside the hold would park the attach
                    // that is taking the hold. The tasks are awaited below instead.
                    detaches.Enqueue(child.DetachHostedServiceAsync(published, CancellationToken.None));
                }
            };

            // Act
            var attachment = child.AttachHostedService(() =>
            {
                Interlocked.Increment(ref created);
                return new TrackedBackgroundService();
            });

            // Assert - an empty transition on the target's chain drains whatever the attach appended,
            // so the count is read after any start would have run rather than after a delay.
            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            await target.AppendAsync(_ => Task.CompletedTask, CancellationToken.None);

            Assert.All(await Task.WhenAll(detaches), Assert.True);
            Assert.Equal(1, detacher.Taken);
            Assert.Empty(child.GetHostedServiceAttachments());
            Assert.Equal(0, Volatile.Read(ref created));
            Assert.Null(attachment.Current);
            Assert.Null(target.Owner);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAnExplicitDetachRacesTheHostDrain_ThenTheInstanceIsDisposedOnce()
    {
        // Arrange - two stops reach the same instance, so stop and dispose have to be idempotent per
        // target. The seam holds the drain's stop inside its body, so the explicit detach's stop is
        // provably queued behind it rather than merely near it.
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var host = builder.Build();
        await host.StartAsync();

        var parent = new Parent(context);
        var child = new Person();
        var instance = new TrackedBackgroundService();
        var attachment = child.AttachHostedService(() => instance);

        parent.Child = child;
        await AsyncTestHelpers.WaitUntilAsync(() => instance.IsStarted);

        var target = ((IHostedServiceAttachmentTarget)attachment).Target;
        var drainStopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.TransitionGate = () =>
        {
            drainStopEntered.TrySetResult();
            return release.Task;
        };

        // Act
        var stopping = host.StopAsync();
        await drainStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The subject is still in the graph, so the explicit detach still resolves the handler.
        var detached = child.DetachHostedServiceAsync(attachment, CancellationToken.None);
        release.SetResult();

        // Assert
        await stopping;
        Assert.True(await detached);
        Assert.True(instance.IsDisposed);
        Assert.Equal(1, instance.DisposeCount);
    }

    [Fact]
    public async Task WhenAnAttachmentIsAddedDuringTheDrain_ThenNothingIsStarted()
    {
        // Arrange - the drain is held between BeginDraining and the liveness clear, so the attach
        // provably lands inside the drain window rather than near it: the gate is already draining
        // while the subject is still live, which is the interleaving the liveness check alone cannot
        // reject. A start that reaches the chain anyway is caught a second time by the gate re-read in
        // the start body, which is what a start queued before the drain depends on.
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var host = builder.Build();
        await host.StartAsync();

        var parent = new Parent(context);
        var child = new Person();
        parent.Child = child;

        var handler = context.TryGetService<HostedServiceHandler>()!;
        var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.DrainGate = () =>
        {
            drainEntered.TrySetResult();
            return releaseDrain.Task;
        };

        // Act
        var stopping = host.StopAsync();
        await drainEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var created = 0;
        var attachment = child.AttachHostedService(() =>
        {
            Interlocked.Increment(ref created);
            return new TrackedBackgroundService();
        });

        // An empty transition behind the start on the same chain, awaited before the drain is let
        // go: this is what pins the start body inside the window rather than merely near it.
        await ((IHostedServiceAttachmentTarget)attachment).Target
            .AppendAsync(_ => Task.CompletedTask, CancellationToken.None);

        releaseDrain.SetResult();

        // Assert - the drain awaits the stop it appends for the new target, and that stop is queued
        // behind the new target's start, so awaiting the drain is a full quiesce of that chain.
        await stopping;
        Assert.Equal(0, Volatile.Read(ref created));
        Assert.Null(attachment.Current);
    }

    [Fact]
    public async Task WhenAnAttachmentIsAddedDuringTheDrain_ThenTheDrainingHandlerTakesNoOwnership()
    {
        // Arrange - the same drain window, read for the other half of the damage. Nothing a draining
        // handler owns can ever start, and its release loop covers only the targets its own snapshot
        // held, so a target taken past that point stays owned by a dead handler and no later handler
        // can ever win the compare and exchange for it. The public attach paths have to reject the
        // window themselves; the liveness flag they used to check is still set here.
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var host = builder.Build();
        await host.StartAsync();

        var parent = new Parent(context);
        var child = new Person();
        parent.Child = child;

        var handler = context.TryGetService<HostedServiceHandler>()!;
        var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.DrainGate = () =>
        {
            drainEntered.TrySetResult();
            return releaseDrain.Task;
        };

        var stopping = host.StopAsync();
        await drainEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(handler.IsLive(child), "The drain cleared liveness early, so the window under test is unreachable.");

        // Act
        var attachment = child.AttachHostedService(() => new TrackedBackgroundService());

        // Assert - read while the drain is still held. Once it is let go it happens to release this
        // target too, because a take this early is still inside the snapshot it takes next, so the
        // ownership is only observable here.
        Assert.Null(((IHostedServiceAttachmentTarget)attachment).Target.Owner);

        releaseDrain.SetResult();
        await stopping;

        Assert.Null(attachment.Current);
    }

    [Fact]
    public async Task WhenAnAttachmentIsAddedAfterTheSubjectDetached_ThenNothingIsStarted()
    {
        // Arrange - liveness is per subject, which is what makes this case fail closed. Keyed per
        // target, or read as target ownership, the attach would pass its own check: it takes the
        // ownership of the fresh target itself. The subject is constructed with the context, so its
        // own context keeps resolving the handler after the graph detach and the attach really does
        // reach the handler.
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            var parent = new Parent(context);
            var child = new Person(context);
            parent.Child = child;
            parent.Child = null;

            var created = 0;

            // Act
            var attachment = child.AttachHostedService(() =>
            {
                Interlocked.Increment(ref created);
                return new TrackedBackgroundService();
            });

            // Assert - an empty transition on the target's chain drains anything the attach appended,
            // so the count is read after any start would have run rather than after a delay.
            await ((IHostedServiceAttachmentTarget)attachment).Target
                .AppendAsync(_ => Task.CompletedTask, CancellationToken.None);

            Assert.Equal(0, Volatile.Read(ref created));
            Assert.Null(attachment.Current);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAQueuedStartRunsAfterTheSubjectDetached_ThenNothingIsStarted()
    {
        // Arrange - the host is not started, so the startup gate holds every start body at a known
        // point. That is what lets the detach provably overtake a start that is already queued.
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var host = builder.Build();

        var parent = new Parent(context);
        var child = new Person(context);
        parent.Child = child;

        var created = 0;
        var attachment = child.AttachHostedService(() =>
        {
            Interlocked.Increment(ref created);
            return new TrackedBackgroundService();
        });

        // Act
        parent.Child = null;
        await host.StartAsync();

        try
        {
            // Assert
            await ((IHostedServiceAttachmentTarget)attachment).Target
                .AppendAsync(_ => Task.CompletedTask, CancellationToken.None);

            Assert.Equal(0, Volatile.Read(ref created));
            Assert.Null(attachment.Current);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAStopIsStillQueuedWhenTheDrainCompletes_ThenItStillStopsAndDisposes()
    {
        // Arrange - the detached subject's stop and its attachment's stop are both queued while the
        // drain snapshots an empty running set, so both run with the gate already Drained.
        var (child, created, release) = await ArrangeDetachedSubjectWithTheDrainCompletedAsync();

        // Act
        release.SetResult();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => created.ToArray() is [{ IsStopped: true, IsDisposed: true }],
            message: "The queued stop was dropped at Drained, so the attachment was never stopped and never disposed.");

        Assert.Equal(1, child.StopCount);
    }

    [Fact]
    public async Task WhenAStopRanAfterTheDrain_ThenTheNextHandlerStartsTheSubjectAndItsAttachmentAgain()
    {
        // Arrange - the permanent half of the same defect. A stop dropped at Drained leaves Current
        // set, and the one instance guard then skips every later start, so the subject sits in a live
        // graph with nothing running.
        var (child, created, release) = await ArrangeDetachedSubjectWithTheDrainCompletedAsync();
        release.SetResult();

        var builder = Host.CreateApplicationBuilder();

        var secondContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var secondHost = builder.Build();
        await secondHost.StartAsync();

        try
        {
            // Act - the new starts queue behind the released stops, on the same two chains.
            var secondParent = new HostedParent(secondContext);
            secondParent.Child = child;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => child.StartCount == 2 && created.ToArray() is [_, { IsStarted: true }],
                message: "The second handler started nothing: the dropped stop left both targets holding a stale instance.");

            Assert.True(created.ToArray()[0].IsDisposed);
        }
        finally
        {
            await secondHost.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAQueuedStartIsSkippedByTheDrain_ThenAnEarlierFaultSurvives()
    {
        // Arrange - a start that never creates anything must not clear the fault a caller has not
        // read yet. The drain skips it through whichever guard it reaches first, the gate re-read or
        // the cleared liveness, and the fault has to survive either way. Two seams: the transition
        // seam queues the start, and the drain seam proves the drain has begun when it runs.
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var host = builder.Build();
        await host.StartAsync();

        var parent = new Parent(context);
        var child = new Person();
        var shouldThrow = true;

        var attachment = child.AttachHostedService(() =>
        {
            if (shouldThrow)
            {
                shouldThrow = false;
                throw new InvalidOperationException("first attempt fails");
            }

            return new TrackedBackgroundService();
        });

        parent.Child = child;
        await AsyncTestHelpers.WaitUntilAsync(() => attachment.Fault is not null);

        var target = ((IHostedServiceAttachmentTarget)attachment).Target;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.TransitionGate = () => release.Task;

        var handler = context.TryGetService<HostedServiceHandler>()!;
        var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.DrainGate = () =>
        {
            drainEntered.TrySetResult();
            return releaseDrain.Task;
        };

        // Act - the re-attach's start is queued behind the held stop and runs once draining began.
        parent.Child = null;
        parent.Child = child;

        var stopping = host.StopAsync();
        await drainEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        release.SetResult();
        releaseDrain.SetResult();

        // Assert - the drain appends its own stop behind that start and awaits it, so the start has
        // provably run by the time the shutdown returns.
        await stopping;
        Assert.NotNull(attachment.Fault);
    }

    [Fact]
    public async Task WhenASubjectLeavesTheGraph_ThenItStopsBeforeItsAttachmentIsDisposed()
    {
        // Arrange - the context detach half of the ordering. A hosted subject's stop is slow, because
        // BackgroundService.StopAsync awaits its execute task, and the attachments it uses must not be
        // disposed underneath it while it unwinds. The shutdown path builds the same shape from its own
        // code, so it pins nothing here: dropping the wait DetachSubject passes leaves the drain test
        // below green. The hold makes the window the subject is inside observable rather than timed.
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            var parent = new HostedParent(context);
            var child = new CountingHostedSubject();
            var instance = new TrackedBackgroundService();
            var attachment = child.AttachHostedService(() => instance);

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => child.StartCount == 1 && instance.IsStarted);

            var subjectStopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            child.StopHold = () =>
            {
                subjectStopEntered.TrySetResult();
                return release.Task;
            };

            // Act
            parent.Child = null;
            await subjectStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Assert - an unordered detach clears Current at the top of the attachment's stop body,
            // which runs the moment that stop is appended, a whole transition delay before the
            // subject's own StopAsync is entered.
            Assert.NotNull(attachment.Current);
            Assert.False(instance.IsStopped);
            Assert.False(instance.IsDisposed);

            release.SetResult();

            await AsyncTestHelpers.WaitUntilAsync(
                () => instance.IsStopped && instance.IsDisposed,
                message: "The attachment was never stopped and disposed after the subject's stop returned.");

            Assert.Equal(1, child.StopCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenTheHostDrains_ThenASubjectStopsBeforeItsAttachmentIsDisposed()
    {
        // Arrange - shutdown shares the ordering hazard of a context detach: a hosted subject's stop
        // is slow, and the attachments it uses must not be disposed underneath it while it unwinds.
        // The hold makes the window the subject is inside observable rather than timed.
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var host = builder.Build();
        await host.StartAsync();

        var parent = new HostedParent(context);
        var child = new CountingHostedSubject();
        var instance = new TrackedBackgroundService();
        var attachment = child.AttachHostedService(() => instance);

        parent.Child = child;
        await AsyncTestHelpers.WaitUntilAsync(() => child.StartCount == 1 && instance.IsStarted);

        var subjectStopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        child.StopHold = () =>
        {
            subjectStopEntered.TrySetResult();
            return release.Task;
        };

        // Act
        var stopping = host.StopAsync();
        await subjectStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert - an unordered drain clears Current at the top of the attachment's stop body, which
        // runs the moment that stop is appended, a whole transition delay before the subject's own
        // StopAsync is entered.
        Assert.NotNull(attachment.Current);
        Assert.False(instance.IsStopped);
        Assert.False(instance.IsDisposed);

        release.SetResult();
        await stopping;

        Assert.True(instance.IsStopped);
        Assert.True(instance.IsDisposed);
    }

    [Fact]
    public async Task WhenAStopIsInFlightWhenTheHostDrains_ThenTheDrainWaitsForIt()
    {
        // Arrange - a stop queued before the drain left the running set when it was appended, so the
        // drain's own snapshot cannot see it, and the host disposes the service provider as soon as
        // the drain returns. The second subject is what makes the ordering observable: the drain
        // releases the ownership of the targets it snapshotted only once it has waited for
        // everything, so that owner is still set when the queued stop finally runs.
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var host = builder.Build();
        await host.StartAsync();

        var detachingParent = new HostedParent(context);
        var detaching = new CountingHostedSubject();
        detachingParent.Child = detaching;
        await AsyncTestHelpers.WaitUntilAsync(() => detaching.StartCount == 1);

        var remainingParent = new Parent(context);
        var remaining = new Person();
        var remainingInstance = new TrackedBackgroundService();
        var remainingAttachment = remaining.AttachHostedService(() => remainingInstance);
        remainingParent.Child = remaining;
        await AsyncTestHelpers.WaitUntilAsync(() => remainingInstance.IsStarted);

        var remainingTarget = ((IHostedServiceAttachmentTarget)remainingAttachment).Target;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerWhenTheQueuedStopRan =
            new TaskCompletionSource<HostedServiceHandler?>(TaskCreationOptions.RunContinuationsAsynchronously);

        ((IInterceptorSubject)detaching).TryGetSubjectTarget()!.TransitionGate = async () =>
        {
            await release.Task;
            ownerWhenTheQueuedStopRan.TrySetResult(remainingTarget.Owner);
        };

        detachingParent.Child = null;

        // Act
        var stopping = host.StopAsync();
        await AsyncTestHelpers.WaitUntilAsync(
            () => remainingInstance.IsDisposed,
            message: "The drain never ran the stops it snapshotted itself.");

        release.SetResult();
        await stopping;

        // Assert
        var owner = await ownerWhenTheQueuedStopRan.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(owner);
        Assert.Equal(1, detaching.StopCount);
    }

    [Fact]
    public async Task WhenASubjectEntersTheGraph_ThenItsStartupHoldIsTakenBeforeTheGraphWriteReturns()
    {
        // Arrange - the hold closes the window in which "the graph has finished starting" can be
        // reached while a start is still queued, so it has to exist by the time the graph write
        // returns. That is the constraint on where the hold may be taken, and it is why the take is
        // still inside the lifecycle lock: the event that appends the start arrives already inside
        // that lock, so taking the hold anywhere later reopens the window.
        var (host, context, deferrer) = await StartHostWithDeferrerAsync();

        try
        {
            var parent = new Parent(context);
            var child = new Person();
            var attachment = child.AttachHostedService(() => new TrackedBackgroundService());

            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            target.TransitionGate = () => release.Task;

            // Act - the start is appended while the graph write runs, and its body is held at the
            // seam, so the hold is read while the start it belongs to is provably still pending.
            parent.Child = child;

            // Assert
            Assert.Equal(1, deferrer.Taken);
            Assert.Equal(1, deferrer.Outstanding);

            release.SetResult();
            await target.AppendAsync(_ => Task.CompletedTask, CancellationToken.None);

            Assert.Equal(0, deferrer.Outstanding);
            Assert.NotNull(attachment.Current);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAQueuedStartIsSkippedByTheDrain_ThenItsStartupHoldIsReleased()
    {
        // Arrange - a hold that outlives the start it belongs to hangs every synchronization wait on
        // that tree forever, which is worse than never having taken it, so every way out of the start
        // body has to release. This is the drain's way out: the start is appended while the gate is
        // Running and its body runs once draining has begun. Two seams, so both halves are pinned
        // rather than timed.
        var (host, context, deferrer) = await StartHostWithDeferrerAsync();

        var parent = new Parent(context);
        var child = new Person();
        var created = 0;

        var attachment = child.AttachHostedService(() =>
        {
            Interlocked.Increment(ref created);
            return new TrackedBackgroundService();
        });

        var target = ((IHostedServiceAttachmentTarget)attachment).Target;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.TransitionGate = () => release.Task;

        parent.Child = child;
        Assert.Equal(1, deferrer.Outstanding);

        var handler = context.TryGetService<HostedServiceHandler>()!;
        var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.DrainGate = () =>
        {
            drainEntered.TrySetResult();
            return releaseDrain.Task;
        };

        // Act
        var stopping = host.StopAsync();
        await drainEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        release.SetResult();
        releaseDrain.SetResult();

        // Assert - the drain appends its own stop behind that start and awaits it, so the start body
        // has provably run by the time the shutdown returns.
        await stopping;

        Assert.Equal(0, Volatile.Read(ref created));
        Assert.Equal(0, deferrer.Outstanding);
    }

    [Fact]
    public async Task WhenAQueuedStartFindsItsSubjectDetached_ThenItsStartupHoldIsReleased()
    {
        // Arrange - the same leak through the liveness guard, which is the way out a graph move takes.
        var (host, context, deferrer) = await StartHostWithDeferrerAsync();

        try
        {
            var parent = new Parent(context);
            var child = new Person();
            var created = 0;

            var attachment = child.AttachHostedService(() =>
            {
                Interlocked.Increment(ref created);
                return new TrackedBackgroundService();
            });

            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            target.TransitionGate = () => release.Task;

            parent.Child = child;
            Assert.Equal(1, deferrer.Outstanding);

            // Act - the detach clears liveness while the start is held at the seam.
            parent.Child = null;
            release.SetResult();

            // Assert
            await target.AppendAsync(_ => Task.CompletedTask, CancellationToken.None);

            Assert.Equal(0, Volatile.Read(ref created));
            Assert.Equal(0, deferrer.Outstanding);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAQueuedStartIsSkippedByTheOneInstanceGuard_ThenItsStartupHoldIsReleased()
    {
        // Arrange - the third way out, and the one no other test reaches: a subject visible from two
        // hosting contexts raises one context attach per context and the OWNING handler sees both, so
        // it appends a second start for a target that is already running. That start skips its work
        // in the body, where the chain serializes the two, and owes the release from there.
        var builder = Host.CreateApplicationBuilder();

        var firstContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var secondContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        // Registered on one context only: the subject's own context reaches it through the fallback,
        // so both handlers resolve the same single deferrer.
        var deferrer = new CallbackStartupDeferrer();
        firstContext.AddService<IStartupCompletionDeferrer>(deferrer);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            var subject = new CountingHostedSubject();
            ((IInterceptorSubject)subject).Context.AddFallbackContext(firstContext);

            var target = ((IInterceptorSubject)subject).TryGetSubjectTarget()!;
            await target.AppendAsync(_ => Task.CompletedTask, CancellationToken.None);

            Assert.Equal(1, subject.StartCount);
            var takenByTheFirstAttach = deferrer.Taken;

            // Act
            ((IInterceptorSubject)subject).Context.AddFallbackContext(secondContext);

            // Assert - an empty transition drains whatever the second attach appended.
            await target.AppendAsync(_ => Task.CompletedTask, CancellationToken.None);

            Assert.Equal(1, subject.StartCount);

            // Exact, because "more than before" is also satisfied by the non owning handler alone: it
            // takes a hold, loses the compare and exchange, and releases the hold again without ever
            // reaching the guard under test. The second attach raises one context attach per handler,
            // so two more holds is the owning handler's queued start plus that refused append.
            Assert.Equal(
                takenByTheFirstAttach + 2,
                deferrer.Taken);

            Assert.Equal(0, deferrer.Outstanding);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task<(IHost Host, IInterceptorSubjectContext Context, CallbackStartupDeferrer Deferrer)>
        StartHostWithDeferrerAsync()
    {
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var deferrer = new CallbackStartupDeferrer();
        context.AddService<IStartupCompletionDeferrer>(deferrer);

        var host = builder.Build();
        await host.StartAsync();
        return (host, context, deferrer);
    }

    /// <summary>
    /// Starts a host over a hosted subject that carries a factory attachment, then detaches the
    /// subject from inside the drain window with its own stop held on the transition seam. The
    /// detach lands after the drain has snapshotted the stops it will wait for and both targets have
    /// left the running set, so the drain waits for neither and reaches Drained with both stops still
    /// queued. Returns before the hold is released.
    /// </summary>
    private static async Task<(CountingHostedSubject Child, ConcurrentQueue<TrackedBackgroundService> Created, TaskCompletionSource Release)>
        ArrangeDetachedSubjectWithTheDrainCompletedAsync()
    {
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var host = builder.Build();
        await host.StartAsync();

        var parent = new HostedParent(context);
        var child = new CountingHostedSubject();
        var created = new ConcurrentQueue<TrackedBackgroundService>();

        child.AttachHostedService(() =>
        {
            var instance = new TrackedBackgroundService();
            created.Enqueue(instance);
            return instance;
        });

        parent.Child = child;
        await AsyncTestHelpers.WaitUntilAsync(
            () => child.StartCount == 1 && created.ToArray() is [{ IsStarted: true }]);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subjectTarget = ((IInterceptorSubject)child).TryGetSubjectTarget()!;
        subjectTarget.TransitionGate = () => release.Task;

        var handler = context.TryGetService<HostedServiceHandler>()!;
        var drainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.DrainGate = () =>
        {
            drainEntered.TrySetResult();
            return releaseDrain.Task;
        };

        var stopping = host.StopAsync();
        await drainEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        parent.Child = null;
        releaseDrain.SetResult();
        await stopping;

        subjectTarget.TransitionGate = null;
        return (child, created, release);
    }

    private static async Task RunWithAppLifecycleAsync(Func<IInterceptorSubjectContext, Task> action)
    {
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
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
