namespace HomeBlaze.Components;

/// <summary>A slot of a block that holds a list of blocks.</summary>
/// <param name="Name">The name of its blocks, such as Value or Tab.</param>
/// <param name="ItemType">The type of an item, or null when an item holds page content.</param>
public sealed record PageListSlot(string Name, Type? ItemType)
{
    /// <summary>Gets the slot named <paramref name="name"/> among <paramref name="slots"/>, or null.</summary>
    public static PageListSlot? Find(IReadOnlyList<PageListSlot> slots, string name)
    {
        foreach (var slot in slots)
        {
            if (slot.Name == name)
            {
                return slot;
            }
        }

        return null;
    }
}
