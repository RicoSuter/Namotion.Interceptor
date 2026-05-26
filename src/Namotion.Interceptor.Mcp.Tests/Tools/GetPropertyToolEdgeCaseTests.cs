using System.Text.Json;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class GetPropertyToolEdgeCaseTests
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
            PathProvider = DefaultPathProvider.Instance
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "get_property");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "Device" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Contains("subject", json.GetProperty("error").GetString());
        Assert.Contains("browse", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenPropertyHasNoAttributes_ThenAttributesFieldIsOmitted()
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
        var input = JsonSerializer.SerializeToElement(new { path = "Temperature" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.False(json.TryGetProperty("attributes", out _));
    }
}
