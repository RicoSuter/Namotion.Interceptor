using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Pages;
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
    /// block of a block without a subject become <c>***</c>, as does a json block that cannot be read as an object.
    /// A block without secret values keeps its exact text.
    /// </summary>
    /// <param name="page">The page as last parsed.</param>
    /// <param name="blockSubjects">The subjects of the blocks of <paramref name="page"/> by <see cref="MarkdownBlock.Index"/>.</param>
    public static string Redact(MarkdownPage page, IReadOnlyDictionary<int, IInterceptorSubject> blockSubjects)
    {
        var source = page.Source;
        List<(MarkdownSpan Span, string Text)>? replacements = null;
        foreach (var block in page.AllBlocks)
        {
            // A block at a place where blocks are not allowed has no parsed head, but its line can still hold arguments.
            var head = block.Head;
            if (head is null &&
                !MarkdownBlockHead.TryParse(source.Substring(block.HeadSpan.Start, block.HeadSpan.Length), out head, out _))
            {
                continue;
            }

            var subject = blockSubjects.GetValueOrDefault(block.Index);
            if (RedactArguments(head, subject) is { } redactedHead)
            {
                (replacements ??= []).Add((block.HeadSpan, redactedHead.Format()));
            }

            if (block is { ConfigurationSpan: { } configurationSpan, ConfigurationJson: { } json } &&
                RedactConfiguration(json, subject, MarkdownBlockWriter.GetLineEnding(source, configurationSpan.Start)) is { } redactedFence)
            {
                (replacements ??= []).Add((configurationSpan, redactedFence));
            }
        }

        if (replacements is null)
        {
            return source;
        }

        // From the end, so the spans of the replacements still to come stay valid.
        var result = source;
        foreach (var (span, text) in replacements.OrderByDescending(replacement => replacement.Span.Start))
        {
            result = string.Concat(result.AsSpan(0, span.Start), text, result.AsSpan(span.End));
        }

        return result;
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
    private static string? RedactConfiguration(string json, IInterceptorSubject? subject, string lineEnding)
    {
        if (subject is null)
        {
            return FormatFence(RedactedValue, lineEnding);
        }

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
            var key = jsonObject.Select(pair => pair.Key).FirstOrDefault(key => key.Equals(property.Name, StringComparison.OrdinalIgnoreCase));
            if (key is null || jsonObject[key] is not { } value)
            {
                continue;
            }

            if (property.GetConfigurationMetadata()?.IsSecret == true)
            {
                jsonObject[key] = RedactedValue;
                isRedacted = true;
            }
            else
            {
                isRedacted |= RedactNested(value, property.GetValue());
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
                var isRedacted = false;
                foreach (var (key, entry) in entries)
                {
                    if (entry is not null && dictionary.Contains(key))
                    {
                        isRedacted |= RedactNested(entry, dictionary[key]);
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
