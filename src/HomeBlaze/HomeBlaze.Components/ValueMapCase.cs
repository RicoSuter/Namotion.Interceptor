using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using HomeBlaze.Services.Expressions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components;

/// <summary>
/// One case of a <see cref="ValueMap"/>: which values it is for, and what they get.
/// </summary>
[ExcludeFromCreation]
[PageLayout]
[InterceptorSubject]
public partial class ValueMapCase : IConfigurable
{
    private ParsedColor? _parsedColor;
    private ParsedColor? _parsedTextColor;
    private ParsedCondition? _parsedCondition;
    private ParsedText? _parsedText;

    /// <summary>
    /// The value this case is for, as its raw text, for example "True" or the name of an enum value. Without it, and without
    /// <see cref="When"/>, the case is the default case.
    /// </summary>
    [Configuration]
    public partial string? Value { get; set; }

    /// <summary>
    /// The condition of the case, such as <c>subject/IsCharging and value &gt; 1000</c>, or null for none. <c>value</c> is the
    /// value the map is applied to and <c>subject</c> its subject; any other path resolves from the page that holds the map.
    /// A case matches when its <see cref="Value"/> and its condition hold.
    /// </summary>
    [Configuration]
    public partial string? When { get; set; }

    /// <summary>
    /// The text shown for the value, in which an expression between double braces, such as <c>{{ subject/ChargingPower }}</c>,
    /// shows its value with the pipes <c>display</c> and <c>raw</c>. Without it the formatted value is shown.
    /// </summary>
    [Configuration]
    [TextTemplate]
    public partial string? Text { get; set; }

    /// <summary>
    /// The color of the value: the name of a theme color, such as "Success", or a CSS color, such as "#ffffff80".
    /// See <see cref="ValueColor.TryParse"/> for what is taken.
    /// </summary>
    [Configuration]
    public partial string? Color { get; set; }

    /// <summary>
    /// Name of the icon shown for the value, for example "Lightbulb".
    /// </summary>
    [Configuration]
    public partial string? Icon { get; set; }

    /// <summary>
    /// The text color on a tile or a chip that the case fills, in the same forms as <see cref="Color"/>. Without it, or
    /// with "Default", the text has the color that goes with the fill.
    /// </summary>
    [Configuration]
    public partial string? TextColor { get; set; }

    /// <summary>
    /// Gets the parsed <see cref="Color"/>, or <see cref="ValueColor.Default"/> when it is no color, which
    /// <see cref="ValueReader.ResolveMap"/> reports. The result is kept until the text changes, so that a render parses
    /// nothing and gets the same instance.
    /// </summary>
    public ValueColor GetColor() => GetParsed(Color, ref _parsedColor).Color ?? ValueColor.Default;

    /// <summary>
    /// Gets the parsed <see cref="TextColor"/>, or <see cref="ValueColor.Default"/> when it is not set or no color, kept
    /// like <see cref="GetColor"/>.
    /// </summary>
    public ValueColor GetTextColor() => GetParsed(TextColor, ref _parsedTextColor).Color ?? ValueColor.Default;

    /// <summary>Gets whether the case has neither a value nor a condition, so that it matches every value.</summary>
    internal bool IsDefault => string.IsNullOrEmpty(Value) && string.IsNullOrEmpty(When);

    /// <summary>
    /// Gets what is wrong with the condition or the text, such as <c>When: expected ')' at position 5</c>, or null. Kept like
    /// <see cref="GetColor"/>, so that a render parses nothing.
    /// </summary>
    internal string? GetExpressionError()
    {
        if (GetCondition(When).Error is { } conditionError)
        {
            return $"When: {conditionError}";
        }

        return GetTemplate().Error is { } textError ? $"Text: {textError}" : null;
    }

