using System.Text;

namespace HomeBlaze.Components;

/// <summary>A slot of a block to which the bar of the block adds a widget, text or an item.</summary>
public sealed class PageAddTarget
{
    /// <param name="kind">What an add puts into the slot.</param>
    /// <param name="slotName">The name of the slot, or null for the default slot, whose content stands directly in the block.</param>
    /// <param name="itemType">The type of an item of a slot of <see cref="PageAddTargetKind.ConfiguredItem"/>, otherwise null.</param>
    public PageAddTarget(PageAddTargetKind kind, string? slotName, Type? itemType)
    {
        Kind = kind;
        SlotName = slotName;
        ItemType = itemType;
        Label = slotName is null ? null : GetSlotLabel(slotName);
    }

    /// <summary>Gets what an add puts into the slot.</summary>
    public PageAddTargetKind Kind { get; }

    /// <summary>Gets the name of the slot, or null for the default slot.</summary>
    public string? SlotName { get; }

    /// <summary>Gets the type of an item of a slot of <see cref="PageAddTargetKind.ConfiguredItem"/>, otherwise null.</summary>
    public Type? ItemType { get; }

    /// <summary>Gets the name of the slot as words, such as "Header right" for HeaderRight, or null for the default slot.</summary>
    public string? Label { get; }

    /// <summary>Gets the name of the action that adds a widget, such as "Add widget to Footer", or null for configured items.</summary>
    public string? AddWidgetName => Kind switch
    {
        PageAddTargetKind.DefaultContent => "Add widget",
        PageAddTargetKind.NamedContent => $"Add widget to {Label}",
        PageAddTargetKind.ContentItem => $"Add {Label} with widget",
        _ => null
    };

    /// <summary>Gets the name of the action that adds text, such as "Add text to Footer", or null for configured items.</summary>
    public string? AddTextName => Kind switch
    {
        PageAddTargetKind.DefaultContent => "Add text",
        PageAddTargetKind.NamedContent => $"Add text to {Label}",
        PageAddTargetKind.ContentItem => $"Add {Label} with text",
        _ => null
    };

    /// <summary>Gets the name of the action that adds a configured item, such as "Add Tab", or null for the other kinds.</summary>
    public string? AddItemName => Kind == PageAddTargetKind.ConfiguredItem ? $"Add {Label}" : null;

    /// <summary>Gets the slot named <paramref name="slotName"/> among <paramref name="targets"/>, or null.</summary>
    public static PageAddTarget? Find(IReadOnlyList<PageAddTarget> targets, string slotName)
    {
        foreach (var target in targets)
        {
            if (target.SlotName == slotName)
            {
                return target;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the name of a slot as words: the first letter as it is, a space before each capital that does not follow a capital,
    /// and every capital after the first letter in lower case.
    /// </summary>
    public static string GetSlotLabel(string slotName)
    {
        StringBuilder? builder = null;
        for (var index = 1; index < slotName.Length; index++)
        {
            var character = slotName[index];
            if (char.IsUpper(character))
            {
                builder ??= new StringBuilder(slotName.Length + 4).Append(slotName, 0, index);
                if (!char.IsUpper(slotName[index - 1]))
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder?.Append(character);
            }
        }

        return builder?.ToString() ?? slotName;
    }
}

/// <summary>What an add of the bar of a block puts into a slot.</summary>
public enum PageAddTargetKind
{
    /// <summary>A widget or text at the end of the default content of the block.</summary>
    DefaultContent,

    /// <summary>A widget or text at the end of the content of a named slot, whose block is added when it has none.</summary>
    NamedContent,

    /// <summary>A new item of a list slot, holding a widget or text.</summary>
    ContentItem,

    /// <summary>A new item of a list slot, configured in the setup dialog of its type.</summary>
    ConfiguredItem
}
