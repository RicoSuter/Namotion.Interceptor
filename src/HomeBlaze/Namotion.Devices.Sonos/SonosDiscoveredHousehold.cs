using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Sonos;

/// <summary>
/// A Sonos household whose speakers answered an SSDP search.
/// </summary>
/// <param name="HouseholdId">The household ID the speakers announce, such as <c>Sonos_ZwxPaYZW5IrvVUQVXUr4zlXI6L</c>.</param>
/// <param name="Speakers">The speakers that answered, satellites included, ordered by ID; never empty.</param>
/// <param name="SeedHost">The speaker to set as <see cref="SonosSystem.SeedHost"/>, as host or host:port.</param>
public sealed record SonosDiscoveredHousehold(string HouseholdId, IReadOnlyList<SonosDiscoveredSpeaker> Speakers, string SeedHost) : IDiscoveryResult
{
    /// <inheritdoc />
    public string Key => HouseholdId;

    /// <inheritdoc />
    public string DisplayName => "Sonos household";

    /// <inheritdoc />
    public string? Detail => Speakers.Count == 1 ? $"1 speaker, {SeedHost}" : $"{Speakers.Count} speakers, {SeedHost}";
}
