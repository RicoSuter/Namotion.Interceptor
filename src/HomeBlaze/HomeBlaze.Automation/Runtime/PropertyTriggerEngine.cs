using System.Collections.Frozen;
using System.Runtime.InteropServices;
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
    Action<FrozenSet<PropertyReference>> publishWatched)
{
    private readonly ObservedValues _values = new();
    private readonly List<CompiledPropertyTrigger> _pending = [];

    // Reused by every resolution and publication, so that one whose result stayed the same allocates nothing.
    private readonly List<PropertyReference> _resolved = [];
    private readonly HashSet<PropertyReference> _watchedBuffer = [];

    // The latest instant from which an instant when a trigger or Only if is due was derived; a later tick before it means
    // that the clock was set back.
    private DateTimeOffset _derivedAt = DateTimeOffset.MinValue;

    // The triggers whose properties, and the rules whose Only if properties, a refresh resolved differently since they
    // were last evaluated, so that a resolution outside a graph change loses no difference.
    private readonly HashSet<CompiledPropertyTrigger> _resolvedDifferently = [];
    private readonly HashSet<CompiledRule> _onlyIfResolvedDifferently = [];
    private FrozenSet<PropertyReference> _watched = FrozenSet<PropertyReference>.Empty;

    /// <summary>
    /// Gets the inactive triggers of the rules that are still activated and among <paramref name="allRules"/>, such as
    /// rules whose activation was queued.
    /// </summary>
    public static List<CompiledPropertyTrigger> GetInactiveTriggers(IReadOnlyList<CompiledRule> rules, IReadOnlyList<CompiledRule> allRules) =>
        rules.Where(rule => rule.IsActivated && allRules.Contains(rule)).Distinct()
            .SelectMany(rule => rule.PropertyTriggers).Where(trigger => trigger.State.Phase == TriggerPhase.Inactive).ToList();

    /// <summary>
    /// Activates the triggers, whose properties <see cref="Refresh"/> resolved and published with those of the active
    /// triggers, and returns the runs they request at once.
    /// </summary>
    public IReadOnlyList<RunRequest> Activate(IReadOnlyList<CompiledPropertyTrigger> triggers)
    {
        var now = timeProvider.GetUtcNow();
        var requests = new List<RunRequest>();
        foreach (var trigger in triggers)
        {
            var wasUnknown = trigger.State.IsUnknown;
            if (trigger.State.Activate(trigger.Configuration.OnActivation, Observe(trigger, now), now))
            {
                requests.Add(CreateRequest(trigger));
            }

            ScheduleCheck(trigger, now);
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
        var requests = ObserveWhere(rules, trigger => Array.IndexOf(trigger.Dependencies, property) >= 0, trigger => trigger.Rule.OnlyIf is null);
        return _pending.Count == 0
            ? requests
            : AddPendingRequests(rules, requests, trigger => Array.IndexOf(trigger.Rule.OnlyIfDependencies, property) >= 0);
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

        var requests = ObserveWhere(rules, trigger => trigger.NextCheckAt is { } checkAt && (checkAt <= now || isClockSetBack));
        if (_pending.Count == 0)
        {
            return requests;
        }

        return AddPendingRequests(rules, requests, trigger =>
            trigger.Rule.RechecksOnlyIfEverySecond || trigger.Rule.OnlyIfRecheckAt <= now || (isClockSetBack && trigger.Rule.OnlyIfRecheckAt is not null));
    }

    /// <summary>
    /// Gets when the next trigger or Only if of a pending trigger is due, or null when none is, such as for rules that
    /// neither hold nor read the clock. Allocates nothing.
    /// </summary>
    public DateTimeOffset? GetNextDueAt(IReadOnlyList<CompiledRule> rules, DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        foreach (var rule in rules)
        {
            if (!rule.IsActivated)
            {
                continue;
            }

            foreach (var trigger in rule.PropertyTriggers)
            {
                next = Earliest(next, trigger.NextCheckAt);
            }
        }

        foreach (var trigger in _pending)
        {
            next = Earliest(next, trigger.Rule.RechecksOnlyIfEverySecond ? ExpressionClock.GetNextSecond(now) : trigger.Rule.OnlyIfRecheckAt);
        }

        return next;
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? current, DateTimeOffset? candidate) =>
        candidate is { } instant && (current is null || instant < current) ? instant : current;

    /// <summary>Whether a <see cref="Refresh"/> resolved properties differently that were not evaluated yet.</summary>
    public bool HasResolvedDifferently => _resolvedDifferently.Count > 0 || _onlyIfResolvedDifferently.Count > 0;

    /// <summary>
    /// Evaluates the triggers whose properties a <see cref="Refresh"/> resolved differently since the last evaluation and
    /// requests the runs of the pending triggers whose properties or Only if properties it resolved differently.
    /// </summary>
    public IReadOnlyList<RunRequest> EvaluateResolvedDifferently(IReadOnlyList<CompiledRule> rules)
    {
        try
        {
            var requests = _resolvedDifferently.Count == 0 ? [] : ObserveWhere(rules, _resolvedDifferently.Contains, _ => true);
            return _onlyIfResolvedDifferently.Count == 0 || _pending.Count == 0
                ? requests
                : AddPendingRequests(rules, requests, trigger => _onlyIfResolvedDifferently.Contains(trigger.Rule));
        }
        finally
        {
            ForgetResolvedDifferently();
        }
    }

    /// <summary>Evaluates every active trigger and requests the runs of every pending trigger.</summary>
    public IReadOnlyList<RunRequest> EvaluateAll(IReadOnlyList<CompiledRule> rules)
    {
        try
        {
            return ObserveWhere(rules, _ => true, _ => true);
        }
        finally
        {
            ForgetResolvedDifferently();
        }
    }

    // Forgets the triggers that a refresh resolved differently once they were evaluated, so that triggers of replaced
    // rules are not kept.
    private void ForgetResolvedDifferently()
    {
        _resolvedDifferently.Clear();
        _onlyIfResolvedDifferently.Clear();
    }

    /// <summary>Requests the runs of every pending trigger, such as after a state change skipped them.</summary>
    public IReadOnlyList<RunRequest> RequestPendingRuns(IReadOnlyList<CompiledRule> rules) =>
        _pending.Count == 0 ? [] : AddPendingRequests(rules, [], _ => true);

    /// <summary>Whether a trigger is pending.</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>
    /// Keeps track of the pending triggers after the requests were dispatched: a trigger whose request did not run
    /// becomes pending, and one that ran, turned false or whose rule was deactivated is no longer pending. Sets when Only
    /// if of a rule with a pending trigger is evaluated again for the clock. Does nothing while no trigger is pending and
    /// nothing was requested. Returns whether a trigger became pending, whose Only if may have changed before its properties
    /// were watched, and in <paramref name="hasChanged"/> whether the pending triggers changed, after which the caller
    /// resolves every path again with <see cref="Refresh"/>.
    /// </summary>
    public bool TrackPending(IReadOnlyList<RunRequest> requests, out bool hasChanged)
    {
        hasChanged = false;
        if (_pending.Count == 0 && requests.Count == 0)
        {
            return false;
        }

        var hasAdded = HasNewPending(requests);
        hasChanged = hasAdded || _pending.Exists(trigger => !IsPending(trigger));
        if (hasChanged)
        {
            return hasAdded;
        }

        var now = timeProvider.GetUtcNow();
        foreach (var request in requests)
        {
            if (request.Trigger is { } trigger && IsPending(trigger))
            {
                ScheduleOnlyIfRecheck(trigger.Rule, now);
            }
        }

        return false;
    }

    private bool HasNewPending(IReadOnlyList<RunRequest> requests)
    {
        foreach (var request in requests)
        {
            if (request.Trigger is { } trigger && IsPending(trigger) && !_pending.Contains(trigger))
            {
                return true;
            }
        }

        return false;
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
    /// Resolves the properties of the active triggers, of <paramref name="activating"/>, which are about to be activated,
    /// and of Only if of the rules of the pending triggers, recording their routes with <paramref name="route"/>, and
    /// publishes them as the watched set. Remembers the triggers resolved differently for
    /// <see cref="EvaluateResolvedDifferently"/>.
    /// </summary>
    public void Refresh(IReadOnlyList<CompiledRule> rules, IReadOnlyList<CompiledPropertyTrigger> activating, ISubjectPathRecorder route)
    {
        _pending.Clear();
        var now = timeProvider.GetUtcNow();
        foreach (var rule in rules)
        {
            if (!rule.IsActivated)
            {
                continue;
            }

            foreach (var trigger in rule.PropertyTriggers)
            {
                if (trigger.State.Phase != TriggerPhase.Inactive)
                {
                    RefreshOne(trigger, now, route);
                }
            }
        }

        foreach (var trigger in activating)
        {
            ResolveDependencies(trigger, route);
        }

        // Published before the values are read, so that no change between the read and the publication is lost.
        PublishWatched(rules, activating);
    }

    private void RefreshOne(CompiledPropertyTrigger trigger, DateTimeOffset now, ISubjectPathRecorder route)
    {
        if (ResolveDependencies(trigger, route))
        {
            _resolvedDifferently.Add(trigger);
        }

        ScheduleCheck(trigger, now);

        if (trigger.State.Phase == TriggerPhase.Pending)
        {
            if (ResolveOnlyIfDependencies(trigger.Rule, route))
            {
                _onlyIfResolvedDifferently.Add(trigger.Rule);
            }

            ScheduleOnlyIfRecheck(trigger.Rule, now);
            _pending.Add(trigger);
        }
    }

    public static void Deactivate(CompiledRule rule)
    {
        foreach (var trigger in rule.PropertyTriggers)
        {
            trigger.State.Deactivate();
            trigger.Dependencies = [];
            trigger.NextCheckAt = null;
        }

        rule.OnlyIfDependencies = [];
    }

    // A change queued while its property was watched may arrive after it no longer is; kept, it would hide the current
    // value from a trigger that reads the property again later.
    private bool TryObserve(PropertyChangedInput change) =>
        _watched.Contains(change.Property) && _values.TryApply(change.Property, change.Value, change.Revision);

    // A trigger that stays pending requests its run again where requestPending allows it. Allocates nothing when no
    // trigger matches.
    private IReadOnlyList<RunRequest> ObserveWhere(
        IReadOnlyList<CompiledRule> rules,
        Func<CompiledPropertyTrigger, bool> predicate,
        Func<CompiledPropertyTrigger, bool>? requestPending = null)
    {
        List<RunRequest>? requests = null;
        DateTimeOffset? now = null;
        foreach (var rule in rules)
        {
            if (!rule.IsActivated)
            {
                continue;
            }

            foreach (var trigger in rule.PropertyTriggers)
            {
                if (trigger.State.Phase != TriggerPhase.Inactive && predicate(trigger) &&
                    ObserveOne(trigger, now ??= timeProvider.GetUtcNow(), requestPending) is { } request)
                {
                    (requests ??= []).Add(request);
                }
            }
        }

        return requests ?? (IReadOnlyList<RunRequest>)[];
    }

    private RunRequest? ObserveOne(CompiledPropertyTrigger trigger, DateTimeOffset now, Func<CompiledPropertyTrigger, bool>? requestPending)
    {
        var wasUnknown = trigger.State.IsUnknown;
        var wasPending = trigger.State.Phase == TriggerPhase.Pending;
        RunRequest? request = null;
        if (trigger.State.Observe(Observe(trigger, now), now) ||
            (wasPending && trigger.State.Phase == TriggerPhase.Pending && requestPending?.Invoke(trigger) == true))
        {
            request = CreateRequest(trigger);
        }
        else if (wasPending && trigger.State.Phase != TriggerPhase.Pending)
        {
            EndWait(trigger.Rule);
        }

        ScheduleCheck(trigger, now);
        LogUnknownChange(trigger, wasUnknown);
        return request;
    }

    // Due when the hold ends or the expressions can change with the clock, whichever comes first; a read of the clock
    // that cannot be derived is due at the next second.
    private void ScheduleCheck(CompiledPropertyTrigger trigger, DateTimeOffset now)
    {
        var state = trigger.State;
        DateTimeOffset? next = state is { Phase: TriggerPhase.Holding, HoldStartedAt: { } start }
            ? start + trigger.Configuration.For.GetValueOrDefault()
            : null;
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

            next = Earliest(next, change);
        }

        trigger.NextCheckAt = next;
        _derivedAt = Max(_derivedAt, now);
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;

    // A comparison of elapsed time that changed already changes again only when the wall clock is set back, which a tick
    // notices; checked as often as the worker wakes up at most.
    private static DateTimeOffset CheckForSetBackClock(DateTimeOffset now) => now + WakeTimer.MaxWakeInterval;

    private object? ReadObserved(string path, AutomationRule rule) =>
        AutomationPaths.ResolveProperty(resolver, path, rule) is { } property ? _values.GetValue(property) : null;

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
    private bool ResolveOnlyIfDependencies(CompiledRule rule, ISubjectPathRecorder route)
    {
        var previous = rule.OnlyIfDependencies;
        rule.OnlyIfDependencies = Resolve(rule.OnlyIfPropertyPaths, rule.Rule, previous, route);
        return !ReferenceEquals(rule.OnlyIfDependencies, previous);
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
    private bool ResolveDependencies(CompiledPropertyTrigger trigger, ISubjectPathRecorder route)
    {
        var previous = trigger.Dependencies;
        trigger.Dependencies = Resolve(trigger.PropertyPaths, trigger.Rule.Rule, previous, route);
        return !ReferenceEquals(trigger.Dependencies, previous);
    }

    // Returns previous itself when the properties are the same.
    private PropertyReference[] Resolve(IReadOnlyList<string> paths, AutomationRule rule, PropertyReference[] previous, ISubjectPathRecorder route)
    {
        _resolved.Clear();
        for (var index = 0; index < paths.Count; index++)
        {
            if (AutomationPaths.ResolveProperty(resolver, paths[index], rule, route) is { } property)
            {
                _resolved.Add(property.Reference);
            }
        }

        return CollectionsMarshal.AsSpan(_resolved).SequenceEqual(previous) ? previous : [.. _resolved];
    }

    private void PublishWatched(IReadOnlyList<CompiledRule> rules, IReadOnlyList<CompiledPropertyTrigger> activating)
    {
        _watchedBuffer.Clear();
        foreach (var rule in rules)
        {
            if (rule.IsActivated)
            {
                AddDependencies(rule.PropertyTriggers, includeInactive: false);
            }
        }

        AddDependencies(activating, includeInactive: true);
        foreach (var trigger in _pending)
        {
            AddToWatchedBuffer(trigger.Rule.OnlyIfDependencies);
        }

        var previous = _watched;
        if (!HasSameProperties(previous, _watchedBuffer))
        {
            _watched = _watchedBuffer.ToFrozenSet();
        }

        publishWatched(_watched);

        // A value read while its property was not watched may have changed since without a queued change, so a property
        // that becomes watched is read from the graph again.
        _values.Retain(_watched, previous);
    }

    private static bool HasSameProperties(FrozenSet<PropertyReference> set, HashSet<PropertyReference> properties)
    {
        if (set.Count != properties.Count)
        {
            return false;
        }

        foreach (var property in properties)
        {
            if (!set.Contains(property))
            {
                return false;
            }
        }

        return true;
    }

    private void AddDependencies(IReadOnlyList<CompiledPropertyTrigger> triggers, bool includeInactive)
    {
        for (var index = 0; index < triggers.Count; index++)
        {
            var trigger = triggers[index];
            if (includeInactive || trigger.State.Phase != TriggerPhase.Inactive)
            {
                AddToWatchedBuffer(trigger.Dependencies);
            }
        }
    }

    private void AddToWatchedBuffer(PropertyReference[] properties)
    {
        foreach (var property in properties)
        {
            _watchedBuffer.Add(property);
        }
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
