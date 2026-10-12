using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Tells the layout blocks of a page from the blocks that create or configure subjects. A layout block is a style block
/// or a block of a type marked with <see cref="PageLayoutAttribute"/>, inside the page, a style block or another layout
/// block, whose head line and json block hold only single values of non-secret configuration properties of its type.
/// Such a block holds nothing secret, so it is shown unredacted, and with the permission to edit page layout it may change.
/// </summary>
internal static class MarkdownPageLayout
{
    private static readonly ConcurrentDictionary<Type, LayoutType?> LayoutTypes = new();

    /// <summary>Gets the layout description of a type, or null when it is no layout type.</summary>
    public static LayoutType? GetLayoutType(Type type) => LayoutTypes.GetOrAdd(type, static key =>
    {
        if (key.GetCustomAttribute<PageLayoutAttribute>(inherit: false) is not { } attribute)
        {
            return null;
        }

        var properties = key
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<ConfigurationAttribute>(true) is { IsSecret: false })
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        return new LayoutType(
            properties,
            attribute.ReferencePathProperty,
            (attribute.LinkProperties ?? []).ToHashSet(StringComparer.Ordinal),
            (attribute.MediaProperties ?? []).ToHashSet(StringComparer.Ordinal));
    });

    /// <summary>
    /// Finds the layout blocks of the page. A block inside a block that is no layout block is never one: it belongs to
    /// that block.
    /// </summary>
    /// <param name="page">The parsed page.</param>
    /// <param name="findType">Gets the type that a widget block names with its full name, or null when there is none.</param>
    public static HashSet<MarkdownBlock> FindLayoutBlocks(MarkdownPage page, Func<string, Type?> findType)
    {
        var layoutBlocks = new HashSet<MarkdownBlock>(ReferenceEqualityComparer.Instance);
        Collect(page.Blocks, ownerType: null, findType, layoutBlocks);
        return layoutBlocks;
    }

    private static void Collect(
        IReadOnlyList<MarkdownBlock> blocks, Type? ownerType, Func<string, Type?> findType, HashSet<MarkdownBlock> layoutBlocks)
    {
        foreach (var block in blocks)
        {
            Type? type = null;
            var isLayout = false;
            switch (block.Kind)
            {
                case MarkdownBlockKind.Style:
                    isLayout = true;
                    break;

                case MarkdownBlockKind.Widget:
                    type = findType(block.Head!.Name);
                    isLayout = type is not null && HasLayoutConfiguration(block, type);
                    break;

                // A slot block belongs to the widget around it, which must be a layout block for it to get here.
                case MarkdownBlockKind.Slot when ownerType is not null &&
                                                 SlotMap.For(ownerType).Named.TryGetValue(block.Head!.Name, out var slot):
                    type = slot.IsOpen ? null : slot.ValueType;
                    isLayout = slot.IsOpen
                        ? block.Head.Arguments.Count == 0 && block.Head.Id is null && block.ConfigurationJson is null
                        : HasLayoutConfiguration(block, slot.ValueType);
                    break;
            }

            if (isLayout)
            {
                layoutBlocks.Add(block);
                Collect(block.Children, type, findType, layoutBlocks);
            }
        }
    }

    private static bool HasLayoutConfiguration(MarkdownBlock block, Type type)
    {
        if (GetLayoutType(type) is not { } layoutType)
        {
            return false;
        }

        foreach (var argument in block.Head!.Arguments)
        {
            if (!IsLayoutValue(layoutType, argument.Key, argument.Value))
            {
                return false;
            }
        }

        if (block.ConfigurationJson is not { } json)
        {
            return true;
        }

        // Only single values: an object or a list could hold a subject definition. Read with the rules of a parse, and
        // anything that cannot be read makes the block no layout block, so that it is kept and hidden like any other.
        try
        {
            return JsonNode.Parse(json, documentOptions: MarkdownContentParser.StrictJsonOptions) is JsonObject configuration &&
                   configuration.All(pair => pair.Value is null or JsonValue &&
                                             IsLayoutValue(layoutType, pair.Key, (pair.Value as JsonValue)?.ToString()));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    // A value of a non-secret configuration property, which neither loads content nor links to an address that runs
    // something when it is opened.
    private static bool IsLayoutValue(LayoutType layoutType, string key, string? value) =>
        layoutType.ConfigurationProperties.Contains(key) &&
        !layoutType.MediaProperties.Contains(key) &&
        (!layoutType.LinkProperties.Contains(key) || value is null || MarkdownWriteRules.IsSafeUrl(value));

    /// <summary>
    /// Gets the reference path that a layout block configures, or null when its type refers to no subject or the block
    /// sets no path.
    /// </summary>
    public static string? GetReferencePath(MarkdownBlock block, Type type)
    {
        if (GetLayoutType(type) is not { ReferencePathProperty: { } property })
        {
            return null;
        }

        if (block.Head?.Arguments.FirstOrDefault(argument => argument.Key == property) is { } argument)
        {
            return argument.Value;
        }

        try
        {
            return block.ConfigurationJson is { } json &&
                   JsonNode.Parse(json, documentOptions: MarkdownContentParser.StrictJsonOptions) is JsonObject configuration &&
                   configuration[property] is JsonValue value && value.TryGetValue<string>(out var path)
                ? path
                : null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The non-secret configuration properties of a layout type, its reference path property and its link and media properties.</summary>
    internal sealed record LayoutType(
        IReadOnlySet<string> ConfigurationProperties,
        string? ReferencePathProperty,
        IReadOnlySet<string> LinkProperties,
        IReadOnlySet<string> MediaProperties);
}
