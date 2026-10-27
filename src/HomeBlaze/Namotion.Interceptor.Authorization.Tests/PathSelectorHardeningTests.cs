using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Authorization.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;
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
    public void WhenASubjectIsDetached_ThenDenyRulesWithPathsStillApplyAndAllowRulesWithPathsDoNot()
    {
        // Arrange
        var home = Create();
        var wifi = home.Wifi!;
        var password = Member(wifi, nameof(Wifi.Password));
        var deny = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Wifi/**"])]));
        var allow = Compile(Role("Reader", allow: [Rule(Read, paths: ["/**"])]));
        Assert.False(deny.CanRead(password));
        Assert.True(allow.CanRead(password));

        // Act
        home.Wifi = null;

        // Assert
        Assert.Null(wifi.TryGetRegisteredSubject());
        Assert.Empty(password.Parent.Parents);
        Assert.False(deny.CanRead(password));
        Assert.False(deny.CanRead(password.Parent));
        Assert.False(allow.CanRead(password));
    }

    [Fact]
    public void WhenAParentIsBeingDetached_ThenDenyRulesWithPathsApplyToTheSubjectsBelowIt()
    {
        // Arrange
        var context = CreateContext();
        var home = new Home(context);
        var desk = new Light();
        var annex = new Room { Lights = new Dictionary<string, Light> { ["Desk"] = desk } };
        var kitchen = new Room { Neighbor = annex };
        kitchen.Lights = new Dictionary<string, Light> { ["Ceiling"] = new Light() };
        home.Rooms = new Dictionary<string, Room> { ["Kitchen"] = kitchen };
        var ceilingBrightness = Member(kitchen.Lights["Ceiling"], nameof(Light.Brightness));
        var deskBrightness = Member(desk, nameof(Light.Brightness));
        var registeredAnnex = Registered(annex);
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Rooms[Kitchen]/**"])]));
        Assert.False(policy.CanRead(deskBrightness));

        bool? ceilingReadable = null;
        bool? deskReadable = null;
        context.AddService(new DetachProbe(kitchen, () =>
        {
            // The registry has unlinked the kitchen's children, which are still registered and referenced until their own detach.
            Assert.Empty(ceilingBrightness.Parent.Parents);
            Assert.Empty(registeredAnnex.Parents);
            Assert.NotEmpty(deskBrightness.Parent.Parents);
            Assert.Same(registeredAnnex, annex.TryGetRegisteredSubject());
            ceilingReadable = policy.CanRead(ceilingBrightness);
            deskReadable = policy.CanRead(deskBrightness);
        }));

        // Act
        home.Rooms = [];

        // Assert
        Assert.False(ceilingReadable);
        Assert.False(deskReadable);
        Assert.False(policy.CanRead(ceilingBrightness));
        Assert.False(policy.CanRead(deskBrightness));
    }

    [Fact]
    public void WhenASubjectHiddenByALinkDenyIsDetached_ThenItStaysHidden()
    {
        // Arrange
        var home = Create();
        var kitchen = Registered(home.Rooms["Kitchen"]);
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, members: ["Rooms"])]));
        Assert.False(policy.CanRead(kitchen));

        // Act
        home.Rooms = [];

        // Assert
        Assert.Empty(kitchen.Parents);
        Assert.Equal(new SubjectReadDecision(IsReadable: false, HasReadableMember: true, DeniedProperty: null), policy.Explain(kitchen));
    }

    [Fact]
    public void WhenASubjectWithoutParentsIsRegistered_ThenItIsARootAndDenyRulesForOtherPathsDoNotApply()
    {
        // Arrange
        var home = Create();
        var unattached = new Wifi(CreateContext());
        var deny = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Wifi/**"])]));
        var allow = Compile(Role("Reader", allow: [Rule(Read, paths: ["/*"])]));

        // Act & Assert
        Assert.True(deny.CanRead(Member(home, nameof(Home.Rooms))));
        Assert.True(deny.CanRead(Member(unattached, nameof(Wifi.Ssid))));
        Assert.True(allow.CanRead(Member(home, nameof(Home.Rooms))));
        Assert.True(allow.CanRead(Member(unattached, nameof(Wifi.Ssid))));
        Assert.True(deny.CanRead(Registered(unattached)));
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
        var policy = AuthorizationPolicy.Compile([role], ["Operator"], [], actions, TestTags.Default, Policies.SlashPaths);

        // Assert
        Assert.True(policy.IsAllowed(member, actions[61]));
        Assert.False(policy.IsAllowed(member, actions[60]));
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

    /// <summary>
    /// Runs a callback when the subject detaches, after the registry has handled the detach and before the subject's
    /// context stops inheriting from its parent, which detaches the subjects below it.
    /// </summary>
    [RunsAfter(typeof(SubjectRegistry))]
    [RunsBefore(typeof(ContextInheritanceHandler))]
    private sealed class DetachProbe(IInterceptorSubject subject, Action onDetach) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextDetach && ReferenceEquals(change.Subject, subject))
            {
                onDetach();
            }
        }
    }

    private static AuthorizationPolicy CompileExcludingSpare(AuthorizationRole role)
        => AuthorizationPolicy.Compile([role], [role.Name], [], KnownActions, TestTags.Default, new ExcludingPathProvider(nameof(Home.Spare), '/'));
}
