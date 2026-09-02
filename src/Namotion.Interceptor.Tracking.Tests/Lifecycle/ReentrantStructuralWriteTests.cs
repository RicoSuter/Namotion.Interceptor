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

    [Fact]
    public void WhenAUserEnumerableWritesTheRootDuringCapture_ThenTheChangedSnapshotIsRejected()
    {
        // Arrange
        var context = CreateContext();
        var seededChild = new Person { FirstName = "seeded" };
        var lateChild = new Person { FirstName = "late" };
        var holder = new EnumerableChildrenHolder();

        var initialValue = new ScanHookEnumerable([seededChild]);
        holder.Children = initialValue;

        var lateValue = new List<Person> { lateChild };
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        initialValue.ShouldReenter = () => true;
        initialValue.OnReenter = () => holder.Children = lateValue;

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)holder).AttachToContext(context));

        // Assert: capture invokes the user value once, observes its revision change and publishes no
        // graph or attachment state for either the stale or replacement occurrence.
        Assert.IsType<LifecycleConflictException>(exception);
        Assert.True(initialValue.HasReentered);
        Assert.Equal(1, initialValue.Enumerations);
        Assert.Same(lateValue, holder.Children);
        Assert.Null(((IInterceptorSubject)holder).TryGetContext());
        Assert.False(lifecycle.Graph.IsOwned(holder));
        Assert.Null(((IInterceptorSubject)seededChild).TryGetContext());
        Assert.Null(((IInterceptorSubject)lateChild).TryGetContext());
    }
}
