using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Services;
using Markdig.Helpers;

namespace HomeBlaze.Storage.Pages;

/// <summary>
/// Pure text functions for the head line and the json block of a block, and for inserting and removing whole
/// blocks. They know nothing about subjects.
/// </summary>
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
    /// Creates the text of a new, empty widget block with a fence of <paramref name="fenceLength"/> characters
    /// from a configuration like the one <see cref="WriteConfiguration"/> takes.
    /// Its lines end with <paramref name="lineEnding"/>, except the last one, which has no line ending.
    /// </summary>
    public static string CreateBlock(
        string typeName, string? id, string configurationJson, int fenceLength, string lineEnding = UnixLineEnding) =>
        CreateBlock(typeName, id, configurationJson, fenceLength, lineEnding, hasClosingLine: true);

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
    internal static string CreateBlock(
        string name,
        string? id,
        string configurationJson,
        int fenceLength,
        string lineEnding,
        bool hasClosingLine,
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
    /// block ends the new one as well. The text of the page must have no lone carriage return, see
    /// <see cref="HasLoneCarriageReturn"/>.
    /// </remarks>
    internal static MarkdownBlockPlacement GetSlotBlockPlacement(MarkdownPage page, MarkdownBlock owner, string slotName)
    {
        var source = page.Source;
        if (GetSlotBlocks(owner, slotName).LastOrDefault() is { } lastSibling)
        {
            return new MarkdownBlockPlacement(
                lastSibling.EndLine + 1,
                lastSibling.FenceLength,
                lastSibling.HasClosingLine,
                GetIndentation(source, lastSibling),
                GetLineEnding(source, lastSibling.HeadSpan.End));
        }

        var hasClosingLine = !page.AllBlocks.Any(
            block => block.Kind is (MarkdownBlockKind.Widget or MarkdownBlockKind.Slot) && !block.HasClosingLine);

        // Before the closing line of the owner, or after its last line of content.
        var line = owner.HasClosingLine ? owner.EndLine : owner.EndLine + 1;
        return new MarkdownBlockPlacement(
            line, owner.FenceLength + 1, hasClosingLine, GetIndentation(source, owner), GetLineEnding(source, owner.HeadSpan.End));
    }

    /// <summary>
    /// Gets the slot blocks of <paramref name="owner"/> with the name <paramref name="slotName"/>, in the order of
    /// their lines.
    /// </summary>
    internal static IEnumerable<MarkdownBlock> GetSlotBlocks(MarkdownBlock owner, string slotName) =>
        // The parser lists the children of a block in the order of the text, and a block is a slot block only
        // directly inside a widget or slot block.
        owner.Children.Where(child => child.Kind == MarkdownBlockKind.Slot && child.Head!.Name == slotName);

    // The white space before the head line of the block on its line.
    internal static string GetIndentation(string source, MarkdownBlock block) =>
        source[(source.AsSpan(0, block.HeadSpan.Start).LastIndexOf('\n') + 1)..block.HeadSpan.Start];

    /// <summary>
    /// Inserts <paramref name="text"/> as lines of their own before the 1-based <paramref name="line"/>. A line
    /// after the last line of the text appends them. The text has no line ending after its last line.
    /// </summary>
    /// <remarks>
    /// <paramref name="source"/> must have no lone carriage return when the line comes from a parsed page, see
    /// <see cref="HasLoneCarriageReturn"/>.
    /// </remarks>
    internal static string InsertLines(string source, int line, string text, string lineEnding, out MarkdownLineEdit edit)
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
    /// Inserts the lines of a block before the 1-based <paramref name="line"/>, with an empty line between the block
    /// and a line before or after it that is not empty. A line after the last line of the text appends the block.
    /// <paramref name="blockText"/> has no line ending after its last line.
    /// </summary>
    public static string InsertBlockText(string source, int line, string blockText, string lineEnding) =>
        InsertBlockText(source, line, blockText, lineEnding, out _);

    /// <inheritdoc cref="InsertBlockText(string, int, string, string)"/>
    internal static string InsertBlockText(string source, int line, string blockText, string lineEnding, out MarkdownLineEdit edit)
    {
        var lineStart = GetLineStart(source, line);
        var text = blockText;
        if (line > 1 && !IsEmptyLine(source, GetLineStart(source, line - 1), lineStart))
        {
            text = lineEnding + text;
        }

        if (!IsEmptyLine(source, lineStart, GetLineStart(source, line + 1)))
        {
            text += lineEnding;
        }

        return InsertLines(source, line, text, lineEnding, out edit);
    }

    /// <summary>Gets the 1-based line that <see cref="InsertLines"/> takes to append after the last line of text.</summary>
    internal static int GetAppendLine(string source)
    {
        var lineFeedCount = source.AsSpan().Count('\n');
        return source.Length == 0 || source[^1] == '\n' ? lineFeedCount + 1 : lineFeedCount + 2;
    }

    /// <summary>Gets the lines from <paramref name="line"/> to <paramref name="endLine"/>, without the line ending of the last one.</summary>
    internal static string GetLines(string source, int line, int endLine)
    {
        var start = GetLineStart(source, line);
        var end = GetLineStart(source, endLine + 1);
        return source[start..end].TrimEnd('\n').TrimEnd('\r');
    }

    /// <summary>Gets whether an empty line stands right before or right after the lines, as between blocks that stand apart.</summary>
    internal static bool HasEmptyLineAround(string source, int line, int endLine)
    {
        var start = GetLineStart(source, line);
        var end = GetLineStart(source, endLine + 1);
        return (line > 1 && IsEmptyLine(source, GetLineStart(source, line - 1), start)) ||
               (end < source.Length && IsEmptyLine(source, end, GetLineStart(source, endLine + 2)));
    }

    /// <summary>
    /// Replaces the lines from <paramref name="line"/> to <paramref name="endLine"/> with <paramref name="text"/>, which has
    /// no line ending after its last line. An empty line keeps it apart from a line of text right before or after it.
    /// </summary>
    /// <remarks><paramref name="source"/> must have no lone carriage return, see <see cref="HasLoneCarriageReturn"/>.</remarks>
    internal static string ReplaceLines(string source, int line, int endLine, string text, string lineEnding, out MarkdownLineEdit edit)
    {
        var start = GetLineStart(source, line);
        var end = GetLineStart(source, endLine + 1);
        if (line > 1 && IsTextLine(source, GetLineStart(source, line - 1), start))
        {
            text = lineEnding + text;
        }

        if (IsTextLine(source, end, GetLineStart(source, endLine + 2)))
        {
            text += lineEnding;
        }

        // Lines that end the text without a line ending give the new last line none either.
        var replacement = end == source.Length && source.Length > 0 && source[^1] != '\n' ? text : text + lineEnding;
        edit = new MarkdownLineEdit(line, endLine - line + 1, text.AsSpan().Count('\n') + 1);
        return string.Concat(source.AsSpan(0, start), replacement, source.AsSpan(end));
    }

    /// <summary>Takes <paramref name="indentation"/> off the start of every line of <paramref name="text"/>, which has line feeds, that starts with it.</summary>
    internal static string RemoveIndentation(string text, string indentation)
    {
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith(indentation, StringComparison.Ordinal))
            {
                lines[index] = lines[index][indentation.Length..];
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Removes the lines from <paramref name="line"/> to <paramref name="endLine"/> so that the text around them stays apart
    /// as before. Between two lines of text one empty line stays (see <see cref="RemoveBlock"/>). Of the empty lines around
    /// them, the one after goes with them when the one before is empty too or they start the text, and the one before when
    /// they end the text. When they end the text without a line ending, the text keeps ending without one.
    /// </summary>
    /// <remarks><paramref name="source"/> must have no lone carriage return, see <see cref="HasLoneCarriageReturn"/>.</remarks>
    internal static string CutLines(string source, int line, int endLine, out MarkdownLineEdit edit)
    {
        var start = GetLineStart(source, line);
        var end = GetLineStart(source, endLine + 1);
        var previousStart = GetLineStart(source, line - 1);
        var nextEnd = GetLineStart(source, endLine + 2);

        if (line > 1 && IsTextLine(source, previousStart, start) && IsTextLine(source, end, nextEnd))
        {
            edit = new MarkdownLineEdit(line, endLine - line + 1, 1);
            return source.Remove(start, end - start).Insert(start, GetLineEnding(source, start));
        }

        var isEmptyBefore = line > 1 && IsEmptyLine(source, previousStart, start);
        var isEmptyAfter = end < source.Length && IsEmptyLine(source, end, nextEnd);
        if (isEmptyAfter && (isEmptyBefore || line == 1))
        {
            end = nextEnd;
            endLine++;
        }
        else if (isEmptyBefore && end == source.Length)
        {
            start = previousStart;
            line--;
        }

        // The cut lines end the text without a line ending, so the line before them now ends it and gives up its own.
        if (end == source.Length && start > 0 && source[^1] != '\n')
        {
            start -= start > 1 && source[start - 2] == '\r' ? 2 : 1;
        }

        edit = new MarkdownLineEdit(line, endLine - line + 1, 0);
        return source.Remove(start, end - start);
    }

    /// <summary>
    /// Inserts <paramref name="text"/>, lines that <see cref="CutLines"/> cut, before the 1-based <paramref name="line"/>. An
    /// empty line separates them from a line before or after them that is not empty when they stood apart before
    /// (<paramref name="standsApart"/>) or that line is text, which they would join otherwise.
    /// </summary>
    /// <remarks><paramref name="source"/> must have no lone carriage return, see <see cref="HasLoneCarriageReturn"/>.</remarks>
    internal static string InsertMovedLines(
        string source, int line, string text, string lineEnding, bool standsApart, out MarkdownLineEdit edit)
    {
        var lineStart = GetLineStart(source, line);
        var nextLineStart = GetLineStart(source, line + 1);
        if (line > 1)
        {
            var previousLineStart = GetLineStart(source, line - 1);
            if (!IsEmptyLine(source, previousLineStart, lineStart) && (standsApart || IsTextLine(source, previousLineStart, lineStart)))
            {
                text = lineEnding + text;
            }
        }

        if (!IsEmptyLine(source, lineStart, nextLineStart) && (standsApart || IsTextLine(source, lineStart, nextLineStart)))
        {
            text += lineEnding;
        }

        return InsertLines(source, line, text, lineEnding, out edit);
    }

    // Whether the line between the two offsets has nothing but white space. The text after the last line ending is a
    // line of its own, and it is empty when the text ends with a line ending.
    private static bool IsEmptyLine(string source, int lineStart, int nextLineStart) =>
        source.AsSpan(lineStart, nextLineStart - lineStart).IsWhiteSpace();

    /// <summary>
    /// Removes the block with everything in it: the lines from its head line to its last line, which is its
    /// closing line or, without one, the last line of its content that is not empty.
    /// </summary>
    /// <remarks>
    /// One empty line stays in place of the block when the line before it and the line after it are both text.
    /// As neighbours they would be joined: the text after the block would continue the paragraph, the list item
    /// or the quote before it. <paramref name="source"/> must have no lone carriage return, see
    /// <see cref="HasLoneCarriageReturn"/>.
    /// </remarks>
    internal static string RemoveBlock(string source, MarkdownBlock block, out MarkdownLineEdit edit)
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
    // Next to such a fence line, and next to an empty line, nothing is joined. Mirrors TryReadFence of
    // MarkdownBlockContainerParser through the rules of MarkdownBlockFence: a fence indented as code and a
    // fence before text that starts no head line, such as a time, are text. Both continue a paragraph before them.
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

        // A tab reaches the fourth column, so only spaces need counting.
        if (fenceLength < MarkdownBlockFence.MinimumLength ||
            indentation.Contains('\t') ||
            MarkdownBlockFence.IsIndentedAsCode(indentation.Length))
        {
            return true;
        }

        // The white space that StringSlice.Trim takes off in TryReadFence, which is not that of char.IsWhiteSpace.
        foreach (var character in text[fenceLength..])
        {
            if (!CharHelper.IsWhitespace(character))
            {
                return !MarkdownBlockFence.StartsHeadLine(character);
            }
        }

        return false;
    }

    /// <summary>Puts <paramref name="indentation"/> before every line of <paramref name="text"/> that is not empty or only white space.</summary>
    internal static string Indent(string text, string indentation, string lineEnding)
    {
        if (indentation.Length == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + indentation.Length * 4);
        var remaining = text.AsSpan();
        while (true)
        {
            var lineEnd = remaining.IndexOf(lineEnding);
            var line = lineEnd < 0 ? remaining : remaining[..lineEnd];
            if (!line.IsWhiteSpace())
            {
                builder.Append(indentation);
            }

            builder.Append(line);
            if (lineEnd < 0)
            {
                return builder.ToString();
            }

            builder.Append(lineEnding);
            remaining = remaining[(lineEnd + lineEnding.Length)..];
        }
    }

    // The offset of the first character of the 1-based line, or the length of the text for a line after the last one.
    internal static int GetLineStart(string source, int line)
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
    /// Gets whether <paramref name="source"/> has a carriage return that no line feed follows. Markdig ends a line
    /// there as well, so the line numbers of a page parsed from such a text are not the ones that
    /// <see cref="GetSlotBlockPlacement"/>, <see cref="InsertLines"/> and <see cref="RemoveBlock"/> count.
    /// </summary>
    internal static bool HasLoneCarriageReturn(string source)
    {
        var text = source.AsSpan();
        for (var index = text.IndexOf('\r'); index >= 0; index = text.IndexOf('\r'))
        {
            if (index + 1 == text.Length || text[index + 1] != '\n')
            {
                return true;
            }

            text = text[(index + 2)..];
        }

        return false;
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
