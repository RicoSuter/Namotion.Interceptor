using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class SubjectLifecycleAttachmentsTests
{
    [Fact]
    public void WhenSubjectLeavesThreeGraphsOneByOne_ThenItStaysAttachedUntilItLeftTheLast()
    {
        // Arrange
        var roots = Enumerable.Range(0, 3)
            .Select(_ => new Person(InterceptorSubjectContext.Create().WithLifecycle()))
            .ToArray();
        var child = new Person();
        foreach (var root in roots)
        {
            root.Father = child;
        }

        var states = new List<bool> { SubjectLifecycleAttachments.IsAttached(child) };

        // Act
        roots[1].Father = null;
        states.Add(SubjectLifecycleAttachments.IsAttached(child));
        roots[0].Father = null;
        states.Add(SubjectLifecycleAttachments.IsAttached(child));
        roots[2].Father = null;
        states.Add(SubjectLifecycleAttachments.IsAttached(child));

        // Assert
        Assert.Equal([true, true, true, false], states);
    }

    [Fact]
    public void WhenSubjectIsAttachedAgainAfterItLeftEveryGraph_ThenItIsAttached()
    {
        // Arrange
        var first = new Person(InterceptorSubjectContext.Create().WithLifecycle());
        var second = new Person(InterceptorSubjectContext.Create().WithLifecycle());
        var child = new Person();
        first.Father = child;
        first.Father = null;
        var isAttachedAfterLeaving = SubjectLifecycleAttachments.IsAttached(child);

        // Act
        second.Father = child;

        // Assert
        Assert.False(isAttachedAfterLeaving);
        Assert.True(SubjectLifecycleAttachments.IsAttached(child));
    }

    [Fact]
    public void WhenInterceptorsAreEqualButNotTheSame_ThenRemovingOneKeepsTheOther()
    {
        // Arrange
        var first = new EqualLifecycleInterceptor();
        var second = new EqualLifecycleInterceptor();
        var child = new Person();
        SubjectLifecycleAttachments.Add(child, first);
        SubjectLifecycleAttachments.Add(child, second);
        SubjectLifecycleAttachments.Remove(child, first);

        // Act
        SubjectLifecycleAttachments.Remove(child, first);

        // Assert
        Assert.True(SubjectLifecycleAttachments.IsAttached(child));
    }

    private sealed class EqualLifecycleInterceptor : LifecycleInterceptor
    {
        public override bool Equals(object? obj) => obj is EqualLifecycleInterceptor;

        public override int GetHashCode() => 0;
    }
}
