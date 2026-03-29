using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class BrowseToolTests
{
    [Fact]
    public async Task Browse_returns_subject_tree_with_children()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(room, config);
        var tools = factory.CreateTools();
        var browseTool = tools.First(t => t.Name == "browse");

        // Act
        var input = JsonSerializer.SerializeToElement(new { depth = 1, includeProperties = true });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("result", out _));
        Assert.True(json.GetProperty("subjectCount").GetInt32() > 0);
    }

    [Fact]
    public async Task Browse_depth_zero_returns_no_children()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(room, config);
        var tools = factory.CreateTools();
        var browseTool = tools.First(t => t.Name == "browse");

        // Act
        var input = JsonSerializer.SerializeToElement(new { depth = 0, includeProperties = true });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert - depth=0 means subject properties only, no child subjects expanded
        Assert.Equal(0, json.GetProperty("subjectCount").GetInt32());
    }

    [Fact]
    public async Task Browse_includeProperties_controls_property_values()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(room, config);
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        // Act - without properties
        var inputWithout = JsonSerializer.SerializeToElement(new { depth = 0, includeProperties = false });
        var resultWithout = await browseTool.Handler(inputWithout, CancellationToken.None);
        var jsonWithout = JsonSerializer.SerializeToElement(resultWithout);

        // Act - with properties
        var inputWith = JsonSerializer.SerializeToElement(new { depth = 0, includeProperties = true });
        var resultWith = await browseTool.Handler(inputWith, CancellationToken.None);
        var jsonWith = JsonSerializer.SerializeToElement(resultWith);

        // Assert - without properties, subjects tree should not contain property values
        var subjectsWithout = jsonWithout.GetProperty("result");
        var subjectsWith = jsonWith.GetProperty("result");

        // With properties enabled, Name and Temperature should appear
        Assert.True(subjectsWith.TryGetProperty("Name", out _));
        Assert.True(subjectsWith.TryGetProperty("Temperature", out _));

        // Without properties, Name and Temperature should not appear
        Assert.False(subjectsWithout.TryGetProperty("Name", out _));
        Assert.False(subjectsWithout.TryGetProperty("Temperature", out _));
    }

    [Fact]
    public async Task Browse_subject_enrichers_are_called()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var enricher = new TestSubjectEnricher();
        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            SubjectEnrichers = { enricher }
        };
        var factory = new McpToolFactory(room, config);
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        // Act
        var input = JsonSerializer.SerializeToElement(new { depth = 1, includeProperties = false });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert - enricher should have been called for the Device child subject
        Assert.True(enricher.EnrichedSubjects.Count > 0);

        // The Device node should contain the $test field from the enricher
        var subjects = json.GetProperty("result");
        var deviceNode = subjects.GetProperty("Device");
        Assert.Equal("enriched", deviceNode.GetProperty("$test").GetString());
    }

    [Fact]
    public async Task Browse_truncates_when_max_subjects_exceeded()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            MaxSubjectsPerResponse = 0  // Force truncation immediately
        };
        var factory = new McpToolFactory(room, config);
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        // Act
        var input = JsonSerializer.SerializeToElement(new { depth = 1, includeProperties = true });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Browse_property_values_include_type_and_isWritable()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false
        };
        var factory = new McpToolFactory(room, config);
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        // Act
        var input = JsonSerializer.SerializeToElement(new { depth = 0, includeProperties = true });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var subjects = json.GetProperty("result");
        var temperature = subjects.GetProperty("Temperature");
        Assert.Equal("number", temperature.GetProperty("type").GetString());
        Assert.True(temperature.GetProperty("isWritable").GetBoolean());
    }

    [Fact]
    public async Task Browse_property_values_omit_isWritable_when_read_only()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = true
        };
        var factory = new McpToolFactory(room, config);
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        // Act
        var input = JsonSerializer.SerializeToElement(new { depth = 0, includeProperties = true });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var subjects = json.GetProperty("result");
        var temperature = subjects.GetProperty("Temperature");
        Assert.Equal("number", temperature.GetProperty("type").GetString());
        Assert.False(temperature.TryGetProperty("isWritable", out _));
    }

    [Fact]
    public async Task Browse_maxSubjects_limits_response_count()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var childA = new TestContainer(context) { Name = "A" };
        var childB = new TestContainer(context) { Name = "B" };
        var childC = new TestContainer(context) { Name = "C" };
        var container = new TestContainer(context) { Name = "Root" };
        container.Children = new Dictionary<string, TestContainer>
        {
            ["A"] = childA,
            ["B"] = childB,
            ["C"] = childC
        };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(container, config);
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        // Act — request only 1 subject
        var input = JsonSerializer.SerializeToElement(new { depth = 1, maxSubjects = 1 });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Equal(1, json.GetProperty("subjectCount").GetInt32());
        Assert.True(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Browse_excludes_methods_and_interfaces_by_default()
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
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        // Act — no includeMethods/includeInterfaces flags
        var input = JsonSerializer.SerializeToElement(new { depth = 1 });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — $methods and $interfaces should be stripped
        var device = json.GetProperty("result").GetProperty("Device");
        Assert.False(device.TryGetProperty("$methods", out _));
        Assert.False(device.TryGetProperty("$interfaces", out _));
        // $title should still be present (not filtered)
        Assert.True(device.TryGetProperty("$title", out _));
    }

    [Fact]
    public async Task Browse_includes_methods_and_interfaces_when_requested()
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
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        // Act
        var input = JsonSerializer.SerializeToElement(new { depth = 1, includeMethods = true, includeInterfaces = true });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var device = json.GetProperty("result").GetProperty("Device");
        Assert.True(device.TryGetProperty("$methods", out _));
        Assert.True(device.TryGetProperty("$interfaces", out _));
    }

    private class TestSubjectEnricher : IMcpSubjectEnricher
    {
        public List<RegisteredSubject> EnrichedSubjects { get; } = [];

        public IDictionary<string, object?> GetSubjectEnrichments(RegisteredSubject subject)
        {
            EnrichedSubjects.Add(subject);
            return new Dictionary<string, object?> { ["$test"] = "enriched" };
        }
    }

    [Fact]
    public async Task Browse_excludeTypes_filters_out_matching_subjects()
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
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        // Act — exclude TestDevice
        var input = JsonSerializer.SerializeToElement(new
        {
            depth = 1,
            excludeTypes = new[] { "TestDevice" }
        });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — Device should not appear
        var subjects = json.GetProperty("result");
        Assert.False(subjects.TryGetProperty("Device", out _));
    }

    [Fact]
    public async Task Browse_depth_boundary_shows_itemType_for_homogeneous_collection()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var container = new TestContainer(context) { Name = "Root" };
        container.Children = new Dictionary<string, TestContainer>
        {
            ["A"] = new TestContainer(context) { Name = "A" },
            ["B"] = new TestContainer(context) { Name = "B" }
        };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(container, config);
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        // Act — depth 0, so Children hits the boundary
        var input = JsonSerializer.SerializeToElement(new { depth = 0 });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var children = json.GetProperty("result").GetProperty("Children");
        Assert.Equal(2, children.GetProperty("$count").GetInt32());
        Assert.Equal("TestContainer", children.GetProperty("$itemType").GetString());
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
