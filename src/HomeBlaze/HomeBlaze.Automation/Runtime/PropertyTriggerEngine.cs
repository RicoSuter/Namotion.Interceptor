using System.Collections.Frozen;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The property triggers of active rules: resolves the properties they read and publishes them as the watched set to
/// <paramref name="publishWatched"/>, evaluates them against the observed values on changes, graph changes and ticks,
/// and returns the run requests of the triggers that fire. A tick evaluates only the triggers that are due: a hold that
/// ends, or a trigger whose expressions can change with the clock at the instant <see cref="ExpressionClock"/> derives,
/// every second only for a read of the clock that it cannot derive; <see cref="GetNextDueAt"/> tells when the next one
/// is due. A trigger whose run did not run stays pending, and its rule requests the run again only when its Only if can
/// have changed: on a change of a property Only if reads, which is watched only while a trigger is pending, at the
/// instant derived for an Only if that reads the clock, and on a change of the trigger's own properties for a rule
/// without Only if. Rules without a pending trigger cost nothing for this. Used by the worker only.
/// </summary>
internal sealed class PropertyTriggerEngine(
    SubjectPathResolver resolver,
    TimeProvider timeProvider,
    ILogger logger,
    Func<string> getAutomationName,
    Action<FrozenSet<PropertyReference>> publishWatched,
    GraphRoutes routes)
{
    private readonly ObservedValues _values = new();
    private readonly long _startedAt = timeProvider.GetTimestamp();
    private readonly List<CompiledPropertyTrigger> _pending = [];

    // The latest instant from which an instant when a trigger or Only if is due was derived; a later tick before it means
    // that the clock was set back.
    private DateTimeOffset _derivedAt = DateTimeOffset.MinValue;

    // The triggers whose properties, and the rules whose Only if properties, a refresh resolved differently since the last
    // graph change, which evaluates them; a refresh for an activation evaluates only the triggers it activates.
    private readonly HashSet<CompiledPropertyTrigger> _rebound = [];
    private readonly HashSet<CompiledRule> _reboundOnlyIfRules = [];
    private FrozenSet<PropertyReference> _watched = FrozenSet<PropertyReference>.Empty;

    // Reused by every publication of the watched set, which is frozen only when it changed.
    private readonly HashSet<PropertyReference> _nextWatched = [];

    /// <summary>
    /// Activates the inactive triggers of the rules that are still activated and among <paramref name="allRules"/>, such
    /// as rules whose activation was queued, and returns the runs they request at once.
    /// </summary>
    public IReadOnlyList<RunRequest> Activate(IReadOnlyList<CompiledRule> rules, IReadOnlyList<CompiledRule> allRules)
    {
        var triggers = rules.Where(rule => rule.IsActivated && allRules.Contains(rule)).Distinct()
            .SelectMany(rule => rule.PropertyTriggers).Where(trigger => trigger.State.Phase == TriggerPhase.Inactive).ToList();
        foreach (var trigger in triggers)
        {
            ResolveDependencies(trigger);
        }

        // Published before the values are read, so that no change between the read and the publication is lost.
        PublishWatched(allRules, triggers);
        var time = GetTime();
        var requests = new List<RunRequest>();
        foreach (var trigger in triggers)
        {
            var wasUnknown = trigger.State.IsUnknown;
            if (trigger.State.Activate(trigger.Configuration.OnActivation, Observe(trigger, time.Now), time))
            {
                requests.Add(CreateRequest(trigger));
            }

            ScheduleCheck(trigger, time);
            LogUnknownChange(trigger, wasUnknown);
        }

        return requests;
    }

    /// <summary>
    /// Observes the change of a property that is still watched, evaluates the active triggers that read it and requests
    /// the runs of the pending triggers whose Only if reads it.
    /// </summary>
    public IReadOnlyList<RunRequest> OnPropertyChanged(IReadOnlyList<CompiledRule> rules, PropertyChangedInput input)
    {
        if (!TryObserve(input))
        {
            return [];
        }

        var property = input.Property;
        var requests = ObserveWhere(
            rules, property, static (trigger, property) => Array.IndexOf(trigger.Dependencies, property) >= 0, static trigger => trigger.Rule.OnlyIf is null);
        return _pending.Count == 0
            ? requests
            : AddPendingRequests(rules, requests, property, static (trigger, property) => Array.IndexOf(trigger.Rule.OnlyIfDependencies, property) >= 0);
    }

    /// <summary>
    /// Evaluates the triggers that are due and requests the runs of the pending triggers whose Only if is due; after the
    /// clock was set back, every trigger and Only if that reads the clock, since the instants derived before have moved.
    /// </summary>
    public IReadOnlyList<RunRequest> OnTick(IReadOnlyList<CompiledRule> rules)
    {
        var now = timeProvider.GetUtcNow();
        var isClockSetBack = now < _derivedAt;
        if (isClockSetBack)
        {
            _derivedAt = now;
        }

        // After the clock was set back, every instant derived before is due.
        var dueBy = isClockSetBack ? DateTimeOffset.MaxValue : now;
        var requests = ObserveWhere(rules, dueBy, static (trigger, dueBy) => trigger.NextCheckAt <= dueBy);
        if (_pending.Count == 0)
        {
            return requests;
        }

        return AddPendingRequests(
            rules, requests, dueBy, static (trigger, dueBy) => trigger.Rule.RechecksOnlyIfEverySecond || trigger.Rule.OnlyIfRecheckAt <= dueBy);
    }

    /// <summary>
    /// Gets when the next trigger or Only if of a pending trigger is due, or null when none is, such as for rules that
    /// neither hold nor read the clock. Allocates nothing.
    /// </summary>
    public DateTimeOffset? GetNextDueAt(IReadOnlyList<CompiledRule> rules, DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
        {
            var rule = rules[ruleIndex];
            if (!rule.IsActivated)
            {
                continue;
            }

            foreach (var trigger in rule.PropertyTriggers)
            {
                next = AutomationTriggers.Earliest(next, trigger.NextCheckAt);
            }
        }

        foreach (var trigger in _pending)
        {
            next = AutomationTriggers.Earliest(next, trigger.Rule.RechecksOnlyIfEverySecond ? ExpressionClock.GetNextSecond(now) : trigger.Rule.OnlyIfRecheckAt);
        }

        return next;
    }

    /// <summary>
    /// Resolves the properties again, since paths may lead elsewhere now, and evaluates the triggers whose properties
    /// changed, or every active trigger with <paramref name="evaluateAll"/>; requests the runs of the pending triggers
    /// whose properties or Only if properties changed, or of every pending trigger with <paramref name="evaluateAll"/>.
    /// </summary>
    public IReadOnlyList<RunRequest> OnGraphChanged(IReadOnlyList<CompiledRule> rules, bool evaluateAll)
    {
        Refresh(rules);
        try
        {
            if (evaluateAll)
            {
                return ObserveWhere(rules, 0, static (_, _) => true, static _ => true);
            }

            var requests = _rebound.Count == 0 ? [] : ObserveWhere(rules, _rebound, static (trigger, rebound) => rebound.Contains(trigger), static _ => true);
            return _reboundOnlyIfRules.Count == 0 || _pending.Count == 0
                ? requests
                : AddPendingRequests(rules, requests, _reboundOnlyIfRules, static (trigger, reboundRules) => reboundRules.Contains(trigger.Rule));
        }
        finally
        {
            ForgetRebound();
        }
    }

    /// <summary>
    /// Forgets the triggers that a refresh resolved differently, for a graph change that leaves the structure as it was,
    /// so that triggers of replaced rules are not kept.
    /// </summary>
    public void ForgetRebound()
    {
        _rebound.Clear();
        _reboundOnlyIfRules.Clear();
        routes.AwaitsGraphChange = false;
    }

    /// <summary>Requests the runs of every pending trigger, such as after a state change skipped them.</summary>
    public IReadOnlyList<RunRequest> RequestPendingRuns(IReadOnlyList<CompiledRule> rules) =>
        _pending.Count == 0 ? [] : AddPendingRequests(rules, [], 0, static (_, _) => true);

    /// <summary>Whether a trigger is pending.</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>
    /// Keeps track of the pending triggers after the requests were dispatched: a trigger whose request did not run
    /// becomes pending and the properties of its Only if are watched, and one that ran, turned false or whose rule was
    /// deactivated is no longer pending. Sets when Only if of a rule with a pending trigger is evaluated again for the
    /// clock. Does nothing while no trigger is pending and nothing was requested. Returns whether a trigger became
    /// pending, whose Only if may have changed before its properties were watched.
    /// </summary>
    public bool TrackPending(IReadOnlyList<CompiledRule> rules, IReadOnlyList<RunRequest> requests)
    {
        if (_pending.Count == 0 && requests.Count == 0)
        {
            return false;
        }

        var hasRemoved = RemoveEndedPending(rules);
        var hasAdded = false;
        var now = timeProvider.GetUtcNow();
        foreach (var request in requests)
        {
            if (request.Trigger is not { } trigger || !IsPending(trigger))
            {
                continue;
            }

            if (!_pending.Contains(trigger))
            {
                ResolveOnlyIfDependencies(trigger.Rule);
                _pending.Add(trigger);
                hasAdded = true;
            }

            ScheduleOnlyIfRecheck(trigger.Rule, now);
        }

        if (hasRemoved || hasAdded)
        {
            PublishWatched(rules, []);
        }

        return hasAdded;
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
        properties.Count == 0 ? [] : ObserveWhere(rules, properties, static (trigger, properties) => ReadsAny(trigger, properties));

    private static bool ReadsAny(CompiledPropertyTrigger trigger, IReadOnlySet<PropertyReference> properties)
    {
        foreach (var dependency in trigger.Dependencies)
        {
            if (properties.Contains(dependency))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the properties of the active triggers and of Only if of the rules of their pending triggers again, tracks
    /// the pending triggers it does not track yet, such as a pending state that a Save carried over, and publishes the
    /// properties. A trigger or Only if whose properties resolve as before keeps when it is due and its place among the
    /// pending triggers; one whose properties changed and that reads the clock is due at once, since the instants derived
    /// before read the properties it was bound to. Returns whether it began to track a pending trigger, whose Only if may
    /// have changed before its properties were watched.
    /// </summary>
    public bool Refresh(IReadOnlyList<CompiledRule> rules)
    {
        RemoveEndedPending(rules);
        DateTimeOffset? now = null;
        var trackedCount = _pending.Count;
        foreach (var rule in rules)
        {
            if (!rule.IsActivated)
            {
                continue;
            }

            var isTracked = IsTracked(rule);
            if (RefreshTriggers(rule, ref now))
            {
                RefreshOnlyIf(rule, isTracked, ref now);
            }
        }

        // The next graph change evaluates them, whatever it changed.
        routes.AwaitsGraphChange = _rebound.Count > 0 || _reboundOnlyIfRules.Count > 0;
        PublishWatched(rules, []);
        return _pending.Count > trackedCount;
    }

    // Returns whether a trigger of the rule is pending.
    private bool RefreshTriggers(CompiledRule rule, ref DateTimeOffset? now)
    {
        var hasPending = false;
        foreach (var trigger in rule.PropertyTriggers)
        {
            var phase = trigger.State.Phase;
            if (phase == TriggerPhase.Inactive)
            {
                continue;
            }

            if (ResolveDependencies(trigger))
            {
                OnRebound(trigger, ref now);
            }

            if (phase == TriggerPhase.Pending)
            {
                hasPending = true;
                if (!_pending.Contains(trigger))
                {
                    _pending.Add(trigger);
                }
            }
        }

        return hasPending;
    }

    // The instants derived for the clock read the properties the trigger was bound to before.
    private void OnRebound(CompiledPropertyTrigger trigger, ref DateTimeOffset? now)
    {
        _rebound.Add(trigger);
        if (trigger.Clock is not null)
        {
            trigger.NextCheckAt = AutomationTriggers.Earliest(trigger.NextCheckAt, now ??= timeProvider.GetUtcNow());
        }
    }

    // Derives when Only if of a rule that was not tracked as pending is due; the instants derived for a tracked rule stay
    // unless its properties changed.
    private void RefreshOnlyIf(CompiledRule rule, bool isTracked, ref DateTimeOffset? now)
    {
        var isRebound = ResolveOnlyIfDependencies(rule);
        if (isRebound)
        {
            _reboundOnlyIfRules.Add(rule);
        }

        if (!isTracked)
        {
            ScheduleOnlyIfRecheck(rule, now ??= timeProvider.GetUtcNow());
        }
        else if (isRebound && rule.OnlyIfClock is { IsEverySecond: false })
        {
            rule.OnlyIfRecheckAt = AutomationTriggers.Earliest(rule.OnlyIfRecheckAt, now ??= timeProvider.GetUtcNow());
        }
    }

    // Whether a trigger of the rule is tracked as pending, so that when its Only if is due is derived already.
    private bool IsTracked(CompiledRule rule)
    {
        foreach (var trigger in _pending)
        {
            if (ReferenceEquals(trigger.Rule, rule))
            {
                return true;
            }
        }

        return false;
    }

    // Returns whether a trigger was no longer pending. Only if of a rule without a tracked pending trigger is resolved
    // again when one becomes tracked.
    private bool RemoveEndedPending(IReadOnlyList<CompiledRule> rules)
    {
        if (_pending.RemoveAll(static trigger => !IsPending(trigger)) == 0)
        {
            return false;
        }

        foreach (var rule in rules)
        {
            if (!rule.OnlyIfRoute.IsEmpty && !IsTracked(rule))
            {
                rule.OnlyIfRoute.Clear();
            }
        }

        return true;
    }

    public static void Deactivate(CompiledRule rule)
    {
        foreach (var trigger in rule.PropertyTriggers)
        {
            trigger.State.Deactivate();
            trigger.Dependencies = [];
            trigger.DependencyRoute.Clear();
            trigger.NextCheckAt = null;
        }

        rule.OnlyIfDependencies = [];
        rule.OnlyIfRoute.Clear();
    }

    // A change queued while its property was watched may arrive after it no longer is; kept, it would hide the current
    // value from a trigger that reads the property again later.
    private bool TryObserve(PropertyChangedInput change) =>
        _watched.Contains(change.Property) && _values.TryApply(change.Property, change.Value, change.Revision);

    // A trigger that stays pending requests its run again where requestPending allows it. Allocates nothing when no
    // trigger matches, as long as the callers pass static lambdas with the state they read.
    private IReadOnlyList<RunRequest> ObserveWhere<TState>(
        IReadOnlyList<CompiledRule> rules,
        TState state,
        Func<CompiledPropertyTrigger, TState, bool> predicate,
        Func<CompiledPropertyTrigger, bool>? requestPending = null)
    {
        List<RunRequest>? requests = null;
        TriggerTime? time = null;
        for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
        {
            var rule = rules[ruleIndex];
            if (!rule.IsActivated)
            {
                continue;
            }

            foreach (var trigger in rule.PropertyTriggers)
            {
                if (trigger.State.Phase != TriggerPhase.Inactive && predicate(trigger, state) &&
                    ObserveOne(trigger, time ??= GetTime(), requestPending) is { } request)
                {
                    (requests ??= []).Add(request);
                }
            }
        }

        return requests ?? (IReadOnlyList<RunRequest>)[];
    }

    private RunRequest? ObserveOne(CompiledPropertyTrigger trigger, TriggerTime time, Func<CompiledPropertyTrigger, bool>? requestPending)
    {
        var wasUnknown = trigger.State.IsUnknown;
        var wasPending = trigger.State.Phase == TriggerPhase.Pending;
        RunRequest? request = null;
        if (trigger.State.Observe(Observe(trigger, time.Now), time) ||
            (wasPending && trigger.State.Phase == TriggerPhase.Pending && requestPending?.Invoke(trigger) == true))
        {
            request = CreateRequest(trigger);
        }
        else if (wasPending && trigger.State.Phase != TriggerPhase.Pending)
        {
            EndWait(trigger.Rule);
        }

        ScheduleCheck(trigger, time);
        LogUnknownChange(trigger, wasUnknown);
        return request;
    }

    // Due when the hold ends or the expressions can change with the clock, whichever comes first; a read of the clock
    // that cannot be derived is due at the next second.
    private void ScheduleCheck(CompiledPropertyTrigger trigger, TriggerTime time)
    {
        var state = trigger.State;
        var now = time.Now;
        var next = state.GetHoldEnd(time);
        if (state.Phase != TriggerPhase.Inactive && trigger.Clock is { } clock)
        {
            var rule = trigger.Rule.Rule;
            if (clock.IsEverySecond ||
                !clock.TryGetNextChange(path => ReadObserved(path, rule), now, timeProvider.LocalTimeZone, out var change, out var hasChanged))
            {
                change = ExpressionClock.GetNextSecond(now);
            }
            else if (change is null && hasChanged)
            {
                change = CheckForSetBackClock(now);
            }

            next = AutomationTriggers.Earliest(next, change);
        }

        trigger.NextCheckAt = next;
        _derivedAt = Max(_derivedAt, now);
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;

    private TriggerTime GetTime() => new(timeProvider.GetUtcNow(), timeProvider.GetElapsedTime(_startedAt));

    // A comparison of elapsed time that changed already changes again only when the wall clock is set back, which a tick
    // notices; checked as often as the worker wakes up at most.
    private static DateTimeOffset CheckForSetBackClock(DateTimeOffset now) => now + WakeTimer.MaxWakeInterval;

    private object? ReadObserved(string path, AutomationRule rule) =>
        AutomationPaths.ResolveProperty(resolver, path, rule) is { } property ? _values.GetValue(property) : null;

    // Adds the requests of the pending triggers that match and have none yet, in rule order.
    private IReadOnlyList<RunRequest> AddPendingRequests<TState>(
        IReadOnlyList<CompiledRule> rules, IReadOnlyList<RunRequest> requests, TState state, Func<CompiledPropertyTrigger, TState, bool> predicate)
    {
        List<RunRequest>? added = null;
        foreach (var trigger in _pending)
        {
            if (IsPending(trigger) && predicate(trigger, state) && !HasRequest(requests, trigger))
            {
                (added ??= []).Add(CreateRequest(trigger));
            }
        }

        return added is null
            ? requests
            : [.. requests.Concat(added).OrderBy(request => AutomationTriggers.IndexOf(rules, request.Rule))];
    }

    private static bool HasRequest(IReadOnlyList<RunRequest> requests, CompiledPropertyTrigger trigger)
    {
        for (var index = 0; index < requests.Count; index++)
        {
            if (ReferenceEquals(requests[index].Trigger, trigger))
            {
                return true;
            }
        }

        return false;
    }

    // The wait ended without a run, so the reason it did not run no longer applies and a new wait is logged again.
    private static void EndWait(CompiledRule rule)
    {
        if (!rule.PropertyTriggers.Exists(trigger => trigger.State.Phase == TriggerPhase.Pending))
        {
            rule.SkipReason = null;
        }
    }

    private static bool IsPending(CompiledPropertyTrigger trigger) =>
        trigger.State.Phase == TriggerPhase.Pending && trigger.Rule.IsActivated;

    // Returns whether the properties differ from those resolved before.
    private bool ResolveOnlyIfDependencies(CompiledRule rule)
    {
        var previous = rule.OnlyIfDependencies;
        rule.OnlyIfDependencies = Resolve(rule.OnlyIfPropertyPaths, rule.Rule, rule.OnlyIfRoute);
        return !rule.OnlyIfDependencies.AsSpan().SequenceEqual(previous);
    }

    // Derives when Only if can change with the clock alone, from the instants its comparisons read now.
    private void ScheduleOnlyIfRecheck(CompiledRule rule, DateTimeOffset now)
    {
        rule.OnlyIfRecheckAt = null;
        rule.RechecksOnlyIfEverySecond = false;
        _derivedAt = Max(_derivedAt, now);
        if (rule.OnlyIfClock is not { } clock)
        {
            return;
        }

        if (clock.IsEverySecond ||
            !clock.TryGetNextChange(
                path => AutomationPaths.ResolveProperty(resolver, path, rule.Rule)?.GetValue(), now, timeProvider.LocalTimeZone, out var nextChange, out var hasChanged))
        {
            rule.RechecksOnlyIfEverySecond = true;
            return;
        }

        rule.OnlyIfRecheckAt = nextChange ?? (hasChanged ? CheckForSetBackClock(now) : null);
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

    // Returns whether the properties differ from those resolved before.
    private bool ResolveDependencies(CompiledPropertyTrigger trigger)
    {
        var previous = trigger.Dependencies;
        trigger.Dependencies = Resolve(trigger.PropertyPaths, trigger.Rule.Rule, trigger.DependencyRoute);
        return !trigger.Dependencies.AsSpan().SequenceEqual(previous);
    }

    private PropertyReference[] Resolve(IReadOnlyList<string> paths, AutomationRule rule, GraphRouteSlot route)
    {
        var recorder = routes.BeginRoute();
        var resolved = new List<PropertyReference>(paths.Count);
        foreach (var path in paths)
        {
            if (AutomationPaths.ResolveProperty(resolver, path, rule, recorder) is { } property)
            {
                resolved.Add(property.Reference);
            }
        }

        routes.EndRoute(route);
        return [.. resolved];
    }

    private void PublishWatched(IReadOnlyList<CompiledRule> rules, IReadOnlyList<CompiledPropertyTrigger> activating)
    {
        var next = _nextWatched;
        next.Clear();
        for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
        {
            var rule = rules[ruleIndex];
            if (!rule.IsActivated)
            {
                continue;
            }

            foreach (var trigger in rule.PropertyTriggers)
            {
                if (trigger.State.Phase != TriggerPhase.Inactive)
                {
                    AddAll(next, trigger.Dependencies);
                }
            }
        }

        for (var index = 0; index < activating.Count; index++)
        {
            AddAll(next, activating[index].Dependencies);
        }

        foreach (var trigger in _pending)
        {
            AddAll(next, trigger.Rule.OnlyIfDependencies);
        }

        var previous = _watched;
        if (!IsEqual(previous, next))
        {
            _watched = next.ToFrozenSet();
            publishWatched(_watched);
        }

        // A value read while its property was not watched may have changed since without a queued change, so a property
        // that becomes watched is read from the graph again.
        _values.Retain(_watched, previous);
    }

    private static void AddAll(HashSet<PropertyReference> set, PropertyReference[] properties)
    {
        foreach (var property in properties)
        {
            set.Add(property);
        }
    }

    private static bool IsEqual(FrozenSet<PropertyReference> previous, HashSet<PropertyReference> next)
    {
        if (previous.Count != next.Count)
        {
            return false;
        }

        foreach (var property in next)
        {
            if (!previous.Contains(property))
            {
                return false;
            }
        }

        return true;
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
