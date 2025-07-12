using System.Text.Json;
using Namotion.Interceptor.AspNetCore.Extensions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;
using Person = Namotion.Interceptor.Registry.Tests.Models.Person;

namespace Namotion.Interceptor.Registry.Tests;

public class SubjectRegistryTests
{
    [Fact]
    public void WhenTwoChildrenAreAttachedSequentially_ThenWeHaveThreeAttaches()
    {
        // Arrange
        var attaches = new List<SubjectLifecycleChange>();
        var detaches = new List<SubjectLifecycleChange>();

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
        var attaches = new List<SubjectLifecycleChange>();
        var detaches = new List<SubjectLifecycleChange>();

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
        var attaches = new List<SubjectLifecycleChange>();
        var detaches = new List<SubjectLifecycleChange>();

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
    public void WhenRemovingMiddleElement_ThenChildrenAreAlsoRemoved()
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
        // TODO: Move to Namotion.Interceptor.AspNetCore.Tests
        
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
        var jsonSerializerOptions = new JsonSerializerOptions(JsonSerializerOptions.Default)
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        
        await Verify(person.ToJsonObject(jsonSerializerOptions).ToJsonString(jsonSerializerOptions));
    }

    [Fact]
    public async Task WhenChangingCollection_ThenIndexAreCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Children = [child1, child2, child3]
        };

        // Act
        person.Children = person.Children.Union([new Person { FirstName = "Child4" }]).ToArray(); // add child4
        person.Children = person.Children.Skip(2).ToArray(); // remove child1 and child2
        
        // Assert
        var children = person
            .TryGetRegisteredSubject()?
            .TryGetProperty(nameof(Person.Children))?
            .Children
            .Select(c => new
            {
                Index = c.Index,
                Subject = c.Subject is Person p ? p.FirstName : "n/a"
            });

        await Verify(children).DisableDateCounting();
    }
}