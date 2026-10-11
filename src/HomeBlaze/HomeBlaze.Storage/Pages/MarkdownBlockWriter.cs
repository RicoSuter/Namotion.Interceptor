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
    /// Creates the text of a new, empty widget block with a fence of <paramref name="fenceLength"/> characters
    /// from a configuration like the one <see cref="WriteConfiguration"/> takes.
    /// Its lines end with <paramref name="lineEnding"/>, except the last one, which has no line ending.
    /// </summary>
    public static string CreateBlock(
        string typeName, string? id, string configurationJson, int fenceLength, string lineEnding = UnixLineEnding)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fenceLength, MarkdownBlockFence.MinimumLength);

        var (arguments, remainder) = SplitConfiguration(configurationJson);
        var head = new MarkdownBlockHead(fenceLength, typeName, id, arguments).Format();
        var closingLine = MarkdownBlockFence.Create(fenceLength);
        return remainder is null
            ? $"{head}{lineEnding}{closingLine}"
            : $"{head}{lineEnding}{FormatFence(remainder, lineEnding)}{lineEnding}{closingLine}";
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
