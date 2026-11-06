using HomeBlaze.Automation.Events;
using HomeBlaze.Automation.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Evaluates a rule for <see cref="AutomationRule.TestNow"/>: its triggers and its guard against the graph at this
/// instant, and its actions with their rendered arguments. It compiles the rule on its own, so it works in every mode and
/// while the automation is stopped, and it changes no runtime state.
/// </summary>
internal static class RuleTester
{
    public static AutomationRuleTestResult Test(Automation automation, AutomationRule rule)
    {
        var key = automation.Rules.FirstOrDefault(pair => ReferenceEquals(pair.Value, rule)).Key ?? string.Empty;
        if (!AutomationCompiler.TryCompileRule(key, rule, automation.States, out var compiled, out var error))
        {
            return new AutomationRuleTestResult { Error = error };
        }

        var services = automation.Services;
        var evaluation = new AutomationEvaluation(services.PathResolver, rule, services.TimeProvider.GetUtcNow(), services.TimeProvider.LocalTimeZone);
        var guard = compiled.Guard is { } guardExpression ? evaluation.EvaluateCondition(guardExpression) : true;
        IReadOnlyList<string> guardMissingPaths = guard is null ? [.. evaluation.MissingPaths] : [];
        var triggers = compiled.PropertyTriggers.Select(trigger => TestPropertyTrigger(trigger, evaluation))
            .Concat(compiled.EventTriggers.Select(trigger => TestEventTrigger(trigger, automation)))
            .OrderBy(trigger => trigger.Number)
            .ToList();
        var executor = new ActionExecutor(services.PathResolver, services.Authorizer, services.ServiceProvider, services.TimeProvider);
        var scope = new ActionScope(automation, rule, evaluation, compiled.AllowsEvent);
        return new AutomationRuleTestResult
        {
            IsActive = compiled.IsActiveIn(automation.Enabled, automation.CurrentState),
            Triggers = triggers,
            HasGuard = compiled.Guard is not null,
            GuardResult = guard,
            GuardMissingPaths = guardMissingPaths,
            Actions = [.. compiled.Actions.Select(action => RuleRunner.Describe(executor, action, scope))]
        };
    }

    private static AutomationTriggerTestResult TestPropertyTrigger(CompiledPropertyTrigger trigger, AutomationEvaluation evaluation)
    {
        var result = evaluation.EvaluateCondition(trigger.Expression);
        return new AutomationTriggerTestResult
        {
            Number = trigger.Index + 1,
            Result = result,
            MissingPaths = result is null ? [.. evaluation.MissingPaths] : [],
            Note = trigger.Configuration.For is { } hold && hold > TimeSpan.Zero ? $"Fires when true for {hold:c}" : null
        };
    }

    private static AutomationTriggerTestResult TestEventTrigger(CompiledEventTrigger trigger, Automation automation) => new()
    {
        Number = trigger.Index + 1,
        Note = DescribeEventSource(trigger, automation)
    };

    private static string DescribeEventSource(CompiledEventTrigger trigger, Automation automation)
    {
        var path = trigger.Configuration.SubjectPath;
        var eventType = trigger.Configuration.EventType;
        var source = AutomationPaths.ResolveSubject(automation.Services.PathResolver, path, trigger.Rule.Rule);
        if (source is null)
        {
            return $"No subject at {path}";
        }

        return EventSources.FindEventType(source.GetType(), eventType) is null
            ? $"{path} publishes no {eventType}"
            : $"Runs on each {eventType} of {path} that passes the filter";
    }
}
