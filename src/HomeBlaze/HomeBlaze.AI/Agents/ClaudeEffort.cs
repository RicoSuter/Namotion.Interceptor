namespace HomeBlaze.AI.Agents;

/// <summary>
/// Converts <see cref="AgentEffort"/> to Claude's wire values.
/// </summary>
internal static class ClaudeEffort
{
    /// <summary>
    /// The value used by the Claude CLI <c>--effort</c> flag and the API <c>output_config.effort</c> field.
    /// </summary>
    public static string ToWireValue(AgentEffort effort) => effort switch
    {
        AgentEffort.Low => "low",
        AgentEffort.Medium => "medium",
        AgentEffort.High => "high",
        AgentEffort.ExtraHigh => "xhigh",
        AgentEffort.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, null)
    };
}
