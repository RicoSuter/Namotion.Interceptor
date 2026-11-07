namespace HomeBlaze.Automation;

/// <summary>
/// The outcome of a recorded run of a rule. A request that did not run, such as one that Only if rejected, is not a run
/// and has no outcome.
/// </summary>
public enum AutomationRunOutcome
{
    /// <summary>Every action succeeded.</summary>
    Succeeded,

    /// <summary>An action failed and the remaining actions ran.</summary>
    CompletedWithErrors,

    /// <summary>An action failed and the remaining actions were skipped.</summary>
    StoppedAfterError,

    /// <summary>The rule is a dry run: its actions were rendered and not executed.</summary>
    DryRun,

    /// <summary>The run started and was canceled before its actions completed, because the automation stopped.</summary>
    Canceled
}
