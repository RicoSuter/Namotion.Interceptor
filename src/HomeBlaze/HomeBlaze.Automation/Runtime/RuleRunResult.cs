namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The outcome of one run of one rule: what the rule records, whether actions ran and changed the state, and the error
/// that the status of the automation shows.
/// </summary>
internal sealed record RuleRunResult(AutomationRunOutcome Outcome, string Message, bool ExecutedActions, bool ChangedState, string? Error)
{
    /// <summary>The longest <see cref="Message"/>, which keeps it within what history stores for a text.</summary>
    public const int MaxMessageLength = 500;

    public static RuleRunResult Rejected(string message) => new(AutomationRunOutcome.RejectedByGuard, Shorten(message), false, false, null);

    public static RuleRunResult DryRun(string message) => new(AutomationRunOutcome.DryRun, Shorten(message), false, false, null);

    public static RuleRunResult Canceled(string message) => new(AutomationRunOutcome.Canceled, Shorten(message), false, false, null);

    public static string Shorten(string text) =>
        text.Length <= MaxMessageLength ? text : string.Concat(text.AsSpan(0, MaxMessageLength - 3), "...");
}
