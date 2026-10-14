using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Services;

namespace HomeBlaze.Storage.Pages;

/// <summary>Writes the configuration of one block back into the page text. Block content is never touched.</summary>
public static class MarkdownBlockWriter
{
    private const string UnixLineEnding = "\n";
    private const string WindowsLineEnding = "\r\n";

    private static readonly JsonSerializerOptions UnixIndentedOptions = new() { WriteIndented = true, NewLine = UnixLineEnding };
    private static readonly JsonSerializerOptions WindowsIndentedOptions = new() { WriteIndented = true, NewLine = WindowsLineEnding };

    /// <summary>
    /// Replaces the head line arguments and the json fence of <paramref name="block"/> with
    /// <paramref name="configurationJson"/>, a JSON object as the configuration serializer produces it
    /// with <see cref="ConfigurationNaming.CSharpProperty"/>. The keys are written as they are.
    /// </summary>
    public static string WriteConfiguration(string source, MarkdownBlock block, string configurationJson)
    {
        var head = block.Head ?? throw new ArgumentException("The block has no head.", nameof(block));
        var (arguments, remainder) = SplitConfiguration(configurationJson);

        var newHead = (head with { Arguments = arguments }).Format();
        var lineEnding = GetLineEnding(source, block.HeadSpan.End);
        var result = source;

        if (block.ConfigurationSpan is { } configurationSpan)
        {
            if (remainder is not null)
            {
                result = result.Remove(configurationSpan.Start, configurationSpan.Length)
                    .Insert(configurationSpan.Start, FormatFence(remainder, lineEnding));
            }
            else
            {
                var start = configurationSpan.Start;
                var end = configurationSpan.End;
                if (end < result.Length && result[end] == '\r')
                {
                    end++;
                }

                if (end < result.Length && result[end] == '\n')
                {
                    end++;
                }

                // A fence that ends the text has no line ending after it, so the one before it goes with it.
                if (end == configurationSpan.End)
                {
                    if (start > block.HeadSpan.End && result[start - 1] == '\n')
                    {
                        start--;
                    }

                    if (start > block.HeadSpan.End && result[start - 1] == '\r')
                    {
                        start--;
                    }
                }

                result = result.Remove(start, end - start);
            }
        }
        else if (remainder is not null)
        {
            result = result.Insert(block.HeadSpan.End, lineEnding + FormatFence(remainder, lineEnding));
        }

        return result.Remove(block.HeadSpan.Start, block.HeadSpan.Length).Insert(block.HeadSpan.Start, newHead);
    }

    /// <summary>
    /// Creates the text of a new block with a fence of <paramref name="fenceLength"/> characters from a
    /// configuration like the one <see cref="WriteConfiguration"/> takes.
    /// Its lines end with <paramref name="lineEnding"/>, except the last one, which has no line ending.
    /// </summary>
    /// <param name="name">The full type name of a widget block, or the name of a slot block.</param>
    /// <param name="id">The name of the block after <c>#</c>, or null.</param>
    /// <param name="configurationJson">The configuration as a JSON object.</param>
    /// <param name="fenceLength">The number of fence characters.</param>
    /// <param name="lineEnding">The line ending between the lines.</param>
    /// <param name="hasClosingLine">Whether the block ends with a closing line. Without one, what follows it must end it.</param>
    /// <param name="content">Lines that stand inside the block after its json block, or null.</param>
    public static string CreateBlock(
        string name,
        string? id,
        string configurationJson,
        int fenceLength,
        string lineEnding = UnixLineEnding,
        bool hasClosingLine = true,
        string? content = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fenceLength, MarkdownBlockFence.MinimumLength);

        var (arguments, remainder) = SplitConfiguration(configurationJson);
        var lines = new List<string> { new MarkdownBlockHead(fenceLength, name, id, arguments).Format() };
        if (remainder is not null)
        {
            lines.Add(FormatFence(remainder, lineEnding));
        }

        if (!string.IsNullOrEmpty(content))
        {
            lines.Add(content);
        }

        if (hasClosingLine)
        {
            lines.Add(MarkdownBlockFence.Create(fenceLength));
        }

