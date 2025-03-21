using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Extensions;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Tests;

public class SubjectUpdateTests
{
    [Fact]
    public async Task WhenGeneratingCompleteSubjectDescription_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person { FirstName = "Father" };
        var mother = new Person { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
            Children = [child1, child2, child3]
        };

        // Act
        var completeSubjectUpdate = SubjectUpdate
            .CreateCompleteUpdate(person)
            .ConvertToJsonCamelCasePath();

        // Assert
        await Verify(completeSubjectUpdate);
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectDescription_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person { FirstName = "Father" };
        var mother = new Person { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
            Children = [child1, child2, child3]
        };

        var changes = new[]
        {
            new PropertyChangedContext(new PropertyReference(person, "FirstName"), "Old", "NewPerson"),
            new PropertyChangedContext(new PropertyReference(father, "FirstName"), "Old", "NewFather"),
            new PropertyChangedContext(new PropertyReference(child1, "FirstName"), "Old", "NewChild1"),
            new PropertyChangedContext(new PropertyReference(child3, "FirstName"), "Old", "NewChild3"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(person, changes)
            .ConvertToJsonCamelCasePath();

        // Assert
        await Verify(partialSubjectUpdate);
    }

    [Fact]
    public async Task WhenGeneratingPartialSubjectDescriptionForNonRoot_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };
        var father = new Person { FirstName = "Father", Children = [child1, child2, child3] };
        var mother = new Person { FirstName = "Mother" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
        };

        var changes = new[]
        {
            new PropertyChangedContext(new PropertyReference(person, "FirstName"), "Old", "NewPerson"), // ignored
            new PropertyChangedContext(new PropertyReference(father, "FirstName"), "Old", "NewFather"),
            new PropertyChangedContext(new PropertyReference(mother, "FirstName"), "Old", "NewMother"), // ignored
            new PropertyChangedContext(new PropertyReference(child1, "FirstName"), "Old", "NewChild1"),
            new PropertyChangedContext(new PropertyReference(child3, "FirstName"), "Old", "NewChild3"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(father, changes) // TODO(perf): This method can probably made much faster in case of non-root subjects (no need to create many objects)
            .ConvertToJsonCamelCasePath();

        // Assert
        await Verify(partialSubjectUpdate);
    }

    [Fact]
    public async Task WhenCreatingSubjectUpdateFromPath_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person { FirstName = "Father" };
        var mother = new Person { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
            Children = [child1, child2, child3]
        };

        // Act
        var sourcePathProvider = new TestSourcePathProvider();
        var partialSubjectUpdate = person.CreateUpdateFromSourcePaths(
            new Dictionary<string, object?>
            {
                { "Children[1].FirstName", "RandomName1" },
                { "Children[2].FirstName", "RandomName2" },
                { "Father.FirstName", "RandomName3" }
            }, sourcePathProvider);

        // Assert
        await Verify(partialSubjectUpdate);
    }

    public class TestSourcePathProvider : ISourcePathProvider
    {
        public bool IsPropertyIncluded(RegisteredSubjectProperty property)
        {
            return true;
        }

        public string? TryGetPropertyName(RegisteredSubjectProperty property)
        {
            return property.BrowseName;
        }

        public string GetPropertyAttributeFullPath(RegisteredSubjectProperty attribute, string pathPrefix)
        {
            return pathPrefix;
        }

        public string GetPropertyFullPath(string path, RegisteredSubjectProperty property)
        {
            return path;
        }
    }

    private static JsonSerializerOptions CreateJsonSerializerOptions()
    {
        var jsonSerializerOptions = new JsonSerializerOptions(JsonSerializerOptions.Default)
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        return jsonSerializerOptions;
    }
}