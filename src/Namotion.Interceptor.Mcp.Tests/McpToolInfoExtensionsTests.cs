using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Namotion.Interceptor.Mcp.Tests;

public class McpToolInfoExtensionsTests
{
    [Fact]
    public async Task WhenAIFunctionIsInvoked_ThenHandlerReceivesArgumentsAsJson()
    {
        // Arrange
        JsonElement? received = null;
        var tool = new McpToolInfo
        {
            Name = "echo",
            Description = "Echoes the input",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            Handler = (input, _) =>
            {
                received = input;
                return Task.FromResult<object?>("done");
            }
        };
        var function = tool.ToAIFunction();

        // Act
        var result = await function.InvokeAsync(new AIFunctionArguments { ["path"] = "/Demo", ["depth"] = 2 });

        // Assert
        Assert.Equal("echo", function.Name);
        Assert.Equal("Echoes the input", function.Description);
        Assert.Equal("done", result);
        Assert.Equal("/Demo", received!.Value.GetProperty("path").GetString());
        Assert.Equal(2, received.Value.GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task WhenAIFunctionIsInvokedWithJsonElementArguments_ThenTheyReachTheHandlerUnchanged()
    {
        // Arrange
        JsonElement? received = null;
        var tool = new McpToolInfo
        {
            Name = "echo",
            Description = "Echoes the input",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            Handler = (input, _) =>
            {
                received = input;
                return Task.FromResult<object?>(null);
            }
        };
        var function = tool.ToAIFunction();
        using var document = JsonDocument.Parse("""{ "flag": true, "nested": { "a": [1, "x"] } }""");

        // Act
        await function.InvokeAsync(new AIFunctionArguments
        {
            ["flag"] = document.RootElement.GetProperty("flag"),
            ["nested"] = document.RootElement.GetProperty("nested"),
            ["missing"] = null
        });

        // Assert
        Assert.Equal(JsonValueKind.True, received!.Value.GetProperty("flag").ValueKind);
        Assert.True(JsonElement.DeepEquals(document.RootElement.GetProperty("nested"), received.Value.GetProperty("nested")));
        Assert.Equal(JsonValueKind.Null, received.Value.GetProperty("missing").ValueKind);
    }
}
