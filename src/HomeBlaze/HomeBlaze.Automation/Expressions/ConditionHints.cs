using System.Text.RegularExpressions;
using HomeBlaze.Automation.Runtime;

namespace HomeBlaze.Automation.Expressions;

/// <summary>
/// The hints of the editor for a condition that the automation checks again when the clock moves: a comparison of the
/// clock with <c>==</c> or <c>!=</c>, which never matches a moment, and a use of the clock that
/// <see cref="ExpressionClock"/> cannot schedule, which is checked every second.
/// </summary>
internal static partial class ConditionHints
{
    public const string ClockEquality =
        "The clock never equals a value exactly, so now or time with == or != does not match a moment: use >=, such as time >= 7h.";

    public const string EverySecond =
        "Checked every second, because when this use of the clock changes cannot be computed. " +
        "A comparison such as now - ./LastTriggeredAt > 30m or time >= 7h is checked only when its result can change.";

    public static IReadOnlyList<string> Get(string? text)
    {
        if (!AutomationExpressionCheck.TryParseExpression(text, allowsEvent: true, out var expression, out _) ||
            ExpressionClock.Create(expression) is not { } clock)
        {
            return [];
        }

        var hasEquality = ClockEqualityPattern().IsMatch(expression.Text);
        return (hasEquality, clock.IsEverySecond) switch
        {
            (true, true) => [ClockEquality, EverySecond],
            (true, false) => [ClockEquality],
            (false, true) => [EverySecond],
            _ => []
        };
    }

    // The clock and an equality operator within one comparison: the text between them holds no other comparison, no
    // "and", "or", parenthesis or quote.
    [GeneratedRegex("""(?<![\w./])(now|time)(?![\w/])((?!\b(and|or)\b|&&|\|\|)[^=!<>()"'])*(==|!=)|(==|!=)((?!\b(and|or)\b|&&|\|\|)[^=!<>()"'])*(?<![\w./])(now|time)(?![\w/])""")]
    private static partial Regex ClockEqualityPattern();
}
