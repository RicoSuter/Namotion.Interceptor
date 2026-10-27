using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class ContextInheritanceHandlerTests
{
    private sealed class DerivedContextInheritanceHandler : ContextInheritanceHandler;

    private sealed class ReleasingHandler(IInterceptorSubject subject, IInterceptorSubject parent, Action release) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsPropertyReferenceRemoved &&
                ReferenceEquals(change.Subject, subject) &&
                ReferenceEquals(change.Property?.Subject, parent))
            {
                release();
            }
        }
    }

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
    
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenParentThatAttachedSharedChildIsRemoved_ThenChildInheritsContextOfRemainingParent(bool isReleasedBeforeRemoval)
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var shared = new Person { FirstName = "Shared" };
        var first = new Person { FirstName = "First", Mother = shared };
        var second = new Person { FirstName = "Second" };
        var root = new Person(context) { Mother = first, Father = second };
        second.Mother = shared;

        // Act
        if (isReleasedBeforeRemoval)
        {
            first.Mother = null;
        }

        root.Mother = null;

        // Assert
        Assert.Empty(first.GetServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), shared.GetServices<ILifecycleInterceptor>());
        Assert.True(context.TryGetLifecycleInterceptor()!.TryRunWhileAttached(shared, () => { }));
        Assert.Equal(1, ((IInterceptorSubject)shared).GetReferenceCount());
    }

    [Fact]
    public void WhenHandlerReleasesSharedChildFromAnotherParentWhileItIsReleased_ThenChildInheritsContextOfParentThatStillHoldsIt()
    {
        // Arrange: the handler runs before the inheritance handler and changes which parents hold the child.
        var shared = new Person { FirstName = "Shared" };
        var first = new Person { FirstName = "First", Mother = shared };
        var second = new Person { FirstName = "Second", Mother = shared };
        var third = new Person { FirstName = "Third", Mother = shared };
        var handler = new ReleasingHandler(shared, first, () => second.Mother = null);
        var context = InterceptorSubjectContext.Create();
        context.AddService<ILifecycleHandler>(handler);
        context.WithContextInheritance();
        var root = new Person(context) { Mother = first, Father = second, Children = [third] };

        // Act
        first.Mother = null;
        root.Father = null;

        // Assert
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), shared.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenParentReleasesSharedChildThatItStillHoldsInAnotherProperty_ThenChildKeepsItsContext()
    {
        // Arrange: a service of the first parent's context tells which context the child inherits.
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var shared = new Person { FirstName = "Shared" };
        var first = new Person { FirstName = "First" };
        var second = new Person { FirstName = "Second" };
        _ = new Person(context) { Mother = first, Father = second };
        ((IInterceptorSubject)first).Context.AddService("first");
        first.Mother = shared;
        first.Father = shared;
        second.Mother = shared;

        // Act
        first.Mother = null;

        // Assert
        Assert.Equal("first", Assert.Single(shared.GetServices<string>()));
    }

    [Fact]
    public void WhenSharedChildMovesWithinTheArrayOfTheParentThatAttachedItBeforeItIsReleased_ThenChildInheritsContextOfRemainingParent()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var other = new Person { FirstName = "Other" };
        var shared = new Person { FirstName = "Shared" };
        var first = new Person { FirstName = "First" };
        var second = new Person { FirstName = "Second" };
        var root = new Person(context) { Mother = first, Father = second };
        first.Children = [other, shared];
        second.Mother = shared;

        // Act
        first.Children = [shared];
        first.Children = [];
        root.Mother = null;

        // Assert
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), shared.GetServices<ILifecycleInterceptor>());
        Assert.True(context.TryGetLifecycleInterceptor()!.TryRunWhileAttached(shared, () => { }));
    }

    [Fact]
    public void WhenParentReleasesSubjectThatOnlyItsOwnChildStillHolds_ThenSubjectKeepsItsContextAndCanBeAssignedAgain()
    {
        // Arrange: the child of the subject holds it as well, which would make a cycle of their contexts.
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

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
    public void WhenRemainingParentFallsBackOnTheSubjectThroughAnOldFallbackContext_ThenSubjectsKeepTheServicesOfTheGraph()
    {
        // Arrange: after the first release the subject keeps the context of the parent that released it, because only
        // its own child still holds it. That parent then becomes a child of the subject.
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var parent = new Person { FirstName = "Parent" };
        var subject = new Person { FirstName = "Subject" };
        var child = new Person { FirstName = "Child" };
        var root = new Person(context) { Mother = parent };
        parent.Mother = subject;
        subject.Mother = child;
        child.Mother = subject;
        root.Father = child;
        parent.Mother = null;
        subject.Father = parent;

        // Act
        root.Mother = null;
        parent.FirstName = "Changed";

        // Assert
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), parent.GetServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), subject.GetServices<ILifecycleInterceptor>());
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
