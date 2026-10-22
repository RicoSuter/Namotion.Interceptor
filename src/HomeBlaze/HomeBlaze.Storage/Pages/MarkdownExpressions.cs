using System.Diagnostics.CodeAnalysis;
using System.Text;
using HomeBlaze.Services.Expressions;

namespace HomeBlaze.Storage.Pages;

/// <summary>
/// Parses the text between the braces of an expression in the text of a page and checks what a page takes: the pipes
/// <c>display</c> and <c>raw</c> without arguments, and no variable, because the text of a page has no value of its own.
/// </summary>
public static class MarkdownExpressions
{
    /// <summary>The pipe that formats the value as the property panel shows it. It is the default.</summary>
    public const string DisplayPipe = "display";

    /// <summary>The pipe that writes the value the same way on every system.</summary>
    public const string RawPipe = "raw";

    /// <summary>
    /// Parses the text, or returns null with the message to show in place of the value. A <c>\|</c>, which a table cell
    /// needs, is a <c>|</c>. A path with a space in a segment, as pages wrote one before expressions had operators, is
    /// read with the space percent-encoded.
    /// </summary>
    public static ParsedExpression? Parse(string text, out string? error)
    {
        var unescaped = text.Contains(@"\|", StringComparison.Ordinal)
            ? text.Replace(@"\|", "|", StringComparison.Ordinal)
            : text;

        if (!ParsedExpression.TryParse(unescaped, out var expression, out error))
        {
            // The error of the expression stays, unless the text is an old path with a space.
            if (!TryParseLegacyPath(unescaped, out expression))
            {
                return null;
            }
        }

        error = Check(expression);
        return error is null ? expression : null;
    }

    private static string? Check(ParsedExpression expression)
    {
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

        foreach (var path in expression.Paths)
        {
            if (ExpressionVariables.TryGetVariable(path, out var variable))
            {
                return $"'{variable}' is not available in the text of a page";
            }
        }

        return null;
    }

    // The path before the last '|' outside brackets, with its spaces outside brackets percent-encoded. Only a text that
    // then parses as one path counts, so that a mistake in an expression keeps its own message.
    private static bool TryParseLegacyPath(string text, [NotNullWhen(true)] out ParsedExpression? expression)
    {
        expression = null;
        var separator = FindLastPipeOutsideBrackets(text);
        var path = (separator < 0 ? text : text[..separator]).Trim();
        if (!path.Contains(' ') || !LooksLikeAPath(path))
        {
            return false;
        }

        var encoded = EncodeSpacesOutsideBrackets(path) + (separator < 0 ? string.Empty : text[separator..]);
        return ParsedExpression.TryParse(encoded, out expression, out _) && expression.SinglePath is not null;
    }

    // A space inside a bracket key, such as "Living Room" in "[Living Room]", stays as written because the key is looked
    // up that way.
    private static string EncodeSpacesOutsideBrackets(string path)
    {
        var builder = new StringBuilder(path.Length + 16);
        var depth = 0;
        foreach (var character in path)
        {
            if (character == '[')
            {
                depth++;
            }
            else if (character == ']' && depth > 0)
            {
                depth--;
            }
            else if (character == ' ' && depth == 0)
            {
                builder.Append("%20");
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    // Text with an operator character, or with an operator or a keyword between spaces, is a mistake in an expression
    // and not a path, such as "a - b" or "a = b".
    private static bool LooksLikeAPath(string path)
    {
        if (path.AsSpan().IndexOfAny("=!<>&()\"'") >= 0)
        {
            return false;
        }

        foreach (var piece in path.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (piece is "+" or "-" or "*" or "/" ||
                piece.Equals("and", StringComparison.OrdinalIgnoreCase) ||
                piece.Equals("or", StringComparison.OrdinalIgnoreCase) ||
                piece.Equals("not", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static int FindLastPipeOutsideBrackets(string text)
    {
        var depth = 0;
        var separator = -1;
        for (var index = 0; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '[':
                    depth++;
                    break;

                case ']' when depth > 0:
                    depth--;
                    break;

                case '|' when depth == 0:
                    separator = index;
                    break;
            }
        }

        return separator;
    }
}
