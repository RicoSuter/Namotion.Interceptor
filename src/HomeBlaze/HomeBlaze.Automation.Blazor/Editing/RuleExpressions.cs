using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Abstractions.Expressions;
using HomeBlaze.Components.Abstractions.TimeZones;

namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// Validates and previews the expressions and templates of one rule for the fields of the editor, with relative paths
/// from the saved rule with the key. Previews show instants in the zone of <paramref name="timeZone"/>, as the pages do for the viewer,
/// and only the values that <paramref name="permissions"/> allow to read; null shows none.
/// </summary>
public sealed class RuleExpressions(Automation automation, string ruleKey, bool allowsEvent, ITimeZoneDisplay? timeZone = null, IUserPermissions? permissions = null)
{
    internal Automation Automation => automation;

    private IUserPermissions Permissions => permissions ?? DenyAllPermissions.Instance;

    /// <summary>Whether the rule has an event trigger, so that the expressions of its actions may read <c>event</c> and <c>subject</c>.</summary>
    internal bool AllowsEvent => allowsEvent;

    /// <summary>Validates an expression of an action, which reads the event when the rule has an event trigger.</summary>
    internal string? Validate(string text) => AutomationValidator.TryParseExpression(text, allowsEvent, out _, out var error) ? null : error;

    /// <summary>Validates a condition of a trigger, a rearm or Only if, which never sees an event.</summary>
    internal static string? ValidateCondition(string text) => AutomationValidator.TryParseExpression(text, allowsEvent: false, out _, out var error) ? null : error;

    /// <summary>Validates an event filter, which always sees the captured event.</summary>
    internal static string? ValidateFilter(string text) => AutomationValidator.TryParseExpression(text, allowsEvent: true, out _, out var error) ? null : error;

    internal string? ValidateTemplate(string text) => AutomationValidator.TryParseTemplate(text, allowsEvent, out _, out var error) ? null : error;

    /// <summary>
    /// Prepares a condition of a trigger, a rearm or Only if, or a path of one, for its preview: the function gives the
    /// value and the result with the current values each time it is called. Null when the condition is invalid.
    /// </summary>
    internal Func<ExpressionPreview>? PrepareConditionPreview(string text) =>
        AutomationValidator.TryParseExpression(text, allowsEvent: false, out var parsed, out _)
            ? () => AutomationPreview.Evaluate(automation, ruleKey, parsed, Permissions, timeZone)
            : null;

    /// <summary>
    /// Prepares a template for its preview: the function renders it with the current values each time it is called.
    /// Null when the template is invalid.
    /// </summary>
    internal Func<string>? PrepareTemplatePreview(string text) =>
        AutomationValidator.TryParseTemplate(text, allowsEvent, out var parsed, out _) ? () => AutomationPreview.Render(automation, ruleKey, parsed, Permissions) : null;
}
