using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The property and event triggers of the rules of an automation: subscribes the triggers of rules that became active
/// and queues the activation of their property triggers, binds them again when paths may lead elsewhere, and turns the
/// inputs of the worker into run requests. Used by the worker only, except <see cref="IsWatched"/>, which writer threads
/// call.
/// </summary>
internal sealed class AutomationTriggers
{
    private readonly AutomationInputQueue _queue;
    private readonly PropertyTriggerEngine _propertyTriggers;
    private readonly EventTriggerEngine _eventTriggers;
    private readonly List<CompiledRule> _droppedActivations = [];

    public AutomationTriggers(AutomationServices services, AutomationInputQueue queue, ILogger logger)
    {
        _queue = queue;
        _propertyTriggers = new PropertyTriggerEngine(services.PathResolver, services.TimeProvider, logger);
        _eventTriggers = new EventTriggerEngine(services.PathResolver, services.TimeProvider, queue);
    }

    public bool IsWatched(PropertyReference property) => _propertyTriggers.IsWatched(property);

    /// <summary>
    /// Subscribes the event triggers of the rules that became active, publishes the watched set of all active rules and
    /// queues the activation of the property triggers of the new rules, which evaluates them in a later input. An
    /// activation the queue drops is kept for <see cref="Resynchronize"/>.
    /// </summary>
    public void Activate(IReadOnlyList<CompiledRule> activated, IReadOnlyList<CompiledRule> rules)
    {
        foreach (var rule in activated)
        {
            _eventTriggers.Activate(rule);
        }

        _propertyTriggers.Refresh(rules);
        if (activated.Count > 0 && !_queue.TryEnqueue(new ActivationInput(activated, AutomationInputQueue.CausedChainDepth)))
        {
            _droppedActivations.AddRange(activated);
        }
    }

    /// <summary>
    /// Evaluates the triggers again after the queue dropped inputs: forgets the observed values, so that a dropped change
    /// is read from the graph, binds the paths again, evaluates the active property triggers and activates those whose
    /// activation was dropped. Returns the runs they request in rule order.
    /// </summary>
    public IReadOnlyList<RunRequest> Resynchronize(IReadOnlyList<CompiledRule> rules)
    {
        _propertyTriggers.ForgetValues();
        var requests = new List<RunRequest>(OnGraphChanged(rules));
        if (_droppedActivations.Count == 0)
        {
            return requests;
        }

        var activated = _droppedActivations.Where(rule => rule.IsActivated && rules.Contains(rule)).Distinct().ToList();
        _droppedActivations.Clear();
        requests.AddRange(_propertyTriggers.Activate(activated, rules));
        return [.. requests.OrderBy(request => IndexOf(rules, request.Rule))];
    }

    public static void Deactivate(CompiledRule rule)
    {
        PropertyTriggerEngine.Deactivate(rule);
        EventTriggerEngine.Deactivate(rule);
    }

    /// <summary>Hands the state of each property trigger of the old rule to the trigger at the same position with an equal configuration.</summary>
    public static void CarryOver(CompiledRule old, CompiledRule rule)
    {
        foreach (var trigger in rule.PropertyTriggers)
        {
            var match = old.PropertyTriggers.FirstOrDefault(candidate =>
                candidate.Index == trigger.Index && candidate.Configuration == trigger.Configuration);
            match?.MoveStateTo(trigger);
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

    private static int IndexOf(IReadOnlyList<CompiledRule> rules, CompiledRule rule)
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
