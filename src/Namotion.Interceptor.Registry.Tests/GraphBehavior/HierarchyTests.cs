using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Registry.Tests.GraphBehavior;

public class HierarchyTests
{
    [Fact]
    public Task WhenAttachingNestedSubjects_ThenAllAreAttached()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        // Act
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = new Person
            {
                FirstName = "Mother",
                Mother = new Person
                {
                    FirstName = "Grandmother"
                }
            }
        };

        // Assert
        return Verify(helper.GetEvents());
    }

    [Fact]
    public Task WhenRemovingBranch_ThenBranchIsDetached()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = new Person
            {
                FirstName = "Mother",
                Mother = new Person
                {
                    FirstName = "Grandmother"
                }
            }
        };

        helper.Clear();

        // Act
        person.Mother = null;

        // Assert
        return Verify(helper.GetEvents());
    }

    [Fact]
    public Task WhenRemovingMiddleElement_ThenOnlyChildrenAreDetached()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        var grandmother = new Person { FirstName = "Grandmother" };
        var mother = new Person
        {
            FirstName = "Mother",
            Mother = grandmother
        };
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother
        };

        helper.Clear();

        // Act - Remove grandmother from mother (middle element removal)
        mother.Mother = null;

        // Assert - Only grandmother detaches, mother stays
        return Verify(helper.GetEvents());
    }

    [Fact]
    public Task WhenRemovingOneSibling_ThenOtherSiblingStays()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var device1 = new Person { FirstName = "Device1" };
        var device2 = new Person
        {
            FirstName = "Device2",
            Children = [child1, child2]
        };

        var root = new Person(context)
        {
            FirstName = "Root",
            Father = device1,
            Mother = device2
        };

        helper.Clear();

        // Act - Remove device2 (with children), device1 should stay
        root.Mother = null;

        // Assert - device2, child1, child2 detach; device1 stays
        return Verify(helper.GetEvents());
    }
}
