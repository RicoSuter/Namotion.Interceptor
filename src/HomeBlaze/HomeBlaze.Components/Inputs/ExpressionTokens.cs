namespace HomeBlaze.Components.Inputs;

/// <summary>
/// Splits an expression at white space outside quotes and brackets, keeping every token as it is written. Like the
/// expression parser, it reads a quote inside brackets as part of the key, and a bracket inside a quote as text.
/// </summary>
internal static class ExpressionTokens
{
    /// <summary>Gets the tokens, or null when a quote or a bracket is not closed.</summary>
    public static IReadOnlyList<string>? Split(string? text)
    {
        var tokens = new List<string>();
        if (text is null)
        {
            return tokens;
        }

        var start = -1;
        var quote = '\0';
        var bracketDepth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quote == '\0' && bracketDepth == 0 && char.IsWhiteSpace(character))
            {
                if (start >= 0)
                {
                    tokens.Add(text[start..index]);
                    start = -1;
                }

                continue;
            }

            if (start < 0)
            {
                start = index;
            }

            if (quote != '\0')
            {
                quote = character == quote ? '\0' : quote;
            }
            else if (character is '"' or '\'' && bracketDepth == 0)
            {
                quote = character;
            }
            else if (character == '[')
            {
                bracketDepth++;
            }
            else if (character == ']' && bracketDepth > 0)
            {
                bracketDepth--;
            }
        }

        if (quote != '\0' || bracketDepth != 0)
        {
            return null;
        }

        if (start >= 0)
        {
            tokens.Add(text[start..]);
        }

        return tokens;
    }
}
