using System.Text.Json;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class SetPropertyToolTests
{
    [Fact]
    public async Task SetProperty_updates_value_and_returns_success()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "set_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "Temperature", value = 25.0 });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal(21.5m, json.GetProperty("previousValue").GetDecimal());
        Assert.Equal(25.0m, room.Temperature);
    }

    [Fact]
    public async Task SetProperty_blocked_when_read_only()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = true
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "set_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "Temperature", value = 25.0 });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
        Assert.Equal(21.5m, room.Temperature);
    }

    [Fact]
    public async Task SetProperty_returns_error_for_invalid_path()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "set_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "NonExistent", value = 25.0 });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }
}
