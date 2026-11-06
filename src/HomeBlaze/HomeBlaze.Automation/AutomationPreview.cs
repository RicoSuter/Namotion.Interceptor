using HomeBlaze.Automation.Expressions;
using HomeBlaze.Services.Display;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation;

/// <summary>
/// Evaluates what an editor shows next to the expressions of a rule: the value and the result of an expression with the
/// current values of the graph, a rendered template, and the target of an action. Paths start at the saved rule with the
/// key, as they do when the rule runs; for a rule that is not saved yet, relative paths lead nowhere. Executes and
/// records nothing.
/// </summary>
public static class AutomationPreview
{
    public static AutomationExpressionPreview Evaluate(Automation automation, string? ruleKey, string? expression, bool allowsEvent)
    {
        if (!AutomationExpressionCheck.TryParseExpression(expression, allowsEvent, out var parsed, out var error))
        {
            return new AutomationExpressionPreview { Error = error };
        }

        var evaluation = CreateEvaluation(automation, ruleKey);
        var result = evaluation.EvaluateCondition(parsed);
        IReadOnlyList<string> missingPaths = [.. evaluation.MissingPaths];
        var value = evaluation.Evaluate(parsed);
        return new AutomationExpressionPreview
        {
            Result = result,
            Value = value.IsMissing ? null : value.Value,
            Text = value.IsMissing ? null : ExpressionDisplay.ToText(value, timeZone: null),
            MissingPaths = missingPaths
        };
    }

    public static AutomationExpressionPreview Render(Automation automation, string? ruleKey, string? template, bool allowsEvent)
    {
        if (!AutomationExpressionCheck.TryParseTemplate(template, allowsEvent, out var parsed, out var error))
        {
            return new AutomationExpressionPreview { Error = error };
        }

        var evaluation = CreateEvaluation(automation, ruleKey);
        var text = evaluation.Render(parsed);
        return new AutomationExpressionPreview { Value = text, Text = text, MissingPaths = [.. evaluation.MissingPaths] };
    }

    /// <summary>Resolves the property or operation an action targets, or null when the path leads nowhere.</summary>
    public static RegisteredSubjectProperty? ResolveTarget(Automation automation, string? ruleKey, string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : AutomationPaths.ResolveTarget(automation.Services.PathResolver, path, GetAnchor(automation, ruleKey));

    /// <summary>The saved rule with the key, or a rule outside the graph, from which relative paths lead nowhere.</summary>
    internal static IInterceptorSubject GetAnchor(Automation automation, string? ruleKey) =>
        ruleKey is not null && automation.Rules.TryGetValue(ruleKey, out var rule) ? rule : new AutomationRule();

    private static AutomationEvaluation CreateEvaluation(Automation automation, string? ruleKey)
    {
        var services = automation.Services;
        return new AutomationEvaluation(
            services.PathResolver, GetAnchor(automation, ruleKey), services.TimeProvider.GetUtcNow(), services.TimeProvider.LocalTimeZone);
    }
}
