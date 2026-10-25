namespace HomeBlaze.AI.Agents;

/// <summary>
/// The triggers of a background run, as stored in <see cref="AgentTimelineEntry.Trigger"/>.
/// </summary>
public static class AgentTriggers
{
    /// <summary>The poll interval elapsed.</summary>
    public const string Poll = "poll";

    /// <summary>A watched property changed.</summary>
    public const string Change = "change";

    /// <summary>The operator started the run.</summary>
    public const string Manual = "manual";
}
