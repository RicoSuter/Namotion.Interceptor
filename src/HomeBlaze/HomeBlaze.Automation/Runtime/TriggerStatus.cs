namespace HomeBlaze.Automation.Runtime;

/// <summary>What the status of a rule shows about one of its triggers.</summary>
internal readonly record struct TriggerStatus(
    TriggerPhase Phase,
    DateTimeOffset? HoldStartedAt,
    TimeSpan? Hold,
    DateTimeOffset? UnknownSince,
    IReadOnlyList<string> MissingPaths,
    string? Problem)
{
    public static TriggerStatus FromProperty(PropertyTriggerState state, TimeSpan? hold) =>
        new(state.Phase, state.HoldStartedAt, hold, state.UnknownSince, state.MissingPaths, null);

    /// <summary>An event trigger, armed unless <paramref name="problem"/> says why it receives no events.</summary>
    public static TriggerStatus FromEvent(string? problem) => new(TriggerPhase.Armed, null, null, null, [], problem);
}
