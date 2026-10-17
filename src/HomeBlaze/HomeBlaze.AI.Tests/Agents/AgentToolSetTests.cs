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

    [Fact]
    public void WhenOnlyRemovedToolNamesAreAllowed_ThenOnlyUpdateMemoryIsAvailable()
    {
        // Arrange
        var availableTools = new[] { Tool("browse"), Tool("invoke_query"), Tool("invoke_operation") };

        // Act
        var toolSet = AgentToolSet.Create(availableTools, ["invoke_method", "read_file"], MemoryTool, maxToolCalls: 5, progress: null);

        // Assert
        Assert.Equal(["update_memory"], toolSet.Tools.Select(tool => tool.Name));
    }

    [Theory]
    [InlineData(new[] { "browse" }, true)]
    [InlineData(new[] { "browse", "invoke_operation" }, false)]
    [InlineData(new[] { "browse", "invoke_query" }, true)]
    [InlineData(new[] { "set_property" }, false)]
    [InlineData(new[] { "browse", "edit_markdown" }, false)]
    [InlineData(new[] { "write_markdown" }, false)]
    [InlineData(new[] { "create_markdown" }, false)]
    public void WhenCheckingAllowedTools_ThenWriteToolsDisableReadOnly(string[] allowedTools, bool expected)
    {
        // Act
        var isReadOnly = AgentToolSet.IsReadOnly(allowedTools);

        // Assert
        Assert.Equal(expected, isReadOnly);
    }

    [Fact]
    public void WhenUsingTheDefaultAllowedTools_ThenInvokeQueryIsAllowedAndTheToolsAreReadOnly()
    {
        // Act
        var isReadOnly = AgentToolSet.IsReadOnly(AgentToolSet.DefaultAllowedTools);

        // Assert
        Assert.Contains("invoke_query", AgentToolSet.DefaultAllowedTools);
        Assert.DoesNotContain("invoke_operation", AgentToolSet.DefaultAllowedTools);
        Assert.True(isReadOnly);
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
    public async Task WhenTheBudgetIsExhausted_ThenUpdateMemoryStillRuns()
    {
        // Arrange
        var toolSet = AgentToolSet.Create([Tool("browse")], ["browse"], MemoryTool, maxToolCalls: 1, progress: null);
        var browse = toolSet.Tools.First(tool => tool.Name == "browse");
        var memory = toolSet.Tools.First(tool => tool.Name == "update_memory");
        var input = JsonSerializer.SerializeToElement(new { path = "/" });

        // Act
        await browse.Handler(input, CancellationToken.None);
        var memoryResult = JsonSerializer.SerializeToElement(await memory.Handler(input, CancellationToken.None));

        // Assert
        Assert.False(memoryResult.TryGetProperty("error", out _));
        Assert.Equal(2, toolSet.Calls.Count);
        Assert.False(toolSet.Calls[1].IsError);
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

    [Fact]
    public async Task WhenToolArgumentsAreLong_ThenTheRecordedArgumentsAreTruncatedLikeResults()
    {
        // Arrange
        var toolSet = AgentToolSet.Create([Tool("browse")], ["browse"], MemoryTool, maxToolCalls: 5, progress: null);
        var longArguments = JsonSerializer.SerializeToElement(new { path = new string('x', 3000) });

        // Act
        await toolSet.Tools[0].Handler(longArguments, CancellationToken.None);
        toolSet.RecordBackendCall("web_fetch", new string('y', 3000), "ok", isError: false);

        // Assert
        Assert.Equal(2003, toolSet.Calls[0].Arguments.Length);
        Assert.StartsWith("""{"path":"xxx""", toolSet.Calls[0].Arguments);
        Assert.EndsWith("...", toolSet.Calls[0].Arguments);
        Assert.Equal(2003, toolSet.Calls[1].Arguments.Length);
    }

    [Fact]
    public async Task WhenABackendToolCallIsRecorded_ThenItShowsAsACallAndCountsAgainstTheBudget()
    {
        // Arrange
        var updates = new List<AgentTurnUpdate>();
        var toolSet = AgentToolSet.Create([Tool("browse")], ["browse"], MemoryTool, maxToolCalls: 1, new SynchronousProgress<AgentTurnUpdate>(updates.Add));

        // Act
        toolSet.RecordBackendCall("web_search", """{"query":"weather"}""", new string('x', 3000), isError: false);
        var result = await toolSet.Tools[0].Handler(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        // Assert
        Assert.Equal(["web_search", "browse"], toolSet.Calls.Select(call => call.Name));
        Assert.Equal("""{"query":"weather"}""", toolSet.Calls[0].Arguments);
        Assert.Equal(2003, toolSet.Calls[0].ResultPreview.Length);
        Assert.True(toolSet.Calls[1].IsError);
        Assert.Contains("budget", JsonSerializer.Serialize(result));
        Assert.Equal(2, updates.Count);
    }
}
