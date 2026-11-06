using HomeBlaze.Abstractions.Messaging;

namespace HomeBlaze.Automation;

/// <summary>
/// Published by an <see cref="Automation"/> when it entered another state, after the state was saved. Restoring a saved
/// state after a restart publishes none.
/// </summary>
public sealed record AutomationStateChangedEvent : IEvent
{
    /// <summary>The state before, or null when the automation was in none.</summary>
    public string? PreviousState { get; init; }

    public required string NewState { get; init; }

    /// <summary>When the automation entered <see cref="NewState"/>.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}
