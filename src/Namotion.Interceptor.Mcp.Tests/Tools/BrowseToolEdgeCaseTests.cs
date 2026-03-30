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
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "browse");

        // Act
        var input = JsonSerializer.SerializeToElement(new { format = "json", path = "NonExistent.Path" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenDepthZeroWithChildren_ThenShowsCollapsedInsteadOfExpanding()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "browse");

        // Act
        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 0, includeProperties = true });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var properties = json.GetProperty("result").GetProperty("properties");
        var deviceProp = properties.GetProperty("Device");
        Assert.Equal("object", deviceProp.GetProperty("kind").GetString());
        Assert.True(deviceProp.GetProperty("isCollapsed").GetBoolean());
    }

    [Fact]
    public async Task WhenEnricherThrows_ThenExceptionPropagates()
    {
        // Arrange
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

        // Act & Assert
        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.Handler(input, CancellationToken.None));
    }

    [Fact]
    public async Task WhenEmptyDictionary_ThenChildrenAreNotIncluded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var container = new TestContainer(context) { Name = "Root" };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(container, config);
        var tool = factory.CreateTools().First(t => t.Name == "browse");

        // Act
        var input = JsonSerializer.SerializeToElement(new { format = "json", depth = 1, includeProperties = true });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var resultNode = json.GetProperty("result");
        if (resultNode.TryGetProperty("properties", out var props))
        {
            Assert.False(props.TryGetProperty("Children", out _));
        }
    }

    [Fact]
    public async Task WhenBrowsingSubpath_ThenReturnsSubjectAtPath()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "browse");

        // Act
        var input = JsonSerializer.SerializeToElement(new { format = "json", path = "Device", depth = 0, includeProperties = true });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        var properties = json.GetProperty("result").GetProperty("properties");
        var deviceName = properties.GetProperty("DeviceName");
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
