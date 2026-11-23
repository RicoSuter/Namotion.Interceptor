namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Something that comes with a found device, such as a speaker of a household, shown under the device and not
/// selectable on its own.
/// </summary>
/// <param name="DisplayName">The name the user is shown for the item.</param>
/// <param name="Detail">A second part for the user, such as the model and host address, or null.</param>
public sealed record DiscoveredItem(string DisplayName, string? Detail)
{
    /// <summary>
    /// Orders items by <see cref="DisplayName"/> ignoring case, then by <see cref="Detail"/> ordinally, so that the
    /// items of a device read the same in every scan whatever order the devices answered in. Null comes first.
    /// </summary>
    public static IComparer<DiscoveredItem> DisplayOrder { get; } = Comparer<DiscoveredItem>.Create(Compare);

    private static int Compare(DiscoveredItem? first, DiscoveredItem? second)
    {
        if (first is null || second is null)
        {
            return first is null ? (second is null ? 0 : -1) : 1;
        }

        var byName = StringComparer.OrdinalIgnoreCase.Compare(first.DisplayName, second.DisplayName);
        return byName != 0 ? byName : StringComparer.Ordinal.Compare(first.Detail, second.Detail);
    }
}