    /// <summary>
    /// Gets whether the case matches a value: its <see cref="Value"/>, when set, equals the raw text of the value without
    /// regard to case, and its <see cref="When"/>, when set, is true. A condition with an error matches nothing.
    /// </summary>
    /// <param name="rawText">The raw text of the value of the binding, or null when it has none.</param>
    /// <param name="binding">The value and its subject.</param>
    /// <param name="resolver">The resolver of the paths of the condition.</param>
    internal bool Matches(string? rawText, in ValueBinding binding, SubjectPathResolver resolver)
    {
        var value = Value;
        if (!string.IsNullOrEmpty(value) && !string.Equals(value, rawText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var when = When;
        if (string.IsNullOrEmpty(when))
        {
            return true;
        }

        if (GetCondition(when).Expression is not { } condition)
        {
            return false;
        }

        var context = new ValueBindingContext(resolver, this, binding, timeZone: null);
        return condition.EvaluateCondition(ref context) == true;
    }

    /// <summary>
    /// Gets the text of the case for a value with its expressions evaluated, or null when the case has no text or its text
    /// has an error.
    /// </summary>
    /// <param name="binding">The value and its subject.</param>
    /// <param name="resolver">The resolver of the paths of the text.</param>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for that of the server.</param>
    internal string? GetText(in ValueBinding binding, SubjectPathResolver resolver, ITimeZoneDisplay? timeZone)
    {
        if (GetTemplate().Template is not { } template)
        {
            return null;
        }

        var context = new ValueBindingContext(resolver, this, binding, timeZone);
        return template.Render(ref context);
    }

    private ParsedCondition GetCondition(string? text)
    {
        var parsed = _parsedCondition;
        if (parsed is null || !string.Equals(parsed.Text, text, StringComparison.Ordinal))
        {
            // Replaced as a whole, so that a render on another thread sees the text and its expression together.
            parsed = ParsedCondition.Parse(text);
            _parsedCondition = parsed;
        }

        return parsed;
    }

    private ParsedText GetTemplate()
    {
        var text = Text;
        var parsed = _parsedText;
        if (parsed is null || !string.Equals(parsed.Text, text, StringComparison.Ordinal))
        {
            parsed = ParsedText.Parse(text);
            _parsedText = parsed;
        }

        return parsed;
    }

    /// <summary>Gets whether <see cref="Color"/> is a theme color, a CSS color that <see cref="ValueColor.TryParse"/> takes, or not set.</summary>
    internal bool IsColorValid() => GetParsed(Color, ref _parsedColor).Color is not null;

    /// <summary>Gets whether <see cref="TextColor"/> is a theme color, a CSS color that <see cref="ValueColor.TryParse"/> takes, or not set.</summary>
    internal bool IsTextColorValid() => GetParsed(TextColor, ref _parsedTextColor).Color is not null;

    private static ParsedColor GetParsed(string? text, ref ParsedColor? cache)
    {
        var parsed = cache;
        if (parsed is null || !string.Equals(parsed.Text, text, StringComparison.Ordinal))
        {
            // Replaced as a whole, so that a render on another thread sees the text and its color together.
            parsed = new ParsedColor(text, ValueColor.TryParse(text, out var color) ? color : null);
            cache = parsed;
        }

        return parsed;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private sealed record ParsedColor(string? Text, ValueColor? Color);

    private sealed record ParsedCondition(string? Text, ParsedExpression? Expression, string? Error)
    {
        public static ParsedCondition Parse(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new ParsedCondition(text, null, null);
            }

            if (!ParsedExpression.TryParse(text, out var expression, out var error))
            {
                return new ParsedCondition(text, null, error);
            }

            return ExpressionDisplay.Check(expression, ExpressionUse.CaseCondition) is { } checkError
                ? new ParsedCondition(text, null, checkError)
                : new ParsedCondition(text, expression, null);
        }
    }

    private sealed record ParsedText(string? Text, TextTemplate? Template, string? Error)
    {
        public static ParsedText Parse(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new ParsedText(text, null, null);
            }

            if (!TextTemplate.TryParse(text, out var template, out var error))
            {
                return new ParsedText(text, null, error);
            }

            foreach (var expression in template.Expressions)
            {
                if (ExpressionDisplay.Check(expression, ExpressionUse.CaseText) is { } checkError)
                {
                    return new ParsedText(text, null, checkError);
                }
            }

            return new ParsedText(text, template, null);
        }
    }
}
