using System.Collections;
using System.Globalization;
using System.Reflection;
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
    /// block of a block without a subject become <c>***</c>, also of a block inside an invalid block, as does every
    /// value in a json block that does not match the subject, the rest of a head line after a valid name when the line
    /// cannot be read, and the content of a fenced code block of the old <c>subject(name)</c> form. The view keeps
    /// the lines and the block structure of the page, and a block without secret values keeps its exact text.
    /// </summary>
    /// <param name="page">The page as last parsed.</param>
    /// <param name="blockSubjects">The subjects of the blocks of <paramref name="page"/> by <see cref="MarkdownBlock.Index"/>.</param>
    public static string Redact(MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects) =>
        Redact(page, blockSubjects, out _);

    /// <inheritdoc cref="Redact(MarkdownPage, IReadOnlyDictionary{int, IInterceptorSubject})"/>
    /// <param name="page">The page as last parsed.</param>
    /// <param name="blockSubjects">The subjects of the blocks of <paramref name="page"/> by <see cref="MarkdownBlock.Index"/>.</param>
    /// <param name="redactions">Each replaced span of the source with the span of its replacement in the returned view, in order.</param>
    public static string Redact(
        MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects, out List<Redaction> redactions)
    {
        var source = page.Source;
        var replacements = new List<(MarkdownSpan Span, string Text)>();

        // Every block node, also one inside an invalid block that the parser does not read.
        foreach (var container in page.Document.Descendants<MarkdownBlockContainer>())
        {
            var block = page.DescribeBlock(container);
            if (block.Kind == MarkdownBlockKind.Style)
            {
                continue;
            }

            var subject = block.Index >= 0 ? blockSubjects.GetValueOrDefault(block.Index) : null;
            AddHeadReplacement(source, block, subject, replacements);

            if (container.Count > 0 && container[0] is FencedCodeBlock { Info: "json" } jsonFence)
            {
                var redactedJson = subject is not null && block.ConfigurationJson is { } json
                    ? RedactConfiguration(json, subject, MarkdownBlockWriter.GetLineEnding(source, jsonFence.Span.Start))
                    : RedactedValue;
                if (redactedJson is not null)
                {
                    AddContentReplacement(source, jsonFence, redactedJson, replacements);
                }
            }
        }

        foreach (var codeBlock in page.Document.Descendants<FencedCodeBlock>())
        {
            if (MarkdownSegmentBuilder.IsOldSubjectFence(codeBlock))
            {
                AddContentReplacement(source, codeBlock, RedactedValue, replacements);
            }
        }

        redactions = [];
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
            redactions.Add(new Redaction(span, new MarkdownSpan(result.Length, text.Length)));
            result.Append(text);
            copied = span.End;
        }

        return result.Append(source, copied, source.Length - copied).ToString();
    }

    private static void AddHeadReplacement(
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
    }

    /// <summary>
    /// Replaces the content lines of a fenced code block and keeps its fence lines. Each line of the text after the first
    /// gets the characters that stand before the first content line, such as the indentation of a list item or the
    /// marker of a quote, so the code block stays where it is.
    /// </summary>
    private static void AddContentReplacement(
        string source, FencedCodeBlock codeBlock, string text, List<(MarkdownSpan Span, string Text)> replacements)
    {
        if (codeBlock.Lines.Count == 0)
        {
            return;
        }

        var start = codeBlock.Lines.Lines[0].Slice.Start;
        var end = MarkdownPageParser.GetLineEnd(source, codeBlock.Lines.Lines[codeBlock.Lines.Count - 1].Slice.Start);
        var lineStart = start == 0 ? 0 : source.LastIndexOf('\n', start - 1) + 1;
        var linePrefix = source[lineStart..start];
        var lineEnding = MarkdownBlockWriter.GetLineEnding(source, start);
        var prefixedText = text.Replace(lineEnding, lineEnding + linePrefix, StringComparison.Ordinal);
        replacements.Add((new MarkdownSpan(start, Math.Max(end, start) - start), prefixedText));
    }

    /// <summary>
    /// Keeps the fence and the longest valid name at the start of a head line that cannot be read, with a valid #id after
    /// the whole name, and replaces the rest by <c>***</c>, so the line stays a head line. Returns null when nothing
    /// follows them.
    /// </summary>
    private static string? RedactUnreadableHead(string headText)
    {
        var position = 0;
        while (position < headText.Length && headText[position] == MarkdownBlockFence.Character)
        {
            position++;
        }

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
        if (token.Length == 0)
        {
            return null;
        }

        // A head line starts with a letter, '_' or '{', so the first character always keeps the line a head line.
        var idStart = token.IndexOf('#');
        var name = idStart < 0 ? token : token[..idStart];
        var keptLength = 1;
        for (var length = name.Length; length > 0; length--)
        {
            if (MarkdownBlockHead.IsValidName(name[..length]))
            {
                keptLength = length;
                break;
            }
        }

        if (keptLength == name.Length && idStart >= 0 && MarkdownBlockHead.IsValidId(token[(idStart + 1)..]))
        {
            keptLength = token.Length;
        }

        var keptEnd = tokenStart + keptLength;
        return string.IsNullOrWhiteSpace(headText[keptEnd..]) ? null : $"{headText[..keptEnd]} {RedactedValue}";
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
            return RedactedValue;
        }

        if (node is not JsonObject jsonObject)
        {
            return RedactedValue;
        }

        var options = lineEnding == "\r\n" ? WindowsIndentedOptions : UnixIndentedOptions;
        return RedactObject(jsonObject, subject) ? jsonObject.ToJsonString(options) : null;
    }

    /// <summary>
    /// Hides the values of the secret properties of the subject, and every value whose key is no configuration property
    /// of it, then the values below the other properties as <see cref="RedactNested"/> does.
    /// </summary>
    private static bool RedactObject(JsonObject jsonObject, IInterceptorSubject subject)
    {
        var properties = subject.GetConfigurationProperties().ToList();
        var isRedacted = false;
        foreach (var (key, node) in jsonObject.ToList())
        {
            if (key == ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName || node is null)
            {
                continue;
            }

            var property = properties.Find(candidate => candidate.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (property is null || property.GetConfigurationMetadata()?.IsSecret == true)
            {
                jsonObject[key] = RedactedValue;
                isRedacted = true;
            }
            else if (RedactNested(node, property.GetValue(), out var isReplaced))
            {
                isRedacted = true;
                if (isReplaced)
                {
                    jsonObject[key] = RedactedValue;
                }
            }
        }

        return isRedacted;
    }

    /// <summary>
    /// Hides what in the JSON of a configuration value is secret or cannot be matched one to one with the value of the
    /// property, so that nothing that the metadata cannot vouch for is shown: an object or a list for a missing value or a
    /// value of another kind, a subject of another type, a list item or a dictionary entry without a counterpart.
    /// </summary>
    /// <param name="node">The JSON of the value.</param>
    /// <param name="value">The value of the property.</param>
    /// <param name="isReplaced">Set when the whole node must be replaced by <c>***</c>.</param>
    /// <returns>Whether anything was hidden.</returns>
    private static bool RedactNested(JsonNode node, object? value, out bool isReplaced)
    {
        isReplaced = false;
        if (node is JsonValue)
        {
            return false;
        }

        switch (value)
        {
            case IInterceptorSubject subject when node is JsonObject subjectObject && IsOfType(subjectObject, subject):
                return RedactObject(subjectObject, subject);

            case IDictionary dictionary when node is JsonObject entries:
                return RedactEntries(entries, dictionary);

            case IEnumerable items and not string when node is JsonArray array:
                return RedactItems(array, items);

            case not null and not (IInterceptorSubject or IEnumerable) when
                !value.GetType().IsPrimitive && !value.GetType().IsEnum && node is JsonObject plainObject:
                return RedactPlainObject(plainObject, value);

            default:
                isReplaced = true;
                return true;
        }
    }

    /// <summary>
    /// Hides the values of a plain object, which is no subject and has no secret properties, whose key is no public
    /// property of its runtime type, then the values below the other properties.
    /// </summary>
    private static bool RedactPlainObject(JsonObject jsonObject, object value)
    {
        var properties = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var isRedacted = false;
        foreach (var (key, node) in jsonObject.ToList())
        {
            if (key == ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName || node is null)
            {
                continue;
            }

            var property = Array.Find(properties, candidate =>
                candidate.GetIndexParameters().Length == 0 && candidate.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (property is null)
            {
                jsonObject[key] = RedactedValue;
                isRedacted = true;
            }
            else if (RedactNested(node, property.GetValue(value), out var isReplaced))
            {
                isRedacted = true;
                if (isReplaced)
                {
                    jsonObject[key] = RedactedValue;
                }
            }
        }

        return isRedacted;
    }

    private static bool RedactEntries(JsonObject entries, IDictionary dictionary)
    {
        // A key that is not a string, such as a number or an enum value, is written as its text.
        var keys = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var key in dictionary.Keys)
        {
            if (Convert.ToString(key, CultureInfo.InvariantCulture) is { } keyText)
            {
                keys.TryAdd(keyText, key);
            }
        }

        var isRedacted = false;
        foreach (var (keyText, entry) in entries.ToList())
        {
            if (entry is null)
            {
                continue;
            }

            if (!keys.TryGetValue(keyText, out var key))
            {
                entries[keyText] = RedactedValue;
                isRedacted = true;
            }
            else if (RedactNested(entry, dictionary[key], out var isReplaced))
            {
                isRedacted = true;
                if (isReplaced)
                {
                    entries[keyText] = RedactedValue;
                }
            }
        }

        return isRedacted;
    }

    private static bool RedactItems(JsonArray array, IEnumerable items)
    {
        var values = items.Cast<object?>().ToList();
        var isRedacted = false;
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not { } element)
            {
                continue;
            }

            if (index >= values.Count)
            {
                array[index] = RedactedValue;
                isRedacted = true;
            }
            else if (RedactNested(element, values[index], out var isReplaced))
            {
                isRedacted = true;
                if (isReplaced)
                {
                    array[index] = RedactedValue;
                }
            }
        }

        return isRedacted;
    }

    private static bool IsOfType(JsonObject jsonObject, IInterceptorSubject subject) =>
        jsonObject[ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName] is JsonValue type &&
        type.TryGetValue<string>(out var typeName) &&
        typeName == subject.GetType().FullName;

    /// <summary>A replaced span of the source and the span of its replacement in the view.</summary>
    internal readonly record struct Redaction(MarkdownSpan Source, MarkdownSpan Redacted);
}
