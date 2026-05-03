using System.Text.Json;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

/// <summary>
/// Tests that the factory-per-request pattern works correctly,
/// simulating how WithSubjectRegistryTools creates a fresh McpToolFactory
/// for each MCP request to avoid scoped IServiceProvider disposal issues.
/// </summary>
public class ScopedServiceProviderTests
{
    [Fact]
    public async Task WhenFactoryCreatedPerRequest_ThenToolsWorkAcrossMultipleInvocations()
    {
        // Arrange — simulate the pattern used by WithSubjectRegistryTools:
        // each request gets a fresh factory built from a Func<IServiceProvider, ...>
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };

        Func<IInterceptorSubject> rootProvider = () => room;
        McpServerConfiguration ConfigFactory() => new() { PathProvider = DefaultPathProvider.Instance };

        // Act — simulate two separate requests, each creating a fresh factory
        for (var i = 0; i < 3; i++)
        {
            var factory = new McpToolFactory(rootProvider, ConfigFactory());
            var tool = factory.CreateTools().First(t => t.Name == "get_property");

            var input = JsonSerializer.SerializeToElement(new { path = "Temperature" });
            var result = await tool.Handler(input, CancellationToken.None);
            var json = JsonSerializer.SerializeToElement(result);

            // Assert — each invocation should work independently
            Assert.Equal(21.5m, json.GetProperty("value").GetDecimal());
        }
    }

    [Fact]
    public async Task WhenRootSubjectChanges_ThenNewFactoryReflectsChange()
    {
        // Arrange — simulate root subject being updated between requests
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room1 = new TestRoom(context) { Name = "Room1", Temperature = 20.0m };
        var room2 = new TestRoom(context) { Name = "Room2", Temperature = 25.0m };

        var currentRoom = room1;

        var config = new McpServerConfiguration
        {
            PathProvider = DefaultPathProvider.Instance
        };

        // Act — first request uses room1
        var factory1 = new McpToolFactory(() => currentRoom, config);
        var tool1 = factory1.CreateTools().First(t => t.Name == "get_property");
        var input = JsonSerializer.SerializeToElement(new { path = "Temperature" });
        var result1 = await tool1.Handler(input, CancellationToken.None);
        var json1 = JsonSerializer.SerializeToElement(result1);

        // Switch to room2
        currentRoom = room2;

        // Second request creates fresh factory — should see room2
        var factory2 = new McpToolFactory(() => currentRoom, config);
        var tool2 = factory2.CreateTools().First(t => t.Name == "get_property");
        var result2 = await tool2.Handler(input, CancellationToken.None);
        var json2 = JsonSerializer.SerializeToElement(result2);

        // Assert
        Assert.Equal(20.0m, json1.GetProperty("value").GetDecimal());
        Assert.Equal(25.0m, json2.GetProperty("value").GetDecimal());
    }
}
