using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Sources.Updates;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Sources.Tests;

public class PathExtensionsTests
{
    public static IEnumerable<object[]> GetProviders()
    {
        yield return ["default", DefaultSourcePathProvider.Instance];
        yield return ["attribute", new AttributeBasedSourcePathProvider("test", ".", null)];
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public async Task WhenRetrievingAllPaths_ThenListIsCorrect(string name, ISourcePathProvider connectorPathProvider)
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
        var allPaths = person
            .TryGetRegisteredSubject()?
            .GetAllProperties()
            .GetSourcePaths(connectorPathProvider, person)
            .ToArray() ?? [];

        // Assert
        await Verify(allPaths?.Select(p => p.path))
            .UseMethodName($"{nameof(WhenRetrievingPropertyPath_ThenItIsCorrect)}_{name}")
            .DisableDateCounting();
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public async Task WhenApplyValuesFromSourceAndPaths_ThenSubjectAndChildrenShouldBeUpdated(string name, ISourcePathProvider connectorPathProvider)
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

        var timestamp = DateTimeOffset.UtcNow.AddDays(-200);

        // Act
        person.UpdatePropertyValuesFromSourcePaths(new Dictionary<string, object?>
        {
            { "FirstName", "NewPerson" },
            { "Children[0].FirstName", "NewChild1" },
            { "Children[2].FirstName", "NewChild3" }
        }, timestamp, connectorPathProvider, null);

        person.UpdatePropertyValuesFromSourcePaths(
            ["LastName"], timestamp, (_, _) => "NewLn", connectorPathProvider, null);
        person.UpdatePropertyValueFromSourcePath(
            "Father.FirstName", timestamp, "NewFather", connectorPathProvider, null);
        
        var completeUpdate = SubjectUpdate
            .CreateCompleteUpdate(person, [JsonCamelCasePathProcessor.Instance]);

        // Assert
        Assert.Equal(timestamp, person
            .GetPropertyReference("FirstName")
            .TryGetWriteTimestamp());
     
        await Verify(completeUpdate)
            .UseMethodName($"{nameof(WhenApplyValuesFromSourceAndPaths_ThenSubjectAndChildrenShouldBeUpdated)}_{name}")
            .DisableDateCounting();
    }
    
    [Fact]
    public void WhenRetrievingPropertyPath_ThenItIsCorrect()
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
        var path = person
            .TryGetRegisteredProperty(p => p.Children[2].FirstName)?
            .TryGetSourcePath(DefaultSourcePathProvider.Instance, null);

        // Assert
        Assert.Equal("Children[2].FirstName", path);
    }
    
    [Theory]
    [InlineData("FirstName", "FirstName")]
    [InlineData("Children", "Children")]
    [InlineData("Children[0].FirstName", "FirstName")]
    [InlineData("Children[2].FirstName", "FirstName")]
    public void WhenTryGetPropertyFromSourcePath_ReturnsResolvedProperty(string fullPath, string propertyName)
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
        var (property, _) = person.TryGetPropertyFromSourcePath(fullPath, DefaultSourcePathProvider.Instance);

        // Assert
        Assert.Equal(propertyName, property!.Name);
    }
}