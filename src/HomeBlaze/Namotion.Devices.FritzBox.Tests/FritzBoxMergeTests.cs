using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.FritzBox.Model;
using Namotion.Devices.FritzBox.Parsing;
using Namotion.Devices.FritzBox.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxMergeTests
{
    [Fact]
    public void WhenASnapshotIsApplied_ThenClientsAccessPointsAndWlansAreCreated()
    {
        // Arrange
        var router = CreateRouter();

        // Act
        router.ApplySnapshot(Snapshots.Create(), router.WlanCommandGeneration);

        // Assert
        Assert.Equal([Snapshots.Laptop, Snapshots.Desktop, Snapshots.Phone], router.Clients.Keys.Order());
        Assert.Equal(3, router.ClientCount);
        Assert.Equal(2, router.PresentClientCount);
        Assert.Equal(2, router.AccessPoints.Count);
        var repeater = router.AccessPoints[Snapshots.RepeaterAccessPoint];
        Assert.Same(router.Clients[Snapshots.Laptop], Assert.Single(repeater.Clients).Value);
        Assert.Equal("192.168.178.30", repeater.IpAddress);
        Assert.Same(router.Clients[Snapshots.Desktop], Assert.Single(router.AccessPoints[Snapshots.RouterAccessPoint].Clients).Value);
        Assert.IsType<FritzBoxGuestWlan>(router.Wlans[FritzBoxWlanRoles.Guest]);
        Assert.IsNotType<FritzBoxGuestWlan>(router.Wlans[FritzBoxWlanRoles.Band24]);
        Assert.Equal("FRITZ!Box 7590", router.Model);
        Assert.Equal("AVM", router.Manufacturer);
        Assert.True(router.IsInternetConnected);
        Assert.Equal(5m, router.UploadRate);
        Assert.Equal(250m, router.MaximumDownloadRate);
    }

    [Fact]
    public void WhenTheSameSnapshotIsAppliedAgain_ThenNoCollectionIsReplaced()
    {
        // Arrange
        var router = CreateRouter();
        router.ApplySnapshot(Snapshots.Create(), router.WlanCommandGeneration);
        var clients = router.Clients;
        var accessPoints = router.AccessPoints;
        var wlans = router.Wlans;
        var repeaterClients = accessPoints[Snapshots.RepeaterAccessPoint].Clients;
        var routerClients = accessPoints[Snapshots.RouterAccessPoint].Clients;

        // Act
        router.ApplySnapshot(Snapshots.Create(), router.WlanCommandGeneration);

        // Assert
        Assert.Same(clients, router.Clients);
        Assert.Same(accessPoints, router.AccessPoints);
        Assert.Same(wlans, router.Wlans);
        Assert.Same(repeaterClients, router.AccessPoints[Snapshots.RepeaterAccessPoint].Clients);
        Assert.Same(routerClients, router.AccessPoints[Snapshots.RouterAccessPoint].Clients);
    }

    [Fact]
    public void WhenAClientMovesToAnotherAccessPoint_ThenItKeepsItsInstanceAndOnlyTheTwoListsChange()
    {
        // Arrange
        var router = CreateRouter();
        var mesh = Snapshots.Mesh();
        router.ApplySnapshot(Snapshots.Create(mesh: mesh), router.WlanCommandGeneration);
        var clients = router.Clients;
        var laptop = router.Clients[Snapshots.Laptop];
        var positions = new Dictionary<string, ClientPosition>(mesh.ClientPositions)
        {
            [Snapshots.Laptop] = new(Snapshots.RouterAccessPoint, "fritz.box", FritzBoxWlanRoles.Band5, null, 433m, true)
        };

        // Act
        router.ApplySnapshot(Snapshots.Create(mesh: mesh with { ClientPositions = positions }), router.WlanCommandGeneration);

        // Assert
        Assert.Same(clients, router.Clients);
        Assert.Same(laptop, router.Clients[Snapshots.Laptop]);
        Assert.Contains(laptop, router.AccessPoints[Snapshots.RouterAccessPoint].Clients.Values);
        Assert.Empty(router.AccessPoints[Snapshots.RepeaterAccessPoint].Clients);
        Assert.Equal("fritz.box", laptop.AccessPointName);
        Assert.Equal(FritzBoxWlanRoles.Band5, laptop.Band);
    }

    [Fact]
    public void WhenAClientIsInTwoCollections_ThenItHasBothAsParents()
    {
        // Arrange
        var router = CreateRouter();

        // Act
        router.ApplySnapshot(Snapshots.Create(), router.WlanCommandGeneration);

        // Assert
        var laptop = router.Clients[Snapshots.Laptop];
        var parents = laptop.TryGetRegisteredSubject()!.Parents;
        Assert.Equal(2, parents.Length);
        Assert.Contains(parents, parent => ReferenceEquals(parent.Property.Subject, router));
        Assert.Contains(parents, parent => ReferenceEquals(parent.Property.Subject, router.AccessPoints[Snapshots.RepeaterAccessPoint]));
    }

    [Fact]
    public void WhenAHostDisappears_ThenItsClientIsRemovedAndDetached()
    {
        // Arrange
        var router = CreateRouter();
        router.ApplySnapshot(Snapshots.Create(), router.WlanCommandGeneration);
        var phone = router.Clients[Snapshots.Phone];
        var hosts = Snapshots.Hosts().Where(host => host.MacAddress != Snapshots.Phone).ToList();

        // Act
        router.ApplySnapshot(Snapshots.Create(hosts: hosts), router.WlanCommandGeneration);

        // Assert
        Assert.False(router.Clients.ContainsKey(Snapshots.Phone));
        Assert.Null(phone.TryGetRegisteredSubject());
    }

    [Fact]
    public void WhenAddressesRepeatInAnotherCaseOrAreMissing_ThenOneClientPerAddressIsKept()
    {
        // Arrange
        var router = CreateRouter();
        List<HostEntry> hosts =
        [
            new("aa:aa:aa:00:00:09", "10.0.0.1", true, "first", "Ethernet", 1, 100, false),
            new("AA:AA:AA:00:00:09", "10.0.0.2", true, "second", "Ethernet", 1, 100, false),
            new(null, "10.0.0.3", true, "no address", "", 0, 0, false),
            new("   ", "10.0.0.4", true, "blank address", "", 0, 0, false)
        ];

        // Act
        router.ApplySnapshot(Snapshots.Create(hosts: hosts, hasMesh: false), router.WlanCommandGeneration);

        // Assert
        var client = Assert.Single(router.Clients);
        Assert.Equal("AA:AA:AA:00:00:09", client.Key);
        Assert.Equal("10.0.0.2", client.Value.IpAddress);
    }

    [Fact]
    public void WhenAClientIsAbsent_ThenItBelongsToNoAccessPoint()
    {
        // Arrange
        var router = CreateRouter();
        var hosts = Snapshots.Hosts().Select(host => host.MacAddress == Snapshots.Laptop ? host with { IsActive = false } : host).ToList();

        // Act
        router.ApplySnapshot(Snapshots.Create(hosts: hosts), router.WlanCommandGeneration);

        // Assert
        var laptop = router.Clients[Snapshots.Laptop];
        Assert.False(laptop.IsPresent);
        Assert.Null(laptop.AccessPointName);
        Assert.Empty(router.AccessPoints[Snapshots.RepeaterAccessPoint].Clients);
    }

    [Fact]
    public void WhenThereIsNoMeshList_ThenThereAreNoAccessPointsAndRepeatersCountAsClients()
    {
        // Arrange
        var router = CreateRouter();

        // Act
        router.ApplySnapshot(Snapshots.Create(hasMesh: false), router.WlanCommandGeneration);

        // Assert
        Assert.Empty(router.AccessPoints);
        Assert.Equal(4, router.Clients.Count);
        Assert.Null(router.Clients[Snapshots.Laptop].AccessPointName);
    }

    [Fact]
    public void WhenTheSerialNumberChanges_ThenEveryCollectionStartsOver()
    {
        // Arrange
        var router = CreateRouter();
        router.ApplySnapshot(Snapshots.Create(), router.WlanCommandGeneration);
        var laptop = router.Clients[Snapshots.Laptop];

        // Act
        router.ApplySnapshot(Snapshots.Create(router: Snapshots.Router("SERIAL0002")), router.WlanCommandGeneration);

        // Assert
        Assert.NotSame(laptop, router.Clients[Snapshots.Laptop]);
        Assert.Equal("SERIAL0002", router.SerialNumber);
    }

    [Fact]
    public void WhenAWlanCommandRanDuringThePoll_ThenThePolledSwitchStateIsIgnored()
    {
        // Arrange
        var router = CreateRouter();
        router.ApplySnapshot(Snapshots.Create(wlans: Snapshots.Wlans(isGuestEnabled: false)), router.WlanCommandGeneration);
        var guest = router.Wlans[FritzBoxWlanRoles.Guest];
        var generationAtPollStart = router.WlanCommandGeneration;
        router.AdvanceWlanCommandGeneration();
        guest.IsOn = false;

        // Act
        router.ApplySnapshot(Snapshots.Create(wlans: Snapshots.Wlans(isGuestEnabled: true)), generationAtPollStart);

        // Assert
        Assert.False(guest.IsOn);
        Assert.Equal("Guests", guest.Ssid);
    }

    [Fact]
    public void WhenAPartOfThePollHasNoAnswer_ThenOnlyItsValuesAreNull()
    {
        // Arrange
        var router = CreateRouter();
        router.ApplySnapshot(Snapshots.Create(), router.WlanCommandGeneration);

        // Act
        router.ApplySnapshot(Snapshots.Create() with { Traffic = null }, router.WlanCommandGeneration);

        // Assert
        Assert.Null(router.UploadRate);
        Assert.Null(router.DownloadRate);
        Assert.Equal("FRITZ!Box 7590", router.Model);
    }

    [Fact]
    public void WhenAWlanIsNewAndACommandRanDuringThePoll_ThenItsSwitchStateIsStillWritten()
    {
        // Arrange
        var router = CreateRouter();
        var generationAtPollStart = router.WlanCommandGeneration;
        router.AdvanceWlanCommandGeneration();

        // Act
        router.ApplySnapshot(Snapshots.Create(wlans: Snapshots.Wlans(isGuestEnabled: true)), generationAtPollStart);

        // Assert
        Assert.True(router.Wlans[FritzBoxWlanRoles.Guest].IsOn);
        Assert.True(router.Wlans[FritzBoxWlanRoles.Band24].IsOn);
    }

    [Fact]
    public void WhenAClientBecomesPresent_ThenThePresentCountChangesWithoutReplacingTheClients()
    {
        // Arrange
        var router = CreateRouter();
        router.ApplySnapshot(Snapshots.Create(), router.WlanCommandGeneration);
        var clients = router.Clients;
        var hosts = Snapshots.Hosts().Select(host => host.MacAddress == Snapshots.Phone ? host with { IsActive = true } : host).ToList();

        // Act
        router.ApplySnapshot(Snapshots.Create(hosts: hosts), router.WlanCommandGeneration);

        // Assert
        Assert.Same(clients, router.Clients);
        Assert.Equal(3, router.PresentClientCount);
        Assert.Equal(3, router.ClientCount);
    }

    [Fact]
    public void WhenOnlyAnInterfaceAddressOfAnAccessPointIsListed_ThenItsIpAddressIsFound()
    {
        // Arrange
        var router = CreateRouter();
        var hosts = Snapshots.Hosts()
            .Select(host => host.MacAddress == Snapshots.RepeaterAccessPoint ? host with { MacAddress = "BB:BB:BB:00:00:12", IpAddress = "192.168.178.31" } : host)
            .ToList();

        // Act
        router.ApplySnapshot(Snapshots.Create(hosts: hosts), router.WlanCommandGeneration);

        // Assert
        Assert.Equal("192.168.178.31", router.AccessPoints[Snapshots.RepeaterAccessPoint].IpAddress);
        Assert.False(router.Clients.ContainsKey("BB:BB:BB:00:00:12"));
    }

    [Fact]
    public void WhenAnAccessPointDisappears_ThenItIsRemovedAndDetached()
    {
        // Arrange
        var router = CreateRouter();
        var mesh = Snapshots.Mesh();
        router.ApplySnapshot(Snapshots.Create(mesh: mesh), router.WlanCommandGeneration);
        var repeater = router.AccessPoints[Snapshots.RepeaterAccessPoint];
        var laptop = router.Clients[Snapshots.Laptop];
        var reducedMesh = mesh with
        {
            AccessPoints = mesh.AccessPoints.Where(accessPoint => accessPoint.MacAddress != Snapshots.RepeaterAccessPoint).ToList(),
            ClientPositions = mesh.ClientPositions.Where(position => position.Key != Snapshots.Laptop).ToDictionary()
        };

        // Act
        router.ApplySnapshot(Snapshots.Create(mesh: reducedMesh), router.WlanCommandGeneration);

        // Assert
        Assert.False(router.AccessPoints.ContainsKey(Snapshots.RepeaterAccessPoint));
        Assert.Null(repeater.TryGetRegisteredSubject());
        Assert.Same(laptop, router.Clients[Snapshots.Laptop]);
        Assert.Single(laptop.TryGetRegisteredSubject()!.Parents);
    }

    [Fact]
    public void WhenTheSerialNumberChanges_ThenThePreviousSubjectsAreDetachedAndThePresentedKeyIsCleared()
    {
        // Arrange
        var router = CreateRouter();
        router.ApplySnapshot(Snapshots.Create(), router.WlanCommandGeneration);
        router.PresentedCertificatePin = "presented";
        var laptop = router.Clients[Snapshots.Laptop];
        var repeater = router.AccessPoints[Snapshots.RepeaterAccessPoint];
        var guest = router.Wlans[FritzBoxWlanRoles.Guest];

        // Act
        router.ApplySnapshot(Snapshots.Create(router: Snapshots.Router("SERIAL0002")), router.WlanCommandGeneration);

        // Assert
        Assert.Null(laptop.TryGetRegisteredSubject());
        Assert.Null(repeater.TryGetRegisteredSubject());
        Assert.Null(guest.TryGetRegisteredSubject());
        Assert.Null(router.PresentedCertificatePin);
        Assert.NotSame(guest, router.Wlans[FritzBoxWlanRoles.Guest]);
    }

    private static FritzBox CreateRouter()
    {
        var router = new FritzBox(NullLogger<FritzBox>.Instance);
        ((IInterceptorSubject)router).Context.AddFallbackContext(InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry());
        return router;
    }
}
