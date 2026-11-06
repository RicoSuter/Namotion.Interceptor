using HomeBlaze.Automation.Persistence;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Changes the state of an automation: writes the checkpoint with the new state first, and only when that succeeded sets
/// the state, its entry time and publishes the state-change event.
/// </summary>
internal sealed class StateTransitions(Automation automation, AutomationStateKeeper keeper, TimeProvider timeProvider, ILogger logger)
{
    /// <summary>Enters the state; returns why it did not, or null. Entering the current state does nothing.</summary>
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
        Publish(previous, targetState, now);
        return null;
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
            Publish(previous, initialState, now);
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

    private void Publish(string? previous, string state, DateTimeOffset enteredAt)
    {
        logger.LogInformation("The automation {Automation} changed from state {Previous} to {State}.", automation.Title, previous, state);
        try
        {
            automation.PublishStateChanged(new AutomationStateChangedEvent { PreviousState = previous, NewState = state, Timestamp = enteredAt });
        }
        catch (Exception exception)
        {
            // The state is entered and saved already, so a failing subscriber neither undoes it nor fails the rule.
            logger.LogError(exception, "A subscriber of the state changes of automation {Automation} failed.", automation.Title);
        }
    }
}
