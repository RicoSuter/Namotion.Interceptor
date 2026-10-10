using System.Text.Json;
using System.Text.Json.Nodes;

namespace HomeBlaze.Storage.Pages;

/// <summary>Writes the configuration of one block back into the page text. Block content is never touched.</summary>
public static class MarkdownBlockWriter
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>
    /// Replaces the head line arguments and the json fence of <paramref name="block"/> with
    /// <paramref name="configurationJson"/>, a JSON object as the configuration serializer produces it.
    /// </summary>
    public static string WriteConfiguration(string source, MarkdownBlock block, string configurationJson)
    {
        var head = block.Head ?? throw new ArgumentException("The block has no head.", nameof(block));
        var (arguments, remainder) = SplitConfiguration(configurationJson);

        var newHead = (head with { Arguments = arguments }).Format();
        var result = source;

        if (block.ConfigurationSpan is { } configurationSpan)
        {
            if (remainder is not null)
            {
                result = result.Remove(configurationSpan.Start, configurationSpan.Length)
                    .Insert(configurationSpan.Start, FormatFence(remainder));
            }
            else
            {
                var end = configurationSpan.End;
                if (end < result.Length && result[end] == '\r')
                {
                    end++;
                }

                if (end < result.Length && result[end] == '\n')
                {
                    end++;
                }

                result = result.Remove(configurationSpan.Start, end - configurationSpan.Start);
            }
        }
        else if (remainder is not null)
        {
            result = result.Insert(block.HeadSpan.End, "\n" + FormatFence(remainder));
        }

        return result.Remove(block.HeadSpan.Start, block.HeadSpan.Length).Insert(block.HeadSpan.Start, newHead);
    }

    /// <summary>Creates the text of a new, empty widget block.</summary>
    public static string CreateBlock(string typeName, string? id, string configurationJson)
    {
        var (arguments, remainder) = SplitConfiguration(configurationJson);
        var head = new MarkdownBlockHead(3, typeName, id, arguments).Format();
        return remainder is null
            ? $"{head}\n:::"
            : $"{head}\n{FormatFence(remainder)}\n:::";
    }

    private static (List<MarkdownBlockArgument> Arguments, JsonObject? Remainder) SplitConfiguration(string configurationJson)
    {
        var configuration = JsonNode.Parse(configurationJson) as JsonObject
            ?? throw new ArgumentException("The configuration must be a JSON object.", nameof(configurationJson));

        var arguments = new List<MarkdownBlockArgument>();
        JsonObject? remainder = null;

        foreach (var (key, value) in configuration)
        {
            if (key == "$type" || value is null)
            {
                continue;
            }

            switch (value.GetValueKind())
            {
                case JsonValueKind.String:
                    arguments.Add(new MarkdownBlockArgument(key, value.GetValue<string>(), false));
                    break;

                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
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

    private static string FormatFence(JsonObject remainder) =>
        $"```json\n{remainder.ToJsonString(IndentedOptions)}\n```";
}
