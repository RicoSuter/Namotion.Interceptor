using System.Reactive.Concurrency;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Paths;

public class PathExtensionsTests
{
    public static IEnumerable<object[]> GetProviders()
    {
        yield return ["default", DefaultPathProvider.Instance];
        yield return ["attribute", new AttributeBasedPathProvider("test")];
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public async Task WhenRetrievingAllPaths_ThenListIsCorrect(string name, PathProviderBase pathProvider)
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
            .GetPaths(pathProvider, person)
            .ToArray() ?? [];

        // Assert
        await Verify(allPaths.Select(p => p.path))
            .UseMethodName($"{nameof(WhenRetrievingPropertyPath_ThenItIsCorrect)}_{name}")
            .DisableDateCounting();
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public async Task WhenApplyValuesFromSourceAndPaths_ThenSubjectAndChildrenShouldBeUpdated(string name, PathProviderBase pathProvider)
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
        person.UpdatePropertyValuesFromPaths(new Dictionary<string, object?>
        {
            { "FirstName", "NewPerson" },
            { "Children[0].FirstName", "NewChild1" },
            { "Children[2].FirstName", "NewChild3" }
        }, timestamp, pathProvider, null);

        person.UpdatePropertyValuesFromPaths(
            ["LastName"], timestamp, (_, _) => "NewLn", pathProvider, null);
        person.UpdatePropertyValueFromPath(
            "Father.FirstName", timestamp, "NewFather", pathProvider, null);
        
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
        var defaultPathProvider = DefaultPathProvider.Instance;
        var path = person
            .TryGetRegisteredProperty(p => p.Children[2].FirstName)?
            .TryGetPath(defaultPathProvider, null);

        // Assert
        Assert.Equal("Children[2].FirstName", path);
    }

