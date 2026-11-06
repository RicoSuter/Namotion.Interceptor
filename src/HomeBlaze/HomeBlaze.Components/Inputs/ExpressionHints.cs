using Namotion.Interceptor.Expressions;

namespace HomeBlaze.Components.Inputs;

/// <summary>
/// Warns about a bare word right of a comparison, which the grammar reads as text, as in <c>x &gt; Now</c>: a word that
/// differs from a variable only in case, and a word compared with a value that is a number, a date, a duration or a
/// boolean, which a text never equals. A word compared with a text or an enum, as in <c>Mode == Away</c>, is meant as text.
/// </summary>
internal static class ExpressionHints
{
    private static readonly string[] Variables =
    [
        ExpressionVariables.Now, ExpressionVariables.Time, ExpressionVariables.Event, ExpressionVariables.Subject, ExpressionVariables.Value
    ];

    private static readonly string[] ArithmeticSymbols = ["+", "-", "*", "/"];

    /// <param name="text">The expression.</param>
    /// <param name="pathSeparator">The separator of the paths of the host.</param>
    /// <param name="readValue">Reads the current value of the path left of a comparison, or null when the host has no values.</param>
    public static IReadOnlyList<string> GetBareWordHints(string? text, char pathSeparator, Func<string, object?>? readValue)
    {
        var tokens = ExpressionTokens.Split(text);
        if (tokens is null)
        {
            return [];
        }

        var hints = new List<string>();
        for (var index = 1; index + 1 < tokens.Count; index++)
        {
            if (ExpressionRows.Operators.Contains(tokens[index]) &&
                IsBareWord(tokens[index + 1], pathSeparator) &&
                !IsFollowedByArithmetic(tokens, index + 2) &&
                GetHint(tokens[index - 1], tokens[index + 1], readValue) is { } hint)
            {
                hints.Add(hint);
            }
        }

        return hints;
    }

    // In "x > Now - 5m" the word is an operand of arithmetic, which reads it as a path, not as text.
    private static bool IsFollowedByArithmetic(IReadOnlyList<string> tokens, int index) =>
        index < tokens.Count && ArithmeticSymbols.Contains(tokens[index]);

    private static string? GetHint(string left, string word, Func<string, object?>? readValue)
    {
        var variable = Array.Find(Variables, variable => variable.Equals(word, StringComparison.OrdinalIgnoreCase));
        if (variable is not null)
        {
            return $"'{word}' is read as the text \"{word}\". The variable is written in lowercase: {variable}.";
        }

        return IsNeverText(readValue?.Invoke(left))
            ? $"'{word}' is read as the text \"{word}\", which never equals the value of {left}. Write a path with '/', or a number."
            : null;
    }

    private static bool IsBareWord(string token, char pathSeparator) =>
        token.Length > 0 &&
        (char.IsLetter(token[0]) || token[0] == '_') &&
        token.IndexOf(pathSeparator) < 0 &&
        token.IndexOf('[') < 0 &&
        !ExpressionRows.IsKeyword(token) &&
        !Variables.Contains(token, StringComparer.Ordinal);

    private static bool IsNeverText(object? value) =>
        value is bool or DateTime or DateTimeOffset or TimeSpan or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
