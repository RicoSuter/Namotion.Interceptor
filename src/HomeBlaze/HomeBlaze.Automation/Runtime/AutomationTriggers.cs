using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The property and event triggers of the rules of an automation: subscribes the triggers of rules that became active
/// and queues the activation of their property triggers, resolves every path of the active rules again when they may lead
/// elsewhere, and turns the inputs of the worker into run requests. The paths are always resolved all at once, into one
/// graph route of <see cref="GraphRoutes"/>, so that the route of a path that is no longer resolved leaves it, and every
/// resolution that finds other properties has them evaluated, in the same input or in one queued for it. Used by the
/// worker only.
/// </summary>
internal sealed class AutomationTriggers
{
    private readonly GraphRoutes _routes;
    private readonly AutomationInputQueue _queue;
    private readonly PropertyTriggerEngine _propertyTriggers;
    private readonly EventTriggerEngine _eventTriggers;

    /// <summary>
    /// Creates the triggers, which record the routes of their paths with <paramref name="routes"/>, and whose property
    /// triggers publish their watched set to <paramref name="publishWatched"/>.
    /// </summary>
    public AutomationTriggers(
        GraphRoutes routes,
        TimeProvider timeProvider,
        AutomationInputQueue queue,
        ILogger logger,
        Func<string> getAutomationName,
        Action<FrozenSet<PropertyReference>> publishWatched)
    {
        _routes = routes;
        _queue = queue;
        _propertyTriggers = new PropertyTriggerEngine(routes.Resolver, timeProvider, logger, getAutomationName, publishWatched);
        _eventTriggers = new EventTriggerEngine(routes.Resolver, timeProvider, queue);
    }

    /// <summary>
    /// Subscribes the event triggers of the rules that became active, publishes the watched set of all active rules and
    /// queues the activation of the property triggers of the new rules, which evaluates them in a later input.
    /// </summary>
    public void Activate(IReadOnlyList<CompiledRule> activated, IReadOnlyList<CompiledRule> rules)
    {
        Resolve(rules, []);
        if (activated.Count > 0)
        {
            _queue.TryEnqueue(new ActivationInput(activated, AutomationInputQueue.CausedChainDepth));
        }
    }

    /// <summary>
    /// Makes up for the inputs dropped from a feedback chain: resolves the paths again after a dropped graph change,
    /// observes the dropped changes and activates the rules of the dropped activations. Returns the runs they request,
    /// which must not execute.
    /// </summary>
    public IReadOnlyList<RunRequest> MakeUpForChain(IReadOnlyList<CompiledRule> rules, DroppedInputs dropped)
    {
        if (dropped.HasGraphChange)
        {
            Resolve(rules, []);
        }

        return MakeUpFor(rules, dropped.Changes.Where(DroppedInputs.IsChainDrop), dropped.Activations.Where(DroppedInputs.IsChainDrop), evaluateAll: false);
    }

    /// <summary>Makes up for the inputs dropped because the queue was full and evaluates every active trigger again.</summary>
    public IReadOnlyList<RunRequest> MakeUpForOverload(IReadOnlyList<CompiledRule> rules, DroppedInputs dropped) => MakeUpFor(
        rules,
        dropped.Changes.Where(change => !DroppedInputs.IsChainDrop(change)),
        dropped.Activations.Where(activation => !DroppedInputs.IsChainDrop(activation)),
        evaluateAll: true);

    /// <summary>
    /// Makes up for dropped inputs: observes the dropped changes that are newer than the observed values, then evaluates
    /// every active property trigger after resolving the paths again (<paramref name="evaluateAll"/>) or only those that
    /// read a changed property, and activates the rules of the dropped activations that are still activated. Returns the
    /// runs they request in rule order.
    /// </summary>
    private IReadOnlyList<RunRequest> MakeUpFor(
        IReadOnlyList<CompiledRule> rules,
        IEnumerable<PropertyChangedInput> changes,
        IEnumerable<ActivationInput> activations,
        bool evaluateAll)
    {
        var changed = _propertyTriggers.Apply(changes);
        var requests = new List<RunRequest>(evaluateAll ? EvaluateAll(rules) : _propertyTriggers.ObserveReaders(rules, changed));
        var activated = ActivateTriggers([.. activations.SelectMany(activation => activation.Rules)], rules);
        if (activated.Count == 0)
        {
            return requests;
        }

        requests.AddRange(activated);
        return [.. requests.OrderBy(request => IndexOf(rules, request.Rule))];
    }

    public static void Deactivate(CompiledRule rule)
    {
        PropertyTriggerEngine.Deactivate(rule);
        EventTriggerEngine.Deactivate(rule);
    }

    /// <summary>
    /// Hands the state of each property trigger and the subscription of each event trigger of the old rule to a trigger
    /// with an equal configuration, wherever it moved in the rule: each new trigger takes the first old one with an equal
    /// configuration that no earlier new trigger took, so that equal triggers pair up in order.
    /// </summary>
    public static void CarryOver(CompiledRule old, CompiledRule rule)
    {
        var propertyTriggers = new List<CompiledPropertyTrigger>(old.PropertyTriggers);
        foreach (var trigger in rule.PropertyTriggers)
        {
            var index = propertyTriggers.FindIndex(candidate => candidate.Configuration == trigger.Configuration);
            if (index >= 0)
            {
                propertyTriggers[index].MoveStateTo(trigger);
                propertyTriggers.RemoveAt(index);
            }
        }

        var eventTriggers = new List<CompiledEventTrigger>(old.EventTriggers);
        foreach (var trigger in rule.EventTriggers)
        {
            var index = eventTriggers.FindIndex(candidate => candidate.Configuration == trigger.Configuration);
            if (index >= 0)
            {
                eventTriggers[index].MoveSubscriptionTo(trigger);
                eventTriggers.RemoveAt(index);
            }
        }
    }

