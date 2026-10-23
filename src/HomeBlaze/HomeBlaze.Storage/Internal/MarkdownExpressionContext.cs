using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using HomeBlaze.Services.Expressions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Evaluates an expression in the text of a page: a path resolves from the page like the path of a widget, the pipes
/// are those of <see cref="ExpressionDisplay"/>, and <c>map</c> applies a value map that the page names. Created on the stack for one evaluation.
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
        name.Equals(ExpressionDisplay.MapPipe, StringComparison.OrdinalIgnoreCase)
            ? ApplyMap(arguments[0], input)
            : ExpressionDisplay.ApplyFormat(name, input, timeZone);

    // The name of the map resolves from the page, like the Map of a widget. A value without a matching case keeps its text.
    private ExpressionValue ApplyMap(string mapName, ExpressionValue input)
    {
        IInterceptorSubject? map;
        try
        {
            map = pathResolver.ResolveScopedSubject(mapName, PathStyle.Canonical, page);
        }
        catch
        {
            map = null;
        }

        if (map is not IValueMap valueMap)
        {
            return ExpressionValue.FromText($"Cannot resolve map: {mapName}");
        }

        var binding = ValueBinding.FromExpressionValue(input);
        return ExpressionValue.FromText(valueMap.GetText(binding, pathResolver, timeZone) ?? ToText(input));
    }

    public string ToText(ExpressionValue result) => ExpressionDisplay.ToText(result, timeZone);
}
