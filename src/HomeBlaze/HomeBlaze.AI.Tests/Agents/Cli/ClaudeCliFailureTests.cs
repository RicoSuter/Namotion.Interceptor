using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Cli;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Cli;

public class ClaudeCliFailureTests
{
    private static readonly DateTimeOffset FiveHourReset = new(2026, 10, 11, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SevenDayReset = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OpusReset = new(2026, 10, 16, 18, 0, 0, TimeSpan.Zero);
    private const int MaxTurns = 35;

    private static AgentTurnException? Classify(ClaudeCliRunResult result) => ClaudeCliFailure.Classify(result, MaxTurns);

    private static ClaudeCliRunResult CreateResult(
        int exitCode = 0,
        string? resultText = "Done",
        bool isError = false,
        string? errorCode = null,
        AgentRateLimit? rateLimit = null,
        string standardError = "",
        string? subtype = null) =>
        new(exitCode, "session", resultText, isError, subtype ?? (isError ? "error_during_execution" : "success"), errorCode, null, rateLimit, standardError);

    [Fact]
    public void WhenTheRunSucceeded_ThenNoFailureIsReturned()
    {
        // Act
        var failure = Classify(CreateResult());

        // Assert
        Assert.Null(failure);
    }

    [Fact]
    public void WhenTheCliExitedWithoutAResult_ThenTheFailureIsTransientAndNamesTheExitCode()
    {
        // Act
        var failure = Classify(CreateResult(exitCode: 0, resultText: null));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.Transient, failure.Kind);
        Assert.Contains("code 0", failure.Message);
    }

    [Fact]
    public void WhenTheCliFailedWithOnlyStandardError_ThenTheMessageIsTheStandardError()
    {
        // Act
        var failure = Classify(CreateResult(exitCode: 2, resultText: null, standardError: "  boom\n"));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.Transient, failure.Kind);
        Assert.Equal("boom", failure.Message);
    }

    [Fact]
    public void WhenAuthenticationFailed_ThenTheFailureIsAConfigurationFailureWithALoginHint()
    {
        // Act
        var failure = Classify(CreateResult(exitCode: 1, resultText: "Not logged in", isError: true, errorCode: "authentication_failed"));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.Configuration, failure.Kind);
        Assert.Contains("log in", failure.Message);
    }

    [Theory]
    [InlineData("Claude AI usage limit reached", true, null, "", AgentFailureKind.UsageLimit)]
    [InlineData(null, true, null, "No conversation found with session ID: x", AgentFailureKind.Transient)]
    [InlineData(null, false, null, "hint: run /login if needed\nInvalid API key in environment", AgentFailureKind.Transient)]
    [InlineData("Please run /login", true, "overloaded_error", "", AgentFailureKind.Transient)]
    [InlineData("Used 15290 tokens. Please run /login", true, null, "", AgentFailureKind.Configuration)]
    public void WhenTheRunFailedWithoutARateLimit_ThenItIsClassifiedByItsOutputWithoutAResetTime(
        string? resultText, bool isError, string? errorCode, string standardError, AgentFailureKind expectedKind)
    {
        // Act
        var failure = Classify(CreateResult(exitCode: 1, resultText, isError, errorCode, standardError: standardError));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(expectedKind, failure.Kind);
        Assert.Null(failure.RetryAfter);
    }

    [Theory]
    [InlineData(1, 0.5, false)]
    [InlineData(0.4, 1, true)]
    public void WhenTheLimitIsRejected_ThenTheFailureIsAUsageLimitUntilTheResetOfTheExhaustedWindow(
        double fiveHourUtilization, double sevenDayUtilization, bool expectsSevenDayReset)
    {
        // Arrange
        var rateLimit = new AgentRateLimit("rejected", fiveHourUtilization, FiveHourReset, sevenDayUtilization, SevenDayReset);

        // Act
        var failure = Classify(CreateResult(exitCode: 1, resultText: "Limit reached", isError: true, rateLimit: rateLimit));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.UsageLimit, failure.Kind);
        Assert.Equal(expectsSevenDayReset ? SevenDayReset : FiveHourReset, failure.RetryAfter);
    }

    [Fact]
    public void WhenTheResumedSessionIsMissing_ThenItIsDetected()
    {
        // Act
        var isSessionMissing = ClaudeCliFailure.IsSessionMissing(
            CreateResult(exitCode: 1, resultText: null, isError: true, standardError: "No conversation found with session ID: x"));

        // Assert
        Assert.True(isSessionMissing);
    }

    [Fact]
    public void WhenTheResultMentionsA529_ThenTheFailureIsTransient()
    {
        // Arrange
        var rateLimit = new AgentRateLimit("rejected", 1, FiveHourReset, 0.5, SevenDayReset);

        // Act
        var failure = Classify(CreateResult(exitCode: 1, resultText: "API Error: Repeated 529 Overloaded errors", isError: true, rateLimit: rateLimit));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.Transient, failure.Kind);
        Assert.Null(failure.RetryAfter);
    }

    [Fact]
    public void WhenTheRunStoppedAtTheTurnLimit_ThenTheFailureNamesTheModelRoundLimit()
    {
        // Act
        var failure = Classify(CreateResult(exitCode: 1, resultText: null, isError: true, subtype: "error_max_turns"));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.Transient, failure.Kind);
        Assert.Equal("The turn stopped after 35 model rounds, the limit set by MaxToolCallsPerTurn.", failure.Message);
    }

    [Fact]
    public void WhenAPerModelLimitRejectsWhileTheWindowsHaveRoom_ThenTheFailureLastsUntilThatLimitResets()
    {
        // Arrange
        var rateLimit = new AgentRateLimit("rejected", 0.31, FiveHourReset, 0.64, SevenDayReset);
        var result = CreateResult(exitCode: 1, resultText: "You've hit your weekly Opus limit", isError: true, rateLimit: rateLimit) with
        {
            LimitResetsAt = OpusReset,
            LimitType = "seven_day_opus"
        };

        // Act
        var failure = Classify(result);

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.UsageLimit, failure.Kind);
        Assert.Equal(OpusReset, failure.RetryAfter);
        Assert.Equal("The Claude weekly Opus usage limit is reached until 2026-10-16 18:00 UTC.", failure.Message);
    }

    [Fact]
    public void WhenTheLimitIsOnlyNamedButNotRejected_ThenTheWindowResetIsUsed()
    {
        // Arrange
        var rateLimit = new AgentRateLimit("allowed_warning", 1, FiveHourReset, 0.5, SevenDayReset);
        var result = CreateResult(exitCode: 1, resultText: "Claude AI usage limit reached", isError: true, rateLimit: rateLimit) with
        {
            LimitResetsAt = OpusReset,
            LimitType = "seven_day_opus"
        };

        // Act
        var failure = Classify(result);

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.UsageLimit, failure.Kind);
        Assert.Equal(FiveHourReset, failure.RetryAfter);
        Assert.StartsWith("The Claude usage limit is reached until", failure.Message);
    }
}
