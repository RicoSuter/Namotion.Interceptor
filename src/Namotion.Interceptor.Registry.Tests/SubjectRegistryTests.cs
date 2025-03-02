using System.Text.Json;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;
using Person = Namotion.Interceptor.Tests.Models.Person;

namespace Namotion.Interceptor.Registry.Tests;

public class SubjectRegistryTests
{
    [Fact]
    public void WhenTwoChildrenAreAttachedSequentially_ThenWeHaveThreeAttaches()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestLifecyleHandler(attaches, detaches);
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => handler);

        // Act
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = new Person
            {
                FirstName = "Mother",
                Mother = new Person
                {
                    FirstName = "Grandmother"
                }
            }
        };

        // Assert
        Assert.Equal(3, attaches.Count);
        Assert.Empty(detaches);

        var registry = context.GetService<ISubjectRegistry>();
        Assert.Equal(3, registry.KnownSubjects.Count());
    }

    [Fact]
    public void WhenTwoChildrenAreAttachedInOneBranch_ThenWeHaveThreeAttaches()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestLifecyleHandler(attaches, detaches);
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => handler);

        // Act
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = new Person
            {
                FirstName = "Mother",
                Mother = new Person
                {
                    FirstName = "Grandmother"
                }
            }
        };

        // Assert
        Assert.Equal(3, attaches.Count);
        Assert.Empty(detaches);

        var registry = context.GetService<ISubjectRegistry>();
        Assert.Equal(3, registry.KnownSubjects.Count());
    }

    [Fact]
    public void WhenProxyWithChildProxyIsRemoved_ThenWeHaveTwoDetaches()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestLifecyleHandler(attaches, detaches);
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => handler);

        // Act
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = new Person
            {
                FirstName = "Mother",
                Mother = new Person
                {
                    FirstName = "Grandmother"
                }
            }
        };

        person.Mother = null;

        // Assert
        Assert.Equal(3, attaches.Count);
        Assert.Equal(2, detaches.Count);

        var registry = context.GetService<ISubjectRegistry>();
        Assert.Single(registry.KnownSubjects);
    }

    [Fact]
    public void WhenAddingTransitiveProxies_ThenAllAreAvailable()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

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
        Assert.Equal(3, registry.KnownSubjects.Count);
        Assert.Contains(person, registry.KnownSubjects.Keys);
        Assert.Contains(mother, registry.KnownSubjects.Keys);
        Assert.Contains(grandmother, registry.KnownSubjects.Keys);
    }

    [Fact]
    public void WhenRemovingMiddleElement_ThenChildrensAreAlsoRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

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

        mother.Mother = null;

        // Assert
        Assert.Equal(2, registry.KnownSubjects.Count());
        Assert.Contains(person, registry.KnownSubjects.Keys);
        Assert.Contains(mother, registry.KnownSubjects.Keys);
        Assert.DoesNotContain(grandmother, registry.KnownSubjects.Keys);
    }

    [Fact]
    public async Task WhenConvertingToJson_ThenGraphIsPreserved()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        // Act
        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = new Person
            {
                FirstName = "Mother",
                Mother = new Person
                {
                    FirstName = "Grandmother"
                }
            }
        };

        // Assert
        await Verify(person.ToJsonObject()
            .ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true }));
    }
}
