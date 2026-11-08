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

    /// <summary>Whether the rule has an event trigger, so that its expressions may read <c>event</c> and <c>subject</c>.</summary>
    public bool AllowsEvent => allowsEvent;

    public string? Validate(string text) => AutomationValidator.ValidateExpression(text, allowsEvent);

    /// <summary>The hints for a condition of a trigger, a rearm or Only if, which the automation checks again as the clock moves.</summary>
    public static IReadOnlyList<string> GetConditionHints(string text) => AutomationValidator.GetConditionHints(text);

    /// <summary>Validates an event filter, which always sees the captured event.</summary>
    public static string? ValidateFilter(string text) => AutomationValidator.ValidateExpression(text, allowsEvent: true);

    public string? ValidateTemplate(string text) => AutomationValidator.ValidateTemplate(text, allowsEvent);

    /// <summary>
    /// Prepares an expression for its preview: the function gives the value and the result with the current values each
    /// time it is called. Null when the expression is invalid.
    /// </summary>
    public Func<ExpressionPreview>? Preview(string text) =>
        AutomationPreview.TryParseExpression(text, allowsEvent, out var parsed, out _) ? () => Evaluate(parsed) : null;

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
