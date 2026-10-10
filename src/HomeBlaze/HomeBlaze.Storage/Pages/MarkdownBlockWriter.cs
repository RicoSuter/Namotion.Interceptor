using System.Text.Json;
using System.Text.Json.Nodes;

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
    /// <paramref name="configurationJson"/>, a JSON object as the configuration serializer produces it.
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
            result = result.Insert(block.HeadSpan.End, lineEnding + FormatFence(remainder, lineEnding));
        }

        return result.Remove(block.HeadSpan.Start, block.HeadSpan.Length).Insert(block.HeadSpan.Start, newHead);
    }

    /// <summary>
    /// Creates the text of a new, empty widget block. Its lines end with <paramref name="lineEnding"/>,
    /// except the last one, which has no line ending.
    /// </summary>
    public static string CreateBlock(
        string typeName, string? id, string configurationJson, string lineEnding = UnixLineEnding)
    {
        var (arguments, remainder) = SplitConfiguration(configurationJson);
        var head = new MarkdownBlockHead(3, typeName, id, arguments).Format();
        return remainder is null
            ? $"{head}{lineEnding}:::"
            : $"{head}{lineEnding}{FormatFence(remainder, lineEnding)}{lineEnding}:::";
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
            if (key == "$type" || value is null)
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
