using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Regression tests for a defect shipped in 0.9.x and earlier: the context composed onto a subject
/// when it joined the graph could outlive its detach, because it was composed from the parent of the
/// first attach and decomposed against the parent of the last detach. For a subject with more than
/// one parent subject those are different contexts, so the decomposition matched nothing.
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
    public void WhenOnlyLifecycleIsRegistered_ThenNoContextIsComposedAndNothingThrows()
    {
        // Arrange: the control. Without the inheritance handler nothing is composed at all, so the
        // shape is harmless whatever the ownership model does.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var root = new Person(context) { FirstName = "Root" };
        var first = new Person { FirstName = "1st" };
        var shared = new Person { FirstName = "Shd" };

        // Act
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

        // Assert
        Assert.Equal("1st", first.FirstName);
    }

    [Fact]
    public void WhenASubjectReferencesItself_ThenNothingThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var root = new Person(context) { FirstName = "Root" };
        var self = new Person { FirstName = "Slf" };

        // Act
        self.Mother = self;
        root.Mother = self;

        // Assert
        Assert.Equal("Slf", self.FirstName);
    }

    [Fact]
    public void WhenTwoSubjectsReferenceEachOther_ThenNothingThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var first = new Person(context) { FirstName = "1st" };
        var second = new Person { FirstName = "2nd" };

        // Act
        first.Mother = second;
        second.Mother = first;

        // Assert
        Assert.Equal("1st", first.FirstName);
        Assert.Equal("2nd", second.FirstName);
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

    private sealed class OuterMarker;

    private sealed class InnerMarker;

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
