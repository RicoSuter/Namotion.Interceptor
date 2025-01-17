using Namotion.Interceptor;

namespace Namotion.Proxy.Tests.Lifecycle;

public class InterceptorInheritanceHandlerTests
{
    [Fact]
    public void WhenPropertyIsAssigned_ThenContextIsSet()
    {
        // Arrange
        var context = InterceptorProvider
            .CreateBuilder()
            .WithInterceptorInheritance()
            .Build();

        // Act
        var person = new Person(context);
        person.Mother = new Person { FirstName = "Susi" };

        // Assert
        Assert.Equal(context.Interceptors, ((IInterceptorSubject)person.Mother).Interceptors?.Interceptors);
    }

    [Fact]
    public void WhenPropertyWithDeepStructureIsAssigned_ThenChildrenAlsoHaveContext()
    {
        // Arrange
        var context = InterceptorProvider
            .CreateBuilder()
            .WithInterceptorInheritance()
            .Build();

        // Act
        var grandmother = new Person
        {
            FirstName = "Grandmother"
        };

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

        // Assert
        Assert.Equal(context.Interceptors, ((IInterceptorSubject)person).Interceptors.Interceptors);
        Assert.Equal(context.Interceptors, ((IInterceptorSubject)mother).Interceptors.Interceptors);
        Assert.Equal(context.Interceptors, ((IInterceptorSubject)grandmother).Interceptors.Interceptors);
    }

    [Fact]
    public void WhenPropertyWithDeepProxiesIsRemoved_ThenAllContextsAreNull()
    {
        // Arrange
        var context = InterceptorProvider
            .CreateBuilder()
            .WithInterceptorInheritance()
            .Build();

        // Act
        var grandmother = new Person
        {
            FirstName = "Grandmother"
        };

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

        person.Mother = null;

        // Assert
        Assert.Equal(context.Interceptors, ((IInterceptorSubject)person).Interceptors.Interceptors);
        Assert.Empty(((IInterceptorSubject)mother).Interceptors.Interceptors);
        Assert.Empty(((IInterceptorSubject)grandmother).Interceptors.Interceptors);
    }

    [Fact]
    public void WhenArrayIsAssigned_ThenAllChildrenAreAttached()
    {
        // Arrange
        var context = InterceptorProvider
            .CreateBuilder()
            .WithInterceptorInheritance()
            .Build();

        // Act
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        var person = new Person(context)
        {
            FirstName = "Mother",
            Children = [
                child1,
                child2
            ]
        };

        // Assert
        Assert.Equal(context.Interceptors, ((IInterceptorSubject)person).Interceptors?.Interceptors);
        Assert.Equal(context.Interceptors, ((IInterceptorSubject)child1).Interceptors?.Interceptors);
        Assert.Equal(context.Interceptors, ((IInterceptorSubject)child2).Interceptors?.Interceptors);
    }

    [Fact]
    public void WhenUsingCircularDependencies_ThenProxiesAreAttached()
    {
        // Arrange
        var context = InterceptorProvider
            .CreateBuilder()
            .WithInterceptorInheritance()
            .Build();

        // Act
        var child1 = new Person(context) { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child2" };

        child1.Mother = child2;
        child2.Mother = child3;
        child3.Mother = child1;

        // Assert
        Assert.Equal(context.Interceptors, ((IInterceptorSubject)child1).Interceptors?.Interceptors);
        Assert.Equal(context.Interceptors, ((IInterceptorSubject)child2).Interceptors?.Interceptors);
        Assert.Equal(context.Interceptors, ((IInterceptorSubject)child3).Interceptors?.Interceptors);
    }
}
