using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Sonos;

/// <summary>
/// A Sonos household whose speakers answered an SSDP search.
/// </summary>
public sealed record SonosDiscoveredHousehold : IDiscoveryResult
{
    /// <param name="householdId">The household ID the speakers announce.</param>
    /// <param name="speakers">The speakers that answered, satellites included, ordered by ID; never empty.</param>
    /// <param name="seedHost">The speaker to set as <see cref="SonosSystem.SeedHost"/>.</param>
    internal SonosDiscoveredHousehold(string householdId, IReadOnlyList<SonosDiscoveredSpeaker> speakers, string seedHost)
    {
        HouseholdId = householdId;
        Speakers = speakers;
        SeedHost = seedHost;
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
}