    /// <summary>Resolves the paths of the active rules again, such as after their rule instances changed.</summary>
    public void Rebind(IReadOnlyList<CompiledRule> rules) => Resolve(rules, []);

    /// <summary>Turns an input of the worker into the run requests of the triggers, in rule order; none for any other input.</summary>
    public IReadOnlyList<RunRequest> GetRequests(AutomationInput input, IReadOnlyList<CompiledRule> rules) => input switch
    {
        PropertyChangedInput change => _propertyTriggers.OnPropertyChanged(rules, change),
        TickInput => _propertyTriggers.OnTick(rules),
        EventInput subjectEvent => _eventTriggers.OnEvent(subjectEvent),
        GraphChangedInput => OnGraphChanged(rules),
        ResolvedDifferentlyInput => _propertyTriggers.EvaluateResolvedDifferently(rules),
        ActivationInput activation => ActivateTriggers(activation.Rules, rules),
        PendingRecheckInput => _propertyTriggers.RequestPendingRuns(rules),
        _ => []
    };

    /// <summary>
    /// Resolves every path again, unless the structure of the graph is as it was at the last resolution, see
    /// <see cref="GraphRoutes.NeedsResolving"/>, such as after a write of the same children, and evaluates the triggers
    /// whose properties changed, also those an earlier resolution found and no input evaluated yet; requests the runs of
    /// the pending triggers whose properties or Only if properties changed.
    /// </summary>
    public IReadOnlyList<RunRequest> OnGraphChanged(IReadOnlyList<CompiledRule> rules)
    {
        if (_routes.NeedsResolving())
        {
            ResolveOnly(rules, []);
        }

        return _propertyTriggers.EvaluateResolvedDifferently(rules);
    }

    /// <summary>Queues a request of the runs of every pending property trigger, such as after a state change skipped them.</summary>
    public void RequestPendingRecheck() => _queue.TryEnqueue(new PendingRecheckInput(AutomationInputQueue.CausedChainDepth));

    /// <summary>Gets when the next property trigger or Only if is due for a tick, or null when none is.</summary>
    public DateTimeOffset? GetNextDueAt(IReadOnlyList<CompiledRule> rules, DateTimeOffset now) => _propertyTriggers.GetNextDueAt(rules, now);

    /// <summary>Whether a property trigger is pending.</summary>
    public bool HasPending => _propertyTriggers.HasPending;

    /// <summary>
    /// Keeps track of the pending property triggers after the requests were dispatched, and resolves every path again
    /// when they changed, so that Only if of a rule whose trigger became pending is watched and Only if of a rule whose
    /// trigger is no longer pending is not. Returns whether a trigger became pending, whose Only if is to be checked again
    /// in a later input: a property it reads may have changed before it was watched.
    /// </summary>
    public bool TrackPending(IReadOnlyList<CompiledRule> rules, IReadOnlyList<RunRequest> requests)
    {
        var hasAdded = _propertyTriggers.TrackPending(requests, out var hasChanged);
        if (hasChanged)
        {
            Resolve(rules, []);
        }

        return hasAdded;
    }

    /// <summary>The position of the rule in rule order; the number of rules for a rule that is not one of them.</summary>
    public static int IndexOf(IReadOnlyList<CompiledRule> rules, CompiledRule rule)
    {
        for (var index = 0; index < rules.Count; index++)
        {
            if (ReferenceEquals(rules[index], rule))
            {
                return index;
            }
        }

        return rules.Count;
    }

    // Activates the inactive property triggers of the rules that are still activated, after resolving every path with
    // theirs, and returns the runs they request at once.
    private IReadOnlyList<RunRequest> ActivateTriggers(IReadOnlyList<CompiledRule> activated, IReadOnlyList<CompiledRule> rules)
    {
        var triggers = PropertyTriggerEngine.GetInactiveTriggers(activated, rules);
        Resolve(rules, triggers);
        return _propertyTriggers.Activate(triggers);
    }

    private IReadOnlyList<RunRequest> EvaluateAll(IReadOnlyList<CompiledRule> rules)
    {
        ResolveOnly(rules, []);
        return _propertyTriggers.EvaluateAll(rules);
    }

    // Resolves every path and queues the evaluation of the triggers and Only if resolved differently, for a caller that
    // does not evaluate them itself.
    private void Resolve(IReadOnlyList<CompiledRule> rules, IReadOnlyList<CompiledPropertyTrigger> activating)
    {
        ResolveOnly(rules, activating);
        if (_propertyTriggers.HasResolvedDifferently)
        {
            _queue.TryEnqueue(new ResolvedDifferentlyInput(AutomationInputQueue.CausedChainDepth));
        }
    }

    // Resolves every path of the active rules into one graph route: the sources of the event triggers, which subscribe
    // again where their source changed, and the properties of the property triggers, of those about to be activated and
    // of Only if of the rules with a pending trigger, which are published as the watched set.
    private void ResolveOnly(IReadOnlyList<CompiledRule> rules, IReadOnlyList<CompiledPropertyTrigger> activating)
    {
        var route = _routes.BeginRoute();
        _eventTriggers.Bind(rules, route);
        _propertyTriggers.Refresh(rules, activating, route);
    }
}
