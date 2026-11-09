using System.Diagnostics;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The arming, hold and unknown state of one property trigger. A trigger fires on a change to true that held for the
/// hold and stays pending until its run runs, which <see cref="Consume"/> records, or until the expression is false or
/// unknown, which arms it again without a run. After its run it rearms when its expression is known false or, with a
/// rearm expression, when that is known true; a rearm while the expression is still true fires only after the
/// expression was false or unknown again. A trigger that starts true waits for the same rearm. Unknown never fires and
/// never rearms, and resets a running hold. Used by the worker only.
/// </summary>
internal sealed class PropertyTriggerState(TimeSpan? hold, bool hasRearm)
{
    public TriggerPhase Phase { get; private set; }

    /// <summary>When the running hold started on the wall clock, for display, or null without one.</summary>
    public DateTimeOffset? HoldStartedAt { get; private set; }

    /// <summary>When the running hold started on the monotonic clock of <see cref="TriggerTime.Elapsed"/>, which measures it.</summary>
    public TimeSpan HoldStartedElapsed { get; private set; }

    /// <summary>When the trigger fired while it is <see cref="TriggerPhase.Pending"/>, or null.</summary>
    public DateTimeOffset? PendingSince { get; private set; }

    /// <summary>When the expression became unknown, or null while it is known.</summary>
    public DateTimeOffset? UnknownSince { get; private set; }

    /// <summary>The paths that led to no value while the expression is unknown.</summary>
    public IReadOnlyList<string> MissingPaths { get; private set; } = [];

    public bool IsUnknown => UnknownSince is not null;

    /// <summary>Increases with every change of the state, so that a status is formatted again only after one.</summary>
    public int Version { get; private set; }

    /// <summary>Activates the trigger with fresh state and returns whether it fires at once.</summary>
    public bool Activate(TriggerActivation policy, TriggerObservation observation, TriggerTime time)
    {
        Deactivate();
        if (policy == TriggerActivation.WaitForFalseToTrue)
        {
            Phase = GetBaselinePhase(observation.Value);
            UpdateUnknown(observation, time.Now);
            return false;
        }

        Phase = TriggerPhase.Armed;
        UpdateUnknown(observation, time.Now);
        return ObserveArmed(observation.Value, time);
    }

    /// <summary>Applies an observation and returns whether the trigger fires.</summary>
    public bool Observe(TriggerObservation observation, TriggerTime time)
    {
        var phase = Phase;
        var holdStartedAt = HoldStartedAt;
        var unknownSince = UnknownSince;
        var missingPaths = MissingPaths;
        var fires = ObserveCore(observation, time);
        if (phase != Phase || holdStartedAt != HoldStartedAt || unknownSince != UnknownSince || !ReferenceEquals(missingPaths, MissingPaths))
        {
            Version++;
        }

        return fires;
    }

    private bool ObserveCore(TriggerObservation observation, TriggerTime time)
    {
        if (Phase == TriggerPhase.Inactive)
        {
            return false;
        }

        UpdateUnknown(observation, time.Now);
        return Phase switch
        {
            TriggerPhase.AwaitingBaseline => SetBaseline(observation, time.Now),
            TriggerPhase.Armed => ObserveArmed(observation.Value, time),
            TriggerPhase.Holding => ObserveHolding(observation.Value, time),
            TriggerPhase.Pending => ObservePending(observation.Value),
            TriggerPhase.WaitingToRearm => ObserveWaitingToRearm(observation),
            TriggerPhase.WaitingForFalse => ObserveWaitingForFalse(observation.Value),
            TriggerPhase.RearmedWhileTrue => ObserveRearmedWhileTrue(observation.Value),
            _ => throw new UnreachableException()
        };
    }

    /// <summary>Records that the run of a pending trigger ran, so that the trigger waits to rearm.</summary>
    public void Consume()
    {
        if (Phase == TriggerPhase.Pending)
        {
            Phase = TriggerPhase.WaitingToRearm;
            PendingSince = null;
            Version++;
        }
    }

