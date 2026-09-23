using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A subject with more than one parent is composed onto the parent it attached through, and must be
/// decomposed from that same context at its last detach, whichever parent lets go last, unless that
/// context belongs to another graph. A leaked composition can close a resolution loop between two
/// contexts.
/// </summary>
public class ContextInheritanceCycleTests
{
    [Fact]
    public void WhenASharedSubjectLosesItsFirstParentBeforeItsLast_ThenItStopsResolvingTheGraphsServices()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var root = new Person(context) { FirstName = "Root" };
        var first = new Person { FirstName = "1st" };
        var shared = new Person { FirstName = "Shd" };

        root.Mother = first;
        first.Mother = shared;
        root.Father = shared;

        // Act: the parent that pulled the subject in is removed first, the other one last.
        first.Mother = null;
        root.Father = null;

        // Assert
        Assert.Equal(0, shared.GetReferenceCount());
        Assert.Empty(((IInterceptorSubject)shared).Context.GetServices<ILifecycleHandler>());
    }

    [Fact]
    public void WhenASharedSubjectLosesItsFirstParentLast_ThenItStopsResolvingTheGraphsServices()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var root = new Person(context) { FirstName = "Root" };
        var first = new Person { FirstName = "1st" };
        var shared = new Person { FirstName = "Shd" };

        root.Mother = first;
        first.Mother = shared;
        root.Father = shared;

        // Act: the same graph, taken apart in the other order.
        root.Father = null;
        first.Mother = null;

        // Assert: the outcome must not depend on the removal order.
        Assert.Equal(0, shared.GetReferenceCount());
        Assert.Empty(((IInterceptorSubject)shared).Context.GetServices<ILifecycleHandler>());
    }

    [Fact]
    public void WhenASharedSubjectLosesItsFirstParentBeforeItsLast_ThenTheGraphsWriteInterceptorsStopFiringForIt()
    {
        // Arrange
        var interceptor = new CountingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();
        context.AddService<IWriteInterceptor>(interceptor);

        var root = new Person(context) { FirstName = "Root" };
        var first = new Person { FirstName = "1st" };
        var shared = new Person { FirstName = "Shd" };

        root.Mother = first;
        first.Mother = shared;
        root.Father = shared;
        first.Mother = null;
        root.Father = null;

        var writesBeforeDetachedWrite = interceptor.WriteCount;

        // Act: a write to a subject every API reports as detached.
        shared.LastName = "X";

        // Assert
        Assert.Equal(0, shared.GetReferenceCount());
        Assert.Equal(writesBeforeDetachedWrite, interceptor.WriteCount);
        Assert.Equal("X", shared.LastName);
    }

    [Fact]
    public void WhenADetachedSubjectIsLaterAttachedBelowItsOwnFormerChild_ThenReadsAndWritesStillWork()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var root = new Person(context) { FirstName = "Root" };
        var first = new Person { FirstName = "1st" };
        var shared = new Person { FirstName = "Shd" };

        root.Mother = first;
        first.Mother = shared;
        root.Father = shared;
        first.Mother = null;
        root.Father = null;
        root.Mother = null;

        // Act: the reverse edge attaches the former parent below its own former child.
        shared.Mother = first;
        root.Mother = shared;
        root.Father = first;
        root.Mother = null;

        // Assert: a leaked composition used to make this a closed resolution loop, so every read
        // and every write on the subject threw. Whether first still resolves the graph's services
        // is not asserted: it is composed onto shared, the parent that pulled it in, and shared has
        // left while first stays attached through Father.
        Assert.Equal(1, first.GetReferenceCount());
        Assert.Equal("1st", first.FirstName);
        first.LastName = "X";
        Assert.Equal("X", first.LastName);
    }

    [Fact]
    public void WhenADetachedSubjectIsLaterAttachedBelowItsOwnFormerChild_ThenTheGraphStaysRepairable()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var root = new Person(context) { FirstName = "Root" };
        var first = new Person { FirstName = "1st" };
        var shared = new Person { FirstName = "Shd" };

        root.Mother = first;
        first.Mother = shared;
        root.Father = shared;
        first.Mother = null;
        root.Father = null;
        root.Mother = null;
        shared.Mother = first;
        root.Mother = shared;
        root.Father = first;
        root.Mother = null;

        // Act: the object model used to be unable to take this graph apart again.
        root.Father = null;

        // Assert
        Assert.Equal(0, first.GetReferenceCount());
        Assert.Empty(((IInterceptorSubject)first).Context.GetServices<ILifecycleHandler>());
    }

    [Fact]
    public void WhenASubtreeBuiltUnderAnotherContextIsGraftedAndRemoved_ThenItResolvesTheOuterContextOnlyWhileGrafted()
    {
        // Arrange: a subtree that already lives in a context of its own is assigned into a graph
        // rooted at another context, which it must resolve through for as long as it is referenced
        // from there, and no longer.
        var outerContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();
        outerContext.AddService(new OuterMarker());

        var innerContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();
        innerContext.AddService(new InnerMarker());

        var outerRoot = new Person(outerContext) { FirstName = "Out" };
        var innerRoot = new Person(innerContext) { FirstName = "In" };
        var innerChild = new Person { FirstName = "Chd" };
        innerRoot.Mother = innerChild;

        // Act
        outerRoot.Mother = innerRoot;

        // Assert
        Assert.Single(((IInterceptorSubject)innerRoot).Context.GetServices<OuterMarker>());
        Assert.Single(((IInterceptorSubject)innerRoot).Context.GetServices<InnerMarker>());
        Assert.Single(((IInterceptorSubject)innerChild).Context.GetServices<OuterMarker>());
        Assert.Single(((IInterceptorSubject)innerChild).Context.GetServices<InnerMarker>());

        // Act
        outerRoot.Mother = null;

        // Assert
        Assert.Empty(((IInterceptorSubject)innerRoot).Context.GetServices<OuterMarker>());
        Assert.Single(((IInterceptorSubject)innerRoot).Context.GetServices<InnerMarker>());
        Assert.Empty(((IInterceptorSubject)innerChild).Context.GetServices<OuterMarker>());
        Assert.Single(((IInterceptorSubject)innerChild).Context.GetServices<InnerMarker>());
    }

    [Fact]
    public void WhenASubjectLetsGoOfItselfLast_ThenItStopsResolvingTheGraphsServices()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var root = new Person(context) { FirstName = "Root" };
        var self = new Person { FirstName = "Self" };

        root.Mother = self;
        self.Mother = self;
        root.Mother = null;

        // Act: the last reference is the subject's own, so the parent it detaches from is itself.
        self.Mother = null;

        // Assert
        Assert.Equal(0, self.GetReferenceCount());
        Assert.Empty(((IInterceptorSubject)self).Context.GetServices<ILifecycleHandler>());
    }

    [Fact]
    public void WhenASharedSubjectLastLeavesAnotherGraphThanTheOneItJoinedThrough_ThenItKeepsResolvingTheFirstGraph()
    {
        // Arrange
        var contextX = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();
        contextX.AddService(new OuterMarker());

        var contextY = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();
        contextY.AddService(new InnerMarker());

        var rootX = new Person(contextX) { FirstName = "X" };
        var rootY = new Person(contextY) { FirstName = "Y" };
        var shared = new Person { FirstName = "Shd" };

        rootX.Mother = shared;
        rootY.Mother = shared;
        rootX.Mother = null;

        // Act
        rootY.Mother = null;

        // Assert: decomposing the first graph's context would take its lock from under the second
        // graph's, so the composition is left in place.
        Assert.Equal(0, shared.GetReferenceCount());
        Assert.Single(((IInterceptorSubject)shared).Context.GetServices<OuterMarker>());
        Assert.Empty(((IInterceptorSubject)shared).Context.GetServices<InnerMarker>());
    }

    [Fact]
    public async Task WhenSharedSubjectsLastLeaveTheOtherGraphConcurrently_ThenBothDetachesComplete()
    {
        // Arrange: each subject joined through one graph and is last referenced from the other.
        var gate = new DetachGate();

        var contextX = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();
        contextX.AddService(gate);

        var contextY = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();
        contextY.AddService(gate);

        var rootX = new Person(contextX) { FirstName = "X" };
        var rootY = new Person(contextY) { FirstName = "Y" };
        var joinedThroughX = new Person { FirstName = "S1" };
        var joinedThroughY = new Person { FirstName = "S2" };

        rootX.Mother = joinedThroughX;
        rootY.Mother = joinedThroughX;
        rootX.Mother = null;

        rootY.Father = joinedThroughY;
        rootX.Father = joinedThroughY;
        rootY.Father = null;

        gate.Arm(joinedThroughX, joinedThroughY);

        // Act: the gate holds each detach inside its own graph's lock until both are there. Dedicated
        // threads, so a deadlocked pair is abandoned without starving the pool.
        var detaches = new[]
        {
            Task.Factory.StartNew(() => rootY.Mother = null, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default),
            Task.Factory.StartNew(() => rootX.Father = null, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
        };
        var exception = await Record.ExceptionAsync(() => Task.WhenAll(detaches).WaitAsync(TimeSpan.FromSeconds(10)));

        // Assert
        Assert.True(gate.BothArrived, "The detaches did not overlap, so the test exercised nothing.");
        Assert.Null(exception);
        Assert.Equal(0, joinedThroughX.GetReferenceCount());
        Assert.Equal(0, joinedThroughY.GetReferenceCount());
    }

    private sealed class OuterMarker;

    private sealed class InnerMarker;

    [RunsBefore(typeof(ContextInheritanceHandler))]
    private sealed class DetachGate : ILifecycleHandler
    {
        private readonly Barrier _barrier = new(2);
        private IInterceptorSubject[] _subjects = [];
        private int _arrivals;

        internal bool BothArrived => Volatile.Read(ref _arrivals) == 2;

        internal void Arm(params IInterceptorSubject[] subjects) => _subjects = subjects;

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change is { ReferenceCount: 0, IsPropertyReferenceRemoved: true } &&
                Array.IndexOf(_subjects, change.Subject) >= 0 &&
                _barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
            {
                Interlocked.Increment(ref _arrivals);
            }
        }
    }

    private sealed class CountingWriteInterceptor : IWriteInterceptor
    {
        private int _writeCount;

        internal int WriteCount => Volatile.Read(ref _writeCount);

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref _writeCount);
            next(ref context);
        }
    }
}
