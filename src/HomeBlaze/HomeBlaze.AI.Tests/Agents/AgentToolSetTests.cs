using System.Text.Json;
using HomeBlaze.AI.Agents;
using Namotion.Interceptor.Mcp;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentToolSetTests
{
    private static McpToolInfo Tool(string name) => new()
    {
        Name = name,
        Description = name,
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
        Handler = (_, _) => Task.FromResult<object?>(new { tool = name })
    };

    private static readonly McpToolInfo MemoryTool = Tool("update_memory");

    [Fact]
    public void WhenCreatingWithAllowedTools_ThenOnlyThoseAndUpdateMemoryAreAvailable()
    {
        // Act
        var toolSet = AgentToolSet.Create([Tool("browse"), Tool("set_property")], ["browse"], MemoryTool, maxToolCalls: 5, progress: null);

        // Assert
        Assert.Equal(["browse", "update_memory"], toolSet.Tools.Select(tool => tool.Name));
    }

    [Theory]
    [InlineData(new[] { "browse" }, true)]
    [InlineData(new[] { "browse", "invoke_method" }, false)]
    [InlineData(new[] { "set_property" }, false)]
    public void WhenCheckingAllowedTools_ThenWriteToolsDisableReadOnly(string[] allowedTools, bool expected)
    {
        // Act
        var isReadOnly = AgentToolSet.IsReadOnly(allowedTools);

        // Assert
        Assert.Equal(expected, isReadOnly);
    }

    [Fact]
    public async Task WhenTheBudgetIsExhausted_ThenFurtherCallsReturnAnErrorWithoutRunningTheTool()
    {
        // Arrange
        var toolSet = AgentToolSet.Create([Tool("browse")], ["browse"], MemoryTool, maxToolCalls: 1, progress: null);
        var browse = toolSet.Tools.First(tool => tool.Name == "browse");
        var input = JsonSerializer.SerializeToElement(new { path = "/" });

        // Act
        await browse.Handler(input, CancellationToken.None);
        var second = JsonSerializer.SerializeToElement(await browse.Handler(input, CancellationToken.None));

        // Assert
        Assert.Contains("budget", second.GetProperty("error").GetString());
        Assert.Equal(2, toolSet.Calls.Count);
        Assert.False(toolSet.Calls[0].IsError);
        Assert.True(toolSet.Calls[1].IsError);
    }

    [Fact]
    public async Task WhenAToolIsCalled_ThenTheCallIsRecordedAndReported()
    {
        // Arrange
        var reported = new List<AgentTurnUpdate>();
        var toolSet = AgentToolSet.Create([Tool("browse")], ["browse"], MemoryTool, maxToolCalls: 5,
            new SynchronousProgress<AgentTurnUpdate>(reported.Add));

        // Act
        await toolSet.Tools[0].Handler(JsonSerializer.SerializeToElement(new { path = "/Kitchen" }), CancellationToken.None);

        // Assert
        var call = Assert.Single(toolSet.Calls);
        Assert.Equal("browse", call.Name);
        Assert.Contains("/Kitchen", call.Arguments);
        Assert.Contains("browse", call.ResultPreview);
        Assert.IsType<AgentToolCallCompleted>(Assert.Single(reported));
    }
}
