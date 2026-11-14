namespace HomeBlaze.AI.Blazor;

/// <summary>
/// Splits Markdown that is still streaming into a stable prefix, whose blocks are complete, and the tail after it.
/// </summary>
internal static class AgentStreamingMarkdown
{
    /// <summary>
    /// Returns the length of the longest prefix of <paramref name="text"/> that ends after a blank line outside a
    /// fenced code block, or 0 when there is none. A blank line ends paragraphs, lists and tables, so the prefix
    /// renders the same when more text follows; the unfinished last line never counts.
    /// </summary>
    /// <remarks>
    /// Loose lists, list continuation paragraphs and reference links defined later can still render differently until
    /// the final reply.
    /// </remarks>
    public static int FindStableLength(ReadOnlySpan<char> text)
    {
        var stableLength = 0;
        var fenceCharacter = '\0';
        var fenceLength = 0;
        var lineStart = 0;
        int lineLength;
        while ((lineLength = text[lineStart..].IndexOf('\n')) >= 0)
        {
            var line = text.Slice(lineStart, lineLength).Trim();
            lineStart += lineLength + 1;
            if (fenceCharacter == '\0')
            {
                if (line.IsEmpty)
                {
                    stableLength = lineStart;
                }
                else if (TryGetFence(line, out fenceCharacter, out fenceLength))
                {
                    // A blank line inside the code block does not end it, so boundaries wait for the closing fence.
                }
            }
            else if (TryGetFence(line, out var character, out var length) &&
                     character == fenceCharacter && length >= fenceLength && length == line.Length)
            {
                fenceCharacter = '\0';
            }
        }

        return stableLength;
    }

    // Matches indented fences too, such as inside list items; a misread line only holds the boundary back.
    private static bool TryGetFence(ReadOnlySpan<char> line, out char character, out int length)
    {
        character = '\0';
        length = 0;
        if (line.IsEmpty || line[0] is not ('`' or '~'))
        {
            return false;
        }

        while (length < line.Length && line[length] == line[0])
        {
            length++;
        }

        if (length < 3)
        {
            length = 0;
            return false;
        }

        character = line[0];
        return true;
    }
}