    public void Deactivate()
    {
        Version++;
        Phase = TriggerPhase.Inactive;
        HoldStartedAt = null;
        PendingSince = null;
        UnknownSince = null;
        MissingPaths = [];
    }

    private TriggerPhase GetBaselinePhase(bool? value) => value switch
    {
        true => hasRearm ? TriggerPhase.WaitingToRearm : TriggerPhase.WaitingForFalse,
        false => TriggerPhase.Armed,
        null => TriggerPhase.AwaitingBaseline
    };

    private bool SetBaseline(TriggerObservation observation, DateTimeOffset now)
    {
        Phase = GetBaselinePhase(observation.Value);
        UpdateUnknown(observation, now);
        return false;
    }

    /// <summary>When the running hold ends on the wall clock as it reads at <paramref name="time"/>, or null without a running hold.</summary>
    public DateTimeOffset? GetHoldEnd(TriggerTime time)
    {
        if (Phase != TriggerPhase.Holding)
        {
            return null;
        }

        var remaining = hold.GetValueOrDefault() - (time.Elapsed - HoldStartedElapsed);
        return remaining >= DateTimeOffset.MaxValue - time.Now ? DateTimeOffset.MaxValue : time.Now + remaining;
    }

    private bool ObserveArmed(bool? value, TriggerTime time)
    {
        if (value != true)
        {
            return false;
        }

        if (hold is not { } duration || duration <= TimeSpan.Zero)
        {
            return Fire(time.Now);
        }

        Phase = TriggerPhase.Holding;
        HoldStartedAt = time.Now;
        HoldStartedElapsed = time.Elapsed;
        return false;
    }

    private bool ObserveHolding(bool? value, TriggerTime time)
    {
        if (value != true)
        {
            Phase = TriggerPhase.Armed;
            HoldStartedAt = null;
            return false;
        }

        if (time.Elapsed - HoldStartedElapsed < hold.GetValueOrDefault())
        {
            return false;
        }

        return Fire(time.Now);
    }

    // A pending trigger keeps waiting while the expression is true, and a value that is not true ends the wait without a
    // run. Its run is requested again by Only if, not by the expression, so it fires no second time here.
    private bool ObservePending(bool? value)
    {
        if (value != true)
        {
            Phase = TriggerPhase.Armed;
            PendingSince = null;
        }

        return false;
    }

    private bool ObserveWaitingToRearm(TriggerObservation observation)
    {
        var isRearmed = hasRearm ? observation.Rearm == true : observation.Value == false;
        if (!isRearmed)
        {
            return false;
        }

        Phase = observation.Value == true ? TriggerPhase.RearmedWhileTrue : TriggerPhase.Armed;
        return false;
    }

    private bool ObserveWaitingForFalse(bool? value)
    {
        if (value == false)
        {
            Phase = TriggerPhase.Armed;
        }

        return false;
    }

    private bool ObserveRearmedWhileTrue(bool? value)
    {
        if (value != true)
        {
            Phase = TriggerPhase.Armed;
        }

        return false;
    }

    private bool Fire(DateTimeOffset now)
    {
        Phase = TriggerPhase.Pending;
        PendingSince = now;
        HoldStartedAt = null;
        return true;
    }

    private void UpdateUnknown(TriggerObservation observation, DateTimeOffset now)
    {
        var isRearmUnknown = Phase == TriggerPhase.WaitingToRearm && hasRearm && observation.Rearm is null;
        if (observation.Value is null || isRearmUnknown)
        {
            UnknownSince ??= now;

            // Every evaluation lists the paths anew, so equal paths keep the list and the version stays.
            if (!AreEqual(MissingPaths, observation.MissingPaths))
            {
                MissingPaths = observation.MissingPaths;
            }

            return;
        }

        UnknownSince = null;
        MissingPaths = [];
    }

    private static bool AreEqual(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        for (var index = 0; index < first.Count; index++)
        {
            if (!string.Equals(first[index], second[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
