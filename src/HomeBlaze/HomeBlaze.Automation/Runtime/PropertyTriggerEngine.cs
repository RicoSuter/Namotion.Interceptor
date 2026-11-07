using System.Collections.Frozen;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The property triggers of active rules: resolves the properties they read and publishes them as the watched set to
/// <paramref name="publishWatched"/>, evaluates them against the observed values on changes, ticks and graph changes,
/// and returns the run requests of the triggers that fire. A trigger whose run did not run stays pending, and its rule
/// requests the run again only when its guard can have changed: on a change of a property the guard reads, which is
/// watched only while a trigger is pending, at the instant <see cref="GuardClock"/> derives for a guard that reads the
/// clock, every second for any other guard that reads the clock, and on a change of the trigger's own properties for a
/// rule without a guard. Rules without a pending trigger cost nothing for this. Used by the worker only.
/// </summary>
internal sealed class PropertyTriggerEngine(
    SubjectPathResolver resolver,
    TimeProvider timeProvider,
    ILogger logger,
    Func<string> getAutomationName,
    Action<FrozenSet<PropertyReference>> publishWatched)
{
    private readonly ObservedValues _values = new();
    private readonly List<CompiledPropertyTrigger> _pending = [];
    private FrozenSet<PropertyReference> _watched = FrozenSet<PropertyReference>.Empty;

    /// <summary>Activates the inactive triggers of the rules and returns the runs they request at once.</summary>
    public IReadOnlyList<RunRequest> Activate(IReadOnlyList<CompiledRule> rules, IReadOnlyList<CompiledRule> allRules)
    {
        var triggers = rules.SelectMany(rule => rule.PropertyTriggers).Where(trigger => trigger.State.Phase == TriggerPhase.Inactive).ToList();
        foreach (var trigger in triggers)
        {
            ResolveDependencies(trigger);
        }

        // Published before the values are read, so that no change between the read and the publication is lost.
        PublishWatched(allRules, triggers);
        var now = timeProvider.GetUtcNow();
        var requests = new List<RunRequest>();
        foreach (var trigger in triggers)
        {
            var wasUnknown = trigger.State.IsUnknown;
            if (trigger.State.Activate(trigger.Configuration.OnActivation, Observe(trigger, now), now))
            {
                requests.Add(CreateRequest(trigger));
            }

            LogUnknownChange(trigger, wasUnknown);
        }

        return requests;
    }

    /// <summary>
    /// Observes the change of a property that is still watched, evaluates the active triggers that read it and requests
    /// the runs of the pending triggers whose guard reads it.
    /// </summary>
    public IReadOnlyList<RunRequest> OnPropertyChanged(IReadOnlyList<CompiledRule> rules, PropertyChangedInput input)
    {
        if (!TryObserve(input))
        {
            return [];
        }

        var property = input.Property;
        var requests = ObserveWhere(rules, trigger => Array.IndexOf(trigger.Dependencies, property) >= 0, trigger => trigger.Rule.Guard is null);
        return _pending.Count == 0
            ? requests
            : AddPendingRequests(rules, requests, trigger => Array.IndexOf(trigger.Rule.GuardDependencies, property) >= 0);
    }

    public IReadOnlyList<RunRequest> OnTick(IReadOnlyList<CompiledRule> rules)
    {
        var requests = ObserveWhere(rules, trigger => trigger.UsesTime || trigger.State.Phase == TriggerPhase.Holding);
        if (_pending.Count == 0)
        {
            return requests;
        }

        var now = timeProvider.GetUtcNow();
        return AddPendingRequests(rules, requests, trigger => trigger.Rule.RechecksGuardEverySecond || trigger.Rule.GuardRecheckAt <= now);
    }

    /// <summary>
    /// Resolves the properties again, since paths may lead elsewhere now, evaluates every active trigger and requests the
    /// runs of the pending triggers.
    /// </summary>
    public IReadOnlyList<RunRequest> OnGraphChanged(IReadOnlyList<CompiledRule> rules)
    {
        Refresh(rules);
        return ObserveWhere(rules, _ => true, _ => true);
    }

    /// <summary>Requests the runs of every pending trigger, such as after a state change skipped them.</summary>
    public IReadOnlyList<RunRequest> RequestPendingRuns(IReadOnlyList<CompiledRule> rules) =>
        _pending.Count == 0 ? [] : AddPendingRequests(rules, [], _ => true);

    /// <summary>
    /// Keeps track of the pending triggers after the requests were dispatched: a trigger whose request did not run
    /// becomes pending and the properties of its guard are watched, and one that ran, turned false or whose rule was
    /// deactivated is no longer pending. Sets when the guard of a rule with a pending trigger is evaluated again for the
    /// clock. Does nothing while no trigger is pending and nothing was requested.
    /// </summary>
    public void TrackPending(IReadOnlyList<CompiledRule> rules, IReadOnlyList<RunRequest> requests)
    {
        if (_pending.Count == 0 && requests.Count == 0)
        {
            return;
        }

        var hasChanged = _pending.RemoveAll(trigger => !IsPending(trigger)) > 0;
        var now = timeProvider.GetUtcNow();
        foreach (var request in requests)
        {
            if (request.Trigger is not { } trigger || !IsPending(trigger))
            {
                continue;
            }

            if (!_pending.Contains(trigger))
            {
                ResolveGuardDependencies(trigger.Rule);
                _pending.Add(trigger);
                hasChanged = true;
            }

            ScheduleGuardRecheck(trigger.Rule, now);
        }

        if (hasChanged)
        {
            PublishWatched(rules, []);
        }
    }

    /// <summary>Observes the changes of watched properties that are newer than the observed values and returns their properties.</summary>
    public HashSet<PropertyReference> Apply(IEnumerable<PropertyChangedInput> changes)
    {
        var changed = new HashSet<PropertyReference>();
        foreach (var change in changes)
        {
            if (TryObserve(change))
            {
                changed.Add(change.Property);
            }
        }

        return changed;
    }

    /// <summary>Evaluates the active triggers that read one of the properties.</summary>
    public IReadOnlyList<RunRequest> ObserveReaders(IReadOnlyList<CompiledRule> rules, IReadOnlySet<PropertyReference> properties) =>
        properties.Count == 0 ? [] : ObserveWhere(rules, trigger => trigger.Dependencies.Any(properties.Contains));

    /// <summary>
    /// Resolves the properties of the active triggers and of the guards of their pending triggers again, such as a
    /// pending state that a Save carried over, and publishes them.
    /// </summary>
    public void Refresh(IReadOnlyList<CompiledRule> rules)
    {
        _pending.Clear();
        var now = timeProvider.GetUtcNow();
        foreach (var trigger in ActiveTriggers(rules))
        {
            ResolveDependencies(trigger);
            if (trigger.State.Phase == TriggerPhase.Pending)
            {
                ResolveGuardDependencies(trigger.Rule);
                ScheduleGuardRecheck(trigger.Rule, now);
                _pending.Add(trigger);
            }
        }

        PublishWatched(rules, []);
    }

    public static void Deactivate(CompiledRule rule)
    {
        foreach (var trigger in rule.PropertyTriggers)
        {
            trigger.State.Deactivate();
            trigger.Dependencies = [];
        }

        rule.GuardDependencies = [];
    }

    // A change queued while its property was watched may arrive after it no longer is; kept, it would hide the current
    // value from a trigger that reads the property again later.
    private bool TryObserve(PropertyChangedInput change) =>
        _watched.Contains(change.Property) && _values.TryApply(change.Property, change.Value, change.Revision);

    private static IEnumerable<CompiledPropertyTrigger> ActiveTriggers(IReadOnlyList<CompiledRule> rules) =>
        rules.Where(rule => rule.IsActivated)
            .SelectMany(rule => rule.PropertyTriggers)
            .Where(trigger => trigger.State.Phase != TriggerPhase.Inactive);

    // A trigger that stays pending requests its run again where requestPending allows it.
    private IReadOnlyList<RunRequest> ObserveWhere(
        IReadOnlyList<CompiledRule> rules,
        Func<CompiledPropertyTrigger, bool> predicate,
        Func<CompiledPropertyTrigger, bool>? requestPending = null)
    {
        var now = timeProvider.GetUtcNow();
        var requests = new List<RunRequest>();
        foreach (var trigger in ActiveTriggers(rules).Where(predicate))
        {
            var wasUnknown = trigger.State.IsUnknown;
            var wasPending = trigger.State.Phase == TriggerPhase.Pending;
            if (trigger.State.Observe(Observe(trigger, now), now) ||
                (wasPending && trigger.State.Phase == TriggerPhase.Pending && requestPending?.Invoke(trigger) == true))
            {
                requests.Add(CreateRequest(trigger));
            }

            LogUnknownChange(trigger, wasUnknown);
        }

        return requests;
    }

    // Adds the requests of the pending triggers that match and have none yet, in rule order.
    private IReadOnlyList<RunRequest> AddPendingRequests(
        IReadOnlyList<CompiledRule> rules, IReadOnlyList<RunRequest> requests, Func<CompiledPropertyTrigger, bool> predicate)
    {
        List<RunRequest>? added = null;
        foreach (var trigger in _pending)
        {
            if (IsPending(trigger) && predicate(trigger) && !requests.Any(request => ReferenceEquals(request.Trigger, trigger)))
            {
                (added ??= []).Add(CreateRequest(trigger));
            }
        }

        return added is null
            ? requests
            : [.. requests.Concat(added).OrderBy(request => AutomationTriggers.IndexOf(rules, request.Rule))];
    }

    private static bool IsPending(CompiledPropertyTrigger trigger) =>
        trigger.State.Phase == TriggerPhase.Pending && trigger.Rule.IsActivated;

    private void ResolveGuardDependencies(CompiledRule rule)
    {
        var dependencies = new List<PropertyReference>(rule.GuardPropertyPaths.Count);
        foreach (var path in rule.GuardPropertyPaths)
        {
            if (AutomationPaths.ResolveProperty(resolver, path, rule.Rule) is { } property)
            {
                dependencies.Add(property.Reference);
            }
        }

        rule.GuardDependencies = [.. dependencies];
    }

    // Derives when the guard can change with the clock alone, from the instants its comparisons read now.
    private void ScheduleGuardRecheck(CompiledRule rule, DateTimeOffset now)
    {
        rule.GuardRecheckAt = null;
        rule.RechecksGuardEverySecond = false;
        if (rule.GuardClock is not { } clock)
        {
            return;
        }

        if (clock.IsEverySecond ||
            !clock.TryGetNextChange(path => AutomationPaths.ResolveProperty(resolver, path, rule.Rule)?.GetValue(), now, out var nextChange))
        {
            rule.RechecksGuardEverySecond = true;
            return;
        }

        rule.GuardRecheckAt = nextChange;
    }

    private TriggerObservation Observe(CompiledPropertyTrigger trigger, DateTimeOffset now)
    {
        var evaluation = new AutomationEvaluation(resolver, trigger.Rule.Rule, now, timeProvider.LocalTimeZone, capturedEvent: null, _values);
        var value = evaluation.EvaluateCondition(trigger.Expression);
        IReadOnlyList<string> missingPaths = value is null ? [.. evaluation.MissingPaths] : [];
        if (trigger.Rearm is not { } rearmExpression || !ReadsRearm(trigger.State.Phase, value))
        {
            return new TriggerObservation(value, null, missingPaths);
        }

        var rearm = evaluation.EvaluateCondition(rearmExpression);
        if (rearm is null)
        {
            missingPaths = [.. missingPaths.Concat(evaluation.MissingPaths).Distinct()];
        }

        return new TriggerObservation(value, rearm, missingPaths);
    }

    // A trigger that is activated or finds its baseline while true enters the rearm wait, so it needs the rearm value at once.
    private static bool ReadsRearm(TriggerPhase phase, bool? value) =>
        phase == TriggerPhase.WaitingToRearm || (phase is TriggerPhase.Inactive or TriggerPhase.AwaitingBaseline && value == true);

    private void ResolveDependencies(CompiledPropertyTrigger trigger)
    {
        var dependencies = new List<PropertyReference>(trigger.PropertyPaths.Count);
        foreach (var path in trigger.PropertyPaths)
        {
            if (AutomationPaths.ResolveProperty(resolver, path, trigger.Rule.Rule) is { } property)
            {
                dependencies.Add(property.Reference);
            }
        }

        trigger.Dependencies = [.. dependencies];
    }

    private void PublishWatched(IReadOnlyList<CompiledRule> rules, IEnumerable<CompiledPropertyTrigger> activating)
    {
        var watched = ActiveTriggers(rules).Concat(activating).SelectMany(trigger => trigger.Dependencies)
            .Concat(_pending.SelectMany(trigger => trigger.Rule.GuardDependencies))
            .ToFrozenSet();
        var previous = _watched;
        _watched = watched;
        publishWatched(watched);

        // A value read while its property was not watched may have changed since without a queued change, so a property
        // that becomes watched is read from the graph again.
        _values.Retain(watched, previous);
    }

    private static RunRequest CreateRequest(CompiledPropertyTrigger trigger) => new(trigger.Rule, null, trigger.Description, trigger);

    private void LogUnknownChange(CompiledPropertyTrigger trigger, bool wasUnknown)
    {
        if (wasUnknown == trigger.State.IsUnknown)
        {
            return;
        }

        if (trigger.State.IsUnknown)
        {
            logger.LogInformation(
                "The {Trigger} of rule {Rule} in automation {Automation} is unknown, missing {Paths}.",
                trigger.Description, trigger.Rule.Name, getAutomationName(), string.Join(", ", trigger.State.MissingPaths));
        }
        else
        {
            logger.LogInformation(
                "The {Trigger} of rule {Rule} in automation {Automation} is known again.", trigger.Description, trigger.Rule.Name, getAutomationName());
        }
    }
}
