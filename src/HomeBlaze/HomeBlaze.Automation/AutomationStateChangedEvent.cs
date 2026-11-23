using HomeBlaze.Abstractions.Messaging;

namespace HomeBlaze.Automation;

/// <summary>
/// Published by an <see cref="Automation"/> when it entered another state, after the state was saved. Restoring a saved
/// state after a restart publishes none.
/// </summary>
/// <remarks>
/// The state changes of one input are published in order once its runs ended, when the automation is in the last of the
/// entered states already. A rule handles the event when the worker takes it, so a rule of a state that the same input
/// entered and left again does not handle the event of entering it.
/// </remarks>
public sealed record AutomationStateChangedEvent : IEvent
{
    /// <summary>The state before, or null when the automation was in none.</summary>
    public string? PreviousState { get; init; }

    /// <summary>The state the automation entered.</summary>
    public required string NewState { get; init; }

    /// <summary>When the automation entered <see cref="NewState"/>.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}
