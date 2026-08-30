using System.Collections;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Structural values execute user enumeration only while their immutable occurrence snapshot is
/// built. A later reconcile or release consumes that committed snapshot without executing the old
/// value again.
/// </summary>
public class ReentrantStructuralWriteTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    /// <summary>
    /// A user enumerable that re-enters the write protocol once, the first time it is scanned while
    /// <see cref="ShouldReenter"/> holds.
    ///
    /// The trigger is a condition rather than an enumeration ordinal on purpose. How many times the
    /// protocol scans a given value is an implementation detail that is expected to change, and an
    /// ordinal armed against today's count would silently stop firing when it does. The condition
    /// used by the test below instead names the phase it needs, so a change to the scan count either
    /// still lands in that phase or trips the guard.
    /// </summary>
    private sealed class ScanHookEnumerable(IEnumerable<Person> items) : IEnumerable<Person>
    {
        private bool _hasReentered;

        public int Enumerations { get; private set; }

        public bool HasReentered => _hasReentered;

        public Func<bool>? ShouldReenter { get; set; }

        public Action? OnReenter { get; set; }

        public IEnumerator<Person> GetEnumerator()
        {
            Enumerations++;
            if (!_hasReentered && ShouldReenter?.Invoke() == true)
            {
                _hasReentered = true;
                OnReenter?.Invoke();
            }

            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// The committed value is user-owned and may become unsafe to enumerate immediately after the
    /// setter returns. Replacing it must diff against the immutable occurrence snapshot captured by
    /// the successful setter, not against another pass over the enumerable.
    /// </summary>
    [Fact]
    public void WhenACommittedEnumerableIsReplaced_ThenItIsNotEnumeratedAgain()
    {
        // Arrange
        var context = CreateContext();
        var holder = new EnumerableChildrenHolder(context);
        var child = new Person();
        var committedValue = new ScanHookEnumerable([child]);
        holder.Children = committedValue;
        var committedEnumerations = committedValue.Enumerations;
        committedValue.ShouldReenter = () => true;
        committedValue.OnReenter = () => throw new InvalidOperationException("The committed value was enumerated again.");

        // Act
        var exception = Record.Exception(() => holder.Children = []);

        // Assert
        Assert.Null(exception);
        Assert.Equal(committedEnumerations, committedValue.Enumerations);
        Assert.False(committedValue.HasReentered);
        Assert.Null(child.TryGetContext());
    }

    /// <summary>
    /// An explicit attach claims the whole prospective component before it seeds the root, so
    /// between those two steps the root is attached to the context and not yet in its ownership
    /// graph. The seed reads the root's structural getters and scans their values at callback depth
    /// zero, which is where a user enumerable's own code runs, so a structural write can arrive in
    /// exactly that window. This is the only shape in the tree that reaches the write protocol's
    /// claimed-but-unpublished arm, and it is what that arm is for: there is no owner to reconcile
    /// against yet, and the seed that follows reads the committed value anyway.
    ///
    /// The re-entry is positioned by phase, not by an enumeration ordinal, and the guard below
    /// asserts the phase. The same enumerable is also scanned by the discovery walk that runs before
    /// the claim, where the root is still unattached, so an ordinal armed against today's scan count
    /// would fire in the wrong one.
    /// </summary>
    [Fact]
    public void WhenAUserEnumerableWritesTheRootWhileTheAttachSeedsIt_ThenTheWritePassesThroughAndTheAttachCompletes()
    {
        // Arrange: an unattached root whose structural value runs user code when it is scanned.
        var context = CreateContext();
        var seededChild = new Person { FirstName = "seeded" };
        var lateChild = new Person { FirstName = "late" };
        var holder = new EnumerableChildrenHolder();

        var initialValue = new ScanHookEnumerable([seededChild]);
        holder.Children = initialValue;

        var lateValue = new List<Person> { lateChild };
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        var wasClaimedButUnpublished = false;

        initialValue.ShouldReenter = () =>
            ((IInterceptorSubject)holder).TryGetContext() is not null && !lifecycle.Graph.IsOwned(holder);

        initialValue.OnReenter = () =>
        {
            wasClaimedButUnpublished = true;
            holder.Children = lateValue;
        };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)holder).AttachToContext(context));

        // Assert: the re-entry happened, and it happened in the window this test is about.
        Assert.Null(exception);
        Assert.True(wasClaimedButUnpublished,
            $"the reentrant write never ran in the seeding window; the initial value was scanned " +
            $"{initialValue.Enumerations} times");

        // The write passed through to the backing field rather than being rejected or reconciled.
        Assert.Same(lateValue, holder.Children);

        // The attach still completed, with the seed's own scan result attached through its edge.
        Assert.Same(context, ((IInterceptorSubject)holder).TryGetContext());
        Assert.True(lifecycle.Graph.IsOwned(holder));
        Assert.Same(context, ((IInterceptorSubject)seededChild).TryGetContext());
        Assert.Equal(1, ((IInterceptorSubject)seededChild).GetReferenceCount());

        // Nothing claimed the value the pass-through stored: the seed had already scanned the
        // committed value, so no edge is published for this one.
        Assert.Null(((IInterceptorSubject)lateChild).TryGetContext());
    }
}
