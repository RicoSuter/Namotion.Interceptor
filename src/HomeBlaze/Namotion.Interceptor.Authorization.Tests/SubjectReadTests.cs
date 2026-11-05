using Namotion.Interceptor.Authorization.Tests.Models;
using static Namotion.Interceptor.Authorization.Tests.Policies;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class SubjectReadTests
{
    private static readonly PermissionAction[] Read = [PermissionAction.Read];

    [Fact]
    public void WhenNoMemberIsReadable_ThenSubjectIsNotReadable()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, tags: ["subjects"])]));

        // Act
        var decision = policy.Explain(Registered(home.Wifi!));

        // Assert
        Assert.False(decision.IsReadable);
        Assert.False(decision.HasReadableMember);
        Assert.Null(decision.DeniedProperty);
    }

    [Fact]
    public void WhenMembersAndTheWayToTheRootAreReadable_ThenSubjectIsReadable()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, tags: ["state"]), Rule(Read, tags: ["subjects"])]));

        // Act & Assert
        Assert.True(policy.CanRead(Registered(home.Rooms["Kitchen"].Lights["Ceiling"])));
        Assert.True(policy.CanRead(Registered(home)));
    }

    [Fact]
    public void WhenALinkOnTheWayIsNotReadable_ThenSubjectIsHiddenAndTheLinkIsNamed()
    {
        // Arrange
        var home = Create();
        var role = Role("Reader",
            allow: [Rule(Read, tags: ["state"]), Rule(Read, tags: ["subjects"])],
            deny: [Rule(Read, members: ["Rooms"])]);
        var policy = Compile(role);

        // Act
        var decision = policy.Explain(Registered(home.Rooms["Kitchen"].Lights["Ceiling"]));

        // Assert
        Assert.False(decision.IsReadable);
        Assert.True(decision.HasReadableMember);
        Assert.Same(Member(home, nameof(Home.Rooms)), decision.DeniedProperty);
    }

    [Fact]
    public void WhenAnotherParentLeadsToTheRoot_ThenSubjectIsReadable()
    {
        // Arrange
        var home = Create();
        var ceiling = home.Rooms["Kitchen"].Lights["Ceiling"];
        home.Spare = ceiling;
        var role = Role("Reader",
            allow: [Rule(Read, tags: ["state"]), Rule(Read, tags: ["subjects"])],
            deny: [Rule(Read, members: ["Rooms"])]);
        var policy = Compile(role);

        // Act & Assert
        Assert.True(policy.CanRead(Registered(ceiling)));
        Assert.Equal(new SubjectReadDecision(IsReadable: true, HasReadableMember: true, DeniedProperty: null), policy.Explain(Registered(ceiling)));
        Assert.False(policy.CanRead(Registered(home.Rooms["Kitchen"].Lights["Counter"])));
    }

    [Fact]
    public void WhenParentsFormACycle_ThenTheWalkTerminatesAndReportsTheDeniedLink()
    {
        // Arrange
        var home = Create();
        home.Rooms["Kitchen"].Neighbor = home.Rooms["Bedroom"];
        home.Rooms["Bedroom"].Neighbor = home.Rooms["Kitchen"];
        var role = Role("Reader",
            allow: [Rule(Read, tags: ["state"]), Rule(Read, tags: ["subjects"])],
            deny: [Rule(Read, members: ["Rooms"])]);
        var policy = Compile(role);

        // Act
        var decision = policy.Explain(Registered(home.Rooms["Kitchen"]));

        // Assert
        Assert.False(decision.IsReadable);
        Assert.Same(Member(home, nameof(Home.Rooms)), decision.DeniedProperty);
    }

    [Fact]
    public void WhenADenyCoversABranch_ThenEverySubjectBeneathIsHidden()
    {
        // Arrange
        var home = Create();
        var role = Role("Reader",
            allow: [Rule(Read)],
            deny: [Rule(Read, paths: ["/Rooms[Bedroom]/**"])]);
        var policy = Compile(role);

        // Act & Assert
        Assert.False(policy.CanRead(Registered(home.Rooms["Bedroom"])));
        Assert.False(policy.CanRead(Registered(home.Rooms["Bedroom"].Lights["Lamp"])));
        Assert.True(policy.CanRead(Registered(home.Rooms["Kitchen"])));
    }

    [Fact]
    public void WhenADenyNamesTheHoldingProperty_ThenTheBranchIsHiddenAlthoughItsMembersAreReadable()
    {
        // Arrange
        var home = Create();
        var kitchen = home.Rooms["Kitchen"];
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Rooms[Kitchen]/Sensor"])]));

        // Act
        var decision = policy.Explain(Registered(kitchen.Sensor!));

        // Assert
        Assert.Equal(new SubjectReadDecision(IsReadable: false, HasReadableMember: true, Member(kitchen, nameof(Room.Sensor))), decision);
        Assert.True(policy.CanRead(Member(kitchen.Sensor!, nameof(Sensor.Temperature))));
        Assert.True(policy.CanRead(Registered(kitchen)));
    }

    [Fact]
    public void WhenASubjectIsHeldByAnAttribute_ThenItIsVisibleOnlyWhileTheAttributeIsReadable()
    {
        // Arrange
        var home = Create();
        var sensor = home.Rooms["Kitchen"].Sensor!;
        var probe = new Light();
        sensor.Temperature_Probe = probe;
        var open = Compile(Role("Reader", allow: [Rule(Read)]));
        var hidden = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Rooms[Kitchen]/Sensor/Probe"])]));

        // Act
        var decision = hidden.Explain(Registered(probe));

        // Assert
        Assert.True(open.CanRead(Registered(probe)));
        Assert.Equal(new SubjectReadDecision(IsReadable: false, HasReadableMember: true, Attribute(sensor, nameof(Sensor.Temperature), "Probe")), decision);
        Assert.True(hidden.CanRead(Member(probe, nameof(Light.IsOn))));
    }

    [Fact]
    public void WhenTheSubjectIsTwentyThousandLevelsDeep_ThenItsVisibilityIsAnsweredWithoutOverflowingTheStack()
    {
        // Arrange: a Father chain attached top-down, one level at a time, so building it does not recurse
        var root = new Person(CreateContext());
        var current = root;
        for (var level = 0; level < 20_000; level++)
        {
            var next = new Person();
            current.Father = next;
            current = next;
        }

        var policy = Compile(Role("Reader", allow: [Rule(Read)]));
        var bottom = Registered(current);

        // Act: on a small stack, so a walk that recurses per ancestor crashes the test host
        var canRead = false;
        var thread = new Thread(() => canRead = policy.CanRead(bottom), maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();

        // Assert
        Assert.True(canRead);
    }

    [Fact]
    public void WhenSeveralParentsAreUnreadable_ThenTheFirstUnreadableLinkInDepthFirstOrderIsNamed()
    {
        // Arrange: the subject is held by the first parent's Mother and the second parent's Father; the first branch
        // is readable up to the link two levels above the subject, the second branch is unreadable at its first link
        var root = new Person(CreateContext());
        var grandfather = new Person();
        var firstParent = new Person();
        var secondParent = new Person();
        var subject = new Person();
        root.Father = grandfather;
        grandfather.Father = firstParent;
        root.Mother = secondParent;
        firstParent.Mother = subject;
        secondParent.Father = subject;
        var policy = Compile(Role("Reader",
            allow: [Rule(Read)],
            deny: [Rule(Read, paths: ["/Father/Father"]), Rule(Read, paths: ["/Mother/Father"])]));

        // Act
        var decision = policy.Explain(Registered(subject));

        // Assert
        Assert.False(decision.IsReadable);
        Assert.Same(Member(grandfather, nameof(Person.Father)), decision.DeniedProperty);
    }
}
