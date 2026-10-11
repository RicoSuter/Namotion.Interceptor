using HomeBlaze.Abstractions;
using HomeBlaze.AI.Agents;
using System.Text.Json;
using HomeBlaze.AI.Agents.Cli;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Cli;

public class ClaudeCliAgentTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:5192/mcp/agents");

    private static Task<AgentTestContext<ClaudeCliAgent>> StartAsync(FakeClaudeCli cli, Action<ClaudeCliAgent>? configure = null) =>
        AgentTestContext<ClaudeCliAgent>.StartAsync(
            services => new ClaudeCliAgent(services, NullLogger<ClaudeCliAgent>.Instance),
            agent =>
            {
                agent.CliPath = cli.ScriptPath;
                configure?.Invoke(agent);
            },
            Endpoint);

    private static string SessionPath(AgentTestContext<ClaudeCliAgent> test) =>
        Path.Combine(test.DataDirectory, "Agents", "Agent.session.json");

    // A directory at the session path makes reading, replacing and deleting the session file fail.
    private static void ReplaceSessionFileWithDirectory(AgentTestContext<ClaudeCliAgent> test)
    {
        File.Delete(SessionPath(test));
        Directory.CreateDirectory(SessionPath(test));
    }

    private static string? ValueAfter(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }

    [UnixFact]
    public async Task WhenChatting_ThenTheSessionIsStoredAndResumedOnTheNextMessage()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl");
        await using var test = await StartAsync(cli, agent => agent.Model = "opus");

        // Act
        await test.Agent.SendMessageAsync("first", CancellationToken.None);
        await test.Agent.SendMessageAsync("second", CancellationToken.None);

        // Assert
        Assert.Null(ValueAfter(cli.Invocations[0], "--resume"));
        Assert.Equal("d57c5029-b6b6-4e1a-8767-6df4d71bb4ba", ValueAfter(cli.Invocations[1], "--resume"));
        Assert.Equal("opus", ValueAfter(cli.Invocations[1], "--model"));
        Assert.Equal("Hello world", test.Agent.Timeline.Entries[^1].Text);
        Assert.Contains("d57c5029", await File.ReadAllTextAsync(Path.Combine(test.DataDirectory, "Agents", "Agent.session.json")));
    }

    [UnixFact]
    public async Task WhenTheAgentRestarts_ThenTheSessionFromTheSessionFileIsResumed()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl");
        await using var test = await StartAsync(cli);
        await test.Agent.SendMessageAsync("first", CancellationToken.None);
        await test.Agent.StopAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(test.DataDirectory, "Agents", "Agent.session.json"), """{"sessionId":"stored-session"}""");
        await test.Agent.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Status == ServiceStatus.Running);

        // Act
        await test.Agent.SendMessageAsync("second", CancellationToken.None);

        // Assert
        Assert.Equal("stored-session", ValueAfter(cli.Invocations[1], "--resume"));
    }

    [UnixFact]
    public async Task WhenTheSessionFileIsDamaged_ThenTheAgentStartsAndANewSessionBegins()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl");
        await using var test = await StartAsync(cli);
        await test.Agent.SendMessageAsync("first", CancellationToken.None);
        await test.Agent.StopAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(test.DataDirectory, "Agents", "Agent.session.json"), "not json");
        await test.Agent.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Status == ServiceStatus.Running);

        // Act
        await test.Agent.SendMessageAsync("second", CancellationToken.None);

        // Assert
        Assert.Null(ValueAfter(cli.Invocations[1], "--resume"));
        Assert.Equal("Hello world", test.Agent.Timeline.Entries[^1].Text);
    }

    [UnixFact]
    public async Task WhenTheSessionFileCannotBeReadOrWritten_ThenTurnsSucceedAndTheSessionIsKeptInMemory()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl");
        await using var test = await StartAsync(cli);
        await test.Agent.SendMessageAsync("first", CancellationToken.None);
        await test.Agent.StopAsync(CancellationToken.None);
        ReplaceSessionFileWithDirectory(test);
        await test.Agent.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Status == ServiceStatus.Running);

        // Act
        await test.Agent.SendMessageAsync("second", CancellationToken.None);
        await test.Agent.SendMessageAsync("third", CancellationToken.None);

        // Assert
        Assert.Null(ValueAfter(cli.Invocations[1], "--resume"));
        Assert.Equal("d57c5029-b6b6-4e1a-8767-6df4d71bb4ba", ValueAfter(cli.Invocations[2], "--resume"));
        Assert.DoesNotContain(test.Agent.Timeline.Entries, entry => entry.Kind == AgentTimelineEntryKind.Error);
        Assert.Equal(ServiceStatus.Running, test.Agent.Status);
    }

    [UnixFact]
    public async Task WhenTheSessionFileCannotBeDeleted_ThenANewConversationStillStarts()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl");
        await using var test = await StartAsync(cli);
        await test.Agent.SendMessageAsync("first", CancellationToken.None);
        ReplaceSessionFileWithDirectory(test);

        // Act
        await test.Agent.NewConversationAsync(CancellationToken.None);
        await test.Agent.SendMessageAsync("second", CancellationToken.None);

        // Assert
        Assert.Contains(test.Agent.Timeline.Entries, entry => entry is { Kind: AgentTimelineEntryKind.Divider, Text: "New conversation" });
        Assert.Null(ValueAfter(cli.Invocations[1], "--resume"));
        Assert.DoesNotContain(test.Agent.Timeline.Entries, entry => entry.Kind == AgentTimelineEntryKind.Error);
    }

    [UnixFact]
    public async Task WhenATurnEnds_ThenTheMcpConfigurationWasOwnerOnlyAndTheTokenIsRevoked()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl");
        await using var test = await StartAsync(cli);

        // Act
        await test.Agent.SendMessageAsync("hello", CancellationToken.None);

        // Assert
        Assert.Equal("600", cli.LastMcpConfigurationMode);
        using var configuration = JsonDocument.Parse(cli.LastMcpConfiguration);
        var authorization = configuration.RootElement
            .GetProperty("mcpServers").GetProperty(ClaudeCliArguments.McpServerName)
            .GetProperty("headers").GetProperty("Authorization").GetString()!;
        var token = authorization["Bearer ".Length..];
        Assert.NotEmpty(token);
        Assert.False(test.Services.TokenRegistry.TryGetToolSet(token, out _));
    }

    [UnixFact]
    public async Task WhenStartingANewConversation_ThenTheSessionIsNotResumed()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl");
        await using var test = await StartAsync(cli);
        await test.Agent.SendMessageAsync("first", CancellationToken.None);

        // Act
        await test.Agent.NewConversationAsync(CancellationToken.None);
        await test.Agent.SendMessageAsync("second", CancellationToken.None);

        // Assert
        Assert.Null(ValueAfter(cli.Invocations[1], "--resume"));
    }

    [UnixFact]
    public async Task WhenRunningInTheBackground_ThenNoSessionIsPersistedAndTheMcpConfigurationCarriesTheToken()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl");
        await using var test = await StartAsync(cli, agent => agent.BackgroundModel = "haiku");

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.RunCount == 1);

        // Assert
        var arguments = Assert.Single(cli.Invocations);
        Assert.Contains("--no-session-persistence", arguments);
        Assert.Null(ValueAfter(arguments, "--resume"));
        Assert.Equal("haiku", ValueAfter(arguments, "--model"));
        Assert.Contains(Endpoint.ToString(), cli.LastMcpConfiguration);
        Assert.Contains("Bearer ", cli.LastMcpConfiguration);
        Assert.Contains("Trigger: manual", cli.LastStandardInput);
        Assert.Empty(Directory.GetFiles(Path.Combine(test.DataDirectory, "Agents", "Agent.workdir"), ".mcp-*.json"));
        Assert.False(File.Exists(Path.Combine(test.DataDirectory, "Agents", "Agent.session.json")));
    }

    [UnixFact]
    public async Task WhenTheCliIsNotLoggedIn_ThenTheAgentIsUnavailableWithALoginHint()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("not-logged-in.jsonl", exitCode: 1);
        await using var test = await StartAsync(cli);

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Status == ServiceStatus.Unavailable);

        // Assert
        Assert.Contains("log in", test.Agent.StatusMessage);
        Assert.Empty(Directory.GetFiles(Path.Combine(test.DataDirectory, "Agents", "Agent.workdir"), ".mcp-*.json"));
    }

    [UnixFact]
    public async Task WhenTheResumedSessionIsMissing_ThenANewSessionStartsAndADividerIsWritten()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl", resumeFixture: "resume-missing.jsonl");
        await using var test = await StartAsync(cli);
        await test.Agent.SendMessageAsync("first", CancellationToken.None);

        // Act
        await test.Agent.SendMessageAsync("second", CancellationToken.None);

        // Assert
        Assert.Equal(3, cli.Invocations.Count);
        Assert.Null(ValueAfter(cli.Invocations[2], "--resume"));
        Assert.Contains(test.Agent.Timeline.Entries, entry => entry is { Kind: AgentTimelineEntryKind.Divider, Text: "Session restarted" });
        Assert.Equal(AgentTimelineEntryKind.Assistant, test.Agent.Timeline.Entries[^1].Kind);
        Assert.Contains("d57c5029", await File.ReadAllTextAsync(SessionPath(test)));
    }

    [UnixFact]
    public async Task WhenTheWebServerAddressIsUnknown_ThenTheTurnFailsWithoutStartingTheCli()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl");
        await using var test = await AgentTestContext<ClaudeCliAgent>.StartAsync(
            services => new ClaudeCliAgent(services, NullLogger<ClaudeCliAgent>.Instance),
            agent => agent.CliPath = cli.ScriptPath,
            endpoint: null);

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Status == ServiceStatus.Error);

        // Assert
        Assert.Contains("address", test.Agent.StatusMessage);
        Assert.Empty(cli.Invocations);
    }

    [UnixFact]
    public async Task WhenATurnExceedsTheTimeout_ThenItFailsAndTheProcessIsKilled()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl", sleepSeconds: 30);
        await using var test = await StartAsync(cli, agent => agent.TurnTimeout = TimeSpan.FromSeconds(1));

        // Act
        test.Agent.RunNow();
        await AsyncTestHelpers.WaitUntilAsync(() => test.Agent.Status == ServiceStatus.Error);

        // Assert
        Assert.Contains("did not finish", test.Agent.StatusMessage);
        await AsyncTestHelpers.WaitUntilAsync(() => !IsRunning(cli.LastProcessId!.Value), message: "The CLI process should be killed.");
        Assert.Empty(Directory.GetFiles(Path.Combine(test.DataDirectory, "Agents", "Agent.workdir"), ".mcp-*.json"));
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
