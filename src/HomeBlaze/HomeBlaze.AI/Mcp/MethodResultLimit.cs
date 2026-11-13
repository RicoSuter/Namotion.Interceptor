namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Caps the length of the string results the method tools return, so one large string cannot fill a model's context.
/// Results of any other type, such as objects and collections, are not limited.
/// </summary>
internal static class MethodResultLimit
{
    /// <summary>The maximum length of a string result, in characters.</summary>
    public const int MaxStringLength = 64 * 1024;

    /// <summary>
    /// Returns a string longer than <see cref="MaxStringLength"/> cut to that length, followed by a line
    /// <c>[truncated: N characters]</c> where N is the number of characters cut off. Any other result is returned
    /// unchanged.
    /// </summary>
    public static object? Apply(object? result)
    {
        if (result is not string text || text.Length <= MaxStringLength)
        {
            return result;
        }

        var length = char.IsHighSurrogate(text[MaxStringLength - 1]) ? MaxStringLength - 1 : MaxStringLength;
        return string.Concat(text.AsSpan(0, length), $"\n[truncated: {text.Length - length} characters]");
    }
}
