using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Sources.Updates;
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
            new SubjectPropertyChange(new PropertyReference(person, "FirstName"), DateTimeOffset.Now, "Old", "NewPerson"),
            new SubjectPropertyChange(new PropertyReference(father, "FirstName"), DateTimeOffset.Now, "Old", "NewFather"),
            new SubjectPropertyChange(new PropertyReference(child1, "FirstName"), DateTimeOffset.Now, "Old", "NewChild1"),
            new SubjectPropertyChange(new PropertyReference(child3, "FirstName"), DateTimeOffset.Now, "Old", "NewChild3"),
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
            new SubjectPropertyChange(new PropertyReference(person, "FirstName"), DateTimeOffset.Now, "Old", "NewPerson"), // ignored
            new SubjectPropertyChange(new PropertyReference(father, "FirstName"), DateTimeOffset.Now, "Old", "NewFather"),
            new SubjectPropertyChange(new PropertyReference(mother, "FirstName"), DateTimeOffset.Now, "Old", "NewMother"), // ignored
            new SubjectPropertyChange(new PropertyReference(child1, "FirstName"), DateTimeOffset.Now, "Old", "NewChild1"),
            new SubjectPropertyChange(new PropertyReference(child3, "FirstName"), DateTimeOffset.Now, "Old", "NewChild3"),
        };

        // Act
        var partialSubjectUpdate = SubjectUpdate
            .CreatePartialUpdateFromChanges(father, changes) // TODO(perf): This method can probably made much faster in case of non-root subjects (no need to create many objects)
            .ConvertToJsonCamelCasePath();

        // Assert
        await Verify(partialSubjectUpdate);
    }

    // [Fact]
    // public async Task WhenCreatingSubjectUpdateFromPath_ThenResultIsCorrect()
    // {
    //     // Arrange
    //     var context = InterceptorSubjectContext
    //         .Create()
    //         .WithRegistry();
    //
    //     var father = new Person { FirstName = "Father" };
    //     var mother = new Person { FirstName = "Mother" };
    //     var child1 = new Person { FirstName = "Child1" };
    //     var child2 = new Person { FirstName = "Child2" };
    //     var child3 = new Person { FirstName = "Child3" };
    //
    //     var person = new Person(context)
    //     {
    //         FirstName = "Child",
    //         Mother = mother,
    //         Father = father,
    //         Children = [child1, child2, child3]
    //     };
    //
    //     // Act
    //     var sourcePathProvider = new DefaultSourcePathProvider();
    //     var partialSubjectUpdate = person.CreateUpdateFromSourcePaths(
    //         new Dictionary<string, object?>
    //         {
    //             { "Children[1].FirstName", "RandomName1" },
    //             { "Children[2].FirstName", "RandomName2" },
    //             { "Father.FirstName", "RandomName3" }
    //         }, sourcePathProvider);
    //
    //     // Assert
    //     await Verify(partialSubjectUpdate);
    // }

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