using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Pages;
using Markdig.Syntax;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Hides the values of secret configuration properties in the blocks of a Markdown page, which keep the configuration
/// of the subjects they create in their head line arguments and json blocks.
/// </summary>
internal static class EmbeddedSubjectRedaction
{
    private const string RedactedValue = "***";

    private static readonly JsonSerializerOptions UnixIndentedOptions = new() { WriteIndented = true, NewLine = "\n" };
    private static readonly JsonSerializerOptions WindowsIndentedOptions = new() { WriteIndented = true, NewLine = "\r\n" };

    /// <summary>
    /// Returns the source of the page with each secret value in the configuration of a block replaced by <c>***</c>,
    /// found through the configuration metadata of the subject the block configures. Every argument value and the json
    /// block of a block without a subject become <c>***</c>, as does a json block that cannot be read as an object,
    /// everything after the fence and the name of a head line that cannot be read, and the content of a fenced code block
    /// of the old <c>subject(name)</c> form. A block without secret values keeps its exact text.
    /// </summary>
    /// <param name="page">The page as last parsed.</param>
    /// <param name="blockSubjects">The subjects of the blocks of <paramref name="page"/> by <see cref="MarkdownBlock.Index"/>.</param>
    public static string Redact(MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects) =>
        Redact(page, blockSubjects, out _);

    /// <inheritdoc cref="Redact(MarkdownPage, IReadOnlyDictionary{int, IInterceptorSubject})"/>
    /// <param name="page">The page as last parsed.</param>
    /// <param name="blockSubjects">The subjects of the blocks of <paramref name="page"/> by <see cref="MarkdownBlock.Index"/>.</param>
    /// <param name="redactedSpans">The spans of the replaced text in the returned source, in order.</param>
    public static string Redact(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, out List<MarkdownSpan> redactedSpans)
    {
        var source = page.Source;
        var replacements = new List<(MarkdownSpan Span, string Text)>();
        foreach (var block in page.AllBlocks)
        {
            AddBlockReplacements(source, block, blockSubjects.GetValueOrDefault(block.Index), replacements);
        }

        foreach (var codeBlock in page.Document.Descendants<FencedCodeBlock>())
        {
            if (MarkdownSegmentBuilder.IsOldSubjectFence(codeBlock) && codeBlock.Lines.Count > 0)
            {
                var start = codeBlock.Lines.Lines[0].Position;
                var end = MarkdownPageParser.GetLineEnd(source, codeBlock.Lines.Lines[codeBlock.Lines.Count - 1].Position);
                replacements.Add((new MarkdownSpan(start, end - start), RedactedValue));
            }
        }

        redactedSpans = [];
        if (replacements.Count == 0)
        {
            return source;
        }

        replacements.Sort(static (left, right) => left.Span.Start.CompareTo(right.Span.Start));
        var result = new StringBuilder(source.Length);
        var copied = 0;
        foreach (var (span, text) in replacements)
        {
            result.Append(source, copied, span.Start - copied);
            redactedSpans.Add(new MarkdownSpan(result.Length, text.Length));
            result.Append(text);
            copied = span.End;
        }

        return result.Append(source, copied, source.Length - copied).ToString();
    }

    private static void AddBlockReplacements(
        string source, MarkdownBlock block, IInterceptorSubject? subject, List<(MarkdownSpan Span, string Text)> replacements)
    {
        // A block at a place where blocks are not allowed has no parsed head, but its line can still hold arguments.
        var headText = source.Substring(block.HeadSpan.Start, block.HeadSpan.Length);
        var head = block.Head;
        if (head is not null || MarkdownBlockHead.TryParse(headText, out head, out _))
        {
            if (RedactArguments(head, subject) is { } redactedHead)
            {
                replacements.Add((block.HeadSpan, redactedHead.Format()));
            }
        }
        else if (RedactUnreadableHead(headText) is { } redactedHeadText)
        {
            replacements.Add((block.HeadSpan, redactedHeadText));
        }

        if (block.JsonFenceSpan is { } jsonFenceSpan)
        {
            var lineEnding = MarkdownBlockWriter.GetLineEnding(source, jsonFenceSpan.Start);
            var redactedFence = subject is not null && block.ConfigurationJson is { } json
                ? RedactConfiguration(json, subject, lineEnding)
                : FormatFence(RedactedValue, lineEnding);
            if (redactedFence is not null)
            {
                replacements.Add((jsonFenceSpan, redactedFence));
            }
        }
    }

