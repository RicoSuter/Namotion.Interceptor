using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// <c>WithLifecycle()</c> is the one configuration entry point for graph ownership: context
/// inheritance and parent tracking are intrinsic to the built-in lifecycle rather than opt-in
/// handlers, and the removed <c>WithContextInheritance()</c> and <c>WithParents()</c> have no
/// aliases. The default lifecycle is registered idempotently, and a custom
/// <see cref="ILifecycleInterceptor"/> conflicts with it through the singleton contract.
/// </summary>
public class LifecycleConfigurationTests
{
    #region Context inheritance is intrinsic

    [Fact]
    public void WhenPropertyIsAssigned_ThenContextIsInherited()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        // Act
        var person = new Person(context);
        person.Mother = new Person { FirstName = "Mother" };

        // Assert
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), person.Mother.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenPropertyWithDeepStructureIsAssigned_ThenChildrenAlsoInheritTheContext()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

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
    public void WhenPropertyWithDeepStructureIsRemoved_ThenChildrenStopResolvingTheContext()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

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

        // Act
        person.Mother = null;

        // Assert
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), person.GetServices<ILifecycleInterceptor>());
        Assert.Empty(mother.GetServices<ILifecycleInterceptor>());
        Assert.Empty(grandmother.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenArrayIsAssigned_ThenAllChildrenInheritTheContext()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

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
    public void WhenUsingCircularDependencies_ThenAllSubjectsInheritTheContext()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        // Act
        var child1 = new Person(context) { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };

        child1.Mother = child2;
        child2.Mother = child3;
        child3.Mother = child1;

        // Assert
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), child1.GetServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), child2.GetServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), child3.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenAddingServiceToChild_ThenItAppliesToThatSubjectOnly()
    {
        // Arrange
        var service1 = 1;
        var service2 = 2;

        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => service1, x => x == 1)
            .WithLifecycle();

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

        // Assert: every subject in the graph resolves the context's own service, and the service
        // registered on one subject reaches that subject and nothing else. It used to reach the
        // subjects below it as well, because they resolved through it; they now resolve through the
        // context they are attached to.
        Assert.Contains(1, person.GetServices<int>());
        Assert.DoesNotContain(2, person.GetServices<int>());
        Assert.Single(person.GetServices<LifecycleInterceptor>());

        Assert.Contains(1, person.Mother.GetServices<int>());
        Assert.Contains(2, person.Mother.GetServices<int>());

        Assert.Contains(1, person.Mother.Mother.GetServices<int>());
        Assert.DoesNotContain(2, person.Mother.Mother.GetServices<int>());
        Assert.Single(person.Mother.Mother.GetServices<LifecycleInterceptor>());
    }

    #endregion

    #region Parent tracking is intrinsic

    [Fact]
    public void WhenReferencedByTwoPropertiesOfTheSameParent_ThenTwoParentsAreReported()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

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
    public void WhenReferencesAreSetToNull_ThenParentsAreEmpty()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var parent = new Person(context)
        {
            FirstName = "Parent"
        };

        var person = new Person(context);
        person.FirstName = "Child";
        person.Mother = parent;
        person.Father = parent;

        // Act
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
            .WithLifecycle();

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

    #endregion

    #region Registration is idempotent, the singleton contract guards conflicts

    [Fact]
    public void WhenLifecycleIsConfiguredRepeatedly_ThenOneLifecycleIsRegistered()
    {
        // Arrange & Act
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithLifecycle()
            .WithFullPropertyTracking();

        // Assert: one authority, appearing exactly once in the ordered handler fan-out.
        Assert.Single(context.GetServices<ILifecycleInterceptor>());
        Assert.Single(context.GetServices<ILifecycleHandler>(), handler => handler is LifecycleInterceptor);
    }

    [Fact]
    public void WhenACustomLifecycleIsRegistered_ThenWithLifecycleThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService<ILifecycleInterceptor>(new CustomLifecycle());

        // Act & Assert: the default lifecycle is not silently skipped, because half the
        // configuration surface would then quietly run against an implementation it was not
        // written for. The singleton contract turns the ambiguity into an error.
        var exception = Assert.Throws<InvalidOperationException>(() => context.WithLifecycle());
        Assert.Contains("singleton contract", exception.Message);
    }

    private sealed class CustomLifecycle : ILifecycleInterceptor
    {
        public void EnterStructuralWriteGate()
        {
        }

        public void ExitStructuralWriteGate()
        {
        }

        public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAnchorKind anchor)
            => throw new NotSupportedException();

        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
            => throw new NotSupportedException();

        public bool TryAddProperties(SubjectPropertyRegistrationContext registration)
            => throw new NotSupportedException();

        public void OnContextComposed(IInterceptorSubject subject)
        {
        }

        public void OnContextDecomposed(IInterceptorSubject subject)
        {
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
            => next(ref context);
    }

    #endregion
}
