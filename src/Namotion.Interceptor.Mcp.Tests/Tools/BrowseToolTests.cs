using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class BrowseToolTests
{
    [Fact]
    public async Task Browse_returns_subject_tree_with_children()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 1, includeProperties = true });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.True(json.TryGetProperty("result", out _));
        Assert.True(json.GetProperty("subjectCount").GetInt32() > 0);
    }

    [Fact]
    public async Task Browse_depth_zero_returns_no_children()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 0, includeProperties = true });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.Equal(0, json.GetProperty("subjectCount").GetInt32());
    }

    [Fact]
    public async Task Browse_includeProperties_controls_property_values()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        // Without properties
        var inputWithout = JsonSerializer.SerializeToElement(new { format = "json", depth = 0, includeProperties = false });
        var resultWithout = await browseTool.Handler(inputWithout, CancellationToken.None);
        var jsonWithout = JsonSerializer.SerializeToElement(resultWithout);

        // With properties
        var inputWith = JsonSerializer.SerializeToElement(new { format = "json", depth = 0, includeProperties = true });
        var resultWith = await browseTool.Handler(inputWith, CancellationToken.None);
        var jsonWith = JsonSerializer.SerializeToElement(resultWith);

        // Without properties, result should not have scalar value properties
        var resultWithoutProps = jsonWithout.GetProperty("result");
        var hasScalarsWithout = resultWithoutProps.TryGetProperty("properties", out var propsWithout) &&
            propsWithout.EnumerateObject().Any(p => p.Value.GetProperty("kind").GetString() == "value");
        Assert.False(hasScalarsWithout);

        // With properties, should have Name and Temperature as scalar properties
        var resultWithProps = jsonWith.GetProperty("result");
        var properties = resultWithProps.GetProperty("properties");
        Assert.True(properties.TryGetProperty("Name", out _));
        Assert.True(properties.TryGetProperty("Temperature", out _));
    }

    [Fact]
    public async Task Browse_subject_enrichers_are_called()
    {
        var context = InterceptorSubjectContext.Create()
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

        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 1, includeProperties = false });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Enricher should have been called
        Assert.True(enricher.EnrichedSubjects.Count > 0);

        // Find the Device child in properties
        var properties = json.GetProperty("result").GetProperty("properties");
        var deviceNode = properties.GetProperty("Device").GetProperty("child");
        Assert.Equal("enriched", deviceNode.GetProperty("$test").GetString());
    }

    [Fact]
    public async Task Browse_truncates_when_max_subjects_exceeded()
    {
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
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 1, includeProperties = true });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.True(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Browse_property_values_include_type_and_isWritable()
    {
        var context = InterceptorSubjectContext.Create()
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

        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 0, includeProperties = true });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        var properties = json.GetProperty("result").GetProperty("properties");
        var temperature = properties.GetProperty("Temperature");
        Assert.Equal("number", temperature.GetProperty("type").GetString());
        Assert.True(temperature.GetProperty("isWritable").GetBoolean());
    }

    [Fact]
    public async Task Browse_property_values_omit_isWritable_when_read_only()
    {
        var context = InterceptorSubjectContext.Create()
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

        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 0, includeProperties = true });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        var properties = json.GetProperty("result").GetProperty("properties");
        var temperature = properties.GetProperty("Temperature");
        Assert.Equal("number", temperature.GetProperty("type").GetString());
        Assert.False(temperature.TryGetProperty("isWritable", out _));
    }

    [Fact]
    public async Task Browse_maxSubjects_limits_response_count()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var childA = new TestContainer(context) { Name = "A" };
        var childB = new TestContainer(context) { Name = "B" };
        var childC = new TestContainer(context) { Name = "C" };
        var container = new TestContainer(context) { Name = "Root" };
        container.Children = new Dictionary<string, TestContainer>
        {
            ["A"] = childA, ["B"] = childB, ["C"] = childC
        };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(container, config);
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 1, maxSubjects = 1 });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.Equal(1, json.GetProperty("subjectCount").GetInt32());
        Assert.True(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Browse_excludes_methods_and_interfaces_by_default()
    {
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

        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 1 });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Find Device child node in properties
        var properties = json.GetProperty("result").GetProperty("properties");
        var device = properties.GetProperty("Device").GetProperty("child");

        // Methods and interfaces should NOT be present
        Assert.False(device.TryGetProperty("methods", out _));
        Assert.False(device.TryGetProperty("interfaces", out _));
        // $title should be present
        Assert.True(device.TryGetProperty("$title", out _));
    }

    [Fact]
    public async Task Browse_includes_methods_and_interfaces_when_requested()
    {
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

        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 1, includeMethods = true, includeInterfaces = true });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Find Device child node
        var properties = json.GetProperty("result").GetProperty("properties");
        var device = properties.GetProperty("Device").GetProperty("child");

        Assert.True(device.TryGetProperty("methods", out _));
        Assert.True(device.TryGetProperty("interfaces", out _));
    }

    [Fact]
    public async Task Browse_excludeTypes_filters_out_matching_subjects()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        var input = JsonSerializer.SerializeToElement(new
        {
            format = "json",
            depth = 1,
            excludeTypes = new[] { "TestDevice" }
        });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Device should NOT appear in properties (or should have null child)
        var resultNode = json.GetProperty("result");
        if (resultNode.TryGetProperty("properties", out var properties))
        {
            if (properties.TryGetProperty("Device", out var deviceProp))
            {
                Assert.True(deviceProp.TryGetProperty("child", out var c) && c.ValueKind == JsonValueKind.Null);
            }
        }
    }

    [Fact]
    public async Task Browse_depth_boundary_shows_itemType_for_homogeneous_collection()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var container = new TestContainer(context) { Name = "Root" };
        container.Children = new Dictionary<string, TestContainer>
        {
            ["A"] = new(context) { Name = "A" },
            ["B"] = new(context) { Name = "B" }
        };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(container, config);
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 0 });
        var result = await browseTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Children should be a collapsed dictionary property
        var properties = json.GetProperty("result").GetProperty("properties");
        var children = properties.GetProperty("Children");
        Assert.Equal("dictionary", children.GetProperty("kind").GetString());
        Assert.True(children.GetProperty("isCollapsed").GetBoolean());
        Assert.Equal(2, children.GetProperty("count").GetInt32());
        Assert.Equal("TestContainer", children.GetProperty("itemType").GetString());
    }

    [Fact]
    public async Task Browse_default_format_returns_text()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var browseTool = factory.CreateTools().First(t => t.Name == "browse");

        var input = JsonSerializer.SerializeToElement(new { depth = 0 });
        var result = await browseTool.Handler(input, CancellationToken.None);

        Assert.IsType<string>(result);
        var text = (string)result!;
        Assert.StartsWith("# path [Type]", text);
        Assert.Contains("[0 subjects]", text);
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
