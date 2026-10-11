using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Agents.Cli;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Cli;

public class ClaudeCliFailureTests
{
    private static readonly DateTimeOffset FiveHourReset = new(2026, 10, 11, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SevenDayReset = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);

    private static ClaudeCliRunResult CreateResult(
        int exitCode = 0,
        string? resultText = "Done",
        bool isError = false,
        string? errorCode = null,
        AgentRateLimit? rateLimit = null,
        string standardError = "") =>
        new(exitCode, "session", resultText, isError, isError ? "error_during_execution" : "success", errorCode, null, rateLimit, standardError);

    [Fact]
    public void WhenTheRunSucceeded_ThenNoFailureIsReturned()
    {
        // Act
        var failure = ClaudeCliFailure.Classify(CreateResult());

        // Assert
        Assert.Null(failure);
    }

    [Fact]
    public void WhenTheCliExitedWithoutAResult_ThenTheFailureIsTransientAndNamesTheExitCode()
    {
        // Act
        var failure = ClaudeCliFailure.Classify(CreateResult(exitCode: 0, resultText: null));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.Transient, failure.Kind);
        Assert.Contains("code 0", failure.Message);
    }

    [Fact]
    public void WhenTheCliFailedWithOnlyStandardError_ThenTheMessageIsTheStandardError()
    {
        // Act
        var failure = ClaudeCliFailure.Classify(CreateResult(exitCode: 2, resultText: null, standardError: "  boom\n"));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.Transient, failure.Kind);
        Assert.Equal("boom", failure.Message);
    }

    [Fact]
    public void WhenAuthenticationFailed_ThenTheFailureIsAConfigurationFailureWithALoginHint()
    {
        // Act
        var failure = ClaudeCliFailure.Classify(CreateResult(exitCode: 1, resultText: "Not logged in", isError: true, errorCode: "authentication_failed"));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.Configuration, failure.Kind);
        Assert.Contains("log in", failure.Message);
    }

    [Fact]
    public void WhenTheFiveHourLimitIsRejected_ThenTheFailureIsAUsageLimitUntilTheFiveHourReset()
    {
        // Arrange
        var rateLimit = new AgentRateLimit("rejected", 1, FiveHourReset, 0.5, SevenDayReset);

        // Act
        var failure = ClaudeCliFailure.Classify(CreateResult(exitCode: 1, resultText: "Limit reached", isError: true, rateLimit: rateLimit));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.UsageLimit, failure.Kind);
        Assert.Equal(FiveHourReset, failure.RetryAfter);
    }

    [Fact]
    public void WhenTheSevenDayLimitIsExhausted_ThenTheFailureIsAUsageLimitUntilTheSevenDayReset()
    {
        // Arrange
        var rateLimit = new AgentRateLimit("rejected", 0.4, FiveHourReset, 1, SevenDayReset);

        // Act
        var failure = ClaudeCliFailure.Classify(CreateResult(exitCode: 1, resultText: "Limit reached", isError: true, rateLimit: rateLimit));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.UsageLimit, failure.Kind);
        Assert.Equal(SevenDayReset, failure.RetryAfter);
    }

    [Fact]
    public void WhenTheResultMentionsTheUsageLimit_ThenTheFailureIsAUsageLimitWithoutAResetTime()
    {
        // Act
        var failure = ClaudeCliFailure.Classify(CreateResult(exitCode: 1, resultText: "Claude AI usage limit reached", isError: true));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.UsageLimit, failure.Kind);
        Assert.Null(failure.RetryAfter);
    }

    [Fact]
    public void WhenTheResumedSessionIsMissing_ThenTheFailureIsSessionMissing()
    {
        // Arrange
        var result = CreateResult(exitCode: 1, resultText: null, isError: true, standardError: "No conversation found with session ID: x");

        // Act
        var failure = ClaudeCliFailure.Classify(result);

        // Assert
        Assert.True(ClaudeCliFailure.IsSessionMissing(result));
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.SessionMissing, failure.Kind);
    }

    [Fact]
    public void WhenOnlyStandardErrorMentionsLogin_ThenTheFailureIsNotAConfigurationFailure()
    {
        // Act
        var failure = ClaudeCliFailure.Classify(CreateResult(exitCode: 1, resultText: null, standardError: "hint: run /login if needed\nInvalid API key in environment"));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.Transient, failure.Kind);
    }

    [Fact]
    public void WhenTheErrorCodeIsOverloaded_ThenTheFailureIsTransient()
    {
        // Act
        var failure = ClaudeCliFailure.Classify(CreateResult(exitCode: 1, resultText: "Please run /login", isError: true, errorCode: "overloaded_error"));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.Transient, failure.Kind);
    }

    [Fact]
    public void WhenTheResultMentionsA529_ThenTheFailureIsTransient()
    {
        // Arrange
        var rateLimit = new AgentRateLimit("rejected", 1, FiveHourReset, 0.5, SevenDayReset);

        // Act
        var failure = ClaudeCliFailure.Classify(CreateResult(exitCode: 1, resultText: "API Error: Repeated 529 Overloaded errors", isError: true, rateLimit: rateLimit));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.Transient, failure.Kind);
        Assert.Null(failure.RetryAfter);
    }

    [Fact]
    public void WhenTheResultContainsANumberWith529Inside_ThenItIsNotReadAsAnOverload()
    {
        // Act
        var failure = ClaudeCliFailure.Classify(CreateResult(exitCode: 1, resultText: "Used 15290 tokens. Please run /login", isError: true));

        // Assert
        Assert.NotNull(failure);
        Assert.Equal(AgentFailureKind.Configuration, failure.Kind);
    }
}
