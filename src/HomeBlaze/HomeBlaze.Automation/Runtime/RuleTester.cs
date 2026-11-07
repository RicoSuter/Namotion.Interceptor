using HomeBlaze.Automation.Expressions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Evaluates a rule for <see cref="AutomationRule.TestNow"/>: its triggers and Only if against the graph at this
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

        var evaluation = new AutomationEvaluation(automation.PathResolver, rule, automation.TimeProvider.GetUtcNow(), automation.TimeProvider.LocalTimeZone);
        var onlyIf = compiled.OnlyIf is { } onlyIfExpression ? evaluation.EvaluateCondition(onlyIfExpression) : true;
        IReadOnlyList<string> onlyIfMissingPaths = onlyIf is null ? [.. evaluation.MissingPaths] : [];
        var triggers = compiled.PropertyTriggers.Select(trigger => TestPropertyTrigger(trigger, evaluation))
            .Concat(compiled.EventTriggers.Select(trigger => TestEventTrigger(trigger, automation)))
            .OrderBy(trigger => trigger.Number)
            .ToList();
        var executor = ActionExecutor.For(automation);
        var scope = new ActionScope(automation, rule, evaluation, compiled.AllowsEvent);
        return new AutomationRuleTestResult
        {
            IsActive = compiled.IsActiveIn(automation.Enabled, automation.CurrentState),
            Triggers = triggers,
            HasOnlyIf = compiled.OnlyIf is not null,
            OnlyIfResult = onlyIf,
            OnlyIfMissingPaths = onlyIfMissingPaths,
            Actions = [.. compiled.Actions.Select(action => executor.Describe(action, scope))]
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
            Note = trigger.Configuration.For is { } hold && hold > TimeSpan.Zero ? $"Fires when true for {AutomationDuration.Format(hold)}" : null
        };
    }

    private static AutomationTriggerTestResult TestEventTrigger(CompiledEventTrigger trigger, Automation automation) => new()
    {
        Number = trigger.Index + 1,
        Note = EventTriggerEngine.FindSource(automation.PathResolver, trigger, out _, out _) ??
            $"Runs on each {trigger.Configuration.EventType} of {trigger.Configuration.SubjectPath} that passes the filter"
    };
}
