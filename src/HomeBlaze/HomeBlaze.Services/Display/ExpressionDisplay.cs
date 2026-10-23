using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Services.Expressions;

namespace HomeBlaze.Services.Display;

/// <summary>Where an expression stands, which decides the pipes and variables it may use.</summary>
public enum ExpressionUse
{
    /// <summary>The text of a page: the pipes <c>display</c> and <c>raw</c>, and no variable.</summary>
    PageText,

    /// <summary>The text of a case of a value map: the pipes <c>display</c> and <c>raw</c>, and the variables.</summary>
    CaseText,

    /// <summary>The condition of a case of a value map: no pipe, and the variables.</summary>
    CaseCondition
}

/// <summary>
/// How the expressions of pages write their results, and which pipes and variables an expression may use where it stands.
/// </summary>
public static class ExpressionDisplay
{
    /// <summary>The pipe that formats a value as the property panel shows it. It is the default.</summary>
    internal const string DisplayPipe = "display";

    /// <summary>The pipe that writes a value the same way on every system.</summary>
    internal const string RawPipe = "raw";

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

    /// <summary>Applies the pipe <see cref="DisplayPipe"/> or <see cref="RawPipe"/>, which <see cref="Check"/> accepted.</summary>
    public static ExpressionValue ApplyFormat(string name, ExpressionValue input, ITimeZoneDisplay? timeZone) =>
        ExpressionValue.FromText(name.Equals(RawPipe, StringComparison.OrdinalIgnoreCase)
            ? input.IsText ? (string)input.Value! : RawValueFormatter.Format(input.Value)
            : ToText(input, timeZone));

    /// <summary>Gets what is wrong with the pipes and variables of an expression where it stands, or null.</summary>
    public static string? Check(ParsedExpression expression, ExpressionUse use)
    {
        if (use == ExpressionUse.CaseCondition && expression.Pipes.Length > 0)
        {
            return "A condition takes no pipes";
        }

        foreach (var pipe in expression.Pipes)
        {
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

        if (use == ExpressionUse.PageText)
        {
            foreach (var path in expression.Paths)
            {
                if (ExpressionVariables.TryGetVariable(path, out var variable))
                {
                    return $"'{variable}' is not available in the text of a page";
                }
            }
        }

        return null;
    }
}
