using System.Text.Json;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.ClaudeCli;
using Xunit;
using static HomeBlaze.AI.Tests.Agents.ClaudeCli.FakeClaudeCli;

namespace HomeBlaze.AI.Tests.Agents.ClaudeCli;

public class ClaudeCliArgumentsTests
{
    [Fact]
    public void WhenBuildingAChatResume_ThenIsolationFlagsSessionAndModelArePresent()
    {
        // Arrange
        var options = new ClaudeCliOptions("/tmp/prompt.md", "/tmp/mcp.json", "opus", AgentEffort.ExtraHigh, "sonnet", "session-1", PersistSession: true)
        {
            MaxTurns = 35
        };

        // Act
        var arguments = ClaudeCliArguments.Build(options);

        // Assert
        Assert.Equal("-p", arguments[0]);
        Assert.Equal("stream-json", ValueAfter(arguments, "--output-format"));
        Assert.Contains("--verbose", arguments);
        Assert.Contains("--include-partial-messages", arguments);
        Assert.Equal("/tmp/prompt.md", ValueAfter(arguments, "--system-prompt-file"));
        Assert.DoesNotContain("--system-prompt", arguments);
        Assert.Equal("off", ValueAfter(arguments, "--system-prompt-snapshot"));
        Assert.Equal(string.Empty, ValueAfter(arguments, "--tools"));
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.Equal("/tmp/mcp.json", ValueAfter(arguments, "--mcp-config"));
        Assert.Equal("mcp__homeblaze", ValueAfter(arguments, "--allowedTools"));
        Assert.Equal(string.Empty, ValueAfter(arguments, "--setting-sources"));
        Assert.Equal("opus", ValueAfter(arguments, "--model"));
        Assert.Equal("xhigh", ValueAfter(arguments, "--effort"));
        Assert.Equal("sonnet", ValueAfter(arguments, "--fallback-model"));
        Assert.Contains("--resume=session-1", arguments);
        Assert.DoesNotContain("--resume", arguments);
        Assert.Equal("35", ValueAfter(arguments, "--max-turns"));
        Assert.DoesNotContain("--no-session-persistence", arguments);
        Assert.DoesNotContain("--bare", arguments);
    }

    [Theory]
    [InlineData(AgentEffort.Low, "low")]
    [InlineData(AgentEffort.Medium, "medium")]
    [InlineData(AgentEffort.High, "high")]
    [InlineData(AgentEffort.ExtraHigh, "xhigh")]
    [InlineData(AgentEffort.Max, "max")]
    public void WhenBuildingWithAnEffort_ThenTheEffortArgumentMatchesTheApiName(AgentEffort effort, string expected)
    {
        // Arrange
        var options = new ClaudeCliOptions("/tmp/prompt.md", "/tmp/mcp.json", null, effort, null, null, PersistSession: true);

        // Act
        var arguments = ClaudeCliArguments.Build(options);

        // Assert
        Assert.Equal(expected, ValueAfter(arguments, "--effort"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenTheResumeSessionIdIsBlank_ThenResumeIsOmitted(string sessionId)
    {
        // Arrange
        var options = new ClaudeCliOptions("/tmp/prompt.md", "/tmp/mcp.json", null, null, null, sessionId, PersistSession: true);

        // Act
        var arguments = ClaudeCliArguments.Build(options);

        // Assert
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--resume", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenTheResumeSessionIdStartsWithADash_ThenItStaysTheValueOfTheResumeArgument()
    {
        // Arrange
        var options = new ClaudeCliOptions("/tmp/prompt.md", "/tmp/mcp.json", null, null, null, "--dangerously-skip-permissions", PersistSession: true);

        // Act
        var arguments = ClaudeCliArguments.Build(options);

        // Assert
        Assert.Contains("--resume=--dangerously-skip-permissions", arguments);
        Assert.DoesNotContain("--dangerously-skip-permissions", arguments);
    }

    [Fact]
    public void WhenBuildingTheMcpConfiguration_ThenTheServerUsesHttpWithTheBearerToken()
    {
        // Act
        var json = ClaudeCliArguments.BuildMcpConfiguration(new Uri("http://127.0.0.1:5192/mcp/agents"), "TOKEN");

        // Assert
        var server = JsonDocument.Parse(json).RootElement.GetProperty("mcpServers").GetProperty("homeblaze");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:5192/mcp/agents", server.GetProperty("url").GetString());
        Assert.Equal("Bearer TOKEN", server.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    [Theory]
    [InlineData(new string[0], "", "mcp__homeblaze")]
    [InlineData(new[] { "web_fetch", "web_search", "Bash", "unknown" }, "WebSearch,WebFetch", "mcp__homeblaze,WebSearch,WebFetch")]
    [InlineData(new[] { "web_search" }, "WebSearch", "mcp__homeblaze,WebSearch")]
    public void WhenBackendToolsAreAllowed_ThenOnlyTheWebToolsAmongThemAreBuiltInToolsAndRunWithoutAPrompt(
        string[] backendTools, string expectedCliTools, string expectedAllowedTools)
    {
        // Arrange
        var options = new ClaudeCliOptions("/tmp/prompt.md", "/tmp/mcp.json", null, null, null, null, PersistSession: false)
        {
            BackendTools = backendTools
        };

        // Act
        var arguments = ClaudeCliArguments.Build(options);

        // Assert
        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--system-prompt-file", "/tmp/prompt.md",
             "--system-prompt-snapshot", "off", "--tools", expectedCliTools, "--strict-mcp-config", "--mcp-config", "/tmp/mcp.json",
             "--allowedTools", expectedAllowedTools, "--setting-sources", "", "--no-session-persistence"],
            arguments);
    }

    [Theory]
    [InlineData("WebSearch", "web_search")]
    [InlineData("WebFetch", "web_fetch")]
    [InlineData("Bash", null)]
    [InlineData("mcp__homeblaze__browse", null)]
    public void WhenMappingACliToolName_ThenOnlyWebToolsHaveABackendToolName(string cliToolName, string? expected)
    {
        // Act
        var name = ClaudeCliArguments.GetBackendToolName(cliToolName);

        // Assert
        Assert.Equal(expected, name);
    }
}
