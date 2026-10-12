using System.Globalization;
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
            var block = keyed.Segment.Block;
            if (!layoutBlocks.Contains(block))
            {
                continue;
            }

            if (!OwnsName(block))
            {
                return Format(block.Line, FormatUsedTwice(block.Head!.Id!));
            }

            if (keyed.Type is not { } type)
            {
                continue;
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

            if (!TryArrangeContent(type, slots, keyed.Segment, OwnsName, out _, out var contentError))
            {
                return Format(contentError.Line, contentError.Message);
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

            var contentPath = keyed.Place[..keyed.Place.LastIndexOf("/c:", StringComparison.Ordinal)] + "/" + key;
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
