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

        var message = GetMessage(result);
        var resultText = result.ResultText ?? string.Empty;

        // Overload is checked first: a capacity problem must back off and retry, not pause the agent.
        if (IsOverloaded(result, resultText))
        {
            return new AgentTurnException(AgentFailureKind.Transient, message);
        }

        if (IsNotLoggedIn(result, resultText))
        {
            return new AgentTurnException(
                AgentFailureKind.Configuration,
                "The Claude CLI is not logged in. Run claude once as the HomeBlaze user and log in.");
        }

        if (IsUsageLimitReached(result, message))
        {
            return CreateUsageLimitException(result.RateLimit);
        }

        if (IsSessionMissing(result))
        {
            return new AgentTurnException(AgentFailureKind.SessionMissing, message);
        }

        return new AgentTurnException(AgentFailureKind.Transient, message);
    }

    private static string GetMessage(ClaudeCliRunResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ResultText))
        {
            return result.ResultText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            return result.StandardError.Trim();
        }

        return $"The Claude CLI exited with code {result.ExitCode} without a result.";
    }

    private static bool IsOverloaded(ClaudeCliRunResult result, string resultText) =>
        result.ErrorCode?.Contains("overloaded", StringComparison.OrdinalIgnoreCase) == true ||
        ContainsOverloadedStatusCode(resultText);

    // Standard error is not searched: it may echo prompts or hints that mention logging in.
    private static bool IsNotLoggedIn(ClaudeCliRunResult result, string resultText) =>
        result.ErrorCode == "authentication_failed" ||
        resultText.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
        resultText.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase);

    private static bool IsUsageLimitReached(ClaudeCliRunResult result, string message) =>
        result.RateLimit?.Status == "rejected" ||
        result.ErrorCode?.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) == true ||
        message.Contains("usage limit", StringComparison.OrdinalIgnoreCase);

    private static AgentTurnException CreateUsageLimitException(AgentRateLimit? rateLimit)
    {
        var resetTime = GetResetTime(rateLimit);
        return new AgentTurnException(
            AgentFailureKind.UsageLimit,
            resetTime is { } resetsAt
                ? "The Claude usage limit is reached until " + resetsAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) + "."
                : "The Claude usage limit is reached.",
            resetTime);
    }

    // Matches 529 as a standalone number, so a token count like 15290 does not read as an overload.
    private static bool ContainsOverloadedStatusCode(string text)
    {
        for (var index = text.IndexOf("529", StringComparison.Ordinal); index >= 0; index = text.IndexOf("529", index + 1, StringComparison.Ordinal))
        {
            var end = index + 3;
            if ((index == 0 || !char.IsAsciiDigit(text[index - 1])) && (end == text.Length || !char.IsAsciiDigit(text[end])))
            {
                return true;
            }
        }

        return false;
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
