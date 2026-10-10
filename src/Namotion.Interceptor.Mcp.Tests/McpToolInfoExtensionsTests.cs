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
}
