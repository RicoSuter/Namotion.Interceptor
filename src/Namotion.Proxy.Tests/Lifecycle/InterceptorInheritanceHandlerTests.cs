using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;

namespace Namotion.Proxy.Tests.Lifecycle;

public class InterceptorInheritanceHandlerTests
{
    [Fact]
    public void WhenPropertyIsAssigned_ThenContextIsSet()
    {
        // Arrange
        var collection = InterceptorCollection
            .Create()
            .WithInterceptorInheritance();

        // Act
        var person = new Person(collection);
        person.Mother = new Person { FirstName = "Susi" };

        // Assert
        Assert.Equal(collection.GetServices<IInterceptor>(), ((IInterceptorSubject)person.Mother).Interceptors?.GetServices<IInterceptor>());
    }

    [Fact]
    public void WhenPropertyWithDeepStructureIsAssigned_ThenChildrenAlsoHaveContext()
    {
        // Arrange
        var collection = InterceptorCollection
            .Create()
            .WithInterceptorInheritance();

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

        var person = new Person(collection)
        {
            FirstName = "Child",
            Mother = mother
        };

        // Assert
        Assert.Equal(collection.GetServices<IInterceptor>(), ((IInterceptorSubject)person).Interceptors?.GetServices<IInterceptor>());
        Assert.Equal(collection.GetServices<IInterceptor>(), ((IInterceptorSubject)mother).Interceptors?.GetServices<IInterceptor>());
        Assert.Equal(collection.GetServices<IInterceptor>(), ((IInterceptorSubject)grandmother).Interceptors?.GetServices<IInterceptor>());
    }

    [Fact]
    public void WhenPropertyWithDeepProxiesIsRemoved_ThenAllContextsAreNull()
    {
        // Arrange
        var context = InterceptorCollection
            .Create()
            .WithInterceptorInheritance();

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
        Assert.Equal(context.GetServices<IInterceptor>(), ((IInterceptorSubject)person).Interceptors?.GetServices<IInterceptor>());
        Assert.Empty(((IInterceptorSubject)mother).Interceptors.GetServices<IInterceptor>());
        Assert.Empty(((IInterceptorSubject)grandmother).Interceptors.GetServices<IInterceptor>());
    }

    [Fact]
    public void WhenArrayIsAssigned_ThenAllChildrenAreAttached()
    {
        // Arrange
        var context = InterceptorCollection
            .Create()
            .WithInterceptorInheritance();

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
        Assert.Equal(context.GetServices<IInterceptor>(), ((IInterceptorSubject)person).Interceptors?.GetServices<IInterceptor>());
        Assert.Equal(context.GetServices<IInterceptor>(), ((IInterceptorSubject)child1).Interceptors?.GetServices<IInterceptor>());
        Assert.Equal(context.GetServices<IInterceptor>(), ((IInterceptorSubject)child2).Interceptors?.GetServices<IInterceptor>());
    }

    [Fact]
    public void WhenUsingCircularDependencies_ThenProxiesAreAttached()
    {
        // Arrange
        var context = InterceptorCollection
            .Create()
            .WithInterceptorInheritance();

        // Act
        var child1 = new Person(context) { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child2" };

        child1.Mother = child2;
        child2.Mother = child3;
        child3.Mother = child1;

        // Assert
        Assert.Equal(context.GetServices<IInterceptor>(), ((IInterceptorSubject)child1).Interceptors?.GetServices<IInterceptor>());
        Assert.Equal(context.GetServices<IInterceptor>(), ((IInterceptorSubject)child2).Interceptors?.GetServices<IInterceptor>());
        Assert.Equal(context.GetServices<IInterceptor>(), ((IInterceptorSubject)child3).Interceptors?.GetServices<IInterceptor>());
    }
}
