using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Authorization.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;
using static Namotion.Interceptor.Authorization.Tests.Policies;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class IncompletePathTests
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

        // Act
        var denyCanReadCeiling = deny.CanRead(ceiling);
        var denyCanReadLamp = deny.CanRead(lamp);
        var allowCanReadCeiling = allow.CanRead(ceiling);

        // Assert
        Assert.Same(home.Rooms["Bedroom"], Registered(kitchen).Parents[0].Property.Subject);
        Assert.False(denyCanReadCeiling);
        Assert.True(denyCanReadLamp);
        Assert.True(allowCanReadCeiling);
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

        // Act
        var denyCanReadBrightness = deny.CanRead(brightness);
        var denyExplainsReadingBrightnessAsAllowed = deny.Explain(brightness, PermissionAction.Read).IsAllowed;
        var denyCanReadSsid = deny.CanRead(ssid);
        var allowCanReadBrightness = allow.CanRead(brightness);
        var allowCanReadSsid = allow.CanRead(ssid);

        // Assert
        Assert.False(denyCanReadBrightness);
        Assert.False(denyExplainsReadingBrightnessAsAllowed);
        Assert.True(denyCanReadSsid);
        Assert.False(allowCanReadBrightness);
        Assert.True(allowCanReadSsid);
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void WhenASubjectHasMorePathsThanTheLimit_ThenADenyWithPathsFailsClosedAndAnAllowStillMatchesAListedPath(int diamonds, bool arePathsComplete)
    {
        // Arrange: each stacked diamond doubles the paths of the leaf, so 6 give exactly 64 and 7 give 128.
        var context = CreateContext();
        var current = new Person(context);
        for (var i = 0; i < diamonds; i++)
        {
            var next = new Person(context);
            current.Father = next;
            current.Mother = next;
            current = next;
        }

        var member = Member(current, nameof(Person.Children));
        var deny = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Nope/**"])]));
        var allow = Compile(Role("Reader", allow: [Rule(Read, paths: ["/**"])]));

        // Act
        var denyCanRead = deny.CanRead(member);
        var allowCanRead = allow.CanRead(member);

        // Assert
        Assert.Equal(arePathsComplete, denyCanRead);
        Assert.True(allowCanRead);
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

        // Act
        var denyCanReadBefore = deny.CanRead(password);
        var allowCanReadBefore = allow.CanRead(password);
        home.Wifi = null;
        var denyCanReadAfter = deny.CanRead(password);
        var denyCanReadParentAfter = deny.CanRead(password.Parent);
        var allowCanReadAfter = allow.CanRead(password);

        // Assert
        Assert.False(denyCanReadBefore);
        Assert.True(allowCanReadBefore);
        Assert.Null(wifi.TryGetRegisteredSubject());
        Assert.Empty(password.Parent.Parents);
        Assert.False(denyCanReadAfter);
        Assert.False(denyCanReadParentAfter);
        Assert.False(allowCanReadAfter);
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
        var deskCanReadBefore = policy.CanRead(deskBrightness);
        home.Rooms = [];
        var ceilingCanReadAfter = policy.CanRead(ceilingBrightness);
        var deskCanReadAfter = policy.CanRead(deskBrightness);

        // Assert
        Assert.False(deskCanReadBefore);
        Assert.False(ceilingReadable);
        Assert.False(deskReadable);
        Assert.False(ceilingCanReadAfter);
        Assert.False(deskCanReadAfter);
    }

    [Fact]
    public void WhenASubjectHiddenByALinkDenyIsDetached_ThenItStaysHidden()
    {
        // Arrange
        var home = Create();
        var kitchen = Registered(home.Rooms["Kitchen"]);
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, members: ["Rooms"])]));

        // Act
        var canReadBefore = policy.CanRead(kitchen);
        home.Rooms = [];
        var decisionAfter = policy.Explain(kitchen);

        // Assert
        Assert.False(canReadBefore);
        Assert.Empty(kitchen.Parents);
        Assert.Equal(new SubjectReadDecision(IsReadable: false, HasReadableMember: true, DeniedProperty: null), decisionAfter);
    }

    [Fact]
    public void WhenASubjectWithoutParentsIsRegistered_ThenItIsARootAndDenyRulesForOtherPathsDoNotApply()
    {
        // Arrange
        var home = Create();
        var unattached = new Wifi(CreateContext());
        var deny = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Wifi/**"])]));
        var allow = Compile(Role("Reader", allow: [Rule(Read, paths: ["/*"])]));

        // Act
        var denyCanReadRooms = deny.CanRead(Member(home, nameof(Home.Rooms)));
        var denyCanReadUnattachedSsid = deny.CanRead(Member(unattached, nameof(Wifi.Ssid)));
        var allowCanReadRooms = allow.CanRead(Member(home, nameof(Home.Rooms)));
        var allowCanReadUnattachedSsid = allow.CanRead(Member(unattached, nameof(Wifi.Ssid)));
        var denyCanReadUnattachedSubject = deny.CanRead(Registered(unattached));

        // Assert
        Assert.True(denyCanReadRooms);
        Assert.True(denyCanReadUnattachedSsid);
        Assert.True(allowCanReadRooms);
        Assert.True(allowCanReadUnattachedSsid);
        Assert.True(denyCanReadUnattachedSubject);
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
        var isAllowedAction61 = policy.IsAllowed(member, actions[61]);
        var isAllowedAction60 = policy.IsAllowed(member, actions[60]);

        // Assert
        Assert.True(isAllowedAction61);
        Assert.False(isAllowedAction60);
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

        // Act
        var denyCanReadLights = deny.CanRead(lights);
        var allowCanReadLights = allow.CanRead(lights);

        // Assert
        Assert.False(denyCanReadLights);
        Assert.False(allowCanReadLights);
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
