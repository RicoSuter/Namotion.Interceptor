using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class SubjectChildGroupTests
{
    private static List<(IInterceptorSubject Subject, object?[] Indices)> Group(ReadOnlySpan<SubjectChild> children)
    {
        var groups = new List<(IInterceptorSubject Subject, object?[] Indices)>();
        foreach (var group in children.GroupBySubject())
        {
            var indices = new object?[group.Children.Length];
            for (var i = 0; i < group.Children.Length; i++)
            {
                Assert.Same(group.Subject, group.Children[i].Subject);
                indices[i] = group.Children[i].Index;
            }

            groups.Add((group.Subject, indices));
        }

        return groups;
    }

    [Fact]
    public void WhenSpanIsEmpty_ThenThereIsNoGroup()
    {
        // Act
        var groups = Group([]);

        // Assert
        Assert.Empty(groups);
    }

    [Fact]
    public void WhenSpanHoldsOneSubjectAtSeveralPlaces_ThenItIsOneGroupWithAllPlaces()
    {
        // Arrange
        var car = new Car();

        // Act
        var groups = Group([new SubjectChild(car, 0), new SubjectChild(car, 2), new SubjectChild(car, 3)]);

        // Assert
        var group = Assert.Single(groups);
        Assert.Same(car, group.Subject);
        Assert.Equal<object?>([0, 2, 3], group.Indices);
    }

    [Fact]
    public void WhenSpanHoldsSeveralSubjects_ThenEachIsOneGroupInTheOrderOfTheSpan()
    {
        // Arrange
        var first = new Car();
        var second = new Car();
        var third = new Car();

        // Act
        var groups = Group(
        [
            new SubjectChild(first, "a"),
            new SubjectChild(second, "b"),
            new SubjectChild(second, "d"),
            new SubjectChild(third, "c"),
            new SubjectChild(third, "e"),
            new SubjectChild(third, "f")
        ]);

        // Assert
        Assert.Equal(3, groups.Count);
        Assert.Same(first, groups[0].Subject);
        Assert.Equal<object?>(["a"], groups[0].Indices);
        Assert.Same(second, groups[1].Subject);
        Assert.Equal<object?>(["b", "d"], groups[1].Indices);
        Assert.Same(third, groups[2].Subject);
        Assert.Equal<object?>(["c", "e", "f"], groups[2].Indices);
    }

    [Fact]
    public void WhenSubjectEntriesAreNotAdjacent_ThenEachRunIsAGroup()
    {
        // Arrange
        var car = new Car();
        var other = new Car();

        // Act
        var groups = Group([new SubjectChild(car, 0), new SubjectChild(other, 1), new SubjectChild(car, 2)]);

        // Assert
        Assert.Equal([1, 1, 1], groups.Select(group => group.Indices.Length));
        Assert.Equal<IInterceptorSubject>([car, other, car], groups.Select(group => group.Subject));
    }
}
