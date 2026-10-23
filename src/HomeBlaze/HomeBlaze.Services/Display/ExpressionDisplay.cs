using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;
using Namotion.Interceptor.Expressions;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Display;

/// <summary>Where an expression stands, which decides the pipes and variables it may use.</summary>
public enum ExpressionUse
{
    /// <summary>The text of a page: the pipes <c>display</c>, <c>raw</c> and <c>map</c>, and no variable.</summary>
    PageText,

    /// <summary>
    /// The text of a case of a value map: the pipes <c>display</c> and <c>raw</c>, and the variables. It may not use
    /// <c>map</c>, so that a map cannot reach itself through the text of its cases.
    /// </summary>
    CaseText,

    /// <summary>The condition of a case of a value map: no pipe, and the variables.</summary>
    CaseCondition,

    /// <summary>
    /// A text argument of a widget that binds <c>value</c> and <c>subject</c> from its <c>Path</c>, such as the label of a
    /// value tile: the pipes <c>display</c>, <c>raw</c> and <c>map</c>, and the variables.
    /// </summary>
    WidgetText,

    /// <summary>
    /// A text argument of a widget without a <c>Path</c>, such as the title of a card: the pipes <c>display</c>,
    /// <c>raw</c> and <c>map</c>, and no variable.
    /// </summary>
    WidgetTextWithoutPath
}

/// <summary>
/// How the expressions of pages write their results, and which pipes and variables an expression may use where it stands.
/// </summary>
public static class ExpressionDisplay
{
    /// <summary>The character between the segments of the paths in the expressions of pages.</summary>
    public const char PathSeparator = '/';

    /// <summary>The pipe that formats a value as the property panel shows it. It is the default.</summary>
    internal const string DisplayPipe = "display";

    /// <summary>The pipe that writes a value the same way on every system.</summary>
    internal const string RawPipe = "raw";

    /// <summary>The pipe that applies a value map to the value, <c>map:name</c>.</summary>
    public const string MapPipe = "map";

    /// <summary>
    /// Gets the text that shows a result: the text of a pipe as it is, otherwise the value formatted for display with the
    /// unit of its property, and an empty text for a missing value.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for that of the server.</param>
    public static string ToText(ExpressionValue result, ITimeZoneDisplay? timeZone)
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

    /// <summary>Applies the pipe <c>display</c> or <c>raw</c>, which <see cref="Check"/> accepted.</summary>
    public static ExpressionValue ApplyFormat(string name, ExpressionValue input, ITimeZoneDisplay? timeZone) =>
        ExpressionValue.FromText(name.Equals(RawPipe, StringComparison.OrdinalIgnoreCase)
            ? input.IsText ? (string)input.Value! : RawValueFormatter.Format(input.Value)
            : ToText(input, timeZone));

    /// <summary>
    /// Applies a pipe that <see cref="Check"/> accepted: <c>map</c> with the map that <paramref name="scope"/> names,
    /// otherwise <c>display</c> or <c>raw</c>.
    /// </summary>
    /// <param name="name">The name of the pipe.</param>
    /// <param name="arguments">The arguments of the pipe; <c>map</c> has the name of the map as its one argument.</param>
    /// <param name="input">The value the pipe applies to.</param>
    /// <param name="resolver">The resolver of the name of a map.</param>
    /// <param name="scope">The subject the name of a map resolves from, such as the page or the widget.</param>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for that of the server.</param>
    public static ExpressionValue ApplyPipe(
        string name,
        ImmutableArray<string> arguments,
        ExpressionValue input,
        SubjectPathResolver resolver,
        IInterceptorSubject scope,
        ITimeZoneDisplay? timeZone) =>
        name.Equals(MapPipe, StringComparison.OrdinalIgnoreCase)
            ? ApplyMap(resolver, scope, arguments[0], input, timeZone)
            : ApplyFormat(name, input, timeZone);

