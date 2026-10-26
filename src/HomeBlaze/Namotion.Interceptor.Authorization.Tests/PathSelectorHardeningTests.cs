using Namotion.Interceptor.Authorization.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using static Namotion.Interceptor.Authorization.Tests.Policies;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class PathSelectorHardeningTests
{
    private static readonly PermissionAction[] Read = [PermissionAction.Read];

    [Fact]
    public void WhenAnAncestorHasSeveralParents_ThenADenyOnAnyOfItsPathsApplies()
    {
        // Arrange
        var home = CreateWithNeighborFirst();
        var kitchen = home.Rooms["Kitchen"];
        var ceiling = Member(kitchen.Lights["Ceiling"], nameof(Light.Brightness));
        var lamp = Member(home.Rooms["Bedroom"].Lights["Lamp"], nameof(Light.Brightness));
        var deny = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Rooms[Kitchen]/**"])]));
        var allow = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Rooms[Kitchen]/**"])]));

        // Act & Assert
        Assert.Same(home.Rooms["Bedroom"], Registered(kitchen).Parents[0].Property.Subject);
        Assert.False(deny.CanRead(ceiling));
        Assert.True(deny.CanRead(lamp));
        Assert.True(allow.CanRead(ceiling));
    }

    [Fact]
    public void WhenAPathCannotBeWritten_ThenADenyWithPathsAppliesAndAnAllowDoesNot()
    {
        // Arrange
        var home = Create();
        var spare = new Light();
        home.Spare = spare;
        var brightness = Member(spare, nameof(Light.Brightness));
        var ssid = Member(home.Wifi!, nameof(Wifi.Ssid));
        var deny = CompileExcludingSpare(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Rooms[Bedroom]/**"])]));
        var allow = CompileExcludingSpare(Role("Reader", allow: [Rule(Read, paths: ["/**"])]));

        // Act & Assert
        Assert.False(deny.CanRead(brightness));
        Assert.False(deny.Explain(brightness, PermissionAction.Read).IsAllowed);
        Assert.True(deny.CanRead(ssid));
        Assert.False(allow.CanRead(brightness));
        Assert.True(allow.CanRead(ssid));
    }

    [Fact]
    public void WhenExactly64ActionsAreKnown_ThenTheLastActionIsUsable()
    {
        // Arrange
        var home = Create();
        var member = Member(home.Wifi!, nameof(Wifi.Ssid));
        var actions = Enumerable.Range(1, 62).Select(i => new PermissionAction($"action{i}")).ToArray();
        var role = Role("Operator", allow: [Rule([actions[61]])]);

        // Act
        var policy = AuthorizationPolicy.Compile([role], ["Operator"], [], actions, TestTags.Default, SlashPathProvider.Instance);

        // Assert
        Assert.True(policy.IsAllowed(member, actions[61]));
        Assert.False(policy.IsAllowed(member, actions[60]));
        var exception = Assert.Throws<AuthorizationException>(
            () => AuthorizationPolicy.Compile([], [], [], [.. actions, new PermissionAction("action63")], TestTags.Default, SlashPathProvider.Instance));
        Assert.Equal("At most 64 actions are supported.", exception.Message);
    }

    [Fact]
    public void WhenParentsFormAnOrphanCycle_ThenExplainReportsNoDeniedProperty()
    {
        // Arrange
        var context = CreateContext();
        var first = new Room(context);
        var second = new Room(context) { Neighbor = first };
        first.Neighbor = second;
        var policy = Compile(Role("Reader", allow: [Rule(Read)]));

        // Act
        var decision = policy.Explain(Registered(first));

        // Assert
        Assert.Equal(new SubjectReadDecision(IsReadable: false, HasReadableMember: true, DeniedProperty: null), decision);
    }

    [Fact]
    public void WhenRoomsPointAtEachOtherAndTheirOnlyWayToTheRootIsDenied_ThenExplainReportsTheDeniedLink()
    {
        // Arrange
        var home = Create();
        var kitchen = home.Rooms["Kitchen"];
        var bedroom = home.Rooms["Bedroom"];
        kitchen.Neighbor = bedroom;
        bedroom.Neighbor = kitchen;
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, types: ["Home"], members: ["Rooms"])]));

        // Act
        var decision = policy.Explain(Registered(kitchen));

        // Assert
        Assert.False(decision.IsReadable);
        Assert.True(decision.HasReadableMember);
        Assert.Same(Member(home, nameof(Home.Rooms)), decision.DeniedProperty);
    }

    [Fact]
    public void WhenOrphanRoomsPointAtEachOther_ThenADenyWithPathsAppliesAndAnAllowDoesNot()
    {
        // Arrange
        var context = CreateContext();
        var first = new Room(context);
        var second = new Room(context) { Neighbor = first };
        first.Neighbor = second;
        var lights = Member(first, nameof(Room.Lights));
        var deny = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/**"])]));
        var allow = Compile(Role("Reader", allow: [Rule(Read, paths: ["/**"])]));

        // Act & Assert
        Assert.False(deny.CanRead(lights));
        Assert.False(allow.CanRead(lights));
    }

    [Fact]
    public void WhenTwoRoomsPointAtEachOtherTwiceAndTheirOnlyWayToTheRootIsDenied_ThenExplainReportsTheDeniedLink()
    {
        // Arrange
        var home = Create();
        var kitchen = home.Rooms["Kitchen"];
        var bedroom = home.Rooms["Bedroom"];
        kitchen.Neighbor = bedroom;
        kitchen.Opposite = bedroom;
        bedroom.Neighbor = kitchen;
        bedroom.Opposite = kitchen;
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, members: ["Rooms"])]));

        // Act
        var decision = policy.Explain(Registered(kitchen));

        // Assert
        Assert.Equal(new SubjectReadDecision(IsReadable: false, HasReadableMember: true, Member(home, nameof(Home.Rooms))), decision);
    }

    [Fact]
    public void WhenThreeRoomsPointAtEachOtherAndTheirOnlyWayToTheRootIsDenied_ThenExplainReportsTheDeniedLink()
    {
        // Arrange
        var home = Create();
        var kitchen = home.Rooms["Kitchen"];
        var bedroom = home.Rooms["Bedroom"];
        var office = new Room();
        home.Rooms = new Dictionary<string, Room> { ["Kitchen"] = kitchen, ["Bedroom"] = bedroom, ["Office"] = office };
        kitchen.Neighbor = bedroom;
        kitchen.Opposite = office;
        bedroom.Neighbor = office;
        bedroom.Opposite = kitchen;
        office.Neighbor = kitchen;
        office.Opposite = bedroom;
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, members: ["Rooms"])]));

        // Act
        var decision = policy.Explain(Registered(kitchen));

        // Assert
        Assert.Equal(new SubjectReadDecision(IsReadable: false, HasReadableMember: true, Member(home, nameof(Home.Rooms))), decision);
    }

    private static AuthorizationPolicy CompileExcludingSpare(AuthorizationRole role)
        => AuthorizationPolicy.Compile([role], [role.Name], [], KnownActions, TestTags.Default, new SpareExcludingPathProvider());

    private sealed class SpareExcludingPathProvider : PathProviderBase
    {
        public override char PathSeparator => '/';

        public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
            => !(property.Subject is Home && property.Name == nameof(Home.Spare));
    }
}
