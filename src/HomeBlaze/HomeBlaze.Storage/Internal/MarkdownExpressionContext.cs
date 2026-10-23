using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using HomeBlaze.Services.Expressions;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Evaluates an expression in the text of a page: a path resolves from the page like the path of a widget, and the pipes
/// are those that <see cref="MarkdownExpressions"/> accepts. Created on the stack for one evaluation.
/// </summary>
internal readonly struct MarkdownExpressionContext(
    SubjectPathResolver pathResolver,
    IInterceptorSubject page,
    ITimeZoneDisplay? timeZone) : IExpressionContext
{
    public ExpressionValue ResolvePath(string path)
    {
        RegisteredSubjectProperty? property;
        try
        {
            property = pathResolver.ResolveScopedDisplayableProperty(path, PathStyle.Canonical, page);
        }
        catch
        {
            // The path is written by the author of the page, and a malformed one must not take the page down.
            return ExpressionValue.Missing;
        }

        return property is null ? ExpressionValue.Missing : ExpressionValue.FromProperty(property, property.GetValue());
    }

    // The text of a page binds no variable, and MarkdownExpressions refuses one before an expression is evaluated.
    public ExpressionValue ResolveVariable(string variable, string? memberPath) => ExpressionValue.Missing;

    public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) =>
        ExpressionValue.FromText(name.Equals(MarkdownExpressions.RawPipe, StringComparison.OrdinalIgnoreCase)
            ? input.IsText ? (string)input.Value! : RawValueFormatter.Format(input.Value)
            : ToText(input));

    /// <summary>
    /// Gets the text of a result: the text of a pipe as it is, otherwise the value formatted for display with the unit of
    /// its property, and an empty text for a missing value.
    /// </summary>
    public string ToText(ExpressionValue result)
    {
        if (result.IsText)
        {
            return (string)result.Value!;
        }

        if (result.Value is not { } value)
        {
            return string.Empty;
        }

        return result.Property is { } property
            ? property.GetPropertyDisplayValue(value, timeZone)
            : StateUnitExtensions.FormatDisplayValue(value, timeZone);
    }
}
