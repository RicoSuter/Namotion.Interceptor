using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The property and event triggers of the rules of an automation: subscribes the triggers of rules that became active
/// and queues the activation of their property triggers, binds them again when paths may lead elsewhere, and turns the
/// inputs of the worker into run requests. Used by the worker only.
/// </summary>
internal sealed class AutomationTriggers
{
    private readonly AutomationInputQueue _queue;
    private readonly PropertyTriggerEngine _propertyTriggers;
    private readonly EventTriggerEngine _eventTriggers;

    public AutomationTriggers(
        AutomationServices services,
        AutomationInputQueue queue,
        ILogger logger,
        Func<string> getAutomationName,
        Action<FrozenSet<PropertyReference>> publishWatched)
    {
        _queue = queue;
        _propertyTriggers = new PropertyTriggerEngine(services.PathResolver, services.TimeProvider, logger, getAutomationName, publishWatched);
        _eventTriggers = new EventTriggerEngine(services.PathResolver, services.TimeProvider, queue);
    }

    /// <summary>
    /// Subscribes the event triggers of the rules that became active, publishes the watched set of all active rules and
    /// queues the activation of the property triggers of the new rules, which evaluates them in a later input.
    /// </summary>
    public void Activate(IReadOnlyList<CompiledRule> activated, IReadOnlyList<CompiledRule> rules)
    {
        foreach (var rule in activated)
        {
            _eventTriggers.Activate(rule);
        }

        _propertyTriggers.Refresh(rules);
        if (activated.Count > 0)
        {
            _queue.TryEnqueue(new ActivationInput(activated, AutomationInputQueue.CausedChainDepth));
        }
    }

    /// <summary>
    /// Makes up for the inputs dropped from a feedback chain: binds the paths again after a dropped graph change, observes
    /// the dropped changes and activates the rules of the dropped activations. Returns the runs they request, which must
    /// not execute.
    /// </summary>
    public IReadOnlyList<RunRequest> MakeUpForChain(IReadOnlyList<CompiledRule> rules, DroppedInputs dropped)
    {
        if (dropped.HasGraphChange)
        {
            Rebind(rules);
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
    /// every active property trigger after binding the paths again (<paramref name="evaluateAll"/>) or only those that read
    /// a changed property, and activates the rules of the dropped activations that are still activated. Returns the runs
    /// they request in rule order.
    /// </summary>
    private IReadOnlyList<RunRequest> MakeUpFor(
        IReadOnlyList<CompiledRule> rules,
        IEnumerable<PropertyChangedInput> changes,
        IEnumerable<ActivationInput> activations,
        bool evaluateAll)
    {
        var changed = _propertyTriggers.Apply(changes);
        var requests = new List<RunRequest>(evaluateAll ? OnGraphChanged(rules) : _propertyTriggers.ObserveReaders(rules, changed));
        var activated = activations.SelectMany(activation => activation.Rules)
            .Where(rule => rule.IsActivated && rules.Contains(rule))
            .Distinct()
            .ToList();
        if (activated.Count == 0)
        {
            return requests;
        }

        requests.AddRange(_propertyTriggers.Activate(activated, rules));
        return [.. requests.OrderBy(request => IndexOf(rules, request.Rule))];
    }

    public static void Deactivate(CompiledRule rule)
    {
        PropertyTriggerEngine.Deactivate(rule);
        EventTriggerEngine.Deactivate(rule);
    }

    /// <summary>
    /// Hands the state of each property trigger and the subscription of each event trigger of the old rule to the trigger
    /// at the same position with an equal configuration.
    /// </summary>
    public static void CarryOver(CompiledRule old, CompiledRule rule)
    {
        foreach (var trigger in rule.PropertyTriggers)
        {
            var match = old.PropertyTriggers.FirstOrDefault(candidate =>
                candidate.Index == trigger.Index && candidate.Configuration == trigger.Configuration);
            match?.MoveStateTo(trigger);
        }

        foreach (var trigger in rule.EventTriggers)
        {
            var match = old.EventTriggers.FirstOrDefault(candidate =>
                candidate.Index == trigger.Index && candidate.Configuration == trigger.Configuration);
            match?.MoveSubscriptionTo(trigger);
        }
    }

    /// <summary>Resolves the paths of the active rules again, such as after their rule instances changed.</summary>
    public void Rebind(IReadOnlyList<CompiledRule> rules)
    {
        _propertyTriggers.Refresh(rules);
        _eventTriggers.Rebind(rules);
    }

    public IReadOnlyList<RunRequest> OnPropertyChanged(IReadOnlyList<CompiledRule> rules, PropertyChangedInput input) =>
        _propertyTriggers.OnPropertyChanged(rules, input);

    public IReadOnlyList<RunRequest> OnTick(IReadOnlyList<CompiledRule> rules) => _propertyTriggers.OnTick(rules);

    public IReadOnlyList<RunRequest> OnEvent(EventInput input) => _eventTriggers.OnEvent(input);

    public IReadOnlyList<RunRequest> OnGraphChanged(IReadOnlyList<CompiledRule> rules)
    {
        _eventTriggers.Rebind(rules);
        return _propertyTriggers.OnGraphChanged(rules);
    }

    /// <summary>Activates the property triggers of the rules and returns the runs they request at once.</summary>
    public IReadOnlyList<RunRequest> OnActivation(IReadOnlyList<CompiledRule> activated, IReadOnlyList<CompiledRule> rules) =>
        _propertyTriggers.Activate(activated, rules);

    /// <summary>Requests the runs of every pending property trigger.</summary>
    public IReadOnlyList<RunRequest> OnPendingRecheck(IReadOnlyList<CompiledRule> rules) => _propertyTriggers.RequestPendingRuns(rules);

    /// <summary>Queues a request of the runs of every pending property trigger, such as after a state change skipped them.</summary>
    public void RequestPendingRecheck() => _queue.TryEnqueue(new PendingRecheckInput(AutomationInputQueue.CausedChainDepth));

    /// <summary>Keeps track of the pending property triggers after the requests were dispatched.</summary>
    public void TrackPending(IReadOnlyList<CompiledRule> rules, IReadOnlyList<RunRequest> requests) =>
        _propertyTriggers.TrackPending(rules, requests);

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
}
