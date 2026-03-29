using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class SearchToolTests
{
    [Fact]
    public async Task Search_by_type_returns_matching_subjects()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new { types = new[] { "TestDevice" } });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Equal(1, json.GetProperty("subjectCount").GetInt32());
    }

    [Fact]
    public async Task Search_by_text_matches_enricher_title()
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

        // Act — search for "light" should match the device enriched with $title = "Light"
        var input = JsonSerializer.SerializeToElement(new { text = "light" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("subjectCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Search_by_text_matches_path()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act — search for "Device" should match the path "Device"
        var input = JsonSerializer.SerializeToElement(new { text = "Device" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("subjectCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Search_with_includeProperties_returns_property_values()
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
            types = new[] { "TestDevice" },
            includeProperties = true
        });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — the device subject node should contain properties
        var subjects = json.GetProperty("results");
        var firstSubject = subjects.EnumerateObject().First().Value;
        Assert.True(firstSubject.TryGetProperty("DeviceName", out var deviceName));
        Assert.Equal("Light", deviceName.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Search_respects_max_subjects_limit()
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
        var input = JsonSerializer.SerializeToElement(new { types = new[] { "TestDevice" } });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Search_maxSubjects_limits_response_count()
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

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(container, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act — request only 1 subject
        var input = JsonSerializer.SerializeToElement(new { types = new[] { "TestContainer" }, maxSubjects = 1 });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Equal(1, json.GetProperty("subjectCount").GetInt32());
        Assert.True(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Search_excludes_methods_and_interfaces_by_default()
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

        // Act — no includeMethods/includeInterfaces flags
        var input = JsonSerializer.SerializeToElement(new { types = new[] { "TestDevice" } });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — $methods and $interfaces should be stripped
        var firstSubject = json.GetProperty("results").EnumerateObject().First().Value;
        Assert.False(firstSubject.TryGetProperty("$methods", out _));
        Assert.False(firstSubject.TryGetProperty("$interfaces", out _));
        // $title should still be present (not filtered)
        Assert.True(firstSubject.TryGetProperty("$title", out _));
    }

    [Fact]
    public async Task Search_includes_methods_and_interfaces_when_requested()
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
        var input = JsonSerializer.SerializeToElement(new { types = new[] { "TestDevice" }, includeMethods = true, includeInterfaces = true });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var firstSubject = json.GetProperty("results").EnumerateObject().First().Value;
        Assert.True(firstSubject.TryGetProperty("$methods", out _));
        Assert.True(firstSubject.TryGetProperty("$interfaces", out _));
    }

    [Fact]
    public async Task Search_excludeTypes_filters_out_matching_subjects()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act — search all but exclude TestDevice
        var input = JsonSerializer.SerializeToElement(new
        {
            types = new[] { "TestDevice", "TestRoom" },
            excludeTypes = new[] { "TestDevice" }
        });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — only TestRoom should remain
        Assert.Equal(1, json.GetProperty("subjectCount").GetInt32());
    }

    [Fact]
    public async Task Search_path_scopes_to_subtree()
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

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(container, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act — search only within Children[GroupA]
        var input = JsonSerializer.SerializeToElement(new
        {
            path = "Children[GroupA]",
            types = new[] { "TestContainer" }
        });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — should find GroupA and Nested, but not GroupB or Root
        var subjects = json.GetProperty("results");
        var paths = subjects.EnumerateObject().Select(p => p.Name).ToList();
        Assert.All(paths, p => Assert.StartsWith("Children[GroupA]", p));
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
