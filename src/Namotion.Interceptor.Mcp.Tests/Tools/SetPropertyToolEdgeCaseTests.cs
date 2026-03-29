using System.Text.Json;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class SetPropertyToolEdgeCaseTests
{
    [Fact]
    public async Task WhenPathPointsToSubject_ThenReturnsError()
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
            IsReadOnly = false
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "set_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "Device", value = "something" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Contains("subject", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenStringValueForNonStringType_ThenDeserializesCorrectly()
    {
        // Arrange — MCP SDK may send values as strings (e.g., "true" instead of true)
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = false };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "set_property");

        // Act — send boolean as string "true"
        var input = JsonSerializer.SerializeToElement(new { path = "Device.IsOn", value = "true" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.True(room.Device.IsOn);
    }

    [Fact]
    public async Task WhenSettingProperty_ThenPreviousValueIsCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Original", Temperature = 21.5m };

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance,
            IsReadOnly = false
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "set_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "Name", value = "Updated" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("Original", json.GetProperty("previousValue").GetString());
        Assert.Equal("Updated", room.Name);
    }
}
