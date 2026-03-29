using System.Text.Json;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class GetPropertyToolTests
{
    [Fact]
    public async Task GetProperty_returns_value_and_type()
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
        var tool = factory.CreateTools().First(t => t.Name == "get_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "Temperature" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Equal(21.5m, json.GetProperty("value").GetDecimal());
        Assert.Equal("number", json.GetProperty("type").GetString());
        Assert.True(json.GetProperty("isWritable").GetBoolean());
    }

    [Fact]
    public async Task GetProperty_returns_error_for_invalid_path()
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
        var tool = factory.CreateTools().First(t => t.Name == "get_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "NonExistent" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task GetProperty_omits_isWritable_when_read_only()
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
        var tool = factory.CreateTools().First(t => t.Name == "get_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "Temperature" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.False(json.TryGetProperty("isWritable", out _));
    }

    [Fact]
    public async Task GetProperty_includes_registry_attributes()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };

        // Add an attribute to the Temperature property
        var registered = room.TryGetRegisteredSubject()!;
        var temperatureProperty = registered.TryGetProperty("Temperature")!;
        temperatureProperty.AddAttribute<string>("Unit", _ => "Celsius");

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "get_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "Temperature" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("attributes", out var attributes));
        Assert.Equal("Celsius", attributes.GetProperty("Unit").GetString());
    }
}
