using HomeBlaze.Automation.Expressions;
using HomeBlaze.Components.Abstractions.TimeZones;
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
    /// <summary>
    /// Evaluates the expression once against the live graph. <see cref="AutomationExpressionPreview.Result"/> is the value
    /// as a condition: true or false for a boolean, null for anything else or a missing value. The text shows dates in the
    /// zone of <paramref name="timeZone"/>, as pages do for the viewer, and in the server zone when it is null.
    /// <see cref="AutomationExpressionPreview.Value"/> may be a live subject for an expression that leads to one; display it, do not modify it.
    /// A value that throws when it is read or shown is missing, and the text says why.
    /// </summary>
    public static AutomationExpressionPreview Evaluate(
        Automation automation, string? ruleKey, string? expression, bool allowsEvent, ITimeZoneDisplay? timeZone = null)
    {
        if (!AutomationExpressionCheck.TryParseExpression(expression, allowsEvent, out var parsed, out var error))
        {
            return new AutomationExpressionPreview { Error = error };
        }

        try
        {
            var evaluation = CreateEvaluation(automation, ruleKey);
            var value = evaluation.Evaluate(parsed);
            return new AutomationExpressionPreview
            {
                Result = value.Value as bool?,
                Value = value.IsMissing ? null : value.Value,
                Text = value.IsMissing ? null : ExpressionDisplay.ToText(value, timeZone)
            };
        }
        catch (Exception exception)
        {
            return Unreadable(exception);
        }
    }

    /// <summary>
    /// Renders the template with the current values in the server zone, which is where the runtime renders the texts it sends.
    /// A value that throws when it is read or shown makes the text say why instead.
    /// </summary>
    public static AutomationExpressionPreview Render(Automation automation, string? ruleKey, string? template, bool allowsEvent)
    {
        if (!AutomationExpressionCheck.TryParseTemplate(template, allowsEvent, out var parsed, out var error))
        {
            return new AutomationExpressionPreview { Error = error };
        }

        try
        {
            var evaluation = CreateEvaluation(automation, ruleKey);
            var text = evaluation.Render(parsed);
            return new AutomationExpressionPreview { Value = text, Text = text };
        }
        catch (Exception exception)
        {
            return Unreadable(exception);
        }
    }

    /// <summary>
    /// Resolves the property or operation an action targets, or null when the path leads nowhere. The result is metadata for
    /// display only: callers must not read or write its value, which may be secret.
    /// </summary>
    public static RegisteredSubjectProperty? ResolveTarget(Automation automation, string? ruleKey, string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : AutomationPaths.ResolveTarget(automation.PathResolver, path, GetAnchor(automation, ruleKey));

    /// <summary>The saved rule with the key, or a rule outside the graph, from which relative paths lead nowhere.</summary>
    internal static IInterceptorSubject GetAnchor(Automation automation, string? ruleKey) =>
        ruleKey is not null && automation.Rules.TryGetValue(ruleKey, out var rule) ? rule : new AutomationRule();

    // A getter or a text of a device value that throws is a fault of that device, which the preview names in place of the value.
    private static AutomationExpressionPreview Unreadable(Exception exception) => new() { Text = $"cannot be read: {exception.Message}" };

    private static AutomationEvaluation CreateEvaluation(Automation automation, string? ruleKey)
    {
        return new AutomationEvaluation(
            automation.PathResolver, GetAnchor(automation, ruleKey), automation.TimeProvider.GetUtcNow(), automation.TimeProvider.LocalTimeZone);
    }
}
