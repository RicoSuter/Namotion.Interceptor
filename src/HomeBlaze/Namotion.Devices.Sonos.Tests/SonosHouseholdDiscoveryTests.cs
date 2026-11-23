using System.Net;
using System.Text;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosHouseholdDiscoveryTests
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
        var households = SonosHouseholdDiscovery.ToHouseholds(responses);

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
        var households = SonosHouseholdDiscovery.ToHouseholds(responses);

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
        var households = SonosHouseholdDiscovery.ToHouseholds(responses);

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
        var forward = SonosHouseholdDiscovery.ToHouseholds(responses);
        var reversed = SonosHouseholdDiscovery.ToHouseholds([.. responses.Reverse()]);

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
        var household = Assert.Single(SonosHouseholdDiscovery.ToHouseholds(responses));

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
        using var handler = new DescriptionHandler(_ => Description("Wohnzimmer", "Sonos One"));
        ISubjectDiscovery discovery = new SonosHouseholdDiscovery(handler, new FakeTimeProvider());
        var scan = new DiscoveryScan(search, new EmptyMulticastDnsBrowser(), new UnusedUdpDiscovery(), CancellationToken.None);
        var existing = CreateSystem(seedHost: "192.168.1.102");

        // Act
        var discovered = await discovery.DiscoverAsync(scan, [existing], CancellationToken.None).ToListAsync();

        // Assert
        var single = Assert.Single(discovered);
        var household = Assert.IsType<SonosDiscoveredHousehold>(single.Result);
        Assert.Equal(Household, household.HouseholdId);
        Assert.Same(existing, single.ExistingSubject);
        Assert.Equal([ZonePlayer], search.SearchTargets);
    }

    [Fact]
    public void WhenAnAnswerNamesALocationWithoutAPlainHttpHost_ThenItIsDroppedAndTheOtherSpeakersAreKept()
    {
        // Arrange
        SsdpResponse[] responses =
        [
            Answer("RINCON_A00000000000001400", "192.168.1.101"),
            new(new Uri("urn:x"), $"uuid:RINCON_B00000000000001400::{ZonePlayer}", HouseholdHeaders()),
            new(new Uri("file:///x"), $"uuid:RINCON_C00000000000001400::{ZonePlayer}", HouseholdHeaders()),
            new(new Uri("https://192.168.1.104:1443/xml/device_description.xml"), $"uuid:RINCON_D00000000000001400::{ZonePlayer}", HouseholdHeaders()),
            Answer("RINCON_E00000000000001400", "192.168.1.105")
        ];

        // Act
        var household = Assert.Single(SonosHouseholdDiscovery.ToHouseholds(responses));

        // Assert
        Assert.Equal(["RINCON_A00000000000001400", "RINCON_E00000000000001400"], household.Speakers.Select(speaker => speaker.Id));
        Assert.Equal("192.168.1.101", household.SeedHost);
    }

    [Fact]
    public async Task WhenAHostlessLocationAnswersNextToSpeakers_ThenTheScanFindsTheHouseholdWithTheSpeakers()
    {
        // Arrange
        var search = new AnsweringSsdpSearch(
            Answer("RINCON_A00000000000001400", "192.168.1.101"),
            new SsdpResponse(new Uri("urn:x"), $"uuid:RINCON_B00000000000001400::{ZonePlayer}", HouseholdHeaders()),
            Answer("RINCON_C00000000000001400", "192.168.1.103"));
        using var handler = new DescriptionHandler(_ => Description("Wohnzimmer", "Sonos One"));
        ISubjectDiscovery discovery = new SonosHouseholdDiscovery(handler, new FakeTimeProvider());
        var scan = new DiscoveryScan(search, new EmptyMulticastDnsBrowser(), new UnusedUdpDiscovery(), CancellationToken.None);

        // Act
        var discovered = await discovery.DiscoverAsync(scan, [], CancellationToken.None).ToListAsync();

        // Assert
        var household = Assert.IsType<SonosDiscoveredHousehold>(Assert.Single(discovered).Result);
        Assert.Equal(
            [new DiscoveredItem("Wohnzimmer", "Sonos One, 192.168.1.101"), new DiscoveredItem("Wohnzimmer", "Sonos One, 192.168.1.103")],
            household.IncludedItems);
    }

    [Fact]
    public async Task WhenAnAnswerAdvertisesAnotherPath_ThenTheFixedDescriptionPathIsRequestedOnItsHostAndPort()
    {
        // Arrange
        var requested = new List<Uri>();
        using var handler = new DescriptionHandler(request =>
        {
            lock (requested)
            {
                requested.Add(request.RequestUri!);
            }

            return Description("Wohnzimmer", "Sonos One");
        });
        var discovery = new SonosHouseholdDiscovery(handler, new FakeTimeProvider());
        var households = SonosHouseholdDiscovery.ToHouseholds(
        [
            new(new Uri("http://192.168.1.101:1400/elsewhere?target=192.0.2.1"), $"uuid:RINCON_A00000000000001400::{ZonePlayer}", HouseholdHeaders()),
            new(new Uri("http://192.168.1.102:1401/"), $"uuid:RINCON_B00000000000001400::{ZonePlayer}", HouseholdHeaders())
        ]);

        // Act
        await discovery.DescribeSpeakersAsync(households, CancellationToken.None);

        // Assert
        Assert.Equal(
            [new Uri("http://192.168.1.101:1400/xml/device_description.xml"), new Uri("http://192.168.1.102:1401/xml/device_description.xml")],
            requested.OrderBy(uri => uri.Host, StringComparer.Ordinal));
    }

    [Fact]
    public async Task WhenTheDescriptionsAnswer_ThenEachSpeakerIsIncludedByRoomWithModelAndAddress()
    {
        // Arrange
        using var handler = new DescriptionHandler(request => request.RequestUri!.Host switch
        {
            "192.168.1.101" => Description("Wohnzimmer", "Sonos Arc"),
            "192.168.1.102" => Description("Wohnzimmer", "Sonos Sub"),
            _ => Description(" Bad ", "Sonos One")
        });
        var discovery = new SonosHouseholdDiscovery(handler, new FakeTimeProvider());
        var households = SonosHouseholdDiscovery.ToHouseholds(
        [
            Answer("RINCON_A00000000000001400", "192.168.1.101"),
            Answer("RINCON_B00000000000001400", "192.168.1.102"),
            Answer("RINCON_C00000000000001400", "192.168.1.103")
        ]);

        // Act
        var household = Assert.Single(await discovery.DescribeSpeakersAsync(households, CancellationToken.None));

        // Assert
        Assert.Equal(
            [
                new DiscoveredItem("Bad", "Sonos One, 192.168.1.103"),
                new DiscoveredItem("Wohnzimmer", "Sonos Arc, 192.168.1.101"),
                new DiscoveredItem("Wohnzimmer", "Sonos Sub, 192.168.1.102")
            ],
            ((IDiscoveryResult)household).IncludedItems);
        Assert.Equal("3 speakers, 192.168.1.101", household.Detail);
        Assert.Equal("192.168.1.101", household.SeedHost);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task WhenADescriptionTimesOutOrFails_ThenItsSpeakerIsIncludedByIdAndAddress()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var hangingRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var otherRequestsAnswered = new SemaphoreSlim(0);
        using var handler = new DescriptionHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "192.168.1.101")
            {
                hangingRequestStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            try
            {
                return request.RequestUri.Host switch
                {
                    "192.168.1.102" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                    "192.168.1.103" => throw new HttpRequestException("Connection refused."),
                    "192.168.1.104" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://192.0.2.1/") } },
                    _ => Description("Küche", "Sonos One")
                };
            }
            finally
            {
                otherRequestsAnswered.Release();
            }
        });
        var discovery = new SonosHouseholdDiscovery(handler, timeProvider);
        var households = SonosHouseholdDiscovery.ToHouseholds(
        [
            Answer("RINCON_A00000000000001400", "192.168.1.101"),
            Answer("RINCON_B00000000000001400", "192.168.1.102"),
            Answer("RINCON_C00000000000001400", "192.168.1.103"),
            Answer("RINCON_D00000000000001400", "192.168.1.104"),
            Answer("RINCON_E00000000000001400", "192.168.1.105")
        ]);

        // Act - the deadline passes once only the hanging request is left.
        var describing = discovery.DescribeSpeakersAsync(households, CancellationToken.None);
        await hangingRequestStarted.Task;
        for (var answered = 0; answered < 4; answered++)
        {
            await otherRequestsAnswered.WaitAsync();
        }

        timeProvider.Advance(SonosSpeakerDescriptions.DescriptionTimeout);
        var household = Assert.Single(await describing);

        // Assert
        Assert.Equal(
            [
                new DiscoveredItem("Küche", "Sonos One, 192.168.1.105"),
                new DiscoveredItem("RINCON_A00000000000001400", "192.168.1.101"),
                new DiscoveredItem("RINCON_B00000000000001400", "192.168.1.102"),
                new DiscoveredItem("RINCON_C00000000000001400", "192.168.1.103"),
                new DiscoveredItem("RINCON_D00000000000001400", "192.168.1.104")
            ],
            household.IncludedItems);
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("doctype")]
    [InlineData("deep")]
    [InlineData("malformed")]
    [InlineData("charset")]
    public async Task WhenADescriptionIsOversizedOrHostile_ThenItIsNotRead(string answer)
    {
        // Arrange
        using var handler = new DescriptionHandler(_ => answer switch
        {
            "oversized" => Xml($"<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><roomName>Big</roomName>{new string(' ', SonosSpeakerDescriptions.MaximumDescriptionBytes)}</device></root>"),
            "doctype" => Xml("<?xml version=\"1.0\"?><!DOCTYPE root [<!ENTITY room \"Entity\">]><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><roomName>&room;</roomName></device></root>"),
            "deep" => Xml("<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><roomName>Deep</roomName>" +
                string.Concat(Enumerable.Repeat("<a>", 100)) + string.Concat(Enumerable.Repeat("</a>", 100)) + "</device></root>"),
            "malformed" => Xml("<root><device><roomName>Open"),
            _ => WithCharset(Description("Charset", "Sonos One"), "no-such-charset")
        });
        var discovery = new SonosHouseholdDiscovery(handler, new FakeTimeProvider());
        var households = SonosHouseholdDiscovery.ToHouseholds([Answer("RINCON_A00000000000001400", "192.168.1.101")]);

        // Act
        var household = Assert.Single(await discovery.DescribeSpeakersAsync(households, CancellationToken.None));

        // Assert
        Assert.Equal([new DiscoveredItem("RINCON_A00000000000001400", "192.168.1.101")], household.IncludedItems);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task WhenARoomOrModelNameIsLong_ThenItIsCut()
    {
        // Arrange
        var longName = new string('r', SonosSpeakerDescriptions.MaximumNameLength - 1) + "\U0001F50A" + "tail";
        using var handler = new DescriptionHandler(_ => Description(longName, new string('m', 200)));
        var discovery = new SonosHouseholdDiscovery(handler, new FakeTimeProvider());
        var households = SonosHouseholdDiscovery.ToHouseholds([Answer("RINCON_A00000000000001400", "192.168.1.101")]);

        // Act
        var household = Assert.Single(await discovery.DescribeSpeakersAsync(households, CancellationToken.None));

        // Assert - the surrogate pair that would be cut in half is dropped whole.
        var item = Assert.Single(household.IncludedItems);
        Assert.Equal(new string('r', SonosSpeakerDescriptions.MaximumNameLength - 1), item.DisplayName);
        Assert.Equal($"{new string('m', SonosSpeakerDescriptions.MaximumNameLength)}, 192.168.1.101", item.Detail);
    }

    [Fact]
    public async Task WhenASpeakerAnswersWithARedirect_ThenTheRedirectIsNotFollowed()
    {
        // Arrange - the subject discovery with its own handler, against a speaker on the loopback address, with a
        // deadline that never passes.
        var paths = new List<string>();
        await using var speaker = new LoopbackHttpServer(async context =>
        {
            lock (paths)
            {
                paths.Add(context.Request.Url!.PathAndQuery);
            }

            if (context.Request.Url.AbsolutePath == "/xml/device_description.xml")
            {
                context.Response.StatusCode = (int)HttpStatusCode.Found;
                context.Response.RedirectLocation = "/redirected";
                return;
            }

            var body = Encoding.UTF8.GetBytes("<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><roomName>Redirected</roomName></device></root>");
            context.Response.ContentType = "text/xml; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(body);
        });
        var discovery = new SonosHouseholdDiscovery(handler: null, new FakeTimeProvider());
        var households = SonosHouseholdDiscovery.ToHouseholds([Answer("RINCON_A00000000000001400", "127.0.0.1", port: speaker.Port)]);

        // Act
        var household = Assert.Single(await discovery.DescribeSpeakersAsync(households, CancellationToken.None));

        // Assert
        Assert.Equal([new DiscoveredItem("RINCON_A00000000000001400", "127.0.0.1")], household.IncludedItems);
        Assert.Equal(["/xml/device_description.xml"], paths);
    }

    [Fact]
    public async Task WhenASpeakerAnswersOverTheNetwork_ThenItsRoomIsRead()
    {
        // Arrange - the subject discovery with its own handler, against a speaker on the loopback address, with a
        // deadline that never passes.
        await using var speaker = new LoopbackHttpServer(async context =>
        {
            var body = Encoding.UTF8.GetBytes(TestFixtures.Read("device-description-ray.xml"));
            context.Response.ContentType = "text/xml";
            await context.Response.OutputStream.WriteAsync(body);
        });
        var discovery = new SonosHouseholdDiscovery(handler: null, new FakeTimeProvider());
        var households = SonosHouseholdDiscovery.ToHouseholds([Answer("RINCON_A00000000000001400", "127.0.0.1", port: speaker.Port)]);

        // Act
        var household = Assert.Single(await discovery.DescribeSpeakersAsync(households, CancellationToken.None));

        // Assert
        Assert.Equal([new DiscoveredItem("Büro", $"Sonos Ray, 127.0.0.1")], household.IncludedItems);
    }

    [Fact]
    public async Task WhenMoreSpeakersAnswerThanDescriptionsAreRead_ThenTheFurtherSpeakersAreIncludedById()
    {
        // Arrange
        using var handler = new DescriptionHandler(_ => Description("Room", "Sonos One"));
        var discovery = new SonosHouseholdDiscovery(handler, new FakeTimeProvider());
        var count = SonosSpeakerDescriptions.MaximumDescriptions + 1;
        var households = SonosHouseholdDiscovery.ToHouseholds(
        [
            .. Enumerable.Range(0, count).Select(index =>
                Answer($"RINCON_{index:D3}000000000001400", $"10.0.{index / 250}.{index % 250 + 1}"))
        ]);

        // Act
        var household = Assert.Single(await discovery.DescribeSpeakersAsync(households, CancellationToken.None));

        // Assert
        Assert.Equal(SonosSpeakerDescriptions.MaximumDescriptions, handler.RequestCount);
        Assert.Equal(count, household.IncludedItems.Count);
        Assert.Single(household.IncludedItems, item => item.DisplayName.StartsWith("RINCON_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheScanIsCanceledWhileDescriptionsAreRead_ThenTheCancellationPropagates()
    {
        // Arrange
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DescriptionHandler(async (_, cancellationToken) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Not reached.");
        });
        var discovery = new SonosHouseholdDiscovery(handler, new FakeTimeProvider());
        var households = SonosHouseholdDiscovery.ToHouseholds([Answer("RINCON_A00000000000001400", "192.168.1.101")]);
        using var cancellation = new CancellationTokenSource();

        // Act
        var describing = discovery.DescribeSpeakersAsync(households, cancellation.Token);
        await requestStarted.Task;
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => describing);
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
        Assert.True(SonosHouseholdDiscovery.IsSameHousehold(system, Found(("RINCON_A00000000000001400", "192.168.1.101"), ("RINCON_B00000000000001400", "192.168.1.102"))));
        Assert.False(SonosHouseholdDiscovery.IsSameHousehold(system, Found(("RINCON_C00000000000001400", "192.168.1.103"))));
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
        Assert.True(SonosHouseholdDiscovery.IsSameHousehold(system, Found(("RINCON_S00000000000001400", "192.168.1.110"))));
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
        Assert.True(SonosHouseholdDiscovery.IsSameHousehold(system, Found(("RINCON_A00000000000001400", "192.168.1.101"), ("RINCON_B00000000000001400", "192.168.1.102"))));
    }

    [Fact]
    public void WhenTheSeedHostNamesALocalHostName_ThenItIsComparedIgnoringCase()
    {
        // Arrange
        var system = CreateSystem("Sonos-Kitchen.local");

        // Act & Assert
        Assert.True(SonosHouseholdDiscovery.IsSameHousehold(system, Found(("RINCON_A00000000000001400", "sonos-kitchen.local"))));
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
        Assert.False(SonosHouseholdDiscovery.IsSameHousehold(system, Found(("RINCON_A00000000000001400", "192.168.1.101"))));
    }

    private static Dictionary<string, string> HouseholdHeaders() =>
        new(StringComparer.OrdinalIgnoreCase) { ["X-RINCON-HOUSEHOLD"] = Household };

    private static HttpResponseMessage Description(string roomName, string modelName) =>
        Xml($"<?xml version=\"1.0\" encoding=\"utf-8\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device>" +
            $"<modelName>{modelName}</modelName><roomName>{roomName}</roomName></device></root>");

    private static HttpResponseMessage Xml(string xml) =>
        new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };

    private static HttpResponseMessage WithCharset(HttpResponseMessage response, string charset)
    {
        response.Content.Headers.ContentType!.CharSet = charset;
        return response;
    }

    private sealed class DescriptionHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;
        private int _requestCount;

        public DescriptionHandler(Func<HttpRequestMessage, HttpResponseMessage> answer)
            : this((request, _) => Task.FromResult(answer(request)))
        {
        }

        public DescriptionHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        {
            _answer = answer;
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return _answer(request, cancellationToken);
        }
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
        public Task<IReadOnlyList<MulticastDnsServiceInstance>> BrowseAsync(string serviceType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MulticastDnsServiceInstance>>([]);
    }

    private sealed class UnusedUdpDiscovery : IUdpDiscovery
    {
        public IAsyncEnumerable<UdpDatagram> BroadcastAsync(int port, ReadOnlyMemory<byte> request, TimeSpan window, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<UdpDatagram> ListenAsync(int port, TimeSpan window, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