        return string.Join(lineEnding, lines);
    }

    /// <summary>
    /// Gets where a new block of the slot <paramref name="slotName"/> of <paramref name="owner"/> goes: after
    /// the last block of that slot, written like that block, or without one at the end of the content of the
    /// owner, in the style of the page.
    /// </summary>
    /// <remarks>
    /// The style of the page is without closing lines when any widget or slot block of it has none. After a
    /// block of the slot, the new block has as many fence characters as that block, so that what ended that
    /// block ends the new one as well.
    /// </remarks>
    public static MarkdownBlockPlacement GetSlotBlockPlacement(MarkdownPage page, MarkdownBlock owner, string slotName)
    {
        var source = page.Source;

        MarkdownBlock? lastSibling = null;
        foreach (var child in owner.Children)
        {
            if (child.Kind == MarkdownBlockKind.Slot && child.Head!.Name == slotName && (lastSibling is null || child.Line > lastSibling.Line))
            {
                lastSibling = child;
            }
        }

        if (lastSibling is not null)
        {
            return new MarkdownBlockPlacement(
                lastSibling.EndLine + 1,
                lastSibling.FenceLength,
                lastSibling.HasClosingLine,
                GetIndentation(source, lastSibling),
                GetLineEnding(source, lastSibling.HeadSpan.End));
        }

        var hasClosingLine = true;
        foreach (var block in page.AllBlocks)
        {
            if (block.Kind is (MarkdownBlockKind.Widget or MarkdownBlockKind.Slot) && !block.HasClosingLine)
            {
                hasClosingLine = false;
                break;
            }
        }

        // Before the closing line of the owner, or after its last line of content.
        var line = owner.HasClosingLine ? owner.EndLine : owner.EndLine + 1;
        return new MarkdownBlockPlacement(
            line, owner.FenceLength + 1, hasClosingLine, GetIndentation(source, owner), GetLineEnding(source, owner.HeadSpan.End));
    }

    // The white space before the head line of the block on its line.
    private static string GetIndentation(string source, MarkdownBlock block) =>
        source[GetLineStart(source, block.Line)..block.HeadSpan.Start];

    /// <summary>
    /// Inserts <paramref name="text"/> as lines of their own before the 1-based <paramref name="line"/>. A line
    /// after the last line of the text appends them. The text has no line ending after its last line.
    /// </summary>
    public static string InsertLines(string source, int line, string text, string lineEnding, out MarkdownLineEdit edit)
    {
        var offset = GetLineStart(source, line);
        var insertedLineCount = text.AsSpan().Count('\n') + 1;

        // The last line has no line ending, so the new lines get one before them instead of after them.
        if (offset == source.Length && source.Length > 0 && source[^1] != '\n')
        {
            edit = new MarkdownLineEdit(source.AsSpan().Count('\n') + 2, 0, insertedLineCount);
            return source + lineEnding + text;
        }

        edit = new MarkdownLineEdit(source.AsSpan(0, offset).Count('\n') + 1, 0, insertedLineCount);
        return source.Insert(offset, text + lineEnding);
    }

    /// <summary>
    /// Removes the block with everything in it: the lines from its head line to its last line, which is its
    /// closing line or, without one, the last line of its content that is not empty.
    /// </summary>
    /// <remarks>
    /// One empty line stays in place of the block when the line before it and the line after it are both text.
    /// As neighbours they would be joined: the text after the block would continue the paragraph, the list item
    /// or the quote before it.
    /// </remarks>
    public static string RemoveBlock(string source, MarkdownBlock block, out MarkdownLineEdit edit)
    {
        var start = GetLineStart(source, block.Line);
        var end = GetLineStart(source, block.EndLine + 1);
        var removedLineCount = block.EndLine - block.Line + 1;

        if (IsTextLine(source, GetLineStart(source, block.Line - 1), start) && IsTextLine(source, end, GetLineStart(source, block.EndLine + 2)))
        {
            edit = new MarkdownLineEdit(block.Line, removedLineCount, 1);
            return source.Remove(start, end - start).Insert(start, GetLineEnding(source, block.HeadSpan.End));
        }

        edit = new MarkdownLineEdit(block.Line, removedLineCount, 0);
        return source.Remove(start, end - start);
    }

    // Whether the line between the two offsets has text that is no head line and no closing line of a block.
    // Next to such a fence line, and next to an empty line, nothing is joined. Mirrors TryReadFence and IsHead
    // of MarkdownBlockContainerParser, so a change of what a fence line is there is a change here: a fence
    // after four columns of indentation, which a tab always gives, is code, and a fence before text that starts
    // no head line, such as a time, is text. Both continue a paragraph before them.
    private static bool IsTextLine(string source, int lineStart, int nextLineStart)
    {
        // Only spaces and tabs are indentation, and only a line of nothing else is empty.
        var line = source.AsSpan(lineStart, nextLineStart - lineStart).TrimEnd("\r\n");
        var text = line.TrimStart(" \t");
        if (text.IsEmpty)
        {
            return false;
        }

        var indentation = line[..^text.Length];
        var fenceLength = text.IndexOfAnyExcept(MarkdownBlockFence.Character);
        if (fenceLength < 0)
        {
            fenceLength = text.Length;
        }

        if (fenceLength < MarkdownBlockFence.MinimumLength || indentation.Length >= 4 || indentation.Contains('\t'))
        {
            return true;
        }

        foreach (var character in text[fenceLength..])
        {
            if (!IsMarkdownWhiteSpace(character))
            {
                return !char.IsLetter(character) && character != '_' && character != '{';
            }
        }

        return false;
    }

    // What Markdig trims off the text after a fence: the white space of CommonMark, which has no vertical tab.
    private static bool IsMarkdownWhiteSpace(char character) =>
        character is ' ' or '\t' or '\f' or '\r' or '\n' ||
        char.GetUnicodeCategory(character) == UnicodeCategory.SpaceSeparator;

    /// <summary>Puts <paramref name="indentation"/> before every line of <paramref name="text"/>.</summary>
    public static string Indent(string text, string indentation, string lineEnding) =>
        indentation.Length == 0 ? text : indentation + text.Replace(lineEnding, lineEnding + indentation);

    // The offset of the first character of the 1-based line, or the length of the text for a line after the last one.
    private static int GetLineStart(string source, int line)
    {
        var offset = 0;
        for (var current = 1; current < line; current++)
        {
            var lineFeed = source.IndexOf('\n', offset);
            if (lineFeed < 0)
            {
                return source.Length;
            }

            offset = lineFeed + 1;
        }

        return offset;
    }

    /// <summary>
    /// Gets the line ending that text added to <paramref name="source"/> must use: the one that ends the line
    /// at <paramref name="offset"/>. When that is the last line, it is the first line ending of the text,
    /// and a line feed when the text has none.
    /// </summary>
    public static string GetLineEnding(string source, int offset = 0)
    {
        var lineFeed = source.IndexOf('\n', offset);
        if (lineFeed < 0)
        {
            lineFeed = source.IndexOf('\n');
        }

        return lineFeed > 0 && source[lineFeed - 1] == '\r' ? WindowsLineEnding : UnixLineEnding;
    }

    private static (List<MarkdownBlockArgument> Arguments, JsonObject? Remainder) SplitConfiguration(string configurationJson)
    {
        var configuration = JsonNode.Parse(configurationJson) as JsonObject
            ?? throw new ArgumentException("The configuration must be a JSON object.", nameof(configurationJson));

        var arguments = new List<MarkdownBlockArgument>();
        JsonObject? remainder = null;

        foreach (var (key, value) in configuration)
        {
            if (key == ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName || value is null)
            {
                continue;
            }

            switch (value.GetValueKind())
            {
                case JsonValueKind.String when IsValidHeadKey(key) && !HasLineBreak(value.GetValue<string>()):
                    arguments.Add(new MarkdownBlockArgument(key, value.GetValue<string>(), false));
                    break;

                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False when IsValidHeadKey(key):
                    arguments.Add(new MarkdownBlockArgument(key, value.ToJsonString(), false));
                    break;

                default:
                    remainder ??= new JsonObject();
                    remainder[key] = value.DeepClone();
                    break;
            }
        }

        return (arguments, remainder);
    }

    // Mirrors the key rule of MarkdownBlockHead.TryParse: letters, digits and underscore.
    private static bool IsValidHeadKey(string key)
    {
        if (key.Length == 0)
        {
            return false;
        }

        foreach (var character in key)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasLineBreak(string value) => value.AsSpan().IndexOfAny('\r', '\n') >= 0;

    private static string FormatFence(JsonObject remainder, string lineEnding)
    {
        var options = lineEnding == WindowsLineEnding ? WindowsIndentedOptions : UnixIndentedOptions;
        return $"```json{lineEnding}{remainder.ToJsonString(options)}{lineEnding}```";
    }
}
