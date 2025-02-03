using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class ContextInheritanceHandlerTests
{
    [Fact]
    public void WhenPropertyIsAssigned_ThenContextIsSet()
    {
        // Arrange
        var collection = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        // Act
        var person = new Person(collection);
        person.Mother = new Person { FirstName = "Mother" };

        // Assert
        Assert.Equal(collection.GetServices<IInterceptor>(), person.Mother.GetServices<IInterceptor>());
    }

    [Fact]
    public void WhenPropertyWithDeepStructureIsAssigned_ThenChildrenAlsoHaveContext()
    {
        // Arrange
        var collection = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

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
        Assert.Equal(collection.GetServices<IInterceptor>(), person.GetServices<IInterceptor>());
        Assert.Equal(collection.GetServices<IInterceptor>(), mother.GetServices<IInterceptor>());
        Assert.Equal(collection.GetServices<IInterceptor>(), grandmother.GetServices<IInterceptor>());
    }

    [Fact]
    public void WhenPropertyWithDeepProxiesIsRemoved_ThenAllContextsAreNull()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

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
        Assert.Equal(context.GetServices<IInterceptor>(), person.GetServices<IInterceptor>());
        Assert.Empty(mother.GetServices<IInterceptor>());
        Assert.Empty(grandmother.GetServices<IInterceptor>());
    }

    [Fact]
    public void WhenArrayIsAssigned_ThenAllChildrenAreAttached()
    {
        // Arrange
        var collection = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        // Act
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        var person = new Person(collection)
        {
            FirstName = "Mother",
            Children = [
                child1,
                child2
            ]
        };

        // Assert
        Assert.Equal(collection.GetServices<IInterceptor>(), person.GetServices<IInterceptor>());
        Assert.Equal(collection.GetServices<IInterceptor>(), child1.GetServices<IInterceptor>());
        Assert.Equal(collection.GetServices<IInterceptor>(), child2.GetServices<IInterceptor>());
    }

    [Fact]
    public void WhenUsingCircularDependencies_ThenProxiesAreAttached()
    {
        // Arrange
        var collection = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        // Act
        var child1 = new Person(collection) { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child2" };

        child1.Mother = child2;
        child2.Mother = child3;
        child3.Mother = child1;

        // Assert
        Assert.Equal(collection.GetServices<IInterceptor>(), child1.GetServices<IInterceptor>());
        Assert.Equal(collection.GetServices<IInterceptor>(), child2.GetServices<IInterceptor>());
        Assert.Equal(collection.GetServices<IInterceptor>(), child3.GetServices<IInterceptor>());
    }
    
    [Fact]
    public void WhenAddingInterceptorToChild_ThenServiceFallbackAndScopeWorks()
    {
        // Arrange
        var service1 = 1;
        var service2 = 2;
        
        var collection = InterceptorSubjectContext
            .Create()
            .WithService(() => service1, x => x == 1)
            .WithContextInheritance();

        // Act
        var person = new Person(collection)
        {
            Mother = new Person
            {
                FirstName = "Mother",
                Mother = new Person
                {
                    FirstName = "Grandmother"
                }
            }
        };

        ((IInterceptorSubject)person.Mother).Context
            .WithService(() => service2, x => x == 2)
            .WithContextInheritance();

        // Assert
        Assert.Contains(1, person.GetServices<int>());
        Assert.DoesNotContain(2, person.GetServices<int>());
        Assert.Single(person.GetServices<ContextInheritanceHandler>());

        Assert.Contains(1, person.Mother.GetServices<int>());
        Assert.Contains(2, person.Mother.GetServices<int>());

        Assert.Contains(1, person.Mother.Mother.GetServices<int>());
        Assert.Contains(2, person.Mother.Mother.GetServices<int>());
        Assert.Single(person.Mother.Mother.GetServices<ContextInheritanceHandler>());
    }
}
