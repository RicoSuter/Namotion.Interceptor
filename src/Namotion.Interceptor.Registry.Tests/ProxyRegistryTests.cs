using System.Text.Json;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tests;
using Namotion.Interceptor.Tracking.Abstractions;
using Person = Namotion.Interceptor.Tests.Models.Person;

namespace Namotion.Interceptor.Registry.Tests;

public class ProxyRegistryTests
{
    [Fact]
    public void WhenTwoChildrenAreAttachedSequentially_ThenWeHaveThreeAttaches()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestProxyPropertyRegistryHandler(attaches, detaches);
        var collection = InterceptorCollection
            .Create()
            .WithRegistry()
            .WithService(() => handler);

        // Act
        var person = new Person(collection)
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

        var registry = collection.GetService<IProxyRegistry>();
        Assert.Equal(3, registry.KnownProxies.Count());
    }

    [Fact]
    public void WhenTwoChildrenAreAttachedInOneBranch_ThenWeHaveThreeAttaches()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestProxyPropertyRegistryHandler(attaches, detaches);
        var collection = InterceptorCollection
            .Create()
            .WithRegistry()
            .WithService(() => handler);

        // Act
        var person = new Person(collection)
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

        var registry = collection.GetService<IProxyRegistry>();
        Assert.Equal(3, registry.KnownProxies.Count());
    }

    [Fact]
    public void WhenProxyWithChildProxyIsRemoved_ThenWeHaveTwoDetaches()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestProxyPropertyRegistryHandler(attaches, detaches);
        var collection = InterceptorCollection
            .Create()
            .WithRegistry()
            .WithService(() => handler);

        // Act
        var person = new Person(collection)
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

        var registry = collection.GetService<IProxyRegistry>();
        Assert.Single(registry.KnownProxies);
    }

    [Fact]
    public void WhenAddingTransitiveProxies_ThenAllAreAvailable()
    {
        // Arrange
        var collection = InterceptorCollection
            .Create()
            .WithRegistry();

        var registry = collection.GetService<IProxyRegistry>();

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
        Assert.Equal(3, registry.KnownProxies.Count());
        Assert.Contains(person, registry.KnownProxies.Keys);
        Assert.Contains(mother, registry.KnownProxies.Keys);
        Assert.Contains(grandmother, registry.KnownProxies.Keys);
    }

    [Fact]
    public void WhenRemovingMiddleElement_ThenChildrensAreAlsoRemoved()
    {
        // Arrange
        var collection = InterceptorCollection
            .Create()
            .WithRegistry();

        var registry = collection.GetService<IProxyRegistry>();

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

        mother.Mother = null;

        // Assert
        Assert.Equal(2, registry.KnownProxies.Count());
        Assert.Contains(person, registry.KnownProxies.Keys);
        Assert.Contains(mother, registry.KnownProxies.Keys);
        Assert.DoesNotContain(grandmother, registry.KnownProxies.Keys);
    }

    [Fact]
    public async Task WhenConvertingToJson_ThenGraphIsPreserved()
    {
        // Arrange
        var collection = InterceptorCollection
            .Create()
            .WithRegistry();

        // Act
        var person = new Person(collection)
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
        await Verify(person.ToJsonObject(collection.GetService<IProxyRegistry>())
            .ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true }));
    }
}
