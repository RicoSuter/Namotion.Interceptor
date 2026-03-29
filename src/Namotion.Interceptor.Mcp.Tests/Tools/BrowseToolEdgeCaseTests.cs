using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Xunit;

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

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "browse");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "NonExistent.Path" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenDepthZeroWithChildren_ThenShowsCountInsteadOfExpanding()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "browse");

        // Act
        var input = JsonSerializer.SerializeToElement(new { depth = 0, includeProperties = true });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — Device at depth 0 should show $count
        var subjects = json.GetProperty("result");
        Assert.True(subjects.TryGetProperty("Device", out var deviceNode));
        Assert.True(deviceNode.TryGetProperty("$count", out var countElement));
        Assert.Equal(1, countElement.GetInt32());
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

        // Act & Assert — enricher exception should propagate
        var input = JsonSerializer.SerializeToElement(new { depth = 1 });
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
        // Children dictionary is empty

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(container, config);
        var tool = factory.CreateTools().First(t => t.Name == "browse");

        // Act
        var input = JsonSerializer.SerializeToElement(new { depth = 1, includeProperties = true });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — empty dictionary should not appear in output
        var subjects = json.GetProperty("result");
        Assert.False(subjects.TryGetProperty("Children", out _));
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

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "browse");

        // Act — browse starting from Device path
        var input = JsonSerializer.SerializeToElement(new { path = "Device", depth = 0, includeProperties = true });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — should show Device's properties
        var subjects = json.GetProperty("result");
        Assert.True(subjects.TryGetProperty("DeviceName", out var deviceName));
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
