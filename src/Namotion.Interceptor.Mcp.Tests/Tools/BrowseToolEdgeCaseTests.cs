using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class BrowseToolEdgeCaseTests
{
    [Fact]
    public async Task WhenInvalidStartPath_ThenReturnsError()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "browse");

        var input = JsonSerializer.SerializeToElement(new { format = "json", path = "NonExistent.Path" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenDepthZeroWithChildren_ThenShowsCollapsedInsteadOfExpanding()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "browse");

        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 0, includeProperties = true });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Device at depth 0 should be collapsed
        var properties = json.GetProperty("result").GetProperty("properties");
        var deviceProp = properties.EnumerateArray()
            .FirstOrDefault(p => p.TryGetProperty("name", out var n) && n.GetString() == "Device");
        Assert.NotEqual(default, deviceProp);
        Assert.Equal("object", deviceProp.GetProperty("kind").GetString());
        Assert.True(deviceProp.GetProperty("isCollapsed").GetBoolean());
    }

    [Fact]
    public async Task WhenEnricherThrows_ThenExceptionPropagates()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            SubjectEnrichers = { new ThrowingEnricher() }
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "browse");

        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.Handler(input, CancellationToken.None));
    }

    [Fact]
    public async Task WhenEmptyDictionary_ThenChildrenAreNotIncluded()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var container = new TestContainer(context) { Name = "Root" };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(container, config);
        var tool = factory.CreateTools().First(t => t.Name == "browse");

        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 1, includeProperties = true });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Empty dictionary should not appear as a property
        var resultNode = json.GetProperty("result");
        if (resultNode.TryGetProperty("properties", out var props))
        {
            var hasChildren = props.EnumerateArray()
                .Any(p => p.TryGetProperty("name", out var n) && n.GetString() == "Children");
            Assert.False(hasChildren);
        }
    }

    [Fact]
    public async Task WhenBrowsingSubpath_ThenReturnsSubjectAtPath()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "browse");

        var input = JsonSerializer.SerializeToElement(new { format = "json", path = "Device", depth = 0, includeProperties = true });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Device's scalar properties should include DeviceName
        var properties = json.GetProperty("result").GetProperty("properties");
        var deviceName = properties.EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == "DeviceName");
        Assert.Equal("Light", deviceName.GetProperty("value").GetString());
    }

    private class ThrowingEnricher : IMcpSubjectEnricher
    {
        public IDictionary<string, object?> GetSubjectEnrichments(RegisteredSubject subject)
        {
            throw new InvalidOperationException("Enricher failed");
        }
    }
}
