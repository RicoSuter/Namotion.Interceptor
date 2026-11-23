using HomeBlaze.Abstractions.Discovery;
using Namotion.Devices.Sonos.Client;

namespace Namotion.Devices.Sonos;

/// <summary>
/// A Sonos household whose speakers answered an SSDP search.
/// </summary>
public sealed record SonosDiscoveredHousehold : IDiscoveryResult<SonosSystem>
{
    /// <param name="householdId">The household ID the speakers announce.</param>
    /// <param name="speakers">The speakers that answered, satellites included, ordered by ID; never empty.</param>
    /// <param name="seedHost">The speaker to set as <see cref="SonosSystem.SeedHost"/>.</param>
    internal SonosDiscoveredHousehold(string householdId, IReadOnlyList<SonosDiscoveredSpeaker> speakers, string seedHost)
    {
        HouseholdId = householdId;
        Speakers = speakers;
        SeedHost = seedHost;
        IncludedItems = ToItems(speakers);
    }

    /// <summary>
    /// The household ID the speakers announce, such as <c>Sonos_ZwxPaYZW5IrvVUQVXUr4zlXI6L</c>.
    /// </summary>
    public string HouseholdId { get; }

    /// <summary>
    /// The speaker to set as <see cref="SonosSystem.SeedHost"/>, as host or host:port.
    /// </summary>
    public string SeedHost { get; }

    /// <summary>
    /// The speakers that answered, satellites included, ordered by ID; never empty.
    /// </summary>
    internal IReadOnlyList<SonosDiscoveredSpeaker> Speakers { get; }

    /// <inheritdoc />
    public string Key => HouseholdId;

    /// <inheritdoc />
    public string DisplayName => "Sonos household";

    /// <inheritdoc />
    public string? Detail => Speakers.Count == 1 ? $"1 speaker, {SeedHost}" : $"{Speakers.Count} speakers, {SeedHost}";

    /// <summary>
    /// The speakers, satellites included, ordered by room: each named by its room, or by its ID when its description
    /// could not be read, with its model and host address.
    /// </summary>
    public IReadOnlyList<DiscoveredItem> IncludedItems { get; }

    /// <inheritdoc />
    public bool Matches(SonosSystem subject)
    {
        // The dictionaries are replaced, never mutated, so each one is read once and enumerated safely.
        var players = subject.Players;
        foreach (var speaker in Speakers)
        {
            if (players.ContainsKey(speaker.Id))
            {
                return true;
            }
        }

        foreach (var player in players.Values)
        {
            var satellites = player.Satellites;
            foreach (var speaker in Speakers)
            {
                if (satellites.ContainsKey(speaker.Id))
                {
                    return true;
                }
            }
        }

        // A system that has not connected yet knows no speakers, only its seed.
        var seedHostAddress = GetHostAddress(subject.SeedHost);
        if (seedHostAddress is not null)
        {
            foreach (var speaker in Speakers)
            {
                if (string.Equals(speaker.HostAddress, seedHostAddress, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static DiscoveredItem[] ToItems(IReadOnlyList<SonosDiscoveredSpeaker> speakers)
    {
        var items = new DiscoveredItem[speakers.Count];
        for (var index = 0; index < items.Length; index++)
        {
            var speaker = speakers[index];
            items[index] = new DiscoveredItem(
                speaker.RoomName ?? speaker.Id,
                speaker.ModelName is null ? speaker.HostAddress : $"{speaker.ModelName}, {speaker.HostAddress}");
        }

        // The speakers are ordered by ID; Detail breaks ties between speakers of one room, such as a sub and its player.
        Array.Sort(items, static (first, second) =>
        {
            var byName = StringComparer.OrdinalIgnoreCase.Compare(first.DisplayName, second.DisplayName);
            return byName != 0 ? byName : StringComparer.Ordinal.Compare(first.Detail, second.Detail);
        });

        return items;
    }

    private static string? GetHostAddress(string? seedHost)
    {
        if (string.IsNullOrWhiteSpace(seedHost))
        {
            return null;
        }

        try
        {
            return SonosDiscovery.CreateDeviceUri(seedHost).Host;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
