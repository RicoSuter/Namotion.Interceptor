using System.Collections;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Reconciliation enumerates user values before it commits the property baseline, and that
/// enumeration runs at callback depth zero, where a nested write of the same property is legal.
/// The outer operation then commits its own baseline on top of the newer one the nested write
/// already committed.
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
    /// A user enumerable that runs a callback when a chosen enumeration starts. The write protocol
    /// enumerates a proposed structural value twice, once while claiming the proposed component
    /// and once while reconciling it, so the ordinal selects which of the two windows is entered.
    /// </summary>
    private sealed class ReentrantEnumerable(
        IEnumerable<Person> items, int enumerationToInterruptAt, Action onEnumeration) : IEnumerable<Person>
    {
        private int _enumerations;

        public int Enumerations => _enumerations;

        public IEnumerator<Person> GetEnumerator()
        {
            if (Interlocked.Increment(ref _enumerations) == enumerationToInterruptAt)
            {
                onEnumeration();
            }

            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Reproduces the finding that a reentrant write from inside a user enumerable commits a newer
    /// baseline which the outer operation then overwrites. Reproduces on a single thread, with no
    /// artificially held window: the reentrancy is the enumerable's own code running where the
    /// reconciler invokes it.
    /// </summary>
    [Fact]
    public void WhenAUserEnumerableWritesTheSamePropertyWhileItIsScanned_ThenTheOuterWriteDoesNotOverwriteTheNewerBaseline()
    {
        // Arrange: the second enumeration is the reconciler's own scan of the proposed value, which
        // happens after the terminal stored it and before the baseline is committed.
        var context = CreateContext();
        var holder = new EnumerableChildrenHolder(context);
        var outerChild = new Person { FirstName = "outer" };
        var nestedChild = new Person { FirstName = "nested" };
        var nestedWriteRan = false;
        var outerValue = new ReentrantEnumerable([outerChild], 2, () =>
        {
            nestedWriteRan = true;
            holder.Children = new List<Person> { nestedChild };
        });

        // Act
        holder.Children = outerValue;

        // Assert: the reentrancy actually happened, so the test cannot pass without exercising it.
        Assert.True(nestedWriteRan,
            $"the reentrant write never ran; the proposed value was enumerated {outerValue.Enumerations} times");

        // The nested write is the newer one and its value is what the property holds afterwards.
        Assert.Equal([nestedChild], holder.Children!);
        Assert.Same(context, ((IInterceptorSubject)nestedChild).TryGetContext());

        // The outer write committed its own baseline over the newer one, then published an edge
        // for a value the property no longer holds.
        Assert.True(((IInterceptorSubject)outerChild).TryGetContext() is null,
            "the outer write overwrote the newer baseline committed by the reentrant write and " +
            "published an ownership edge for a value the committed property no longer holds, so " +
            $"'{outerChild.FirstName}' is attached with {((IInterceptorSubject)outerChild).GetReferenceCount()} incoming edge(s) " +
            "while unreachable from the subject graph");
    }
}
