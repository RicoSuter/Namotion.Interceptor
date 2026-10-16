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
/// block, whose head line and json block hold only single, non-secret configuration values of its type. The cells of a
/// grid and the nodes of a canvas are slot blocks, so the same rules apply to them and to what stands inside them.
/// Such a block holds nothing secret, so it is shown unredacted, and with the permission to edit page layout it may change.
/// </summary>
internal static class MarkdownPageLayout
{
    private static readonly ConcurrentDictionary<Type, LayoutType?> LayoutTypes = new();

    /// <summary>Gets the layout description of a type, or null when it is no layout type.</summary>
    public static LayoutType? GetLayoutType(Type type) => LayoutTypes.GetOrAdd(type, static key =>
    {
        if (key.GetCustomAttribute<PageLayoutAttribute>(inherit: false) is null)
        {
            return null;
        }

        var properties = key
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<ConfigurationAttribute>(true) is { IsSecret: false })
            .ToList();

        return new LayoutType(
            GetNames(properties, _ => true),
            GetNames(properties, property => property.GetCustomAttribute<SubjectPathAttribute>(true) is { SubjectType: not null }),
            GetNames(properties, property => property.IsDefined(typeof(LinkAddressAttribute), true)),
            GetNames(properties, property => property.IsDefined(typeof(MediaAddressAttribute), true)));
    });

    private static HashSet<string> GetNames(List<PropertyInfo> properties, Func<PropertyInfo, bool> predicate) =>
        properties.Where(predicate).Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

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

        // Only single values: an object or a list could hold a subject definition.
        return block.ConfigurationJson is null ||
               ParseConfiguration(block) is { } configuration &&
               configuration.All(pair => pair.Value is null or JsonValue &&
                                         IsLayoutValue(layoutType, pair.Key, (pair.Value as JsonValue)?.ToString()));
    }

    // Read with the rules of a parse; anything that cannot be read makes the block no layout block, so that it is kept
    // and hidden like any other.
    private static JsonObject? ParseConfiguration(MarkdownBlock block)
    {
        try
        {
            return JsonNode.Parse(block.ConfigurationJson!, documentOptions: MarkdownContentParser.StrictJsonOptions) as JsonObject;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    // A value of a non-secret configuration property, which neither loads content nor links to an address that runs
    // something when it is opened.
    private static bool IsLayoutValue(LayoutType layoutType, string key, string? value) =>
        layoutType.ConfigurationProperties.Contains(key) &&
        !layoutType.MediaProperties.Contains(key) &&
        (!layoutType.LinkProperties.Contains(key) || value is null || MarkdownWriteRules.IsSafeUrl(value));

    /// <summary>Gets the subject paths that a layout block sets; a path that is not set is left out.</summary>
    public static List<string> GetReferencePaths(MarkdownBlock block, Type type)
    {
        var references = new List<string>();
        if (GetLayoutType(type) is not { } layoutType)
        {
            return references;
        }

        var configuration = block.ConfigurationJson is null ? null : ParseConfiguration(block);
        foreach (var property in layoutType.ReferenceProperties)
        {
            var path = block.Head?.Arguments.FirstOrDefault(argument => argument.Key == property) is { } argument
                ? argument.Value
                : configuration?[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                references.Add(path);
            }
        }

        return references;
    }

    /// <summary>The non-secret configuration properties of a layout type and the names of its reference, link and media properties.</summary>
    internal sealed record LayoutType(
        IReadOnlySet<string> ConfigurationProperties,
        IReadOnlySet<string> ReferenceProperties,
        IReadOnlySet<string> LinkProperties,
        IReadOnlySet<string> MediaProperties);
}
