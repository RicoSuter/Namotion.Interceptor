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
        });

        await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "Disposed");
    }

    [Fact]
    public async Task WhenAttachmentIsCreated_ThenTheFactoryProducesTheRunningInstance()
    {
        // Arrange
        await RunWithAppLifecycleAsync(async context =>
        {
            var person = new Person(context);

            // Act
            var attachment = await person.AttachHostedServiceAsync(
                () => new PersonBackgroundService(person), CancellationToken.None);

            // Assert
            Assert.NotNull(attachment.Current);
            Assert.Equal("John", person.FirstName);
        });
    }

    [Fact]
    public async Task WhenSubjectIsDetachedAndReattached_ThenAFreshInstanceRuns()
    {
        // Arrange - this is the whole point of the factory API. The pre-detach instance must be
        // disposed and a NEW one created; restarting the old one is impossible because a disposed
        // connector cannot restart.
        await RunWithAppLifecycleAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var created = new List<TrackedBackgroundService>();

            child.AttachHostedService(() =>
            {
                var instance = new TrackedBackgroundService();
                created.Add(instance);
                return instance;
            });

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => created.Count == 1 && created[0].IsStarted);

            // Act - detach and reattach with no quiescing in between
            parent.Child = null;
            parent.Child = child;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => created.Count == 2 && created[1].IsStarted,
                message: "The re-attach did not create a second instance.");
            await AsyncTestHelpers.WaitUntilAsync(() => created[0].IsDisposed,
                message: "The pre-detach instance was never disposed.");
            Assert.False(created[1].IsDisposed);
        });
    }

    [Fact]
    public async Task WhenSubjectIsDetached_ThenTheInstanceIsDisposedExactlyOnce()
    {
        // Arrange
        await RunWithAppLifecycleAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var instance = new TrackedBackgroundService();
            child.AttachHostedService(() => instance);
            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => instance.IsStarted);

            // Act
            parent.Child = null;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => instance.IsDisposed);
            Assert.Equal(1, instance.DisposeCount);
        });
    }

    [Fact]
    public async Task WhenAttachmentIsDetachedExplicitly_ThenALaterContextAttachStartsNothing()
    {
        // Arrange
        await RunWithAppLifecycleAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var created = 0;
            var attachment = child.AttachHostedService(() =>
            {
                created++;
                return new TrackedBackgroundService();
            });

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => created == 1);

            // Act
            await child.DetachHostedServiceAsync(attachment, CancellationToken.None);
            parent.Child = null;
            parent.Child = child;

            // Assert - the attachment being gone is the deterministic invariant. Counting creations
            // after a yield would assert an absence on a timer and could pass vacuously.
            Assert.Empty(child.GetHostedServiceAttachments());
        });
    }

    [Fact]
    public async Task WhenTheFactoryThrows_ThenTheFaultIsRecordedAndCurrentStaysNull()
    {
        // Arrange
        await RunWithAppLifecycleAsync(async context =>
        {
            var person = new Person(context);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                person.AttachHostedServiceAsync<TrackedBackgroundService>(
                    () => throw new InvalidOperationException("factory failed"), CancellationToken.None));

            Assert.Equal("factory failed", exception.Message);

            // The transactional guarantee: a caller's catch is never left owning an invisible attachment
            Assert.Empty(person.GetHostedServiceAttachments());
        });
    }

    [Fact]
    public async Task WhenAStartFaults_ThenTheInstanceIsDisposed()
    {
        // Arrange - leaving a half started connector undisposed is the leak this design exists to fix
        await RunWithAppLifecycleAsync(async context =>
        {
            var person = new Person(context);
            var instance = new TrackedBackgroundService { ThrowOnStart = true };

            // Act
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                person.AttachHostedServiceAsync(() => instance, CancellationToken.None));

            // Assert
            Assert.True(instance.IsDisposed);
        });
    }

    [Fact]
    public async Task WhenATransitionFaultedEarlier_ThenTheNextSuccessfulOneClearsTheFault()
    {
        // Arrange - a stale Fault would make a later successful attach throw, and the OPC UA wrappers
        // would turn that into Status = Error with a stale message.
        await RunWithAppLifecycleAsync(async context =>
        {
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

            // Act
            parent.Child = null;
            parent.Child = child;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => attachment.Current is not null);
            Assert.Null(attachment.Fault);
        });
    }

    [Fact]
    public async Task WhenHostStops_ThenHandlerCreatedInstancesAreDisposedAndSubjectsAreNot()
    {
        // Arrange
        var instance = new TrackedBackgroundService();
        PersonWithBackgroundService person = null!;

        await RunWithAppLifecycleAsync(async context =>
        {
            person = new PersonWithBackgroundService(context);
            await person.AttachHostedServiceAsync(() => instance, CancellationToken.None);
        });

        // Assert - the container disposes AddSubject singletons, so the claim under test is
        // specifically that the HANDLER did not dispose the subject.
        Assert.True(instance.IsDisposed);
        Assert.False(person.WasDisposedByHandler);
    }

    [Fact]
    public async Task WhenReparentedWithoutReachingZeroReferences_ThenNothingRestarts()
    {
        // Arrange - add-then-remove keeps the reference count above zero, so isLastDetach never fires
        await RunWithAppLifecycleAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var attachment = child.AttachHostedService(() => new TrackedBackgroundService());

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => attachment.Current is not null);
            var original = attachment.Current;

            // Act - add then remove keeps the reference count above zero, so isLastDetach never fires
            parent.SecondChild = child;
            parent.Child = null;

            // Assert - instance identity is deterministic; a creation count after a yield is not
            Assert.Same(original, attachment.Current);
            Assert.False(original!.IsDisposed);
        });
    }

    [Fact]
    public async Task WhenAServiceIsAttachedAfterItsHandlerDrained_ThenTheNextHandlerStartsIt()
    {
        // Arrange - two hosts, the second still running when the first has drained. The subject stays
        // attached to the drained handler's context, so the attach below really does resolve it and
        // the claim under test is reachable.
        var firstBuilder = Host.CreateApplicationBuilder();

        var firstContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(firstBuilder.Services);

        var firstHost = firstBuilder.Build();
        await firstHost.StartAsync();

        var secondBuilder = Host.CreateApplicationBuilder();

        var secondContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(secondBuilder.Services);

        var secondHost = secondBuilder.Build();
        await secondHost.StartAsync();

        try
        {
            var subject = new Person();
            ((IInterceptorSubject)subject).Context.AddFallbackContext(firstContext);
            await firstHost.StopAsync();

            // Act
            var attachment = subject.AttachHostedService(() => new TrackedBackgroundService());
            ((IInterceptorSubject)subject).Context.AddFallbackContext(secondContext);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => attachment.Current is { IsStarted: true },
                message: "The drained handler claimed the target and never released it, so the live handler could not take it.");
        }
        finally
        {
            await secondHost.StopAsync();
        }
    }

    [Fact]
    public async Task WhenTheAttachAwaitIsCancelled_ThenTheStartStillRunsToCompletion()
    {
        // Arrange - the token bounds the caller's wait, not the transition. Aborting the work instead
        // would leave a half started service behind and record the cancellation as a start failure,
        // which the OPC UA wrappers surface as a user visible error status.
        await RunWithAppLifecycleAsync(async context =>
        {
            var person = new Person(context);
            var instance = new TrackedBackgroundService();
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            // Act
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => person.AttachHostedServiceAsync(() => instance, cancellation.Token));

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => instance.IsStarted,
                message: "The cancelled await aborted the start transition instead of only the wait.");

            var attachment = Assert.Single(person.GetHostedServiceAttachments());
            Assert.Same(instance, attachment.Current);
            Assert.Null(attachment.Fault);
        });
    }

    [Fact]
    public void WhenASubjectWithoutHostedServicesIsRead_ThenNoDataEntryIsInserted()
    {
        // Arrange - both reads run on every context detach, under the lifecycle lock, for every
        // subject in the graph.
        var subject = (IInterceptorSubject)new Person();
        var entriesBefore = subject.Data.Count;

        // Act
        var target = subject.TryGetSubjectTarget();
        var attachments = subject.GetHostedServiceAttachments();

        // Assert
        Assert.Null(target);
        Assert.Empty(attachments);
        Assert.Equal(entriesBefore, subject.Data.Count);
    }

    private static async Task RunWithAppLifecycleAsync(Func<IInterceptorSubjectContext, Task> action)
    {
        var builder = Host.CreateApplicationBuilder();

        // WithContextInheritance, not just WithLifecycle: without it a child subject's Context never
        // resolves the handler and every child scenario below is silently unreachable.
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
