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
    public async Task Search_by_type_returns_matching_subjects()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        var input = JsonSerializer.SerializeToElement(new { format = "json", types = new[] { "TestDevice" } });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.Equal(1, json.GetProperty("subjectCount").GetInt32());
    }

    [Fact]
    public async Task Search_by_text_matches_enricher_title()
    {
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

        var input = JsonSerializer.SerializeToElement(new { format = "json", text = "light" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.True(json.GetProperty("subjectCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Search_by_text_matches_path()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        var input = JsonSerializer.SerializeToElement(new { format = "json", text = "Device" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.True(json.GetProperty("subjectCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Search_with_includeProperties_returns_property_values()
    {
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

        var input = JsonSerializer.SerializeToElement(new
        {
            format = "json",
            types = new[] { "TestDevice" },
            includeProperties = true
        });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Find the device subject in results
        var firstSubject = json.GetProperty("results").EnumerateObject().First().Value;
        // Properties are keyed by name
        var properties = firstSubject.GetProperty("properties");
        var deviceNameProp = properties.GetProperty("DeviceName");
        Assert.Equal("Light", deviceNameProp.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Search_respects_max_subjects_limit()
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
        var tool = factory.CreateTools().First(t => t.Name == "search");

        var input = JsonSerializer.SerializeToElement(new { format = "json", types = new[] { "TestDevice" } });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.True(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Search_maxSubjects_limits_response_count()
    {
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

        var input = JsonSerializer.SerializeToElement(new { format = "json", types = new[] { "TestContainer" }, maxSubjects = 1 });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.Equal(1, json.GetProperty("subjectCount").GetInt32());
        Assert.True(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Search_excludes_methods_and_interfaces_by_default()
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
        var tool = factory.CreateTools().First(t => t.Name == "search");

        var input = JsonSerializer.SerializeToElement(new { format = "json", types = new[] { "TestDevice" } });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        var firstSubject = json.GetProperty("results").EnumerateObject().First().Value;
        // methods and interfaces should NOT be present (null = not serialized)
        Assert.False(firstSubject.TryGetProperty("methods", out _));
        Assert.False(firstSubject.TryGetProperty("interfaces", out _));
        // $title should still be present
        Assert.True(firstSubject.TryGetProperty("$title", out _));
    }

    [Fact]
    public async Task Search_includes_methods_and_interfaces_when_requested()
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
        var tool = factory.CreateTools().First(t => t.Name == "search");

        var input = JsonSerializer.SerializeToElement(new
        {
            format = "json",
            types = new[] { "TestDevice" },
            includeMethods = true,
            includeInterfaces = true
        });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        var firstSubject = json.GetProperty("results").EnumerateObject().First().Value;
        Assert.True(firstSubject.TryGetProperty("methods", out _));
        Assert.True(firstSubject.TryGetProperty("interfaces", out _));
    }

    [Fact]
    public async Task Search_excludeTypes_filters_out_matching_subjects()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // With exclude
        var inputWithExclude = JsonSerializer.SerializeToElement(new
        {
            format = "json",
            types = new[] { "TestDevice" },
            excludeTypes = new[] { "TestDevice" }
        });
        var resultWithExclude = await tool.Handler(inputWithExclude, CancellationToken.None);
        var jsonWithExclude = JsonSerializer.SerializeToElement(resultWithExclude);
        Assert.Equal(0, jsonWithExclude.GetProperty("subjectCount").GetInt32());

        // Without exclude
        var inputWithoutExclude = JsonSerializer.SerializeToElement(new
        {
            format = "json",
            types = new[] { "TestDevice" }
        });
        var resultWithoutExclude = await tool.Handler(inputWithoutExclude, CancellationToken.None);
        var jsonWithoutExclude = JsonSerializer.SerializeToElement(resultWithoutExclude);
        Assert.Equal(1, jsonWithoutExclude.GetProperty("subjectCount").GetInt32());
    }

    [Fact]
    public async Task Search_path_scopes_to_subtree()
    {
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

        var input = JsonSerializer.SerializeToElement(new
        {
            format = "json",
            path = "Children[GroupA]",
            types = new[] { "TestContainer" }
        });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        var subjects = json.GetProperty("results");
        var paths = subjects.EnumerateObject().Select(p => p.Name).ToList();
        Assert.All(paths, p => Assert.StartsWith("Children[GroupA]", p));
    }

    [Fact]
    public async Task Search_default_format_returns_text()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        var input = JsonSerializer.SerializeToElement(new { types = new[] { "TestDevice" } });
        var result = await tool.Handler(input, CancellationToken.None);

        Assert.IsType<string>(result);
        var text = (string)result!;
        Assert.StartsWith("# path [Type]", text);
        Assert.Contains("[1 subject]", text);
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
