using System.Text.Json;
using Namotion.Interceptor.AspNetCore.Models;
using Namotion.Interceptor.AspNetCore.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.AspNetCore.Tests;

public class SubjectDescriptionTests
{
    [Fact]
    public async Task WhenCallingCreate_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person
        {
            FirstName = "Father"
        };

        var mother = new Person
        {
            FirstName = "Mother",
        };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
        };
        
        // Act
        var partialSubjectDescription = SubjectDescription.Create(person, CreateJsonSerializerOptions());
        
        // Assert
        await Verify(partialSubjectDescription);
    }

    [Fact]
    public async Task WhenCallingCreatePartialsFromChanges_ThenResultIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var father = new Person
        {
            FirstName = "Father"
        };

        var mother = new Person
        {
            FirstName = "Mother",
        };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
        };
        
        var changes = new []
        {
            new PropertyChangedContext(new PropertyReference(person, "FirstName"), "Old", "New"),
            new PropertyChangedContext(new PropertyReference(father, "FirstName"), "Old", "New"),
        };

        // Act
        var partialSubjectDescription = 
            SubjectDescription.CreatePartialsFromChanges(changes, CreateJsonSerializerOptions());
        
        // Assert
        await Verify(partialSubjectDescription);
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