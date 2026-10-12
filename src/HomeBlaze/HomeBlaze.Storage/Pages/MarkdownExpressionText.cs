namespace HomeBlaze.Storage.Pages;

/// <summary>
/// The text between the braces of an expression, split into its path and its format.
/// </summary>
/// <param name="Path">The path without the format.</param>
/// <param name="Format">The format, <see cref="ExpressionFormat.Display"/> when none is written.</param>
/// <param name="Error">What is wrong with the format, or null.</param>
public readonly record struct MarkdownExpressionText(string Path, ExpressionFormat Format, string? Error)
{
    /// <summary>
    /// Splits the text at its last <c>|</c> outside square brackets. A <c>|</c> inside brackets is part of a key.
    /// </summary>
    public static MarkdownExpressionText Parse(string text)
    {
        var separator = FindFormatSeparator(text);
        if (separator < 0)
        {
            return new MarkdownExpressionText(text.Trim(), ExpressionFormat.Display, null);
        }

        // A table cell needs the separator escaped, and the text of the page still has the backslash.
        var pathEnd = separator > 0 && text[separator - 1] == '\\' ? separator - 1 : separator;
        var path = text[..pathEnd].Trim();
        var format = text[(separator + 1)..].Trim();

        if (format.Equals("display", StringComparison.OrdinalIgnoreCase))
        {
            return new MarkdownExpressionText(path, ExpressionFormat.Display, null);
        }

        if (format.Equals("raw", StringComparison.OrdinalIgnoreCase))
        {
            return new MarkdownExpressionText(path, ExpressionFormat.Raw, null);
        }

        return new MarkdownExpressionText(path, ExpressionFormat.Display, $"Unknown format '{format}'");
    }

    private static int FindFormatSeparator(string text)
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
