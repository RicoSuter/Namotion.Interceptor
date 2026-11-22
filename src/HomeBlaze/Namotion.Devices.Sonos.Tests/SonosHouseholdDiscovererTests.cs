using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosHouseholdDiscovererTests
{
    private const string ZonePlayer = "urn:schemas-upnp-org:device:ZonePlayer:1";
    private const string Household = "Sonos_ZwxPaYZW5IrvVUQVXUr4zlXI6L";
    private const string OtherHousehold = "Sonos_OtherHousehold0000000000";

    private static SsdpResponse Answer(string speakerId, string host, string? household = Household, int port = 1400) =>
        Raw($"uuid:{speakerId}::{ZonePlayer}", host, household, port);

    private static SsdpResponse Raw(string? usn, string host, string? household = Household, int port = 1400)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (household is not null)
        {
            headers["X-RINCON-HOUSEHOLD"] = household;
        }

        return new SsdpResponse(new Uri($"http://{host}:{port}/xml/device_description.xml"), usn, headers);
    }

    private static SonosSystem CreateSystem(string? seedHost = null) =>
        new(new TestHttpClientFactory(), NullLogger<SonosSystem>.Instance) { SeedHost = seedHost };

    private static SonosDiscoveredHousehold Found(params (string Id, string Host)[] speakers) =>
        new(Household, [.. speakers.Select(speaker => new SonosDiscoveredSpeaker(speaker.Id, speaker.Host, 1400))], speakers[0].Host);

    [Fact]
    public void WhenSpeakersOfTwoHouseholdsAnswer_ThenOneResultPerHouseholdIsFound()
    {
        // Arrange
        SsdpResponse[] responses =
        [
            Answer("RINCON_B00000000000001400", "192.168.1.102"),
            Answer("RINCON_A00000000000001400", "192.168.1.101"),
            Answer("RINCON_C00000000000001400", "192.168.1.150", OtherHousehold)
        ];

        // Act
        var households = SonosHouseholdDiscoverer.ToHouseholds(responses);

        // Assert
        Assert.Equal(2, households.Count);
        var household = Assert.Single(households, household => household.HouseholdId == Household);
        Assert.Equal(Household, household.Key);
        Assert.Equal("Sonos household", household.DisplayName);
        Assert.Equal("2 speakers, 192.168.1.101", household.Detail);
        Assert.Equal(["RINCON_A00000000000001400", "RINCON_B00000000000001400"], household.Speakers.Select(speaker => speaker.Id));
        var other = Assert.Single(households, household => household.HouseholdId == OtherHousehold);
        Assert.Equal("1 speaker, 192.168.1.150", other.Detail);
    }

    [Fact]
    public void WhenAnAnswerIsNotFromASonosSpeakerOrNamesNoHousehold_ThenItIsDropped()
    {
        // Arrange
        SsdpResponse[] responses =
        [
            Answer("RINCON_A00000000000001400", "192.168.1.101"),
            Raw($"uuid:2f402f80-da50-11e1-9b23-001788255acc::{ZonePlayer}", "192.168.1.20"),
            Raw(null, "192.168.1.21"),
            Answer("RINCON_D00000000000001400", "192.168.1.104", household: null),
            Answer("RINCON_E00000000000001400", "192.168.1.105", household: " ")
        ];

        // Act
        var households = SonosHouseholdDiscoverer.ToHouseholds(responses);

        // Assert
        var household = Assert.Single(households);
        var speaker = Assert.Single(household.Speakers);
        Assert.Equal(new SonosDiscoveredSpeaker("RINCON_A00000000000001400", "192.168.1.101", 1400), speaker);
    }

    [Fact]
    public void WhenASpeakerAnswersTwice_ThenItIsKeptOnce()
    {
        // Arrange
        SsdpResponse[] responses =
        [
            Answer("RINCON_A00000000000001400", "192.168.1.101"),
            Answer("RINCON_A00000000000001400", "192.168.1.101")
        ];

        // Act
        var households = SonosHouseholdDiscoverer.ToHouseholds(responses);

        // Assert
        Assert.Single(Assert.Single(households).Speakers);
    }

    [Fact]
    public void WhenTheSpeakersAnswerInAnotherOrder_ThenTheSameSeedHostIsChosen()
    {
        // Arrange
        SsdpResponse[] responses =
        [
            Answer("RINCON_C00000000000001400", "192.168.1.103"),
            Answer("RINCON_A00000000000001400", "192.168.1.120"),
            Answer("RINCON_B00000000000001400", "192.168.1.101")
        ];

        // Act
        var forward = SonosHouseholdDiscoverer.ToHouseholds(responses);
        var reversed = SonosHouseholdDiscoverer.ToHouseholds([.. responses.Reverse()]);

        // Assert
        Assert.Equal("192.168.1.120", Assert.Single(forward).SeedHost);
        Assert.Equal("192.168.1.120", Assert.Single(reversed).SeedHost);
    }

    [Fact]
    public void WhenTheSeedSpeakerAnswersOnAnotherPort_ThenTheSeedHostNamesThePort()
    {
        // Arrange
        SsdpResponse[] responses = [Answer("RINCON_A00000000000001400", "192.168.1.101", port: 80)];

        // Act
        var household = Assert.Single(SonosHouseholdDiscoverer.ToHouseholds(responses));

        // Assert
        Assert.Equal("192.168.1.101:80", household.SeedHost);
    }

    [Fact]
    public async Task WhenAScanRuns_ThenTheZonePlayerTargetIsSearchedAndHouseholdsAreFound()
    {
        // Arrange
        var search = new AnsweringSsdpSearch(
            Answer("RINCON_A00000000000001400", "192.168.1.101"),
            Answer("RINCON_B00000000000001400", "192.168.1.102"));
        ISubjectDiscoverer discoverer = new SonosHouseholdDiscoverer();
        var scan = new DiscoveryScan(search, new EmptyMulticastDnsBrowser(), new UnusedUdpDiscovery(), CancellationToken.None);
        var existing = CreateSystem(seedHost: "192.168.1.102");

        // Act
        var discovered = await discoverer.DiscoverAsync(scan, [existing], CancellationToken.None).ToListAsync();

        // Assert
        var single = Assert.Single(discovered);
        var household = Assert.IsType<SonosDiscoveredHousehold>(single.Result);
        Assert.Equal(Household, household.HouseholdId);
        Assert.Same(existing, single.ExistingSubject);
        Assert.Equal([ZonePlayer], search.SearchTargets);
    }

    [Fact]
    public void WhenASpeakerIsAPlayerOfTheSystem_ThenTheHouseholdIsTheSame()
    {
        // Arrange
        var system = CreateSystem();
        system.Players = new Dictionary<string, SonosPlayer>(StringComparer.Ordinal)
        {
            ["RINCON_B00000000000001400"] = new(system, "RINCON_B00000000000001400")
        };

        // Act & Assert
        Assert.True(SonosHouseholdDiscoverer.IsSameHousehold(system, Found(("RINCON_A00000000000001400", "192.168.1.101"), ("RINCON_B00000000000001400", "192.168.1.102"))));
        Assert.False(SonosHouseholdDiscoverer.IsSameHousehold(system, Found(("RINCON_C00000000000001400", "192.168.1.103"))));
    }

    [Fact]
    public void WhenASpeakerIsASatelliteOfTheSystem_ThenTheHouseholdIsTheSame()
    {
        // Arrange
        var system = CreateSystem();
        var player = new SonosPlayer(system, "RINCON_B00000000000001400");
        player.Satellites = new Dictionary<string, SonosSatellite>(StringComparer.Ordinal)
        {
            ["RINCON_S00000000000001400"] = new("RINCON_S00000000000001400")
        };
        system.Players = new Dictionary<string, SonosPlayer>(StringComparer.Ordinal) { [player.Uuid] = player };

        // Act & Assert
        Assert.True(SonosHouseholdDiscoverer.IsSameHousehold(system, Found(("RINCON_S00000000000001400", "192.168.1.110"))));
    }

    [Theory]
    [InlineData("192.168.1.102")]
    [InlineData(" 192.168.1.102:1400 ")]
    [InlineData("192.168.1.102:5000")]
    public void WhenTheSeedHostIsASpeaker_ThenTheHouseholdIsTheSame(string seedHost)
    {
        // Arrange
        var system = CreateSystem(seedHost);

        // Act & Assert
        Assert.True(SonosHouseholdDiscoverer.IsSameHousehold(system, Found(("RINCON_A00000000000001400", "192.168.1.101"), ("RINCON_B00000000000001400", "192.168.1.102"))));
    }

    [Fact]
    public void WhenTheSeedHostNamesALocalHostName_ThenItIsComparedIgnoringCase()
    {
        // Arrange
        var system = CreateSystem("Sonos-Kitchen.local");

        // Act & Assert
        Assert.True(SonosHouseholdDiscoverer.IsSameHousehold(system, Found(("RINCON_A00000000000001400", "sonos-kitchen.local"))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("192.168.1.199")]
    [InlineData("http://192.168.1.101")]
    [InlineData("bad host")]
    public void WhenNeitherASpeakerNorTheSeedHostIsKnown_ThenTheHouseholdIsAnother(string? seedHost)
    {
        // Arrange
        var system = CreateSystem(seedHost);
        system.Players = new Dictionary<string, SonosPlayer>(StringComparer.Ordinal)
        {
            ["RINCON_Z00000000000001400"] = new(system, "RINCON_Z00000000000001400")
        };

        // Act & Assert
        Assert.False(SonosHouseholdDiscoverer.IsSameHousehold(system, Found(("RINCON_A00000000000001400", "192.168.1.101"))));
    }

    private sealed class AnsweringSsdpSearch(params SsdpResponse[] responses) : ISsdpSearch
    {
        public List<string> SearchTargets { get; } = [];

        public Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken)
        {
            SearchTargets.Add(searchTarget);
            return Task.FromResult<IReadOnlyList<SsdpResponse>>(responses);
        }
    }

    private sealed class EmptyMulticastDnsBrowser : IMulticastDnsBrowser
    {
        public Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MulticastDnsService>>([]);
    }

    private sealed class UnusedUdpDiscovery : IUdpDiscovery
    {
        public IAsyncEnumerable<UdpDatagram> BroadcastAsync(int port, ReadOnlyMemory<byte> request, TimeSpan window, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<UdpDatagram> ListenAsync(int port, TimeSpan window, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
