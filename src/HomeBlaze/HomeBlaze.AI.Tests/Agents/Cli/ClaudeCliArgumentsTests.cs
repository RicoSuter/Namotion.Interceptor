using System.Text.Json;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Cli;
using Xunit;
using static HomeBlaze.AI.Tests.Agents.Cli.FakeClaudeCli;

namespace HomeBlaze.AI.Tests.Agents.Cli;

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
        Assert.Equal("session-1", ValueAfter(arguments, "--resume"));
        Assert.Equal("35", ValueAfter(arguments, "--max-turns"));
        Assert.DoesNotContain("--no-session-persistence", arguments);
        Assert.DoesNotContain("--bare", arguments);
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
        Assert.DoesNotContain("--resume", arguments);
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

    [Fact]
    public void WhenNoBackendToolIsAllowed_ThenNoBuiltInToolIsAvailableAndOnlyTheMcpServerIsAllowed()
    {
        // Arrange
        var options = new ClaudeCliOptions("/tmp/prompt.md", "/tmp/mcp.json", null, null, null, null, PersistSession: false);

        // Act
        var arguments = ClaudeCliArguments.Build(options);

        // Assert
        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--system-prompt-file", "/tmp/prompt.md",
             "--system-prompt-snapshot", "off", "--tools", "", "--strict-mcp-config", "--mcp-config", "/tmp/mcp.json",
             "--allowedTools", "mcp__homeblaze", "--setting-sources", "", "--no-session-persistence"],
            arguments);
    }

    [Theory]
    [InlineData(new[] { "web_fetch", "web_search", "Bash", "unknown" }, "WebSearch,WebFetch")]
    [InlineData(new[] { "web_search" }, "WebSearch")]
    public void WhenWebToolsAreAllowed_ThenOnlyTheyAreBuiltInToolsAndRunWithoutAPrompt(string[] backendTools, string expectedCliTools)
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
             "--allowedTools", "mcp__homeblaze," + expectedCliTools, "--setting-sources", "", "--no-session-persistence"],
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
