using System.Globalization;

namespace HomeBlaze.AI.Agents.Cli;

/// <summary>
/// Classifies failed CLI runs.
/// </summary>
internal static class ClaudeCliFailure
{
    /// <summary>Whether the run failed because the session to resume does not exist.</summary>
    public static bool IsSessionMissing(ClaudeCliRunResult result) =>
        result.ExitCode != 0 && result.StandardError.Contains("No conversation found", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the exception for a failed run, or null when the run succeeded.
    /// </summary>
    /// <param name="result">The run.</param>
    /// <param name="maxTurns">The model rounds the run was given with <c>--max-turns</c>, named in the failure when it ran out of them.</param>
    public static AgentTurnException? Classify(ClaudeCliRunResult result, int maxTurns)
    {
        if (result.Result is { IsError: false, Text: not null } && result.ExitCode == 0)
        {
            return null;
        }

        // The result line of a run stopped by --max-turns has no text, so it would otherwise read as a missing result.
        if (result.Result?.Subtype == "error_max_turns")
        {
            return new AgentTurnException(
                AgentFailureKind.Transient,
                $"The turn stopped after {maxTurns} model rounds, the limit set by MaxToolCallsPerTurn.");
        }

        var message = GetMessage(result);
        var resultText = result.Result?.Text ?? string.Empty;

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
            return CreateUsageLimitException(result);
        }

        return new AgentTurnException(AgentFailureKind.Transient, message);
    }

    private static string GetMessage(ClaudeCliRunResult result)
    {
        if (result.Result?.Text is { } resultText && !string.IsNullOrWhiteSpace(resultText))
        {
            return resultText.Trim();
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
        result.RateLimit?.RateLimit.Status == "rejected" ||
        result.ErrorCode?.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) == true ||
        message.Contains("usage limit", StringComparison.OrdinalIgnoreCase);

    private static AgentTurnException CreateUsageLimitException(ClaudeCliRunResult result)
    {
        // A rejecting line names the limit that rejected the run, such as a per-model weekly limit that the five-hour
        // and seven-day windows do not show.
        var rejectingLimit = result.RateLimit is { RateLimit.Status: "rejected" } limitEvent ? limitEvent : null;
        var resetTime = rejectingLimit?.LimitResetsAt ?? GetWindowResetTime(result.RateLimit?.RateLimit);
        var limitName = GetLimitName(rejectingLimit?.LimitType);
        var limit = limitName is null ? "The Claude usage limit" : "The Claude " + limitName + " usage limit";
        return new AgentTurnException(
            AgentFailureKind.UsageLimit,
            resetTime is { } resetsAt
                ? limit + " is reached until " + resetsAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) + "."
                : limit + " is reached.",
            resetTime);
    }

    private static string? GetLimitName(string? limitType) => limitType switch
    {
        "five_hour" => "five-hour",
        "seven_day" or "seven_day_overage_included" => "weekly",
        "seven_day_opus" => "weekly Opus",
        "seven_day_sonnet" => "weekly Sonnet",
        _ => null
    };

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

    private static DateTimeOffset? GetWindowResetTime(AgentRateLimit? rateLimit) =>
        rateLimit?.SevenDayUtilization >= 1 ? rateLimit.SevenDayResetsAt : rateLimit?.FiveHourResetsAt;
}
