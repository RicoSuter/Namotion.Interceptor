namespace Namotion.Interceptor;

/// <summary>
/// Reports acceptance and terminal mutation for a replay attempt, including a throwing attempt.
/// </summary>
public struct PropertyReplayOutcome
{
    /// <summary>
    /// Gets or sets whether the write reached assignment or was accepted as already equal.
    /// </summary>
    public bool Accepted { get; set; }

    /// <summary>
    /// Gets or sets whether the terminal assignment completed, even if a later callback threw.
    /// </summary>
    public bool Mutated { get; set; }
}
