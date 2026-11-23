namespace HomeBlaze.Automation;

/// <summary>Whether a rule observes its triggers and whether its actions run.</summary>
public enum AutomationRuleMode
{
    /// <summary>Triggers are not observed and nothing runs.</summary>
    Off,

    /// <summary>Triggers are observed; a run records the actions it would execute, with their rendered arguments, and executes none.</summary>
    DryRun,

    /// <summary>Triggers are observed and the actions run.</summary>
    On
}
