using System.Text.Json;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Cli;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Cli;

public class ClaudeCliArgumentsTests
{
    private static string? ValueAfter(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }

    [Fact]
    public void WhenBuildingAChatResume_ThenIsolationFlagsSessionAndModelArePresent()
    {
        // Arrange
        var options = new ClaudeCliOptions("SYSTEM", "/tmp/mcp.json", "opus", AgentEffort.ExtraHigh, "sonnet", "session-1", PersistSession: true);

        // Act
        var arguments = ClaudeCliArguments.Build(options);

        // Assert
        Assert.Equal("-p", arguments[0]);
        Assert.Equal("stream-json", ValueAfter(arguments, "--output-format"));
        Assert.Contains("--verbose", arguments);
        Assert.Contains("--include-partial-messages", arguments);
        Assert.Equal("SYSTEM", ValueAfter(arguments, "--system-prompt"));
        Assert.Equal("off", ValueAfter(arguments, "--system-prompt-snapshot"));
        Assert.Equal(string.Empty, ValueAfter(arguments, "--tools"));
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.Equal("/tmp/mcp.json", ValueAfter(arguments, "--mcp-config"));
        Assert.Equal("mcp__homeblaze", ValueAfter(arguments, "--allowedTools"));
        Assert.Equal("project", ValueAfter(arguments, "--setting-sources"));
        Assert.Equal("opus", ValueAfter(arguments, "--model"));
        Assert.Equal("xhigh", ValueAfter(arguments, "--effort"));
        Assert.Equal("sonnet", ValueAfter(arguments, "--fallback-model"));
        Assert.Equal("session-1", ValueAfter(arguments, "--resume"));
        Assert.DoesNotContain("--no-session-persistence", arguments);
        Assert.DoesNotContain("--bare", arguments);
    }

    [Fact]
    public void WhenBuildingABackgroundRun_ThenTheSessionIsNotPersistedAndOptionalFlagsAreOmitted()
    {
        // Arrange
        var options = new ClaudeCliOptions("SYSTEM", "/tmp/mcp.json", null, null, null, null, PersistSession: false);

        // Act
        var arguments = ClaudeCliArguments.Build(options);

        // Assert
        Assert.Contains("--no-session-persistence", arguments);
        Assert.DoesNotContain("--resume", arguments);
        Assert.DoesNotContain("--model", arguments);
        Assert.DoesNotContain("--effort", arguments);
        Assert.DoesNotContain("--fallback-model", arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenTheResumeSessionIdIsBlank_ThenResumeIsOmitted(string sessionId)
    {
        // Arrange
        var options = new ClaudeCliOptions("SYSTEM", "/tmp/mcp.json", null, null, null, sessionId, PersistSession: true);

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
}
