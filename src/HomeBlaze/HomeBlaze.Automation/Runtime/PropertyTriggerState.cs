using System.Diagnostics;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The arming, hold and unknown state of one property trigger. A trigger fires on a change to true that held for the
/// hold, once, and rearms when its expression is known false or, with a rearm expression, when that is known true.
/// Unknown never fires and never rearms, and resets a running hold. Used by the worker only.
/// </summary>
internal sealed class PropertyTriggerState(TimeSpan? hold, bool hasRearm)
{
    public TriggerPhase Phase { get; private set; }

    /// <summary>When the running hold started, or null without one.</summary>
    public DateTimeOffset? HoldStartedAt { get; private set; }

    /// <summary>When the expression became unknown, or null while it is known.</summary>
    public DateTimeOffset? UnknownSince { get; private set; }

    /// <summary>The paths that led to no value while the expression is unknown.</summary>
    public IReadOnlyList<string> MissingPaths { get; private set; } = [];

    public bool IsUnknown => UnknownSince is not null;

    /// <summary>Activates the trigger with fresh state and returns whether it fires at once.</summary>
    public bool Activate(TriggerActivation policy, TriggerObservation observation, DateTimeOffset now)
    {
        Deactivate();
        UpdateUnknown(observation, now);
        if (policy == TriggerActivation.WaitForFalseToTrue)
        {
            Phase = GetBaselinePhase(observation.Value);
            return false;
        }

        Phase = TriggerPhase.Armed;
        return ObserveArmed(observation.Value, now);
    }

    /// <summary>Applies an observation and returns whether the trigger fires.</summary>
    public bool Observe(TriggerObservation observation, DateTimeOffset now)
    {
        if (Phase == TriggerPhase.Inactive)
        {
            return false;
        }

        UpdateUnknown(observation, now);
        return Phase switch
        {
            TriggerPhase.AwaitingBaseline => SetBaseline(observation.Value),
            TriggerPhase.Armed => ObserveArmed(observation.Value, now),
            TriggerPhase.Holding => ObserveHolding(observation.Value, now),
            TriggerPhase.WaitingToRearm => ObserveWaitingToRearm(observation, now),
            TriggerPhase.WaitingForFalse => ObserveWaitingForFalse(observation.Value),
            _ => throw new UnreachableException()
        };
    }

    public void Deactivate()
    {
        Phase = TriggerPhase.Inactive;
        HoldStartedAt = null;
        UnknownSince = null;
        MissingPaths = [];
    }

    private static TriggerPhase GetBaselinePhase(bool? value) => value switch
    {
        true => TriggerPhase.WaitingForFalse,
        false => TriggerPhase.Armed,
        null => TriggerPhase.AwaitingBaseline
    };

    private bool SetBaseline(bool? value)
    {
        Phase = GetBaselinePhase(value);
        return false;
    }

    private bool ObserveArmed(bool? value, DateTimeOffset now)
    {
        if (value != true)
        {
            return false;
        }

        if (hold is not { } duration || duration <= TimeSpan.Zero)
        {
            return Fire();
        }

        Phase = TriggerPhase.Holding;
        HoldStartedAt = now;
        return false;
    }

    private bool ObserveHolding(bool? value, DateTimeOffset now)
    {
        if (value != true)
        {
            Phase = TriggerPhase.Armed;
            HoldStartedAt = null;
            return false;
        }

        if (now - HoldStartedAt!.Value < hold.GetValueOrDefault())
        {
            return false;
        }

        return Fire();
    }

    private bool ObserveWaitingToRearm(TriggerObservation observation, DateTimeOffset now)
    {
        var isRearmed = hasRearm ? observation.Rearm == true : observation.Value == false;
        if (!isRearmed)
        {
            return false;
        }

        Phase = TriggerPhase.Armed;
        return ObserveArmed(observation.Value, now);
    }

    private bool ObserveWaitingForFalse(bool? value)
    {
        if (value == false)
        {
            Phase = TriggerPhase.Armed;
        }

        return false;
    }

    private bool Fire()
    {
        Phase = TriggerPhase.WaitingToRearm;
        HoldStartedAt = null;
        return true;
    }

    private void UpdateUnknown(TriggerObservation observation, DateTimeOffset now)
    {
        var isRearmUnknown = Phase == TriggerPhase.WaitingToRearm && hasRearm && observation.Rearm is null;
        if (observation.Value is null || isRearmUnknown)
        {
            UnknownSince ??= now;
            MissingPaths = observation.MissingPaths;
            return;
        }

        UnknownSince = null;
        MissingPaths = [];
    }
}
