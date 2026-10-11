namespace HomeBlaze.AI.Blazor;

/// <summary>
/// Operator-facing labels for the trigger of a background run.
/// </summary>
public static class AgentTriggerLabels
{
    /// <summary>Returns the label of a trigger, the trigger itself when it is unknown, or empty for none.</summary>
    public static string Format(string? trigger) => trigger switch
    {
        "poll" => "Poll interval",
        "change" => "Watched change",
        "manual" => "Run now",
        null => string.Empty,
        _ => trigger
    };
}
