using System.Collections.Frozen;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The property triggers of active rules: resolves the properties they read and publishes them as the watched set,
/// evaluates them against the observed values on changes, ticks and graph changes, and returns the run requests of the
/// triggers that fire. Used by the worker only, except <see cref="IsWatched"/>, which writer threads call.
/// </summary>
internal sealed class PropertyTriggerEngine(SubjectPathResolver resolver, TimeProvider timeProvider, ILogger logger)
{
    private readonly ObservedValues _values = new();
    private FrozenSet<PropertyReference> _watched = FrozenSet<PropertyReference>.Empty;

    public bool IsWatched(PropertyReference property) => Volatile.Read(ref _watched).Contains(property);

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

    public IReadOnlyList<RunRequest> OnPropertyChanged(IReadOnlyList<CompiledRule> rules, PropertyChangedInput input) =>
        _values.TryApply(input.Property, input.Value, input.Revision)
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
        if (trigger.Rearm is not { } rearmExpression || trigger.State.Phase != TriggerPhase.WaitingToRearm)
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
        Volatile.Write(ref _watched, watched);
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
            logger.LogInformation("The {Trigger} of rule {Rule} is unknown, missing {Paths}.", trigger.Description, trigger.Rule.Name, string.Join(", ", trigger.State.MissingPaths));
        }
        else
        {
            logger.LogInformation("The {Trigger} of rule {Rule} is known again.", trigger.Description, trigger.Rule.Name);
        }
    }
}
