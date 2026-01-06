using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Registry.Tests.GraphBehavior;

/// <summary>
/// Tests for cyclic graph scenarios. Documents both supported behavior and limitations.
/// </summary>
public class CycleTests
{
    [Fact]
    public Task WhenCreatingSelfReference_ThenSubjectStaysAttached()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        // Act - Create self-reference
        var person = new Person(context) { FirstName = "Narcissus" };
        person.Father = person;

        // Assert
        return Verify(helper.GetEvents());
    }

    [Fact]
    public Task WhenCreatingDirectCycle_ThenBothAttached()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        // Act - Create bidirectional cycle: Alice <-> Bob
        var alice = new Person(context) { FirstName = "Alice" };
        var bob = new Person(context) { FirstName = "Bob" };

        alice.Mother = bob;
        bob.Mother = alice;

        // Assert
        return Verify(helper.GetEvents());
    }

    [Fact]
    public Task WhenBreakingCycle_ThenBothDetach()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        var alice = new Person(context) { FirstName = "Alice" };
        var bob = new Person(context) { FirstName = "Bob" };

        alice.Mother = bob;
        bob.Mother = alice;

        helper.Clear();

        // Act - Break the cycle
        alice.Mother = null;

        // Assert - Both detach (cascade: Alice loses Bob, Bob's ref to Alice becomes orphaned)
        return Verify(helper.GetEvents());
    }

    [Fact]
    public Task WhenCycleProtectedByExternalRef_ThenCycleStays()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        // Setup: Root.Children = [A, B], A <-> B cycle
        var nodeA = new Person { FirstName = "A" };
        var nodeB = new Person { FirstName = "B" };
        nodeA.Mother = nodeB;
        nodeB.Mother = nodeA;

        var root = new Person(context)
        {
            FirstName = "Root",
            Children = [nodeA, nodeB]
        };

        helper.Clear();

        // Act - Break the internal cycle
        nodeA.Mother = null;

        // Assert - A and B stay attached (both still referenced from Root.Children)
        return Verify(helper.GetEvents());
    }

    [Fact]
    public Task WhenInternalCycleOrphaned_ThenCycleStaysAttached_Limitation()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        // Setup: Root -> A -> B <-> C (internal cycle)
        var nodeC = new Person { FirstName = "C" };
        var nodeB = new Person { FirstName = "B", Mother = nodeC };
        nodeC.Mother = nodeB; // Create B <-> C cycle

        var nodeA = new Person { FirstName = "A", Mother = nodeB };

        var root = new Person(context)
        {
            FirstName = "Root",
            Father = nodeA
        };

        helper.Clear();

        // Act - Remove A from root (orphans the B <-> C cycle)
        root.Father = null;

        // Assert - A detaches, but B and C stay attached (they keep each other alive)
        // This documents the reference counting limitation with cycles
        return Verify(helper.GetEvents());
    }
}
