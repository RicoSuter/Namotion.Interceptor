using System.Text.Json;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class SearchToolEdgeCaseTests
{
    [Fact]
    public async Task WhenNoMatchesFound_ThenReturnsEmptyResults()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new { format = "json", text = "zzz_no_match_zzz" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Equal(0, json.GetProperty("subjectCount").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task WhenSearchByTypeFullName_ThenReturnsMatchingSubjects()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new
        {
            format = "json",
            types = new[] { typeof(TestDevice).FullName }
        });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Equal(1, json.GetProperty("subjectCount").GetInt32());
    }

    [Fact]
    public async Task WhenTextSearchIsCaseInsensitive_ThenMatchesRegardlessOfCase()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "search");

        // Act
        var input = JsonSerializer.SerializeToElement(new { format = "json", text = "device" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.Equal(1, json.GetProperty("subjectCount").GetInt32());
    }
}
