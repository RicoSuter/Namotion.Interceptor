namespace HomeBlaze.Automation.Blazor.Editing;

internal static class DraftLists
{
    /// <summary>Moves the item by the offset within the list, stopping at its ends.</summary>
    public static void Move<T>(List<T> items, T item, int offset)
    {
        var index = items.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        items.RemoveAt(index);
        items.Insert(Math.Clamp(index + offset, 0, items.Count), item);
    }
}
