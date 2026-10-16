using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Cli;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Cli;

public class ClaudeCliProcessTests
{
    [UnixFact]
    public async Task WhenTheCliSucceeds_ThenTextSessionUsageAndRateLimitAreReturnedAndDeltasStreamed()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl");
        var updates = new List<AgentTurnUpdate>();
        var invocation = new ClaudeCliInvocation(cli.ScriptPath, ["-p"], cli.Directory, "hello there");

        // Act
        var result = await ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(updates.Add), null, NullLogger.Instance, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Result!.IsError);
        Assert.Equal("Hello world", result.Result.Text);
        Assert.Equal("d57c5029-b6b6-4e1a-8767-6df4d71bb4ba", result.SessionId);
        Assert.Equal(382, result.Result.Usage!.InputTokens);
        Assert.Equal(0.73, result.RateLimit!.RateLimit.SevenDayUtilization);
        Assert.Equal(["Hello", " world"], updates.OfType<AgentTextDelta>().Select(delta => delta.Text));
        Assert.Equal("hello there", cli.LastStandardInput);
    }

    [UnixFact]
    public async Task WhenTheInitLineNamesNoApiKeySource_ThenTheRunSucceedsWithAWarning()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("no-api-key-source.jsonl");
        var logger = new RecordingLogger();
        var invocation = new ClaudeCliInvocation(cli.ScriptPath, ["-p"], cli.Directory, "hi");

        // Act
        var result = await ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), null, logger, CancellationToken.None);

        // Assert
        Assert.Equal("Hello world", result.Result!.Text);
        Assert.Contains("names no API key source", Assert.Single(logger.Warnings));
    }

    [UnixFact]
    public async Task WhenTheInitLineNamesNoApiKeyInUse_ThenNoWarningIsLogged()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl");
        var logger = new RecordingLogger();
        var invocation = new ClaudeCliInvocation(cli.ScriptPath, ["-p"], cli.Directory, "hi");

        // Act
        await ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), null, logger, CancellationToken.None);

        // Assert
        Assert.Empty(logger.Warnings);
    }

    [UnixFact]
    public async Task WhenTheCliReportsAnAuthenticationError_ThenTheErrorCodeIsReturned()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("not-logged-in.jsonl", exitCode: 1);
        var invocation = new ClaudeCliInvocation(cli.ScriptPath, ["-p"], cli.Directory, "hi");

        // Act
        var result = await ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), null, NullLogger.Instance, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.ExitCode);
        Assert.True(result.Result!.IsError);
        Assert.Equal("authentication_failed", result.ErrorCode);
    }

    [UnixFact]
    public async Task WhenTheCliWritesToStandardError_ThenItIsCapturedWithTheResult()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl", resumeFixture: "resume-missing.jsonl");
        var invocation = new ClaudeCliInvocation(cli.ScriptPath, ["-p", "--resume=x"], cli.Directory, "hi");

        // Act
        var result = await ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), null, NullLogger.Instance, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("error_during_execution", result.Result!.Subtype);
        Assert.Contains("No conversation found with session ID", result.StandardError);
    }

    [UnixFact]
    public async Task WhenTheStandardInputIsLargerThanThePipeBuffer_ThenItIsWrittenCompletely()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl");
        var input = new string('x', 1024 * 1024);
        var invocation = new ClaudeCliInvocation(cli.ScriptPath, ["-p"], cli.Directory, input);

        // Act
        var result = await ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), null, NullLogger.Instance, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(input, cli.LastStandardInput);
    }

    [Fact]
    public async Task WhenTheCliDoesNotExist_ThenAConfigurationErrorIsThrown()
    {
        // Arrange
        var invocation = new ClaudeCliInvocation("/nonexistent/claude", ["-p"], Path.GetTempPath(), "hi");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<AgentTurnException>(() =>
            ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), null, NullLogger.Instance, CancellationToken.None));
        Assert.Equal(AgentFailureKind.Configuration, exception.Kind);
    }

    [Fact]
    public async Task WhenTheCliPathIsEmpty_ThenAConfigurationErrorIsThrown()
    {
        // Arrange
        var invocation = new ClaudeCliInvocation(" ", ["-p"], Path.GetTempPath(), "hi");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<AgentTurnException>(() =>
            ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), null, NullLogger.Instance, CancellationToken.None));
        Assert.Equal(AgentFailureKind.Configuration, exception.Kind);
        Assert.Equal("The Claude CLI path is not set.", exception.Message);
    }

    [Theory]
    [InlineData(@"C:\Users\home\AppData\Roaming\npm\claude.cmd")]
    [InlineData("claude.CMD")]
    [InlineData("claude.bat")]
    [InlineData("claude.cmd. ")]
    public async Task WhenTheCliPathIsABatchFile_ThenAConfigurationErrorPointsToTheExecutable(string cliPath)
    {
        // Arrange
        var invocation = new ClaudeCliInvocation(cliPath, ["-p"], Path.GetTempPath(), "hi");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<AgentTurnException>(() =>
            ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), null, NullLogger.Instance, CancellationToken.None));
        Assert.Equal(AgentFailureKind.Configuration, exception.Kind);
        Assert.Contains("claude.exe", exception.Message);
    }

    [UnixFact]
    public async Task WhenTheInitLineNamesAnApiKeySource_ThenTheProcessIsKilledBeforeItAnswersAndAConfigurationErrorNamesTheSource()
    {
        // Arrange - the fake CLI holds back everything after the init line for longer than the test waits.
        using var cli = FakeClaudeCli.Create("api-key-source.jsonl", sleepAfterFirstLineSeconds: 60);
        var updates = new List<AgentTurnUpdate>();
        var invocation = new ClaudeCliInvocation(cli.ScriptPath, ["-p"], cli.Directory, "hi");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<AgentTurnException>(() =>
            ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(updates.Add), null, NullLogger.Instance, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(AgentFailureKind.Configuration, exception.Kind);
        Assert.Contains("'/login managed key'", exception.Message);
        Assert.Empty(updates);
        await AsyncTestHelpers.WaitUntilAsync(() => !cli.IsLastProcessRunning);
    }

    [UnixFact]
    public async Task WhenTheWorkingDirectoryDoesNotExist_ThenAConfigurationErrorNamesTheDirectory()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl");
        var workingDirectory = Path.Combine(cli.Directory, "missing");
        var invocation = new ClaudeCliInvocation(cli.ScriptPath, ["-p"], workingDirectory, "hi");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<AgentTurnException>(() =>
            ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), null, NullLogger.Instance, CancellationToken.None));
        Assert.Equal(AgentFailureKind.Configuration, exception.Kind);
        Assert.Contains(workingDirectory, exception.Message);
        Assert.Empty(cli.Invocations);
    }

    [UnixFact]
    public async Task WhenCancelled_ThenTheProcessIsKilled()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl", sleepSeconds: 30);
        var invocation = new ClaudeCliInvocation(cli.ScriptPath, ["-p"], cli.Directory, "hi");
        using var cancellation = new CancellationTokenSource();
        var run = ClaudeCliProcess.RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), null, NullLogger.Instance, cancellation.Token);
        await AsyncTestHelpers.WaitUntilAsync(() => cli.LastProcessId is not null);

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await AsyncTestHelpers.WaitUntilAsync(() => !cli.IsLastProcessRunning);
    }
}
