using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using Namotion.Interceptor.Expressions;
using Namotion.Interceptor;

namespace HomeBlaze.Components;

/// <summary>
/// A text argument of a widget that is a template, such as <c>Label="Wallbox {{ subject/ChargingPower }}"</c>. The text is
/// parsed when it changes and rendered on every call, so that a render records every value it reads. A text without
/// <c>{{</c> is returned as written. One instance belongs to one argument of one widget component.
/// </summary>
public sealed class WidgetText
{
    private const string ExpressionStart = "{{";

    private readonly string _argumentName;
    private readonly ExpressionUse _use;
    private ParsedText? _parsed;

    /// <summary>Creates the text of an argument.</summary>
    /// <param name="argumentName">The name of the argument, which starts its error, such as <c>Label</c>.</param>
    /// <param name="use"><see cref="ExpressionUse.WidgetText"/> for a widget whose <c>Path</c> binds <c>value</c> and
    /// <c>subject</c>, <see cref="ExpressionUse.WidgetTextWithoutPath"/> for a widget without a <c>Path</c>.</param>
    public WidgetText(string argumentName, ExpressionUse use)
    {
        _argumentName = argumentName;
        _use = use;
    }

    /// <summary>
    /// Renders the text with a bound value. Paths without a variable and the names of maps resolve from the widget.
    /// </summary>
    /// <param name="text">The text as written, or null.</param>
    /// <param name="resolver">The resolver of the paths and the maps.</param>
    /// <param name="widget">The widget, which paths resolve from.</param>
    /// <param name="binding">The value and the subject of the <c>Path</c> of the widget, or an empty binding.</param>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for that of the server.</param>
    /// <param name="error">The widget argument error, such as <c>Label: expected ')' at position 5</c>, or null.</param>
    /// <returns>The rendered text, the text as written when it has no expression, or null when it has an error.</returns>
    public string? Render(
        string? text,
        SubjectPathResolver resolver,
        IInterceptorSubject widget,
        in ValueBinding binding,
        ITimeZoneDisplay? timeZone,
        out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(text) || !text.Contains(ExpressionStart, StringComparison.Ordinal))
        {
            return text;
        }

        var parsed = GetParsed(text);
        if (parsed.Template is not { } template)
        {
            error = parsed.Error;
            return null;
        }

        var context = new ValueBindingContext(resolver, widget, binding, timeZone);
        return template.Render(ref context);
    }

    /// <summary>
    /// Renders the text with the value and the subject that the path binds, as <see cref="ValueBinding.FromPath"/> binds
    /// them. The path is resolved only when the text has an expression.
    /// </summary>
    /// <param name="text">The text as written, or null.</param>
    /// <param name="resolver">The resolver of the paths and the maps.</param>
    /// <param name="widget">The widget, which paths resolve from.</param>
    /// <param name="path">The <c>Path</c> of the widget, or null.</param>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for that of the server.</param>
    /// <param name="error">The widget argument error, or null.</param>
    /// <returns>The rendered text, the text as written when it has no expression, or null when it has an error.</returns>
    public string? Render(
        string? text,
        SubjectPathResolver resolver,
        IInterceptorSubject widget,
        string? path,
        ITimeZoneDisplay? timeZone,
        out string? error)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(ExpressionStart, StringComparison.Ordinal))
        {
            error = null;
            return text;
        }

        return Render(text, resolver, widget, ValueBinding.FromPath(resolver, widget, path), timeZone, out error);
    }

    private ParsedText GetParsed(string text)
    {
        var parsed = _parsed;
        if (parsed is null || !string.Equals(parsed.Text, text, StringComparison.Ordinal))
        {
            // Replaced as a whole, so that a render on another thread sees the text and its template together.
            parsed = Parse(text);
            _parsed = parsed;
        }

        return parsed;
    }

    private ParsedText Parse(string text)
    {
        if (!TextTemplate.TryParse(text, ExpressionDisplay.PathSeparator, out var template, out var error))
        {
            return new ParsedText(text, null, $"{_argumentName}: {error}");
        }

        foreach (var expression in template.Expressions)
        {
            if (ExpressionDisplay.Check(expression, _use) is { } checkError)
            {
                return new ParsedText(text, null, $"{_argumentName}: {checkError}");
            }
        }

        return new ParsedText(text, template, null);
    }

    private sealed record ParsedText(string Text, TextTemplate? Template, string? Error);
}
