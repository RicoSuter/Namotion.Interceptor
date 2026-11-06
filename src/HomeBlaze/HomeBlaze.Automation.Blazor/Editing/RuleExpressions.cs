using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Components.Inputs;

namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// Validates and previews the expressions and templates of one rule for the fields of the editor, with relative paths
/// from the saved rule with the key. Previews show instants in the zone of <paramref name="timeZone"/>, as the pages do for the viewer.
/// </summary>
public sealed class RuleExpressions(Automation automation, string ruleKey, bool allowsEvent, ITimeZoneDisplay? timeZone = null)
{
    public string? Validate(string text) => AutomationValidator.ValidateExpression(text, allowsEvent);

    /// <summary>Validates an event filter, which always sees the captured event.</summary>
    public static string? ValidateFilter(string text) => AutomationValidator.ValidateExpression(text, allowsEvent: true);

    public string? ValidateTemplate(string text) => AutomationValidator.ValidateTemplate(text, allowsEvent);

    /// <summary>The value and the result of an expression with the current values, or null when it is invalid.</summary>
    public ExpressionPreview? Preview(string text)
    {
        var preview = AutomationPreview.Evaluate(automation, ruleKey, text, allowsEvent, timeZone);
        return preview.Error is null ? new ExpressionPreview(preview.Result, preview.Value, preview.Text) : null;
    }

    /// <summary>The template rendered with the current values, or null when it is invalid.</summary>
    public string? RenderTemplate(string text) =>
        AutomationPreview.Render(automation, ruleKey, text, allowsEvent) is { Error: null } preview ? preview.Text : null;
}
