namespace HomeBlaze.Components.Inputs;

/// <summary>
/// Splits an expression at white space outside quotes and brackets, as <see cref="ExpressionScanner"/> reads them,
/// keeping every token as it is written.
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
        var scanner = new ExpressionScanner();
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (scanner.Read(character) && char.IsWhiteSpace(character))
            {
                if (start >= 0)
                {
                    tokens.Add(text[start..index]);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = index;
            }
        }

        if (scanner.IsOpen)
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
