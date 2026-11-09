using System.Collections.Frozen;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The property and event triggers of the rules of an automation: subscribes the event triggers of the rules that listen
/// for events, queues the activation of the property triggers of rules that became active, binds them again when paths
/// may lead elsewhere, and turns the inputs of the worker into run requests. Used by the worker only.
/// </summary>
internal sealed class AutomationTriggers
{
    private readonly AutomationInputQueue _queue;
    private readonly PropertyTriggerEngine _propertyTriggers;
    private readonly EventTriggerEngine _eventTriggers;
    private readonly Func<long> _getStructureVersion;
    private long _boundStructureVersion = -1;

    /// <summary>Creates the triggers of the automation, with the structure version of the registry of its context.</summary>
    public static AutomationTriggers For(
        Automation automation, AutomationInputQueue queue, ILogger logger, Func<string> getAutomationName, Action<FrozenSet<PropertyReference>> publishWatched)
    {
        var registry = ((IInterceptorSubject)automation).Context.TryGetService<ISubjectRegistry>();
        return new AutomationTriggers(
            automation.PathResolver, automation.TimeProvider, queue, logger, getAutomationName, publishWatched, () => registry?.StructureVersion ?? -1);
    }

    /// <summary>
    /// Creates the triggers, whose property triggers publish their watched set to <paramref name="publishWatched"/> and
    /// whose paths are resolved again on a graph change only when <paramref name="getStructureVersion"/>, the structure
    /// version of the registry, changed since.
    /// </summary>
    public AutomationTriggers(
        SubjectPathResolver resolver,
        TimeProvider timeProvider,
        AutomationInputQueue queue,
        ILogger logger,
        Func<string> getAutomationName,
        Action<FrozenSet<PropertyReference>> publishWatched,
        Func<long> getStructureVersion)
    {
        _queue = queue;
        _getStructureVersion = getStructureVersion;
        _propertyTriggers = new PropertyTriggerEngine(resolver, timeProvider, logger, getAutomationName, publishWatched);
        _eventTriggers = new EventTriggerEngine(resolver, timeProvider, queue, logger, getAutomationName);
    }

    /// <summary>
    /// Publishes the watched set of all active rules and queues the activation of the property triggers of the rules that
    /// became active, which evaluates them in a later input. Returns whether a pending property trigger began to be
    /// tracked, whose Only if is to be checked again in a later input.
    /// </summary>
    public bool Activate(IReadOnlyList<CompiledRule> activated, IReadOnlyList<CompiledRule> rules)
    {
        var tracksNewPending = _propertyTriggers.Refresh(rules);
        if (activated.Count > 0)
        {
            _queue.TryEnqueue(new ActivationInput(activated, AutomationInputQueue.CausedChainDepth));
        }

        return tracksNewPending;
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
        var requests = new List<RunRequest>(evaluateAll ? RebindAndEvaluateAll(rules) : _propertyTriggers.ObserveReaders(rules, changed));
        var activated = _propertyTriggers.Activate([.. activations.SelectMany(activation => activation.Rules)], rules);
        if (activated.Count == 0)
        {
            return requests;
        }

        requests.AddRange(activated);
        return [.. requests.OrderBy(request => IndexOf(rules, request.Rule))];
    }

    /// <summary>Stops the property triggers of the rule; its event triggers stay subscribed.</summary>
    public static void Deactivate(CompiledRule rule) => PropertyTriggerEngine.Deactivate(rule);

    /// <summary>Subscribes the event triggers of the rule, unless it listens already.</summary>
    public void Listen(CompiledRule rule) => _eventTriggers.Listen(rule);

    /// <summary>Gives up the subscriptions of the event triggers of the rule.</summary>
    public void StopListening(CompiledRule rule) => _eventTriggers.StopListening(rule);

    /// <summary>Has the event triggers of the rule ignore the events received so far, as if they subscribed now.</summary>
    public static void SkipReceivedEvents(CompiledRule rule) => EventTriggerEngine.SkipReceived(rule);

    /// <summary>
    /// Hands the state of each property trigger of the old rule to a trigger with an equal configuration, wherever it
    /// moved in the rule: each new trigger takes the first old one with an equal configuration that no earlier new trigger
    /// took, so that equal triggers pair up in order.
    /// </summary>
    public static void CarryOverStates(CompiledRule old, CompiledRule rule)
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
    }

    /// <summary>
    /// Hands the subscription of each event trigger of the old rule to a trigger with an equal configuration, paired up as
    /// by <see cref="CarryOverStates"/>, so that the trigger handles the events queued before.
    /// </summary>
    public static void CarryOverSubscriptions(CompiledRule old, CompiledRule rule)
    {
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
    public void Rebind(IReadOnlyList<CompiledRule> rules)
    {
        _propertyTriggers.Refresh(rules);
        _eventTriggers.Rebind(rules);
    }

    /// <summary>Turns an input of the worker into the run requests of the triggers, in rule order; none for any other input.</summary>
    public IReadOnlyList<RunRequest> GetRequests(AutomationInput input, IReadOnlyList<CompiledRule> rules) => input switch
    {
        PropertyChangedInput change => _propertyTriggers.OnPropertyChanged(rules, change),
        TickInput => _propertyTriggers.OnTick(rules),
        EventInput subjectEvent => _eventTriggers.OnEvent(subjectEvent, rules),
        GraphChangedInput => OnGraphChanged(rules),
        ActivationInput activation => _propertyTriggers.Activate(activation.Rules, rules),
        PendingRecheckInput => _propertyTriggers.RequestPendingRuns(rules),
        _ => []
    };

    /// <summary>
    /// Resolves the paths again after a change of a property that holds subjects, unless the structure of the graph is
    /// the same as at the last time, such as for a write of the same children, and evaluates the triggers whose
    /// properties changed.
    /// </summary>
    public IReadOnlyList<RunRequest> OnGraphChanged(IReadOnlyList<CompiledRule> rules)
    {
        // Read before the paths are resolved; an odd version is a change in progress, after which they resolve again.
        var version = _getStructureVersion();
        if (version >= 0 && version == _boundStructureVersion)
        {
            _propertyTriggers.ForgetRebound();
            return [];
        }

        _boundStructureVersion = version % 2 == 0 ? version : -1;
        _eventTriggers.Rebind(rules);
        return _propertyTriggers.OnGraphChanged(rules, evaluateAll: false);
    }

    private IReadOnlyList<RunRequest> RebindAndEvaluateAll(IReadOnlyList<CompiledRule> rules)
    {
        _eventTriggers.Rebind(rules);
        return _propertyTriggers.OnGraphChanged(rules, evaluateAll: true);
    }

    /// <summary>Queues a request of the runs of every pending property trigger, such as after a state change skipped them.</summary>
    public void RequestPendingRecheck() => _queue.TryEnqueue(new PendingRecheckInput(AutomationInputQueue.CausedChainDepth));

    /// <summary>Gets when the next property trigger or Only if is due for a tick, or null when none is.</summary>
    public DateTimeOffset? GetNextDueAt(IReadOnlyList<CompiledRule> rules, DateTimeOffset now) => _propertyTriggers.GetNextDueAt(rules, now);

    /// <summary>Whether a property trigger is pending.</summary>
    public bool HasPending => _propertyTriggers.HasPending;

    /// <summary>
    /// Keeps track of the pending property triggers after the requests were dispatched. Returns whether a trigger became
    /// pending, whose Only if is to be checked again in a later input: a property it reads may have changed before it was
    /// watched.
    /// </summary>
    public bool TrackPending(IReadOnlyList<CompiledRule> rules, IReadOnlyList<RunRequest> requests) =>
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
