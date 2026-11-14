using System.Collections.Immutable;

namespace HomeBlaze.Components.Inputs;

/// <summary>
/// Warns about a bare word right of a comparison, which the grammar reads as text, as in <c>x &gt; Now</c>: a word that
/// differs from a variable the host declares only in case, and a word compared with a value that is a number, a date, a duration or a
/// boolean, which a text never equals. A word compared with a text or an enum, as in <c>Mode == Away</c>, is meant as text.
/// Also warns about a path that starts with a variable in another case, as in <c>Value &gt; 5</c>, while it leads to no
/// value.
/// </summary>
internal static class ExpressionHints
{
    private const char PathSeparator = ExpressionRows.PathSeparator;

    /// <summary>Finds the hints of the expression; a hint with a path is shown while its condition holds for the current value of the path.</summary>
    /// <param name="text">The expression.</param>
    /// <param name="variables">The variables the host declares for its expressions.</param>
    public static IReadOnlyList<ExpressionHint> Find(string? text, ImmutableArray<string> variables)
    {
        var split = ExpressionTokens.Split(text);
        if (split is null || split.Count == 0)
        {
            return [];
        }

        var tokens = new string[split.Count];
        for (var index = 0; index < tokens.Length; index++)
        {
            tokens[index] = Unwrap(split[index]);
        }

        List<ExpressionHint>? hints = null;
        for (var index = 0; index < tokens.Length; index++)
        {
            var isRightOfComparison = index >= 2 && ExpressionRows.IsOperator(tokens[index - 1]);
            var hint = isRightOfComparison && IsBareWord(tokens[index], variables) && !IsFollowedByArithmetic(tokens, index + 1)
                ? GetTextHint(tokens[index - 2], tokens[index], variables)
                : GetCaseHint(tokens[index], variables);
            if (hint is not null)
            {
                (hints ??= []).Add(hint);
            }
        }

        return hints ?? [];
    }

    // Parentheses around a condition are not part of its tokens.
    private static string Unwrap(string token) => token.TrimStart('(').TrimEnd(')');

    // In "x > Now - 5m" the word is an operand of arithmetic, which reads it as a path, not as text.
    private static bool IsFollowedByArithmetic(string[] tokens, int index) =>
        index < tokens.Length &&
        tokens[index].Length > 0 &&
        ExpressionRows.ArithmeticSymbols.Contains(tokens[index][..1]) &&
        (tokens[index].Length == 1 || tokens[index][1] == '(');

    private static ExpressionHint GetTextHint(string left, string word, ImmutableArray<string> variables)
    {
        var variable = FindVariable(variables, word);
        return variable is not null
            ? new ExpressionHint($"'{word}' is read as the text \"{word}\". The variable is written in lowercase: {variable}.", null, ExpressionHintCondition.Always)
            : new ExpressionHint(
                $"'{word}' is read as the text \"{word}\", which never equals the value of {left}. Write a path with '{PathSeparator}', or a number.",
                left,
                ExpressionHintCondition.ValueIsNeverText);
    }

    // Variables are case-sensitive, so a path that starts with one in another case is an ordinary path, which is a mistake
    // when it leads to no value. A leading minus negates the path after it.
    private static ExpressionHint? GetCaseHint(string token, ImmutableArray<string> variables)
    {
        var path = token.StartsWith('-') ? token[1..] : token;
        if (path.Length == 0 || path[0] is '"' or '\'')
        {
            return null;
        }

        var end = path.AsSpan().IndexOfAny(PathSeparator, '[');
        var segment = end < 0 ? path : path[..end];
        var variable = FindVariable(variables, segment);
        return variable is not null && variable != segment
            ? new ExpressionHint($"'{segment}' is not the variable {variable}: variables are written in lowercase.", path, ExpressionHintCondition.PathIsMissing)
            : null;
    }

    private static bool IsBareWord(string token, ImmutableArray<string> variables) =>
        token.Length > 0 &&
        (char.IsLetter(token[0]) || token[0] == '_') &&
        token.IndexOf(PathSeparator) < 0 &&
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
}
