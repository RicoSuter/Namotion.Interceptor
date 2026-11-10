namespace HomeBlaze.Components.Inputs;

/// <summary>A hint about a word of an expression, shown always or while <see cref="Condition"/> holds for the current value of <see cref="Path"/>.</summary>
/// <param name="Text">The hint.</param>
/// <param name="Path">The path whose current value decides whether the hint is shown, or null for a hint that is always shown.</param>
/// <param name="Condition">When the hint is shown.</param>
internal sealed record ExpressionHint(string Text, string? Path, ExpressionHintCondition Condition)
{
    /// <param name="value">The current value of <see cref="Path"/>, or null when it has none.</param>
    /// <param name="hasValues">Whether the host has values at all; without them, no path is known to lead to a value.</param>
    public bool IsShown(object? value, bool hasValues) => Condition switch
    {
        ExpressionHintCondition.Always => true,
        ExpressionHintCondition.PathIsMissing => !hasValues || value is null,
        _ => IsNeverText(value)
    };

    private static bool IsNeverText(object? value) =>
        value is bool or DateTime or DateTimeOffset or TimeSpan or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}

internal enum ExpressionHintCondition
{
    Always,

    /// <summary>Shown while the path leads to no value, which makes a path that is meant as a variable a plausible mistake.</summary>
    PathIsMissing,

    /// <summary>Shown while the value of the path is a number, a date, a duration or a boolean, which a text never equals.</summary>
    ValueIsNeverText
}
