using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Automation.Expressions;
using HomeBlaze.Components.Abstractions.Expressions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using Namotion.Interceptor;
using Namotion.Interceptor.Expressions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation;

/// <summary>
/// Evaluates what an editor shows next to the expressions of a rule: the value and the result of an expression with the
/// current values of the graph, a rendered template, and the target of an action. Paths start at the saved rule with the
/// key, as they do when the rule runs; for a rule that is not saved yet, relative paths lead nowhere. Executes and
/// records nothing. A value that the permissions of the viewer do not allow to read is missing, as in page expressions.
/// </summary>
public static class AutomationPreview
{
    // A rule outside the graph, from which relative paths lead nowhere; only read as the start of a path.
    private static readonly AutomationRule UnsavedRuleAnchor = new();

    /// <summary>
    /// Evaluates an expression from <see cref="AutomationValidator.TryParseExpression"/> against the live graph.
    /// <see cref="ExpressionPreview.Result"/> is true or false for a boolean and null for anything else or a missing value.
    /// The text shows dates in the zone of <paramref name="timeZone"/>, as pages do for the viewer, and in the server zone
    /// when it is null. <see cref="ExpressionPreview.Value"/> may be a live subject for an expression that leads to one;
    /// display it, do not modify it. A value that throws when it is read or shown is missing, and the text says why.
    /// </summary>
    /// <param name="automation">The automation of the rule.</param>
    /// <param name="ruleKey">The key of the saved rule that relative paths start at, or null for a rule that is not saved yet.</param>
    /// <param name="expression">The expression.</param>
    /// <param name="permissions">The permissions of the viewer; a value they do not allow to read is missing.</param>
    /// <param name="timeZone">The zone of the viewer for dates, or null for the server zone.</param>
    public static ExpressionPreview Evaluate(
        Automation automation, string? ruleKey, ParsedExpression expression, IUserPermissions permissions, ITimeZoneDisplay? timeZone = null)
    {
        try
        {
            var evaluation = CreateEvaluation(automation, ruleKey, permissions);
            var value = evaluation.Evaluate(expression);
            return value.IsMissing
                ? new ExpressionPreview(null, null, null)
                : new ExpressionPreview(value.Value as bool?, value.Value, ExpressionDisplay.ToText(value, timeZone));
        }
        catch (Exception exception)
        {
            return new ExpressionPreview(null, null, Unreadable(exception));
        }
    }

    /// <summary>
    /// Renders a template from <see cref="AutomationValidator.TryParseTemplate"/> with the current values in the server
    /// zone, which is where the runtime renders the texts it sends. A value that throws when it is read or shown makes the
    /// text say why instead.
    /// </summary>
    /// <param name="automation">The automation of the rule.</param>
    /// <param name="ruleKey">The key of the saved rule that relative paths start at, or null for a rule that is not saved yet.</param>
    /// <param name="template">The template.</param>
    /// <param name="permissions">The permissions of the viewer; a value they do not allow to read is missing.</param>
    public static string Render(Automation automation, string? ruleKey, TextTemplate template, IUserPermissions permissions)
    {
        try
        {
            return CreateEvaluation(automation, ruleKey, permissions).Render(template);
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
        string.IsNullOrWhiteSpace(path) ? null : automation.PathResolver.ResolveTarget(path, GetAnchor(automation, ruleKey));

    /// <summary>The saved rule with the key, or a rule outside the graph, from which relative paths lead nowhere.</summary>
    internal static AutomationRule GetAnchor(Automation automation, string? ruleKey) =>
        ruleKey is not null && automation.Rules.TryGetValue(ruleKey, out var rule) ? rule : UnsavedRuleAnchor;

    // A getter or a text of a device value that throws is a fault of that device, which the preview names in place of the value.
    private static string Unreadable(Exception exception) => $"cannot be read: {exception.Message}";

    private static AutomationEvaluation CreateEvaluation(Automation automation, string? ruleKey, IUserPermissions permissions)
    {
        return new AutomationEvaluation(
            automation.PathResolver, GetAnchor(automation, ruleKey), automation.TimeProvider.GetUtcNow(), automation.TimeProvider.LocalTimeZone,
            permissions: permissions);
    }
}
