namespace HomeBlaze.Automation;

/// <summary>The outcome of a recorded run of a rule.</summary>
public enum AutomationRunOutcome
{
    /// <summary>Every action succeeded.</summary>
    Succeeded,

    /// <summary>An action failed and the remaining actions ran.</summary>
    CompletedWithErrors,

    /// <summary>An action failed and the remaining actions were skipped.</summary>
    StoppedAfterError,

    /// <summary>The guard was false or unknown, so no action ran.</summary>
    RejectedByGuard,

    /// <summary>The rule is a dry run: its actions were rendered and not executed.</summary>
    DryRun,

    /// <summary>The run was canceled because the automation stopped or its state changed for the same input.</summary>
    Canceled
}
