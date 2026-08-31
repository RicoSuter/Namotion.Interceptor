using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A component snapshot is read before reservations and topology-gate preparation. These tests put
/// a deterministic mutation after one participant's scan and before preparation, so publication
/// must validate that participant rather than trusting a locally stable but subsequently stale
/// snapshot. The last case pins participant-scoped validation: unrelated graph publication is not
/// a reason to repeat user capture.
/// </summary>
public class CaptureValidationTests
{
    private static IInterceptorSubjectContext CreateContext(IWriteInterceptor? interceptor = null)
    {
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        if (interceptor is not null)
        {
            context.AddService(interceptor);
        }

        return context;
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenDescendantOutgoingEdgeChangesAfterCapture_ThenTerminalRecapturesWithoutReplayingInterceptors()
    {
        // Arrange
        var interceptor = new CountingWriteInterceptor();
        var context = CreateContext(interceptor);
        var root = new Person(context) { FirstName = "root" };
        interceptor.Arm(root, nameof(Person.Children));

        var staleChild = new Person { FirstName = "stale" };
        var replacementChild = new Person { FirstName = "replacement" };
        var descendant = new Person { FirstName = "descendant", Father = staleChild };
        var descendantScans = 0;
        AddStructuralGetter(descendant, "CaptureMarker", _ =>
        {
            Interlocked.Increment(ref descendantScans);
            return null;
        });

        var barrier = new CaptureBarrier(() => Volatile.Read(ref descendantScans) > 0);
        var sentinel = new Person { FirstName = "sentinel" };
        AddStructuralGetter(sentinel, "PauseAfterDescendant", barrier.Read);

        Exception? mutationException = null;
        var mutator = new Thread(() =>
        {
            barrier.WaitUntilReached();
            mutationException = Record.Exception(() => descendant.Father = replacementChild);
            barrier.Resume();
        }) { IsBackground = true };

        // Act
        mutator.Start();
        var writeException = Record.Exception(() => root.Children = [sentinel, descendant]);
        var mutationCompleted = mutator.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(mutationCompleted, "the descendant mutator never completed");
        Assert.True(barrier.PrerequisiteObserved,
            "the sentinel ran before the descendant's structural properties were captured");
        Assert.Null(mutationException);
        Assert.Null(writeException);
        Assert.Same(replacementChild, descendant.Father);
        Assert.Same(context, replacementChild.TryGetContext());
        Assert.Null(staleChild.TryGetContext());
        Assert.Equal(2, barrier.ReadCount);
        Assert.Equal(2, descendantScans);
        Assert.Equal(1, interceptor.Count);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenOwnedDescendantDetachesAfterCapture_ThenAttachRecapturesItsOutgoingClosure()
    {
        // Arrange
        var context = CreateContext();
        var child = new Person { FirstName = "child" };
        var descendant = new Person { FirstName = "descendant", Father = child };
        descendant.AttachToContext(context);
        Assert.Same(context, child.TryGetContext());

        var barrier = new CaptureBarrier();
        var sentinel = new Person { FirstName = "sentinel" };
        AddStructuralGetter(sentinel, "PauseAfterOwnedDescendant", barrier.Read);
        var root = new EnumerableChildrenHolder { Children = [sentinel, descendant] };

        Exception? mutationException = null;
        var mutator = new Thread(() =>
        {
            barrier.WaitUntilReached();
            mutationException = Record.Exception(() => descendant.DetachFromContext(context));
            barrier.Resume();
        }) { IsBackground = true };

        // Act
        mutator.Start();
        var attachException = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));
        var mutationCompleted = mutator.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(mutationCompleted, "the descendant detacher never completed");
        Assert.Null(mutationException);
        Assert.Null(attachException);
        Assert.Same(context, root.TryGetContext());
        Assert.Same(context, descendant.TryGetContext());
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
        Assert.Equal(2, barrier.ReadCount);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenUnrelatedGraphChangesAfterCapture_ThenAttachDoesNotRecaptureParticipants()
    {
        // Arrange
        var context = CreateContext();
        var unrelated = new Person(context) { FirstName = "unrelated" };
        var unrelatedChild = new Person { FirstName = "unrelated child" };

        var descendantScans = 0;
        var descendant = new Person { FirstName = "descendant" };
        AddStructuralGetter(descendant, "CaptureMarker", _ =>
        {
            Interlocked.Increment(ref descendantScans);
            return null;
        });

        var barrier = new CaptureBarrier(() => Volatile.Read(ref descendantScans) > 0);
        var sentinel = new Person { FirstName = "sentinel" };
        AddStructuralGetter(sentinel, "PauseAfterCandidate", barrier.Read);
        var root = new EnumerableChildrenHolder { Children = [sentinel, descendant] };

        Exception? mutationException = null;
        var mutator = new Thread(() =>
        {
            barrier.WaitUntilReached();
            mutationException = Record.Exception(() => unrelated.Father = unrelatedChild);
            barrier.Resume();
        }) { IsBackground = true };

        // Act
        mutator.Start();
        var attachException = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));
        var mutationCompleted = mutator.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(mutationCompleted, "the unrelated writer never completed");
        Assert.True(barrier.PrerequisiteObserved,
            "the sentinel ran before the candidate descendant was captured");
        Assert.Null(mutationException);
        Assert.Null(attachException);
        Assert.Same(context, root.TryGetContext());
        Assert.Same(context, descendant.TryGetContext());
        Assert.Same(context, unrelatedChild.TryGetContext());
        Assert.Equal(1, barrier.ReadCount);
        Assert.Equal(1, descendantScans);
    }

    private static void AddStructuralGetter(
        IInterceptorSubject subject,
        string name,
        Func<IInterceptorSubject, object?> getter)
    {
        subject.AddProperties(new SubjectPropertyMetadata(
            name,
            typeof(Person),
            [],
            getter,
            null,
            isIntercepted: true,
            isDynamic: true));
    }

    private sealed class CaptureBarrier(Func<bool>? prerequisite = null)
    {
        private readonly ManualResetEventSlim _reached = new(false);
        private readonly ManualResetEventSlim _resume = new(false);
        private int _readCount;

        internal int ReadCount => Volatile.Read(ref _readCount);

        internal bool PrerequisiteObserved { get; private set; }

        internal object? Read(IInterceptorSubject _)
        {
            if (Interlocked.Increment(ref _readCount) != 1)
            {
                return null;
            }

            PrerequisiteObserved = prerequisite?.Invoke() ?? true;
            _reached.Set();
            if (!_resume.Wait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                throw new TimeoutException("Timed out waiting for the capture mutation.");
            }

            return null;
        }

        internal void WaitUntilReached()
        {
            if (!_reached.Wait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                throw new TimeoutException("Timed out waiting for the post-descendant capture point.");
            }
        }

        internal void Resume() => _resume.Set();
    }

    private sealed class CountingWriteInterceptor : IWriteInterceptor
    {
        private IInterceptorSubject? _target;
        private string? _propertyName;
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        internal void Arm(IInterceptorSubject target, string propertyName)
        {
            _target = target;
            _propertyName = propertyName;
            Volatile.Write(ref _count, 0);
        }

        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.Property.Subject, _target) && context.Property.Name == _propertyName)
            {
                Interlocked.Increment(ref _count);
            }

            next(ref context);
        }
    }
}