    /// <summary>
    /// Keeps the fence and a valid name with its valid #id of a head line that cannot be read, and replaces the rest by
    /// <c>***</c>. Returns null when nothing follows them.
    /// </summary>
    private static string? RedactUnreadableHead(string headText)
    {
        var position = 0;
        while (position < headText.Length && headText[position] == MarkdownBlockFence.Character)
        {
            position++;
        }

        var keptLength = position;
        while (position < headText.Length && char.IsWhiteSpace(headText[position]))
        {
            position++;
        }

        var tokenStart = position;
        while (position < headText.Length && !char.IsWhiteSpace(headText[position]))
        {
            position++;
        }

        var token = headText[tokenStart..position];
        var idStart = token.IndexOf('#');
        var name = idStart < 0 ? token : token[..idStart];
        if (MarkdownBlockHead.IsValidName(name))
        {
            keptLength = tokenStart + (idStart >= 0 && MarkdownBlockHead.IsValidId(token[(idStart + 1)..]) ? token.Length : name.Length);
        }

        return string.IsNullOrWhiteSpace(headText[keptLength..]) ? null : $"{headText[..keptLength]} {RedactedValue}";
    }

    // Returns null when no argument value is hidden, so the head line keeps its exact text.
    private static MarkdownBlockHead? RedactArguments(MarkdownBlockHead head, IInterceptorSubject? subject)
    {
        if (head.Arguments.Count == 0)
        {
            return null;
        }

        // Without the subject the secret properties are unknown, so every value is hidden.
        var secretNames = subject?.GetConfigurationProperties()
            .Where(property => property.GetConfigurationMetadata()?.IsSecret == true)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var isRedacted = false;
        var arguments = new List<MarkdownBlockArgument>(head.Arguments.Count);
        foreach (var argument in head.Arguments)
        {
            if (secretNames is null || secretNames.Contains(argument.Key))
            {
                arguments.Add(argument with { Value = RedactedValue, IsQuoted = false });
                isRedacted = true;
            }
            else
            {
                arguments.Add(argument);
            }
        }

        return isRedacted ? head with { Arguments = arguments } : null;
    }

    // Returns null when the json block has no secret value, so it keeps its exact text.
    private static string? RedactConfiguration(string json, IInterceptorSubject subject, string lineEnding)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return FormatFence(RedactedValue, lineEnding);
        }

        if (node is not JsonObject jsonObject)
        {
            return FormatFence(RedactedValue, lineEnding);
        }

        var options = lineEnding == "\r\n" ? WindowsIndentedOptions : UnixIndentedOptions;
        return RedactObject(jsonObject, subject) ? FormatFence(jsonObject.ToJsonString(options), lineEnding) : null;
    }

    private static string FormatFence(string json, string lineEnding) => $"```json{lineEnding}{json}{lineEnding}```";

    private static bool RedactObject(JsonObject jsonObject, IInterceptorSubject subject)
    {
        var isRedacted = false;
        foreach (var property in subject.GetConfigurationProperties())
        {
            var keys = jsonObject
                .Where(pair => pair.Value is not null && pair.Key.Equals(property.Name, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToList();

            var isSecret = property.GetConfigurationMetadata()?.IsSecret == true;
            foreach (var key in keys)
            {
                if (isSecret)
                {
                    jsonObject[key] = RedactedValue;
                    isRedacted = true;
                }
                else
                {
                    isRedacted |= RedactNested(jsonObject[key]!, property.GetValue());
                }
            }
        }

        return isRedacted;
    }

    private static bool RedactNested(JsonNode node, object? value)
    {
        switch (value)
        {
            case IInterceptorSubject child when node is JsonObject childObject:
                return RedactObject(childObject, child);
            case IDictionary dictionary when node is JsonObject entries:
                // A key that is not a string, such as a number or an enum value, is written as its text.
                var isRedacted = false;
                foreach (DictionaryEntry dictionaryEntry in dictionary)
                {
                    var key = Convert.ToString(dictionaryEntry.Key, CultureInfo.InvariantCulture);
                    if (key is not null && entries[key] is { } entry)
                    {
                        isRedacted |= RedactNested(entry, dictionaryEntry.Value);
                    }
                }

                return isRedacted;
            case IEnumerable items and not string when node is JsonArray array:
                var index = 0;
                var isItemRedacted = false;
                foreach (var item in items)
                {
                    if (index < array.Count && array[index] is { } element)
                    {
                        isItemRedacted |= RedactNested(element, item);
                    }

                    index++;
                }

                return isItemRedacted;
            default:
                return false;
        }
    }
}
