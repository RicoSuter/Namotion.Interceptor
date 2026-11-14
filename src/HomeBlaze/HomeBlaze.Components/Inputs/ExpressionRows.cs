using System.Text;
using HomeBlaze.Services.Display;

namespace HomeBlaze.Components.Inputs;

/// <summary>
/// The simple form of an expression: rows of <c>path operator value</c> joined with <c>and</c> or <c>or</c>, evaluated
/// with the usual precedence of <c>and</c> over <c>or</c>. An expression is shown as rows only when the rows write it
/// again token for token, so that the editor never changes an expression of any other form.
/// </summary>
internal static class ExpressionRows
{
    /// <summary>The character between the segments of a path, as the expressions of the host write it.</summary>
    public const char PathSeparator = ExpressionDisplay.PathSeparator;

    /// <summary>The comparison operators of a row, each with the label the editor shows for it.</summary>
    public static readonly IReadOnlyList<(string Symbol, string Label)> Operators =
        [("==", "equals"), ("!=", "is not"), ("<", "is below"), ("<=", "at most"), (">", "is above"), (">=", "at least")];

    public static readonly IReadOnlyList<string> Combinators = ["and", "or"];

    public static readonly IReadOnlyList<string> ArithmeticSymbols = ["+", "-", "*", "/"];

    private static readonly string[] Keywords = ["and", "or", "not", "true", "false", "null"];

    // The characters that end a token of the expression parser, so that a token holding one outside brackets is no path or value.
    private static readonly char[] Delimiters = ['(', ')', '"', '\'', '|', '&', '=', '!', '<', '>'];

    /// <summary>Reads the expression as rows; false for any other form. An empty expression has no rows.</summary>
    public static bool TryParse(string? text, out IReadOnlyList<ExpressionRow> rows)
    {
        rows = [];
        var tokens = ExpressionTokens.Split(text);
        if (tokens is null || (tokens.Count > 0 && (tokens.Count + 1) % 4 != 0))
        {
            return false;
        }

        var result = new List<ExpressionRow>();
        for (var index = 0; index < tokens.Count; index += 4)
        {
            var combinator = index == 0 ? string.Empty : tokens[index - 1].ToLowerInvariant();
            var row = new ExpressionRow(combinator, tokens[index], tokens[index + 1], tokens[index + 2]);
            if ((index > 0 && !Combinators.Contains(combinator)) || !IsCondition(row))
            {
                return false;
            }

            result.Add(row);
        }

        rows = result;
        return true;
    }

    /// <summary>Writes the rows as an expression, with one space between tokens.</summary>
    public static string Format(IReadOnlyList<ExpressionRow> rows)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < rows.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(' ').Append(rows[index].Combinator).Append(' ');
            }

            builder.Append(rows[index].ToCondition());
        }

        return builder.ToString();
    }

    public static bool IsKeyword(string token) => Keywords.Contains(token, StringComparer.OrdinalIgnoreCase);

    public static bool IsOperator(string token)
    {
        foreach (var (symbol, _) in Operators)
        {
            if (symbol == token)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The label of the operator, or the symbol itself when it is no operator of a row.</summary>
    public static string GetOperatorLabel(string? symbol)
    {
        foreach (var (operatorSymbol, label) in Operators)
        {
            if (operatorSymbol == symbol)
            {
                return label;
            }
        }

        return symbol ?? string.Empty;
    }

    private static bool IsCondition(ExpressionRow row) =>
        IsOperator(row.Operator) && IsPath(row.Path) && IsOperand(row.Value);

    public static bool IsOperand(string token) =>
        IsQuoted(token) || (IsPlainToken(token) && !IsStructuralKeyword(token));

    private static bool IsStructuralKeyword(string token) =>
        Combinators.Contains(token, StringComparer.OrdinalIgnoreCase) || token.Equals("not", StringComparison.OrdinalIgnoreCase);

    // A leading minus negates what follows, so a token that starts with one is no path.
    public static bool IsPath(string token) =>
        IsPlainToken(token) && token[0] != '-' && !IsKeyword(token) && !StartsLikeNumber(token);

    // A token without a delimiter outside brackets is one word for the parser, with no operator or symbol glued to it.
    private static bool IsPlainToken(string token)
    {
        if (token.Length == 0 || ArithmeticSymbols.Contains(token))
        {
            return false;
        }

        var depth = 0;
        foreach (var character in token)
        {
            if (character == '[')
            {
                depth++;
            }
            else if (character == ']' && depth > 0)
            {
                depth--;
            }
            else if (depth == 0 && Delimiters.Contains(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsQuoted(string token) =>
        token.Length >= 2 && token[0] is '"' or '\'' && token.IndexOf(token[0], 1) == token.Length - 1;

    private static bool StartsLikeNumber(string token)
    {
        var unsigned = token[0] is '-' or '+' ? token.AsSpan(1) : token.AsSpan();
        return unsigned.Length > 0 && (char.IsDigit(unsigned[0]) || (unsigned[0] == '.' && unsigned.Length > 1 && char.IsDigit(unsigned[1])));
    }
}
