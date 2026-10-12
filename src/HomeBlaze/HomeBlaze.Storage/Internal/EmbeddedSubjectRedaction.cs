using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Services;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Hides the values of secret configuration properties in the embedded subject blocks of a Markdown source, which
/// the page writes with all configuration of each subject.
/// </summary>
internal static class EmbeddedSubjectRedaction
{
    /// <summary>The text that replaces a secret value.</summary>
    public const string RedactedValue = "***";

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>
    /// Returns the source with each secret value in an embedded subject block replaced by <see cref="RedactedValue"/>,
    /// found through the configuration metadata of the subject the block created. A block without a subject or
    /// with JSON that cannot be read keeps only its type. Blocks without secret values stay as they are.
    /// </summary>
    public static string? Redact(string? source, IDictionary<string, IInterceptorSubject> children)
    {
        if (string.IsNullOrEmpty(source))
        {
            return source;
        }

        return MarkdownContentParser.SubjectBlockRegex().Replace(source, match =>
        {
            var name = match.Groups[1].Value;
            var json = match.Groups[2].Value;
            var redacted = children.TryGetValue(name, out var subject)
                ? RedactBlock(json, subject)
                : KeepTypeOnly(json);
            return redacted is null ? match.Value : $"```subject({name})\n{redacted}\n```";
        });
    }

    // Returns null when the block has no secret value, so it keeps its exact text.
    private static string? RedactBlock(string json, IInterceptorSubject subject)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return KeepTypeOnly(json);
        }

        if (node is not JsonObject jsonObject)
        {
            return KeepTypeOnly(json);
        }

        return RedactObject(jsonObject, subject) ? jsonObject.ToJsonString(IndentedOptions) : null;
    }

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
                foreach (var (key, entry) in entries.ToList())
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

    // Without the subject the secret properties are unknown, so only the type is shown.
    private static string KeepTypeOnly(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject jsonObject &&
                jsonObject[ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName] is JsonValue type)
            {
                return new JsonObject { [ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName] = type.DeepClone() }.ToJsonString(IndentedOptions);
            }
        }
        catch (JsonException)
        {
            // Unreadable JSON is replaced entirely.
        }

        return RedactedValue;
    }
}
