using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class ParentTrackingHandlerTests
{
    [Fact]
    public void WhenReferencedByTwoPropertiesOfTheSameParent_ThenTwoReferencesAreSet()
    {
        // Arrange
        var collection = InterceptorSubjectContext
            .Create()
            .WithParents();

        // Act
        var parent = new Person(collection)
        {
            FirstName = "Parent"
        };

        var person = new Person(collection);
        person.FirstName = "Child";
        person.Mother = parent;
        person.Father = parent;

        // Assert
        var parents = parent.GetParents();
        Assert.Equal(2, parents.Count);
    }

    [Fact]
    public void WhenReferencesAreSetToNull_ThenParentIsEmpty()
    {
        // Arrange
        var collection = InterceptorSubjectContext
            .Create()
            .WithParents();

        // Act
        var parent = new Person(collection)
        {
            FirstName = "Parent"
        };

        var person = new Person(collection);
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
        var collection = InterceptorSubjectContext
            .Create()
            .WithParents();

        // Act
        var mother = new Person(collection);
        mother.FirstName = "Mother";

        var child1 = new Person(collection)
        {
            FirstName = "Child1",
            Mother = mother
        };

        var child2 = new Person(collection)
        {
            FirstName = "Child2",
            Mother = mother
        };

        // Assert
        var parents = mother.GetParents();
        Assert.Equal(2, parents.Count);
    }
}
