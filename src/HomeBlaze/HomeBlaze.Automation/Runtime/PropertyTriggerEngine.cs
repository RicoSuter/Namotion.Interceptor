using System.Collections.Frozen;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The property triggers of active rules: resolves the properties they read and publishes them as the watched set to
/// <paramref name="publishWatched"/>, evaluates them against the observed values on changes, ticks and graph changes,
/// and returns the run requests of the triggers that fire. Used by the worker only.
/// </summary>
internal sealed class PropertyTriggerEngine(
    SubjectPathResolver resolver,
    TimeProvider timeProvider,
    ILogger logger,
    Func<string> getAutomationName,
    Action<FrozenSet<PropertyReference>>? publishWatched = null)
{
    private readonly ObservedValues _values = new();
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

    /// <summary>Observes the change of a property that is still watched and evaluates the active triggers that read it.</summary>
    public IReadOnlyList<RunRequest> OnPropertyChanged(IReadOnlyList<CompiledRule> rules, PropertyChangedInput input) =>
        TryObserve(input)
            ? ObserveWhere(rules, trigger => Array.IndexOf(trigger.Dependencies, input.Property) >= 0)
            : [];

    public IReadOnlyList<RunRequest> OnTick(IReadOnlyList<CompiledRule> rules) =>
        ObserveWhere(rules, trigger => trigger.UsesTime || trigger.State.Phase == TriggerPhase.Holding);

    /// <summary>Resolves the properties again, since paths may lead elsewhere now, and evaluates every active trigger.</summary>
    public IReadOnlyList<RunRequest> OnGraphChanged(IReadOnlyList<CompiledRule> rules)
    {
        Refresh(rules);
        return ObserveWhere(rules, _ => true);
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

    /// <summary>Resolves the properties of the active triggers again and publishes them.</summary>
    public void Refresh(IReadOnlyList<CompiledRule> rules)
    {
        foreach (var trigger in ActiveTriggers(rules))
        {
            ResolveDependencies(trigger);
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
    }

    // A change queued while its property was watched may arrive after it no longer is; kept, it would hide the current
    // value from a trigger that reads the property again later.
    private bool TryObserve(PropertyChangedInput change) =>
        _watched.Contains(change.Property) && _values.TryApply(change.Property, change.Value, change.Revision);

    private static IEnumerable<CompiledPropertyTrigger> ActiveTriggers(IReadOnlyList<CompiledRule> rules) =>
        rules.Where(rule => rule.IsActivated)
            .SelectMany(rule => rule.PropertyTriggers)
            .Where(trigger => trigger.State.Phase != TriggerPhase.Inactive);

    private IReadOnlyList<RunRequest> ObserveWhere(IReadOnlyList<CompiledRule> rules, Func<CompiledPropertyTrigger, bool> predicate)
    {
        var now = timeProvider.GetUtcNow();
        var requests = new List<RunRequest>();
        foreach (var trigger in ActiveTriggers(rules).Where(predicate))
        {
            var wasUnknown = trigger.State.IsUnknown;
            if (trigger.State.Observe(Observe(trigger, now), now))
            {
                requests.Add(CreateRequest(trigger));
            }

            LogUnknownChange(trigger, wasUnknown);
        }

        return requests;
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
        var watched = ActiveTriggers(rules).Concat(activating).SelectMany(trigger => trigger.Dependencies).ToFrozenSet();
        _watched = watched;
        publishWatched?.Invoke(watched);
        _values.Retain(watched);
    }

    private static RunRequest CreateRequest(CompiledPropertyTrigger trigger) => new(trigger.Rule, null, trigger.Description);

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
