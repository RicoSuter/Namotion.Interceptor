using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle.Acceptance;

/// <summary>
/// Defect class 3: a structural write that re-enters the same property from user code the write
/// itself invoked must not leave the graph holding a subject the committed field no longer names,
/// and an attach whose seeding is re-entered must still complete.
/// </summary>
/// <remarks>
/// The re-entry point moved on this branch: a committed value is never re-enumerated, so the only
/// user code left inside a structural write is the capture of the incoming value. Both repros
/// therefore re-enter from the first enumeration of the incoming value rather than from the
/// reconcile scan of the committed one, and each asserts its hook actually ran.
/// </remarks>
public class ReentrantStructuralWriteAcceptanceTests
{
    /// <summary>
    /// PASSES on this branch. Pins that a write re-entered during its own capture leaves attachment
    /// and the committed field in agreement, so neither the outer nor the nested value can end up
    /// attached while unreachable from the property that was written.
    /// </summary>
    [Fact]
    public void WhenAUserEnumerableWritesTheSamePropertyWhileItIsScanned_ThenTheGraphAndTheCommittedFieldAgree()
    {
        // Arrange
        var context = AcceptanceContext.Create();
        var holder = new EnumerableChildrenHolder(context);
        var firstChild = new Person { FirstName = "first" };
        holder.Children = new List<Person> { firstChild };

        var outerChild = new Person { FirstName = "outer" };
        var nestedChild = new Person { FirstName = "nested" };
        var nestedValue = new List<Person> { nestedChild };
        var outerValue = new ReenteringEnumerable([outerChild]);
        Exception? nestedException = null;
        outerValue.OnReenter = () => nestedException = Record.Exception(() => holder.Children = nestedValue);

        // Act
        var outerException = Record.Exception(() => holder.Children = outerValue);

        // Assert
        Assert.True(outerValue.HasReentered,
            $"the reentrant write never ran during capture; the incoming value was scanned {outerValue.Enumerations} times");
        Assert.True(outerException is null || nestedException is null,
            "both the outer and the nested write were rejected, so nothing was written and the repro proves nothing");

        var committed = (holder.Children ?? []).ToHashSet();
        foreach (var (child, name) in new[] { (outerChild, "outer"), (nestedChild, "nested"), (firstChild, "first") })
        {
            Assert.True(committed.Contains(child) == (((IInterceptorSubject)child).TryGetContext() is not null),
                $"the '{name}' child's attachment disagrees with the committed field " +
                $"(held={committed.Contains(child)}, attached={((IInterceptorSubject)child).TryGetContext() is not null})");
        }
    }

    /// <summary>
    /// FAILS on this branch. Demonstrates defect 3b: an explicit attach whose seeding pass is
    /// re-entered by the very enumerable it is seeding aborts the entire attach instead of letting
    /// the later write through. Observed symptom: LifecycleConflictException escapes
    /// AttachToContext with the message "An intercepted property write conflicts with attachment
    /// publication ... Retry the operation", and nothing attaches at all: the root, the seeded child
    /// and the late child are all left with no context. The nested write itself raises nothing, so a
    /// caller cannot tell which of its two operations was refused, and a subject graph that mutates
    /// itself while being attached cannot be attached at all.
    /// </summary>
    [Fact]
    public void WhenAUserEnumerableWritesTheRootWhileTheAttachSeedsIt_ThenTheWritePassesThroughAndTheAttachCompletes()
    {
        // Arrange
        var context = AcceptanceContext.Create();
        var seededChild = new Person { FirstName = "seeded" };
        var lateChild = new Person { FirstName = "late" };
        var holder = new EnumerableChildrenHolder();

        var initialValue = new ReenteringEnumerable([seededChild]);
        holder.Children = initialValue;

        var lateValue = new List<Person> { lateChild };
        initialValue.OnReenter = () => holder.Children = lateValue;

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)holder).AttachToContext(context));

        // Assert
        Assert.True(initialValue.HasReentered,
            $"the reentrant write never ran in the seeding window; the initial value was scanned " +
            $"{initialValue.Enumerations} times");
        Assert.Null(exception);

        Assert.Same(lateValue, holder.Children);

        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        Assert.Same(context, ((IInterceptorSubject)holder).TryGetContext());
        Assert.True(lifecycle.Graph.IsOwned(holder));

        // Which of the two children survives depends on where the re-entry lands relative to
        // seeding, so the end state is asserted as agreement rather than as one specific answer.
        var committed = (holder.Children ?? []).ToHashSet();
        foreach (var (child, name) in new[] { (seededChild, "seeded"), (lateChild, "late") })
        {
            Assert.True(committed.Contains(child) == (((IInterceptorSubject)child).TryGetContext() is not null),
                $"the '{name}' child's attachment disagrees with the committed field " +
                $"(held={committed.Contains(child)}, attached={((IInterceptorSubject)child).TryGetContext() is not null})");
        }
    }
}
