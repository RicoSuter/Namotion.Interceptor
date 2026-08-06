using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class ForEachAttachedSubjectTests
{
    [Fact]
    public void WhenSubjectsAreAttached_ThenForEachAttachedSubjectVisitsEachExactlyOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var mother = new Person(context) { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        mother.Children = [child1, child2];

        // Act
        var visited = new List<IInterceptorSubject>();
        lifecycleInterceptor.ForEachAttachedSubject(visited.Add);

        // Assert
        Assert.Equal(3, visited.Count);
        Assert.Contains(mother, visited);
        Assert.Contains(child1, visited);
        Assert.Contains(child2, visited);
    }

    [Fact]
    public void WhenASubjectHasDetached_ThenForEachAttachedSubjectDoesNotVisitIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var mother = new Person(context) { FirstName = "Mother" };
        var child = new Person { FirstName = "Child" };
        mother.Children = [child];

        // Act
        mother.Children = [];
        var visited = new List<IInterceptorSubject>();
        lifecycleInterceptor.ForEachAttachedSubject(visited.Add);

        // Assert
        Assert.Single(visited);
        Assert.Contains(mother, visited);
        Assert.DoesNotContain(child, visited);
    }
}
