using System.Collections.Immutable;

namespace HomeBlaze.Components.Inputs;

/// <summary>
/// Warns about a bare word right of a comparison, which the grammar reads as text, as in <c>x &gt; Now</c>: a word that
/// differs from a variable the host declares only in case, and a word compared with a value that is a number, a date, a duration or a
/// boolean, which a text never equals. A word compared with a text or an enum, as in <c>Mode == Away</c>, is meant as text.
/// </summary>
internal static class ExpressionHints
{
    private static readonly string[] ArithmeticSymbols = ["+", "-", "*", "/"];

    /// <param name="text">The expression.</param>
    /// <param name="pathSeparator">The separator of the paths of the host.</param>
    /// <param name="variables">The variables the host declares for its expressions.</param>
    /// <param name="readValue">Reads the current value of the path left of a comparison, or null when the host has no values.</param>
    public static IReadOnlyList<string> GetBareWordHints(string? text, char pathSeparator, ImmutableArray<string> variables, Func<string, object?>? readValue)
    {
        var split = ExpressionTokens.Split(text);
        if (split is null)
        {
            return [];
        }

        var tokens = split.Select(Unwrap).ToArray();
        var hints = new List<string>();
        for (var index = 0; index < tokens.Length; index++)
        {
            var isRightOfComparison = index >= 2 && ExpressionRows.Operators.Contains(tokens[index - 1]);
            var hint = isRightOfComparison && IsBareWord(tokens[index], pathSeparator, variables) && !IsFollowedByArithmetic(tokens, index + 1)
                ? GetTextHint(tokens[index - 2], tokens[index], variables, readValue)
                : GetCaseHint(tokens[index], pathSeparator, variables);
            if (hint is not null)
            {
                hints.Add(hint);
            }
        }

        return hints;
    }

    // Parentheses around a condition are not part of its tokens.
    private static string Unwrap(string token) => token.TrimStart('(').TrimEnd(')');

    // In "x > Now - 5m" the word is an operand of arithmetic, which reads it as a path, not as text.
    private static bool IsFollowedByArithmetic(string[] tokens, int index) =>
        index < tokens.Length &&
        tokens[index].Length > 0 &&
        ArithmeticSymbols.Contains(tokens[index][..1]) &&
        (tokens[index].Length == 1 || tokens[index][1] == '(');

    private static string? GetTextHint(string left, string word, ImmutableArray<string> variables, Func<string, object?>? readValue)
    {
        var variable = FindVariable(variables, word);
        if (variable is not null)
        {
            return $"'{word}' is read as the text \"{word}\". The variable is written in lowercase: {variable}.";
        }

        return IsNeverText(readValue?.Invoke(left))
            ? $"'{word}' is read as the text \"{word}\", which never equals the value of {left}. Write a path with '/', or a number."
            : null;
    }

    // Variables are case-sensitive, so a path that starts with one in another case is an ordinary path that does not exist.
    // A leading minus negates the path after it.
    private static string? GetCaseHint(string token, char pathSeparator, ImmutableArray<string> variables)
    {
        var path = token.StartsWith('-') ? token[1..] : token;
        if (path.Length == 0 || path[0] is '"' or '\'')
        {
            return null;
        }

        var end = path.AsSpan().IndexOfAny(pathSeparator, '[');
        var segment = end < 0 ? path : path[..end];
        var variable = FindVariable(variables, segment);
        return variable is not null && variable != segment
            ? $"'{segment}' is not the variable {variable}: variables are written in lowercase."
            : null;
    }

    private static bool IsBareWord(string token, char pathSeparator, ImmutableArray<string> variables) =>
        token.Length > 0 &&
        (char.IsLetter(token[0]) || token[0] == '_') &&
        token.IndexOf(pathSeparator) < 0 &&
        token.IndexOf('[') < 0 &&
        !ExpressionRows.IsKeyword(token) &&
        !variables.Contains(token, StringComparer.Ordinal);

    private static string? FindVariable(ImmutableArray<string> variables, string word)
    {
        foreach (var variable in variables)
        {
            if (variable.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return variable;
            }
        }

        return null;
    }

    private static bool IsNeverText(object? value) =>
        value is bool or DateTime or DateTimeOffset or TimeSpan or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
