using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Components.Inputs;
using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// Validates and previews the expressions and templates of one rule for the fields of the editor, with relative paths
/// from the saved rule with the key. Previews show instants in the zone of <paramref name="timeZone"/>, as the pages do for the viewer.
/// </summary>
public sealed class RuleExpressions(Automation automation, string ruleKey, bool allowsEvent, ITimeZoneDisplay? timeZone = null)
{
    public Automation Automation => automation;

    /// <summary>Whether the rule has an event trigger, so that the expressions of its actions may read <c>event</c> and <c>subject</c>.</summary>
    public bool AllowsEvent => allowsEvent;

    /// <summary>Validates an expression of an action, which reads the event when the rule has an event trigger.</summary>
    public string? Validate(string text) => AutomationValidator.ValidateExpression(text, allowsEvent);

    /// <summary>Validates a condition of a trigger, a rearm or Only if, which never sees an event.</summary>
    public static string? ValidateCondition(string text) => AutomationValidator.ValidateExpression(text, allowsEvent: false);

    /// <summary>The hints for a condition of a trigger, a rearm or Only if, which the automation checks again as the clock moves.</summary>
    public static IReadOnlyList<string> GetConditionHints(string text) => AutomationValidator.GetConditionHints(text);

    /// <summary>Validates an event filter, which always sees the captured event.</summary>
    public static string? ValidateFilter(string text) => AutomationValidator.ValidateExpression(text, allowsEvent: true);

    public string? ValidateTemplate(string text) => AutomationValidator.ValidateTemplate(text, allowsEvent);

    /// <summary>
    /// Prepares a condition of a trigger, a rearm or Only if, or a path of one, for its preview: the function gives the
    /// value and the result with the current values each time it is called. Null when the condition is invalid.
    /// </summary>
    public Func<ExpressionPreview>? PreviewCondition(string text) =>
        AutomationPreview.TryParseExpression(text, allowsEvent: false, out var parsed, out _) ? () => Evaluate(parsed) : null;

    /// <summary>
    /// Prepares a template for its preview: the function renders it with the current values each time it is called.
    /// Null when the template is invalid.
    /// </summary>
    public Func<string?>? Template(string text) =>
        AutomationPreview.TryParseTemplate(text, allowsEvent, out var parsed, out _) ? () => AutomationPreview.Render(automation, ruleKey, parsed).Text : null;

    private ExpressionPreview Evaluate(ParsedExpression parsed)
    {
        var preview = AutomationPreview.Evaluate(automation, ruleKey, parsed, timeZone);
        return new ExpressionPreview(preview.Result, preview.Value, preview.Text);
    }
}
