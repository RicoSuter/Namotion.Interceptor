using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class SearchToolTests
{
    [Fact]
    public async Task WhenSearchByType_ThenReturnsMatchingSubjects()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new { format = "json", types = new[] { "TestDevice" } });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Equal(1, json.GetProperty("subjectCount").GetInt32());
    }

    [Fact]
    public async Task WhenSearchByText_ThenMatchesEnricherTitle()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var enricher = new TitleEnricher();
        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            SubjectEnrichers = { enricher }
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new { format = "json", text = "light" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("subjectCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task WhenSearchByText_ThenMatchesPath()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new { format = "json", text = "Device" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("subjectCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task WhenIncludeProperties_ThenReturnsPropertyValues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new
        {
            format = "json",
            types = new[] { "TestDevice" },
            includeProperties = true
        });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var firstSubject = json.GetProperty("results").EnumerateObject().First().Value;
        var properties = firstSubject.GetProperty("properties");
        var deviceNameProp = properties.GetProperty("DeviceName");
        Assert.Equal("Light", deviceNameProp.GetProperty("value").GetString());
    }

    [Fact]
    public async Task WhenMaxSubjectsExceeded_ThenResultIsTruncated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            MaxSubjectsPerResponse = 0
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new { format = "json", types = new[] { "TestDevice" } });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task WhenMaxSubjectsParameter_ThenLimitsResponseCount()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var childA = new TestContainer(context) { Name = "A" };
        var childB = new TestContainer(context) { Name = "B" };
        var container = new TestContainer(context) { Name = "Root" };
        container.Children = new Dictionary<string, TestContainer>
        {
            ["A"] = childA,
            ["B"] = childB
        };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(container, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new { format = "json", types = new[] { "TestContainer" }, maxSubjects = 1 });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Equal(1, json.GetProperty("subjectCount").GetInt32());
        Assert.True(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task WhenMethodsAndInterfacesNotRequested_ThenTheyAreExcluded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var enricher = new MethodsAndInterfacesEnricher();
        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            SubjectEnrichers = { enricher }
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new { format = "json", types = new[] { "TestDevice" } });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var firstSubject = json.GetProperty("results").EnumerateObject().First().Value;
        Assert.False(firstSubject.TryGetProperty("methods", out _));
        Assert.False(firstSubject.TryGetProperty("interfaces", out _));
        Assert.True(firstSubject.TryGetProperty("$title", out _));
    }

    [Fact]
    public async Task WhenMethodsAndInterfacesRequested_ThenTheyAreIncluded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var enricher = new MethodsAndInterfacesEnricher();
        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            SubjectEnrichers = { enricher }
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new
        {
            format = "json",
            types = new[] { "TestDevice" },
            includeMethods = true,
            includeInterfaces = true
        });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var firstSubject = json.GetProperty("results").EnumerateObject().First().Value;
        Assert.True(firstSubject.TryGetProperty("methods", out _));
        Assert.True(firstSubject.TryGetProperty("interfaces", out _));
    }

    [Fact]
    public async Task WhenExcludeTypes_ThenMatchingSubjectsAreFiltered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act — with exclude
        var inputWithExclude = JsonSerializer.SerializeToElement(new
        {
            format = "json",
            types = new[] { "TestDevice" },
            excludeTypes = new[] { "TestDevice" }
        });
        var resultWithExclude = await tool.Handler(inputWithExclude, CancellationToken.None);
        var jsonWithExclude = JsonSerializer.SerializeToElement(resultWithExclude);

        // Act — without exclude
        var inputWithoutExclude = JsonSerializer.SerializeToElement(new
        {
            format = "json",
            types = new[] { "TestDevice" }
        });
        var resultWithoutExclude = await tool.Handler(inputWithoutExclude, CancellationToken.None);
        var jsonWithoutExclude = JsonSerializer.SerializeToElement(resultWithoutExclude);

        // Assert
        Assert.Equal(0, jsonWithExclude.GetProperty("subjectCount").GetInt32());
        Assert.Equal(1, jsonWithoutExclude.GetProperty("subjectCount").GetInt32());
    }

    [Fact]
    public async Task WhenPathSpecified_ThenScopesToSubtree()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var container = new TestContainer(context) { Name = "Root" };
        container.Children["GroupA"] = new TestContainer(context) { Name = "A" };
        container.Children["GroupB"] = new TestContainer(context) { Name = "B" };

        var innerA = container.Children["GroupA"] as TestContainer;
        innerA!.Children["Nested"] = new TestContainer(context) { Name = "Nested" };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(container, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new
        {
            format = "json",
            path = "Children[GroupA]",
            types = new[] { "TestContainer" }
        });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var paths = json.GetProperty("results").EnumerateObject().Select(p => p.Name).ToList();
        Assert.All(paths, p => Assert.StartsWith("Children[GroupA]", p));
    }

    [Fact]
    public async Task WhenNoFormatSpecified_ThenDefaultsToText()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new { types = new[] { "TestDevice" } });
        var result = await tool.Handler(input, CancellationToken.None);

        // Assert
        var text = Assert.IsType<string>(result);
        Assert.StartsWith("# path [Type]", text);
        Assert.Contains("[1 subject]", text);
    }

    [Fact]
    public async Task WhenPathPrefixConfigured_ThenPathsArePrefixed()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance, PathPrefix = "/" };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new { format = "json", types = new[] { "TestDevice" } });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var firstSubject = json.GetProperty("results").EnumerateObject().First();
        Assert.StartsWith("/", firstSubject.Name);
        Assert.Equal("/Device", firstSubject.Value.GetProperty("$path").GetString());
    }

    [Fact]
    public async Task WhenPathPrefixWithPathScope_ThenScopesByPrefixedPath()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var container = new TestContainer(context) { Name = "Root" };
        container.Children = new Dictionary<string, TestContainer>
        {
            ["GroupA"] = new(context) { Name = "A" },
            ["GroupB"] = new(context) { Name = "B" }
        };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance, PathPrefix = "/" };
        var factory = new McpToolFactory(container, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new { format = "json", path = "/Children[GroupA]", types = new[] { "TestContainer" } });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var paths = json.GetProperty("results").EnumerateObject().Select(p => p.Name).ToList();
        Assert.All(paths, p => Assert.StartsWith("/Children[GroupA]", p));
    }

    private class TitleEnricher : IMcpSubjectEnricher
    {
        public IDictionary<string, object?> GetSubjectEnrichments(RegisteredSubject subject)
        {
            var metadata = new Dictionary<string, object?>();
            if (subject.Subject is TestDevice device)
            {
                metadata["$title"] = device.DeviceName;
            }
            return metadata;
        }
    }

    private class MethodsAndInterfacesEnricher : IMcpSubjectEnricher
    {
        public IDictionary<string, object?> GetSubjectEnrichments(RegisteredSubject subject)
        {
            return new Dictionary<string, object?>
            {
                ["$title"] = "Test",
                ["$methods"] = new[] { "DoSomething" },
                ["$interfaces"] = new[] { "ITest" }
            };
        }
    }
}
