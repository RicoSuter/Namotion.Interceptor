using System.Globalization;

namespace HomeBlaze.AI.Agents.Cli;

/// <summary>
/// Classifies failed CLI runs.
/// </summary>
public static class ClaudeCliFailure
{
    /// <summary>Whether the run failed because the session to resume does not exist.</summary>
    public static bool IsSessionMissing(ClaudeCliRunResult result) =>
        result.ExitCode != 0 && result.StandardError.Contains("No conversation found", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the exception for a failed run, or null when the run succeeded.
    /// </summary>
    public static AgentTurnException? Classify(ClaudeCliRunResult result)
    {
        if (!result.IsError && result.ExitCode == 0 && result.ResultText is not null)
        {
            return null;
        }

        var message = !string.IsNullOrWhiteSpace(result.ResultText) ? result.ResultText.Trim()
            : !string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardError.Trim()
            : $"The Claude CLI exited with code {result.ExitCode} without a result.";

        if (result.ErrorCode == "authentication_failed" ||
            message.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentTurnException(
                AgentFailureKind.Configuration,
                "The Claude CLI is not logged in. Run claude once as the HomeBlaze user and log in.");
        }

        if (result.RateLimit?.Status == "rejected" ||
            result.ErrorCode?.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) == true ||
            message.Contains("usage limit", StringComparison.OrdinalIgnoreCase))
        {
            var resetTime = GetResetTime(result.RateLimit);
            return new AgentTurnException(
                AgentFailureKind.UsageLimit,
                resetTime is { } resetsAt
                    ? "The Claude usage limit is reached until " + resetsAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) + "."
                    : "The Claude usage limit is reached.",
                resetTime);
        }

        if (IsSessionMissing(result))
        {
            return new AgentTurnException(AgentFailureKind.SessionMissing, message);
        }

        return new AgentTurnException(AgentFailureKind.Transient, message);
    }

    private static DateTimeOffset? GetResetTime(AgentRateLimit? rateLimit)
    {
        if (rateLimit is null)
        {
            return null;
        }

        if (rateLimit.SevenDayUtilization >= 1)
        {
            return rateLimit.SevenDayResetsAt;
        }

        return rateLimit.FiveHourResetsAt;
    }
}
