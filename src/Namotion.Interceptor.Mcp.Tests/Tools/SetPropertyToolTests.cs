using System.Text.Json;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class SetPropertyToolTests
{
    [Fact]
    public async Task WhenSettingProperty_ThenUpdatesValueAndReturnsSuccess()
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
    public async Task WhenTheWriteFilterRefusesAProperty_ThenItsErrorIsReturnedAndTheValueIsUnchanged()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false,
            PropertyWriteFilter = property => property.Name == "Temperature" ? "It is protected." : null
        };
        var tool = new McpToolFactory(room, config).CreateTools().First(t => t.Name == "set_property");

        // Act
        var refused = JsonSerializer.SerializeToElement(await tool.Handler(
            JsonSerializer.SerializeToElement(new { path = "Temperature", value = 25.0 }), CancellationToken.None));
        var allowed = JsonSerializer.SerializeToElement(await tool.Handler(
            JsonSerializer.SerializeToElement(new { path = "Name", value = "Changed" }), CancellationToken.None));

        // Assert
        Assert.Equal("Property is not writable: Temperature. It is protected.", refused.GetProperty("error").GetString());
        Assert.Equal(21.5m, room.Temperature);
        Assert.True(allowed.GetProperty("success").GetBoolean());
        Assert.Equal("Changed", room.Name);
    }

    [Fact]
    public async Task WhenReadOnly_ThenSetIsBlocked()
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
    public async Task WhenInvalidPath_ThenReturnsError()
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