    /// <summary>
    /// Applies a value map to a value: the text of the first case that matches, otherwise the text of the value. A name
    /// that leads to no map gives <c>Cannot resolve map: name</c>, and a map with a mistake gives its message.
    /// </summary>
    /// <param name="resolver">The resolver of the name of the map and of the paths of its cases.</param>
    /// <param name="scope">The subject the name resolves from, such as the page or the widget, like the <c>Map</c> of a widget.</param>
    /// <param name="mapName">The name or path of the map.</param>
    /// <param name="input">The value, bound as <see cref="ValueBinding.FromExpressionValue"/> binds it.</param>
    /// <param name="timeZone">The time zone of the viewer for dates, or null for that of the server.</param>
    internal static ExpressionValue ApplyMap(
        SubjectPathResolver resolver,
        IInterceptorSubject scope,
        string mapName,
        ExpressionValue input,
        ITimeZoneDisplay? timeZone)
    {
        IInterceptorSubject? map;
        try
        {
            map = resolver.ResolveScopedSubject(mapName, PathStyle.Canonical, scope);
        }
        catch
        {
            // The name is written by the author of a page, and a malformed one must not take the page down.
            map = null;
        }

        if (map is not IValueMap valueMap)
        {
            return ExpressionValue.FromText($"Cannot resolve map: {mapName}");
        }

        if (valueMap.GetError() is { } error)
        {
            return ExpressionValue.FromText(error);
        }

        var binding = ValueBinding.FromExpressionValue(input);
        return ExpressionValue.FromText(valueMap.GetText(binding, resolver, timeZone) ?? ToText(input, timeZone));
    }

    /// <summary>Gets what is wrong with the pipes and variables of an expression where it stands, or null.</summary>
    public static string? Check(ParsedExpression expression, ExpressionUse use)
    {
        if (use == ExpressionUse.CaseCondition && expression.Pipes.Length > 0)
        {
            return "A condition takes no pipes";
        }

        for (var index = 0; index < expression.Pipes.Length; index++)
        {
            var pipe = expression.Pipes[index];
            if (pipe.Name.Equals(MapPipe, StringComparison.OrdinalIgnoreCase))
            {
                if (use == ExpressionUse.CaseText)
                {
                    return "The pipe 'map' cannot be used in the text of a case";
                }

                if (index > 0)
                {
                    return "The pipe 'map' must be the first pipe";
                }

                if (pipe.Arguments.Length != 1)
                {
                    return "The pipe 'map' takes the name of a value map, such as map:wallbox";
                }

                continue;
            }

            if (!pipe.Name.Equals(DisplayPipe, StringComparison.OrdinalIgnoreCase) &&
                !pipe.Name.Equals(RawPipe, StringComparison.OrdinalIgnoreCase))
            {
                return $"Unknown pipe '{pipe.Name}'";
            }

            if (pipe.Arguments.Length > 0)
            {
                return $"The pipe '{pipe.Name}' takes no arguments";
            }
        }

        var placeWithoutVariables = use switch
        {
            ExpressionUse.PageText => "the text of a page",
            ExpressionUse.WidgetTextWithoutPath => "a widget without a Path",
            _ => null
        };

        foreach (var path in expression.Paths)
        {
            if (!ExpressionVariables.TryGetVariable(path, PathSeparator, out var variable, out var memberPath))
            {
                continue;
            }

            if (placeWithoutVariables is not null)
            {
                return $"'{variable}' is not available in {placeWithoutVariables}";
            }

            if (GetVariablePathError(variable, memberPath) is { } pathError)
            {
                return pathError;
            }
        }

        return null;
    }

    private static string? GetVariablePathError(string variable, string? memberPath)
    {
        if (memberPath is null)
        {
            return null;
        }

        if (variable == ExpressionVariables.Value)
        {
            return "'value' has no members: write subject/... to read a property of its subject";
        }

        if (memberPath.Length == 0)
        {
            return "expected a name after 'subject/'";
        }

        if (memberPath[0] == PathSeparator)
        {
            return "a path after 'subject/' cannot start with '/'";
        }

        return null;
    }
}