    [Theory]
    [InlineData("FirstName", "FirstName")]
    [InlineData("Children", "Children")]
    [InlineData("Children[0].FirstName", "FirstName")]
    [InlineData("Children[2].FirstName", "FirstName")]
    [InlineData("Father.FirstName", "FirstName")]
    [InlineData("Father.Mother.FirstName", "FirstName")]
    [InlineData("Relationships[boss].FirstName", "FirstName")]
    public void WhenTryGetPropertyFromPath_ReturnsResolvedProperty(string fullPath, string propertyName)
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var grandmother = new Person { FirstName = "Grandmother" };
        var father = new Person { FirstName = "Father", Mother = grandmother };
        var mother = new Person { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };
        var boss = new Person { FirstName = "Boss" };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother,
            Father = father,
            Children = [child1, child2, child3],
            Relationships = new Dictionary<string, Person> { ["boss"] = boss }
        };

        // Act
        var defaultPathProvider = DefaultPathProvider.Instance;
        var (property, _) = person.TryGetPropertyFromPath(fullPath, defaultPathProvider);

        // Assert
        Assert.Equal(propertyName, property!.Name);
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public void WhenTryGetPath_SimpleProperty_ReturnsPropertyName(string _, PathProviderBase pathProvider)
    {
        // Arrange
        var person = CreateTestGraph();

        // Act
        var path = person
            .TryGetRegisteredProperty(p => p.FirstName)?
            .TryGetPath(pathProvider, null);

        // Assert
        Assert.Equal("FirstName", path);
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public void WhenTryGetPath_DirectChildProperty_ReturnsNestedPath(string _, PathProviderBase pathProvider)
    {
        // Arrange
        var person = CreateTestGraph();

        // Act
        var path = person
            .TryGetRegisteredProperty(p => p.Father!.FirstName)?
            .TryGetPath(pathProvider, null);

        // Assert
        Assert.Equal("Father.FirstName", path);
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public void WhenTryGetPath_ListIndexedChild_ReturnsIndexedPath(string _, PathProviderBase pathProvider)
    {
        // Arrange
        var person = CreateTestGraph();

        // Act
        var path = person
            .TryGetRegisteredProperty(p => p.Children[2].FirstName)?
            .TryGetPath(pathProvider, null);

        // Assert
        Assert.Equal("Children[2].FirstName", path);
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public void WhenTryGetPath_DictionaryKeyedChild_ReturnsDictionaryPath(string _, PathProviderBase pathProvider)
    {
        // Arrange
        var person = CreateTestGraph();

        // Act
        var path = person
            .TryGetRegisteredProperty(p => p.Relationships!["boss"].FirstName)?
            .TryGetPath(pathProvider, null);

        // Assert
        Assert.Equal("Relationships[boss].FirstName", path);
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public void WhenTryGetPath_DeeplyNested_ReturnsFullPath(string _, PathProviderBase pathProvider)
    {
        // Arrange
        var person = CreateTestGraph();

        // Act
        var path = person
            .TryGetRegisteredProperty(p => p.Father!.Mother!.FirstName)?
            .TryGetPath(pathProvider, null);

        // Assert
        Assert.Equal("Father.Mother.FirstName", path);
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public void WhenTryGetPath_WithRootSubject_ReturnsRelativePath(string _, PathProviderBase pathProvider)
    {
        // Arrange
        var person = CreateTestGraph();

        // Act
        var path = person
            .TryGetRegisteredProperty(p => p.Father!.FirstName)?
            .TryGetPath(pathProvider, person.Father);

        // Assert
        Assert.Equal("FirstName", path);
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public void WhenTryGetPath_StandaloneSubject_ReturnsPropertyName(string _, PathProviderBase pathProvider)
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var standalone = new Person(context) { FirstName = "Standalone" };

        // Act
        var path = standalone
            .TryGetRegisteredProperty(p => p.FirstName)?
            .TryGetPath(pathProvider, null);

        // Assert
        Assert.Equal("FirstName", path);
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public void WhenRoundTrip_SimpleProperty_ResolvesBackToSameProperty(string _, PathProviderBase pathProvider)
    {
        // Arrange
        var person = CreateTestGraph();
        var originalProperty = person.TryGetRegisteredProperty(p => p.FirstName)!;
        var path = originalProperty.TryGetPath(pathProvider, null)!;

        // Act
        var (resolvedProperty, _) = person.TryGetPropertyFromPath(path, pathProvider);

        // Assert
        Assert.NotNull(resolvedProperty);
        Assert.Equal(originalProperty.Name, resolvedProperty.Name);
        Assert.Equal(originalProperty.Parent.Subject, resolvedProperty.Parent.Subject);
    }

    // Multi-segment round-trip tests use DefaultPathProvider only because
    // AttributeBasedPathProvider can't resolve intermediate segments without [Path] attributes.

    [Fact]
    public void WhenRoundTrip_DirectChild_ResolvesBackToSameProperty()
    {
        // Arrange
        var person = CreateTestGraph();
        var pathProvider = DefaultPathProvider.Instance;
        var originalProperty = person.TryGetRegisteredProperty(p => p.Father!.FirstName)!;
        var path = originalProperty.TryGetPath(pathProvider, null)!;

        // Act
        var (resolvedProperty, _) = person.TryGetPropertyFromPath(path, pathProvider);

        // Assert
        Assert.NotNull(resolvedProperty);
        Assert.Equal(originalProperty.Name, resolvedProperty.Name);
        Assert.Equal(originalProperty.Parent.Subject, resolvedProperty.Parent.Subject);
    }

    [Fact]
    public void WhenRoundTrip_ListIndexed_ResolvesBackToSameProperty()
    {
        // Arrange
        var person = CreateTestGraph();
        var pathProvider = DefaultPathProvider.Instance;
        var originalProperty = person.TryGetRegisteredProperty(p => p.Children[1].FirstName)!;
        var path = originalProperty.TryGetPath(pathProvider, null)!;

        // Act
        var (resolvedProperty, _) = person.TryGetPropertyFromPath(path, pathProvider);

        // Assert
        Assert.NotNull(resolvedProperty);
        Assert.Equal(originalProperty.Name, resolvedProperty.Name);
        Assert.Equal(originalProperty.Parent.Subject, resolvedProperty.Parent.Subject);
    }

    [Fact]
    public void WhenRoundTrip_DictionaryKeyed_ResolvesBackToSameProperty()
    {
        // Arrange
        var person = CreateTestGraph();
        var pathProvider = DefaultPathProvider.Instance;
        var originalProperty = person.TryGetRegisteredProperty(p => p.Relationships!["boss"].FirstName)!;
        var path = originalProperty.TryGetPath(pathProvider, null)!;

        // Act
        var (resolvedProperty, _) = person.TryGetPropertyFromPath(path, pathProvider);

        // Assert
        Assert.NotNull(resolvedProperty);
        Assert.Equal(originalProperty.Name, resolvedProperty.Name);
        Assert.Equal(originalProperty.Parent.Subject, resolvedProperty.Parent.Subject);
    }

    [Fact]
    public void WhenRoundTrip_DeeplyNested_ResolvesBackToSameProperty()
    {
        // Arrange
        var person = CreateTestGraph();
        var pathProvider = DefaultPathProvider.Instance;
        var originalProperty = person.TryGetRegisteredProperty(p => p.Father!.Mother!.FirstName)!;
        var path = originalProperty.TryGetPath(pathProvider, null)!;

        // Act
        var (resolvedProperty, _) = person.TryGetPropertyFromPath(path, pathProvider);

        // Assert
        Assert.NotNull(resolvedProperty);
        Assert.Equal(originalProperty.Name, resolvedProperty.Name);
        Assert.Equal(originalProperty.Parent.Subject, resolvedProperty.Parent.Subject);
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public void WhenRegistryTryGetPath_ListIndexedChild_ReturnsIndexedPath(string _, PathProviderBase pathProvider)
    {
        // Arrange
        var person = CreateTestGraph();
        var registeredProperty = person.TryGetRegisteredProperty(p => p.Children[2].FirstName)!;

        // Act — call registry-level TryGetPath directly
        var path = registeredProperty.TryGetPath(pathProvider, null);

        // Assert
        Assert.Equal("Children[2].FirstName", path);
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public void WhenRegistryTryGetPath_DictionaryKeyedChild_ReturnsDictionaryPath(string _, PathProviderBase pathProvider)
    {
        // Arrange
        var person = CreateTestGraph();
        var registeredProperty = person.TryGetRegisteredProperty(p => p.Relationships!["boss"].FirstName)!;

        // Act — call registry-level TryGetPath directly
        var path = registeredProperty.TryGetPath(pathProvider, null);

        // Assert
        Assert.Equal("Relationships[boss].FirstName", path);
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public void WhenRegistryTryGetPath_DeeplyNested_ReturnsFullPath(string _, PathProviderBase pathProvider)
    {
        // Arrange
        var person = CreateTestGraph();
        var registeredProperty = person.TryGetRegisteredProperty(p => p.Father!.Mother!.FirstName)!;

        // Act — call registry-level TryGetPath directly
        var path = registeredProperty.TryGetPath(pathProvider, null);

        // Assert
        Assert.Equal("Father.Mother.FirstName", path);
    }

    [Fact]
    public void WhenGetPropertiesFromPaths_ReturnsMatchingPropertiesAndSkipsInvalid()
    {
        // Arrange
        var person = CreateTestGraph();
        var pathProvider = DefaultPathProvider.Instance;
        var registeredSubject = person.TryGetRegisteredSubject()!;
        var paths = new[] { "FirstName", "NonExistent", "Father.FirstName" };

        // Act
        var results = pathProvider.GetPropertiesFromPaths(registeredSubject, paths).ToList();

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Equal("FirstName", results[0].Name);
        Assert.Equal("FirstName", results[1].Name);
        Assert.Equal(person, results[0].Subject);
        Assert.Equal(person.Father, results[1].Subject);
    }

    [Theory]
    [MemberData(nameof(GetProviders))]
    public void WhenGetPaths_FromChanges_ReturnsPathsForChangedProperties(string _, PathProviderBase pathProvider)
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeObservable();

        var person = new Person(context)
        {
            FirstName = "Root",
            Father = new Person { FirstName = "Father" },
            Children = [new Person { FirstName = "Child1" }, new Person { FirstName = "Child2" }]
        };

        var changes = new List<SubjectPropertyChange>();
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        // Act — trigger changes
        person.FirstName = "NewRoot";
        person.Father!.FirstName = "NewFather";
        person.Children[1].FirstName = "NewChild2";

        var paths = changes
            .GetPaths(pathProvider, person)
            .ToArray();

        // Assert
        Assert.Equal(3, paths.Length);
        Assert.Equal("FirstName", paths[0].path);
        Assert.Equal("Father.FirstName", paths[1].path);
        Assert.Equal("Children[1].FirstName", paths[2].path);
    }

    private static Person CreateTestGraph()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var grandmother = new Person { FirstName = "Grandmother" };
        var father = new Person { FirstName = "Father", Mother = grandmother };
        var mother = new Person { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };
        var boss = new Person { FirstName = "Boss" };
        var mentor = new Person { FirstName = "Mentor" };

        var person = new Person(context)
        {
            FirstName = "Root",
            Father = father,
            Mother = mother,
            Children = [child1, child2, child3],
            Relationships = new Dictionary<string, Person>
            {
                ["boss"] = boss,
                ["mentor"] = mentor
            }
        };

        return person;
    }
}
