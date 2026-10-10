using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Cli;
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
        var result = await new ClaudeCliProcess().RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(updates.Add), CancellationToken.None);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.IsError);
        Assert.Equal("Hello world", result.ResultText);
        Assert.Equal("d57c5029-b6b6-4e1a-8767-6df4d71bb4ba", result.SessionId);
        Assert.Equal(382, result.Usage!.InputTokens);
        Assert.Equal(0.73, result.RateLimit!.SevenDayUtilization);
        Assert.Equal(["Hello", " world"], updates.OfType<AgentTextDelta>().Select(delta => delta.Text));
        Assert.Equal("hello there", cli.LastStandardInput);
    }

    [UnixFact]
    public async Task WhenTheCliReportsAnAuthenticationError_ThenTheErrorCodeIsReturned()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("not-logged-in.jsonl", exitCode: 1);
        var invocation = new ClaudeCliInvocation(cli.ScriptPath, ["-p"], cli.Directory, "hi");

        // Act
        var result = await new ClaudeCliProcess().RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), CancellationToken.None);

        // Assert
        Assert.Equal(1, result.ExitCode);
        Assert.True(result.IsError);
        Assert.Equal("authentication_failed", result.ErrorCode);
    }

    [UnixFact]
    public async Task WhenTheCliWritesToStandardError_ThenItIsCapturedWithTheResult()
    {
        // Arrange
        using var cli = FakeClaudeCli.Create("success.jsonl", resumeFixture: "resume-missing.jsonl");
        var invocation = new ClaudeCliInvocation(cli.ScriptPath, ["-p", "--resume", "x"], cli.Directory, "hi");

        // Act
        var result = await new ClaudeCliProcess().RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), CancellationToken.None);

        // Assert
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("error_during_execution", result.ResultSubtype);
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
        var result = await new ClaudeCliProcess().RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), CancellationToken.None);

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
            new ClaudeCliProcess().RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), CancellationToken.None));
        Assert.Equal(AgentFailureKind.Configuration, exception.Kind);
    }

    [Fact]
    public async Task WhenTheCliPathIsEmpty_ThenAConfigurationErrorIsThrown()
    {
        // Arrange
        var invocation = new ClaudeCliInvocation(" ", ["-p"], Path.GetTempPath(), "hi");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<AgentTurnException>(() =>
            new ClaudeCliProcess().RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), CancellationToken.None));
        Assert.Equal(AgentFailureKind.Configuration, exception.Kind);
        Assert.Equal("The Claude CLI path is not set.", exception.Message);
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
            new ClaudeCliProcess().RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), CancellationToken.None));
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
        var run = new ClaudeCliProcess().RunAsync(invocation, new SynchronousProgress<AgentTurnUpdate>(_ => { }), cancellation.Token);
        await AsyncTestHelpers.WaitUntilAsync(() => cli.LastProcessId is not null);

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await AsyncTestHelpers.WaitUntilAsync(() => !IsRunning(cli.LastProcessId!.Value));
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
