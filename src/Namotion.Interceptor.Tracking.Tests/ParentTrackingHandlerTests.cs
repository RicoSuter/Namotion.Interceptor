using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class ParentTrackingHandlerTests
{
    [Fact]
    public void WhenReferencedByTwoPropertiesOfTheSameParent_ThenTwoReferencesAreSet()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        // Act
        var parent = new Person(context)
        {
            FirstName = "Parent"
        };

        var person = new Person(context);
        person.FirstName = "Child";
        person.Mother = parent;
        person.Father = parent;

        // Assert
        var parents = parent.GetParents();
        Assert.Equal(2, parents.Length);
    }

    [Fact]
    public void WhenReferencesAreSetToNull_ThenParentIsEmpty()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        // Act
        var parent = new Person(context)
        {
            FirstName = "Parent"
        };

        var person = new Person(context);
        person.FirstName = "Child";
        person.Mother = parent;
        person.Father = parent;

        person.Mother = null;
        person.Father = null;

        // Assert
        var parents = parent.GetParents();
        Assert.Empty(parents);
    }

    [Fact]
    public void WhenReferencedByTwoOtherSubjects_ThenItHasTwoParents()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        // Act
        var mother = new Person(context);
        mother.FirstName = "Mother";

        var child1 = new Person(context);
        child1.FirstName = "Child1";
        child1.Mother = mother;

        var child2 = new Person(context)
        {
            FirstName = "Child2",
            Mother = mother
        };

        // Assert
        var parents = mother.GetParents();
        Assert.Equal(2, parents.Length);
    }

    [Fact]
    public void WhenACollectionIsReorderedWithoutTheRegistry_ThenTheTrackedIndexMoves()
    {
        // Parent tracking keeps its own copy of each index, so it has to follow a reorder on its own,
        // without the registry being registered.
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents();

        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };

        var parent = new Person(context) { Children = [first, second] };

        // Act
        parent.Children = [second, first];

        // Assert
        Assert.Equal(1, first.GetParents().Single().Index);
        Assert.Equal(0, second.GetParents().Single().Index);
    }
}
