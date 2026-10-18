using System.Globalization;
using System.Text.Json.Nodes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Internal;

// Checking the layout blocks of a page before it is written, without creating anything.
public sealed partial class MarkdownContentParser
{
    /// <summary>
    /// Runs the checks of a parse that need no instance on every layout block of a page: its name, its key among the
    /// children of its content, its slots, its configuration and the arrangement of its content. Returns the error
    /// that the first such block would be shown as, or null when every layout block would be built.
    /// </summary>
    /// <param name="page">The parsed page.</param>
    /// <param name="keyedBlocks">The blocks of the page with their places, from <see cref="MarkdownSubjectKeys.Walk"/>.</param>
    /// <param name="layoutBlocks">The layout blocks of the page.</param>
    internal string? FindLayoutError(MarkdownPage page, IReadOnlyList<KeyedBlock> keyedBlocks, IReadOnlySet<MarkdownBlock> layoutBlocks)
    {
        var nameOwners = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var block in page.AllBlocks)
        {
            if (block.Head?.Id is { } id)
            {
                nameOwners.TryAdd(id, block.Index);
            }
        }

        bool OwnsName(MarkdownBlock block) => block.Head?.Id is not { } id || nameOwners[id] == block.Index;

        if (FindKeyUsedTwice(keyedBlocks, layoutBlocks) is { } keyError)
        {
            return keyError;
        }

        foreach (var keyed in keyedBlocks)
        {
            if (layoutBlocks.Contains(keyed.Segment.Block))
            {
                // Code of the types runs in these checks, and a failure of it is a refusal like any other.
                string? error;
                try
                {
                    error = FindBlockError(keyed, OwnsName);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    error = Format(keyed.Segment.Block.Line, exception.Message);
                }

                if (error is not null)
                {
                    return error;
                }
            }
        }

        return null;
    }

    private string? FindBlockError(KeyedBlock keyed, Func<MarkdownBlock, bool> ownsName)
    {
        var block = keyed.Segment.Block;
        if (!ownsName(block))
        {
            return Format(block.Line, FormatUsedTwice(block.Head!.Id!));
        }

        if (keyed.Type is not { } type)
        {
            return null;
        }

        var slots = SlotMap.For(type);
        if (slots.Error is not null)
        {
            return Format(block.Line, slots.Error);
        }

        if (!TryBuildConfiguration(type, block, out _, out var configurationError))
        {
            return Format(block.Line, configurationError!);
        }

        // A parse checks the values of the json block only while it applies them, so they are checked here.
        if (FindInvalidJsonValue(type, block) is { } valueError)
        {
            return Format(block.Line, valueError);
        }

        return TryArrangeContent(type, slots, keyed.Segment, ownsName, out _, out var contentError)
            ? null
            : Format(contentError.Line, contentError.Message);
    }

    private string? FindInvalidJsonValue(Type type, MarkdownBlock block)
    {
        if (block.ConfigurationJson is not { } json ||
            JsonNode.Parse(json, documentOptions: StrictJsonOptions) is not JsonObject configuration)
        {
            return null;
        }

        var properties = GetConfigurationProperties(type);
        foreach (var (key, value) in configuration)
        {
            if (key == TypeDiscriminator || !properties.TryGetValue(key, out var propertyType))
            {
                continue;
            }

            var valueJson = value?.ToJsonString() ?? "null";
            if (!_serializer.CanDeserialize(valueJson, propertyType, ConfigurationNaming.CSharpProperty))
            {
                return FormatInvalidValue(valueJson, key, propertyType);
            }
        }

        return null;
    }

    // Two blocks in one content with the same key are an error for the second one, such as a layout block whose name
    // has the shape of the key of a block without a name.
    private static string? FindKeyUsedTwice(IReadOnlyList<KeyedBlock> keyedBlocks, IReadOnlySet<MarkdownBlock> layoutBlocks)
    {
        var keys = new Dictionary<string, MarkdownBlock>(StringComparer.Ordinal);
        foreach (var keyed in keyedBlocks)
        {
            if (keyed.ContentKey is not { } key)
            {
                continue;
            }

            var contentPath = keyed.ContainerPath + "/" + key;
            if (!keys.TryAdd(contentPath, keyed.Segment.Block) &&
                (layoutBlocks.Contains(keyed.Segment.Block) || layoutBlocks.Contains(keys[contentPath])))
            {
                return Format(keyed.Segment.Block.Line, FormatUsedTwice(key));
            }
        }

        return null;
    }

    private static string Format(int line, string message) => $"Line {line.ToString(CultureInfo.InvariantCulture)}: {message}";
}
