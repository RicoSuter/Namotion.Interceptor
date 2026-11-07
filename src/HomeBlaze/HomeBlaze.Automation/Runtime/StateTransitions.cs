using HomeBlaze.Automation.Persistence;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Changes the state of an automation: writes the checkpoint with the new state first, and only when that succeeded sets
/// the state, its entry time and publishes the state-change event. The event of a rule's state change is held until
/// <see cref="PublishPendingChanges"/>, which the engine calls once the rules of the new state are active, so that a
/// rule active in the entered state receives it and a rule of the left state does not.
/// </summary>
internal sealed class StateTransitions(Automation automation, AutomationStateKeeper keeper, TimeProvider timeProvider, ILogger logger)
{
    private readonly List<AutomationStateChangedEvent> _pendingChanges = [];

    /// <summary>Whether <see cref="ChangeState"/> entered a state whose event is not published yet.</summary>
    public bool HasPendingChanges => _pendingChanges.Count > 0;

    /// <summary>
    /// Enters the state and holds its state-change event for <see cref="PublishPendingChanges"/>; returns why it did not,
    /// or null. Entering the current state does nothing.
    /// </summary>
    public string? ChangeState(CompiledAutomation snapshot, string targetState)
    {
        var previous = automation.CurrentState;
        if (string.Equals(previous, targetState, StringComparison.Ordinal))
        {
            return null;
        }

        if (!snapshot.HasState(targetState))
        {
            return $"'{targetState}' is not a state of the automation";
        }

        var now = timeProvider.GetUtcNow();
        if (keeper.TryPersist(AutomationStateKeeper.WithState(keeper.Persisted, targetState, now)) is { } error)
        {
            return $"The state {targetState} was not entered because it could not be saved: {error}";
        }

        Enter(targetState, now);
        _pendingChanges.Add(new AutomationStateChangedEvent { PreviousState = previous, NewState = targetState, Timestamp = now });
        return null;
    }

    /// <summary>Publishes the state-change events that <see cref="ChangeState"/> held, in the order the states were entered.</summary>
    public void PublishPendingChanges()
    {
        if (_pendingChanges.Count == 0)
        {
            return;
        }

        var changes = _pendingChanges.ToArray();
        _pendingChanges.Clear();
        foreach (var change in changes)
        {
            Publish(change);
        }
    }

    /// <summary>Enters the initial state of an automation that has states and is in none yet, without an event.</summary>
    public string? EnterInitialState(CompiledAutomation snapshot)
    {
        if (snapshot.States.Count == 0 || automation.CurrentState is not null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        if (keeper.TryPersist(AutomationStateKeeper.WithState(keeper.Persisted, snapshot.InitialState, now)) is { } error)
        {
            return $"The initial state was not entered because it could not be saved: {error}";
        }

        logger.LogInformation("The automation {Automation} entered its initial state {State}.", automation.Title, snapshot.InitialState);
        Enter(snapshot.InitialState, now);
        return null;
    }

    /// <summary>
    /// Enters the initial state with a new entry time and clears the trigger times of the rules, after the checkpoint with
    /// both was written; then runs <paramref name="restartRules"/> and publishes the state-change event when the state
    /// changed, so that the rules that start over receive that event. Returns why nothing changed, or null.
    /// </summary>
    public string? Reset(CompiledAutomation snapshot, IEnumerable<AutomationRule> rules, Action restartRules)
    {
        var previous = automation.CurrentState;
        var initialState = snapshot.States.Count > 0 ? snapshot.InitialState : null;
        var now = timeProvider.GetUtcNow();
        var next = AutomationStateKeeper.WithoutTriggerTimes(AutomationStateKeeper.WithState(keeper.Persisted, initialState, now));
        if (keeper.TryPersist(next) is { } error)
        {
            return $"The reset was not saved, so nothing changed: {error}";
        }

        foreach (var rule in rules)
        {
            rule.LastTriggeredAt = null;
        }

        logger.LogInformation("The automation {Automation} was reset to its initial state {State}.", automation.Title, initialState);
        Enter(initialState, now);
        restartRules();
        if (initialState is not null && !string.Equals(previous, initialState, StringComparison.Ordinal))
        {
            Publish(new AutomationStateChangedEvent { PreviousState = previous, NewState = initialState, Timestamp = now });
        }

        return null;
    }

    /// <summary>Gets why the current state is not valid, such as a saved state that the configuration no longer has, or null.</summary>
    public static string? CheckState(CompiledAutomation snapshot, string? currentState) =>
        snapshot.States.Count == 0 || currentState is null || snapshot.HasState(currentState)
            ? null
            : $"The saved state '{currentState}' is not a state of the automation; reset the automation to enter '{snapshot.InitialState}'";

    private void Enter(string? state, DateTimeOffset enteredAt)
    {
        automation.CurrentState = state;
        automation.StateEnteredAt = enteredAt;
    }

    private void Publish(AutomationStateChangedEvent change)
    {
        logger.LogInformation("The automation {Automation} changed from state {Previous} to {State}.", automation.Title, change.PreviousState, change.NewState);
        try
        {
            automation.PublishStateChanged(change);
        }
        catch (Exception exception)
        {
            // The state is entered and saved already, so a failing subscriber neither undoes it nor fails the rule.
            logger.LogError(exception, "A subscriber of the state changes of automation {Automation} failed.", automation.Title);
        }
    }
}
