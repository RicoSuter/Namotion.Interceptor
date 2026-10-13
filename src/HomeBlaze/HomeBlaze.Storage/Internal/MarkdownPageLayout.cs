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
/// block, whose head line and json block hold only non-secret configuration values of its type: single values, and
/// subjects or lists of subjects of such types that hold the same, such as the cells of a grid.
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
            .ToDictionary(property => property.Name, property => property.PropertyType, StringComparer.Ordinal);

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
                    isLayout = type is not null && HasLayoutConfiguration(block, type, findType);
                    break;

                // A slot block belongs to the widget around it, which must be a layout block for it to get here.
                case MarkdownBlockKind.Slot when ownerType is not null &&
                                                 SlotMap.For(ownerType).Named.TryGetValue(block.Head!.Name, out var slot):
                    type = slot.IsOpen ? null : slot.ValueType;
                    isLayout = slot.IsOpen
                        ? block.Head.Arguments.Count == 0 && block.Head.Id is null && block.ConfigurationJson is null
                        : HasLayoutConfiguration(block, slot.ValueType, findType);
                    break;
            }

            if (isLayout)
            {
                layoutBlocks.Add(block);
                Collect(block.Children, type, findType, layoutBlocks);
            }
        }
    }

    private static bool HasLayoutConfiguration(MarkdownBlock block, Type type, Func<string, Type?> findType)
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

        return block.ConfigurationJson is null || ParseConfiguration(block) is { } configuration &&
               IsLayoutObject(configuration, layoutType, findType, references: null);
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

    // An object or a list could hold a subject definition, so each one must be a subject of a layout type, named by its
    // $type, or a list of a layout type, and hold layout values itself. Collects the reference paths of nested subjects.
    private static bool IsLayoutObject(
        JsonObject configuration, LayoutType layoutType, Func<string, Type?> findType, List<string?>? references, bool isSubject = false)
    {
        foreach (var (key, value) in configuration)
        {
            if (isSubject && key == ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName)
            {
                continue;
            }

            var isLayout = value switch
            {
                null or JsonValue => IsLayoutValue(layoutType, key, (value as JsonValue)?.ToString()),
                JsonArray items => GetItemLayoutType(layoutType, key) is { } itemType &&
                                   items.All(item => item is JsonObject itemObject &&
                                                     IsLayoutObject(itemObject, itemType, findType, references)),
                JsonObject subject => IsLayoutSubject(subject, layoutType, key, findType, references),
                _ => false
            };

            if (!isLayout)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLayoutSubject(
        JsonObject subject, LayoutType ownerType, string key, Func<string, Type?> findType, List<string?>? references)
    {
        if (!ownerType.ConfigurationProperties.TryGetValue(key, out var propertyType) ||
            subject[ConfigurableSubjectSerializer.TypeDiscriminatorPropertyName] is not JsonValue typeName ||
            !typeName.TryGetValue<string>(out var name) ||
            findType(name) is not { } type ||
            !propertyType.IsAssignableFrom(type) ||
            GetLayoutType(type) is not { } layoutType)
        {
            return false;
        }

        if (layoutType.ReferencePathProperty is { } property)
        {
            references?.Add(subject[property] is JsonValue path && path.TryGetValue<string>(out var text) ? text : null);
        }

        return IsLayoutObject(subject, layoutType, findType, references, isSubject: true);
    }

    private static LayoutType? GetItemLayoutType(LayoutType ownerType, string key)
    {
        if (!ownerType.ConfigurationProperties.TryGetValue(key, out var propertyType))
        {
            return null;
        }

        var itemType = propertyType.IsArray
            ? propertyType.GetElementType()
            : propertyType is { IsGenericType: true } && propertyType.GetGenericArguments() is [var argument] ? argument : null;
        return itemType is null ? null : GetLayoutType(itemType);
    }

    // A value of a non-secret configuration property, which neither loads content nor links to an address that runs
    // something when it is opened.
    private static bool IsLayoutValue(LayoutType layoutType, string key, string? value) =>
        layoutType.ConfigurationProperties.ContainsKey(key) &&
        !layoutType.MediaProperties.Contains(key) &&
        (!layoutType.LinkProperties.Contains(key) || value is null || MarkdownWriteRules.IsSafeUrl(value));

    /// <summary>
    /// Gets the reference paths that a layout block configures: its own, null when its type has a reference path
    /// property and the block sets no path, and those of the subjects nested in its configuration.
    /// </summary>
    public static List<string?> GetReferencePaths(MarkdownBlock block, Type type, Func<string, Type?> findType)
    {
        var references = new List<string?>();
        if (GetLayoutType(type) is not { } layoutType)
        {
            return references;
        }

        var configuration = block.ConfigurationJson is null ? null : ParseConfiguration(block);
        if (layoutType.ReferencePathProperty is { } property)
        {
            references.Add(
                block.Head?.Arguments.FirstOrDefault(argument => argument.Key == property) is { } argument ? argument.Value :
                configuration?[property] is JsonValue value && value.TryGetValue<string>(out var path) ? path : null);
        }

        if (configuration is not null)
        {
            IsLayoutObject(configuration, layoutType, findType, references);
        }

        return references;
    }

    /// <summary>The non-secret configuration properties of a layout type, its reference path property and its link and media properties.</summary>
    internal sealed record LayoutType(
        IReadOnlyDictionary<string, Type> ConfigurationProperties,
        string? ReferencePathProperty,
        IReadOnlySet<string> LinkProperties,
        IReadOnlySet<string> MediaProperties);
}
