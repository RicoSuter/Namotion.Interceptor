using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Namotion.Interceptor.Mcp.Tests;

public class McpToolDispatcherTests
{
    private static McpToolInfo CreateTool(string name, Func<JsonElement, object?> handler) => new()
    {
        Name = name,
        Description = name + " description",
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
        Handler = (input, _) => Task.FromResult(handler(input))
    };

    [Fact]
    public void WhenListingTools_ThenEveryToolIsDescribed()
    {
        // Arrange
        var tools = new[] { CreateTool("a", _ => null), CreateTool("b", _ => null) };

        // Act
        var result = McpToolDispatcher.ListTools(tools);

        // Assert
        Assert.Equal(["a", "b"], result.Tools.Select(tool => tool.Name));
        Assert.Equal("a description", result.Tools[0].Description);
    }

    [Fact]
    public async Task WhenCallingAToolReturningAnObject_ThenResultIsIndentedJsonText()
    {
        // Arrange
        var tools = new[] { CreateTool("get", input => new { value = input.GetProperty("x").GetInt32() + 1 }) };
        var parameters = new CallToolRequestParams
        {
            Name = "get",
            Arguments = new Dictionary<string, JsonElement> { ["x"] = JsonSerializer.SerializeToElement(41) }
        };

        // Act
        var result = await McpToolDispatcher.CallToolAsync(tools, parameters, logger: null, CancellationToken.None);

        // Assert
        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Equal(42, JsonDocument.Parse(text).RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task WhenCallingAnUnknownTool_ThenResultIsAnError()
    {
        // Arrange
        var parameters = new CallToolRequestParams { Name = "missing" };

        // Act
        var result = await McpToolDispatcher.CallToolAsync([], parameters, logger: null, CancellationToken.None);

        // Assert
        Assert.True(result.IsError);
        Assert.Contains("Unknown tool: missing", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task WhenAToolThrows_ThenResultIsAnErrorWithTheMessage()
    {
        // Arrange
        var tools = new[] { CreateTool("fail", _ => throw new InvalidOperationException("boom")) };

        // Act
        var result = await McpToolDispatcher.CallToolAsync(tools, new CallToolRequestParams { Name = "fail" }, logger: null, CancellationToken.None);

        // Assert
        Assert.True(result.IsError);
        Assert.Equal("boom", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }
}
