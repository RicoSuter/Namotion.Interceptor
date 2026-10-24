using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class ContextInheritanceHandlerTests
{
    private sealed class DerivedContextInheritanceHandler : ContextInheritanceHandler;

    [Fact]
    public void WhenEqualBaseAndDerivedHandlersAddedToHashSet_ThenOnlyOneEntryIsStored()
    {
        // Arrange
        var handlers = new HashSet<ContextInheritanceHandler>();

        // Act
        handlers.Add(new ContextInheritanceHandler());
        handlers.Add(new ContextInheritanceHandler());
        handlers.Add(new DerivedContextInheritanceHandler());

        // Assert
        Assert.Single(handlers);
    }

    [Fact]
    public void WhenPropertyIsAssigned_ThenContextIsSet()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        // Act
        var person = new Person(context);
        person.Mother = new Person { FirstName = "Mother" };

        // Assert
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), person.Mother.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenPropertyWithDeepStructureIsAssigned_ThenChildrenAlsoHaveContext()
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

        // Assert
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), person.GetServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), mother.GetServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), grandmother.GetServices<ILifecycleInterceptor>());
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
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), person.GetServices<ILifecycleInterceptor>());
        Assert.Empty(mother.GetServices<ILifecycleInterceptor>());
        Assert.Empty(grandmother.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenArrayIsAssigned_ThenAllChildrenAreAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

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
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), person.GetServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), child1.GetServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), child2.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenUsingCircularDependencies_ThenProxiesAreAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        // Act
        var child1 = new Person(context) { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child2" };

        child1.Mother = child2;
        child2.Mother = child3;
        child3.Mother = child1;

        // Assert
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), child1.GetServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), child2.GetServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), child3.GetServices<ILifecycleInterceptor>());
    }
    
    [Fact]
    public void WhenParentThatAttachedSharedChildIsRemoved_ThenChildInheritsContextOfRemainingParent()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithParents();

        var shared = new Person { FirstName = "Shared" };
        var first = new Person { FirstName = "First", Mother = shared };
        var second = new Person { FirstName = "Second" };
        var root = new Person(context) { Mother = first, Father = second };
        second.Mother = shared;

        // Act
        root.Mother = null;

        // Assert
        Assert.Empty(first.GetServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), shared.GetServices<ILifecycleInterceptor>());
        Assert.True(context.TryGetLifecycleInterceptor()!.TryRunWhileAttached(shared, () => { }));
        Assert.Equal(1, ((IInterceptorSubject)shared).GetReferenceCount());
    }

    [Fact]
    public void WhenParentThatAttachedSharedChildReleasesItAndIsRemovedLater_ThenChildKeepsContextOfRemainingParent()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithParents();

        var shared = new Person { FirstName = "Shared" };
        var first = new Person { FirstName = "First", Mother = shared };
        var second = new Person { FirstName = "Second" };
        var root = new Person(context) { Mother = first, Father = second };
        second.Mother = shared;

        // Act
        first.Mother = null;
        root.Mother = null;

        // Assert
        Assert.Empty(first.GetServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), shared.GetServices<ILifecycleInterceptor>());
        Assert.True(context.TryGetLifecycleInterceptor()!.TryRunWhileAttached(shared, () => { }));
        Assert.Equal(1, ((IInterceptorSubject)shared).GetReferenceCount());
    }

    [Fact]
    public void WhenParentReleasesSubjectThatOnlyItsOwnChildStillHolds_ThenSubjectKeepsItsContextAndCanBeAssignedAgain()
    {
        // Arrange: the child of the subject holds it as well, which would make a cycle of their contexts.
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithParents();

        var subject = new Person { FirstName = "Subject" };
        var child = new Person { FirstName = "Child" };
        var root = new Person(context) { Mother = subject };
        subject.Mother = child;
        child.Mother = subject;

        // Act
        root.Mother = null;
        root.Father = subject;

        // Assert
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), subject.GetServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), child.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenAddingInterceptorToChild_ThenServiceFallbackAndScopeWorks()
    {
        // Arrange
        var service1 = 1;
        var service2 = 2;
        
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => service1, x => x == 1)
            .WithContextInheritance();

        // Act
        var person = new Person(context)
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
            .WithService(() => service2, x => x == 2);

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
