namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The outcome of one request of one rule: what the rule records for a run, whether actions ran and changed the state,
/// and the error that the status of the automation shows. A request that did not run, such as one that Only if rejected,
/// has no <see cref="Outcome"/> and says why in <see cref="Message"/>.
/// </summary>
internal sealed record RuleRunResult(AutomationRunOutcome? Outcome, string Message, bool ExecutedActions, bool ChangedState, string? Error)
{
    /// <summary>The longest <see cref="Message"/>, which keeps it within what history stores for a text.</summary>
    public const int MaxMessageLength = 500;

    /// <summary>Whether the request ran: it executed its actions, was canceled while it did, or is a dry run.</summary>
    public bool IsRun => Outcome is not null;

    public static RuleRunResult NotRun(string reason) => new(null, Shorten(reason), false, false, null);

    public static RuleRunResult DryRun(string message) => new(AutomationRunOutcome.DryRun, Shorten(message), false, false, null);

    public static RuleRunResult Canceled(string message) => new(AutomationRunOutcome.Canceled, Shorten(message), false, false, null);

    public static string Shorten(string text) =>
        text.Length <= MaxMessageLength ? text : string.Concat(text.AsSpan(0, MaxMessageLength - 3), "...");
}
