using HomeBlaze.AI.Agents;

namespace HomeBlaze.AI.Blazor;

/// <summary>
/// Operator-facing labels for the trigger of a background run.
/// </summary>
internal static class AgentTriggerLabels
{
    /// <summary>Returns the label of a trigger, the trigger itself when it is unknown, or empty for none.</summary>
    public static string Format(string? trigger) => trigger switch
    {
        AgentTriggers.Poll => "Poll interval",
        AgentTriggers.Change => "Watched change",
        AgentTriggers.Manual => "Run now",
        null => string.Empty,
        _ => trigger
    };
}
