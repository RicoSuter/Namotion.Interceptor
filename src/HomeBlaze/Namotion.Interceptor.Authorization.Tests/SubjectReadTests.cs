using Namotion.Interceptor.Authorization.Tests.Models;
using Namotion.Interceptor.Registry.Abstractions;
using static Namotion.Interceptor.Authorization.Tests.Policies;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class SubjectReadTests
{
    private static readonly PermissionAction[] Read = [PermissionAction.Read];

    public static TheoryData<string> BranchingGraphs =>
        ["SecondParent", "NeighborCycle", "Diamond", "DiamondAboveDeepChain", "DeepChainAboveDiamond", "Cycle", "BudgetSpentAboveTheOwner", "BudgetSpentAboveTheSubject"];

    [Fact]
    public void WhenNoMemberIsReadable_ThenSubjectIsNotReadable()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, tags: ["subjects"])]));

        // Act
        var decision = policy.ExplainVisibility(Registered(home.Wifi!));

        // Assert
        Assert.False(decision.IsVisible);
        Assert.False(decision.HasReadableMember);
        Assert.Null(decision.DeniedProperty);
    }

    [Fact]
    public void WhenMembersAndTheWayToTheRootAreReadable_ThenSubjectIsReadable()
    {
        // Arrange
        var home = Create();
        var policy = Compile(Role("Reader", allow: [Rule(Read, tags: ["state"]), Rule(Read, tags: ["subjects"])]));

        // Act
        var canReadKitchenCeilingSubject = policy.IsVisible(Registered(home.Rooms["Kitchen"].Lights["Ceiling"]));
        var canReadSubject = policy.IsVisible(Registered(home));

        // Assert
        Assert.True(canReadKitchenCeilingSubject);
        Assert.True(canReadSubject);
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
        var decision = policy.ExplainVisibility(Registered(home.Rooms["Kitchen"].Lights["Ceiling"]));

        // Assert
        Assert.False(decision.IsVisible);
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

        // Act
        var canReadCeiling = policy.IsVisible(Registered(ceiling));
        var decision = policy.ExplainVisibility(Registered(ceiling));
        var canReadCounter = policy.IsVisible(Registered(home.Rooms["Kitchen"].Lights["Counter"]));

        // Assert
        Assert.True(canReadCeiling);
        Assert.Equal(new VisibilityDecision(isVisible: true, hasReadableMember: true, deniedProperty: null), decision);
        Assert.False(canReadCounter);
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
        var decision = policy.ExplainVisibility(Registered(home.Rooms["Kitchen"]));

        // Assert
        Assert.False(decision.IsVisible);
        Assert.Same(Member(home, nameof(Home.Rooms)), decision.DeniedProperty);
    }

    [Fact]
    public void WhenParentsFormARootlessCycleLongerThanTheTrackingDepth_ThenTheWalkTerminatesAndTheSubjectIsNotReadable()
    {
        // Arrange
        const int ringSize = 40;
        var context = CreateContext();
        var ring = Enumerable.Range(0, ringSize).Select(index => index == 0 ? new Person(context) : new Person()).ToArray();
        for (var index = 0; index < ringSize; index++)
        {
            ring[index].Father = ring[(index + 1) % ringSize];
        }

        var policy = Compile(Role("Reader", allow: [Rule(Read)]));

        // Act
        var decision = policy.ExplainVisibility(Registered(ring[20]));

        // Assert
        Assert.Equal(new VisibilityDecision(isVisible: false, hasReadableMember: true, deniedProperty: null), decision);
    }

    [Fact]
    public void WhenTwoBranchesJoinAtOneAncestorAndOnlyOneLinkIsDenied_ThenTheSubjectIsReadableThroughTheOtherBranch()
    {
        // Arrange: R.Father is A and R.Mother is B; X is under A.Father and B.Father, S under X.Father; Mother is denied.
        var context = CreateContext();
        var root = new Person(context);
        var branchA = new Person();
        var branchB = new Person();
        var joined = new Person();
        var subject = new Person();
        root.Father = branchA;
        root.Mother = branchB;
        branchA.Father = joined;
        branchB.Father = joined;
        joined.Father = subject;
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, members: ["Mother"])]));

        // Act
        var decision = policy.ExplainVisibility(Registered(subject));

        // Assert
        Assert.Equal(new VisibilityDecision(isVisible: true, hasReadableMember: true, deniedProperty: null), decision);
    }

    [Fact]
    public void WhenTwoBranchesJoinAtOneAncestorAndEveryLinkOfBothIsDenied_ThenTheSubjectIsHiddenAndADeniedLinkIsNamed()
    {
        // Arrange: R.Father is A and R.Mother is B; X is under A.Mother and B.Mother, S under X.Father; Mother is denied.
        var context = CreateContext();
        var root = new Person(context);
        var branchA = new Person();
        var branchB = new Person();
        var joined = new Person();
        var subject = new Person();
        root.Father = branchA;
        root.Mother = branchB;
        branchA.Mother = joined;
        branchB.Mother = joined;
        joined.Father = subject;
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, members: ["Mother"])]));

        // Act
        var decision = policy.ExplainVisibility(Registered(subject));

        // Assert
        Assert.False(decision.IsVisible);
        Assert.True(decision.HasReadableMember);
        Assert.Equal(nameof(Person.Mother), decision.DeniedProperty?.Name);
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

        // Act
        var canReadBedroomSubject = policy.IsVisible(Registered(home.Rooms["Bedroom"]));
        var canReadBedroomLampSubject = policy.IsVisible(Registered(home.Rooms["Bedroom"].Lights["Lamp"]));
        var canReadKitchenSubject = policy.IsVisible(Registered(home.Rooms["Kitchen"]));

        // Assert
        Assert.False(canReadBedroomSubject);
        Assert.False(canReadBedroomLampSubject);
        Assert.True(canReadKitchenSubject);
    }

    [Fact]
    public void WhenADenyNamesTheHoldingProperty_ThenTheBranchIsHiddenAlthoughItsMembersAreReadable()
    {
        // Arrange
        var home = Create();
        var kitchen = home.Rooms["Kitchen"];
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Rooms[Kitchen]/Sensor"])]));

        // Act
        var decision = policy.ExplainVisibility(Registered(kitchen.Sensor!));
        var canReadKitchenTemperature = policy.CanRead(Member(kitchen.Sensor!, nameof(Sensor.Temperature)));
        var canReadKitchenSubject = policy.IsVisible(Registered(kitchen));

        // Assert
        Assert.Equal(new VisibilityDecision(isVisible: false, hasReadableMember: true, Member(kitchen, nameof(Room.Sensor))), decision);
        Assert.True(canReadKitchenTemperature);
        Assert.True(canReadKitchenSubject);
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
        var decision = hidden.ExplainVisibility(Registered(probe));
        var openCanReadProbeSubject = open.IsVisible(Registered(probe));
        var hiddenCanReadProbeIsOn = hidden.CanRead(Member(probe, nameof(Light.IsOn)));

        // Assert
        Assert.True(openCanReadProbeSubject);
        Assert.Equal(new VisibilityDecision(isVisible: false, hasReadableMember: true, Attribute(sensor, nameof(Sensor.Temperature), "Probe")), decision);
        Assert.True(hiddenCanReadProbeIsOn);
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
        var thread = new Thread(() => canRead = policy.IsVisible(bottom), maxStackSize: 256 * 1024);
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
        var decision = policy.ExplainVisibility(Registered(subject));

        // Assert
        Assert.False(decision.IsVisible);
        Assert.Same(Member(grandfather, nameof(Person.Father)), decision.DeniedProperty);
    }

    [Fact]
    public void WhenTheOwnersPathSearchRunsOutOfBudgetButTheSubjectsDoesNot_ThenALinkDenyWithPathsStillFailsClosed()
    {
        // Arrange: the owner's search re-enters the subject through every filler, so it costs about 4,500 parent links
        // while the subject's costs about 3,000; the owner's paths are incomplete although the subject's are complete
        var root = CreateBranchingGraph("BudgetSpentAboveTheOwner", out _);
        var owner = root.Father!;
        var subject = owner.Father!;
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Mother/**"])]));

        // Act
        var decision = policy.ExplainVisibility(Registered(subject));
        var canReadOwnerFather = policy.CanRead(Member(owner, nameof(Person.Father)));

        // Assert
        Assert.False(canReadOwnerFather);
        Assert.Equal(new VisibilityDecision(isVisible: false, hasReadableMember: true, Member(owner, nameof(Person.Father))), decision);
    }

    [Fact]
    public void WhenAMembersPathSearchRunsOutOfBudget_ThenADenyWithPathsFailsClosedAndAnAllowStillMatchesAListedPath()
    {
        // Arrange: the owner's search spends more than its budget of parent links, so its paths are incomplete; the path
        // Father was listed before the budget ran out.
        var root = CreateBranchingGraph("BudgetSpentAboveTheOwner", out _);
        var owner = root.Father!;
        var member = Member(owner, nameof(Person.Children));
        var deny = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Nope/**"])]));
        var allow = Compile(Role("Reader", allow: [Rule(Read, paths: ["/Father/**"])]));

        // Act
        var denyCanRead = deny.CanRead(member);
        var allowCanRead = allow.CanRead(member);

        // Assert
        Assert.False(denyCanRead);
        Assert.True(allowCanRead);
    }

    [Fact]
    public void WhenTheSubjectsPathSearchRunsOutOfBudgetButTheOwnersDoesNot_ThenTheOwnersCompletePathsDecideTheLink()
    {
        // Arrange: the subject's search costs one parent link more than its owner's, 4,097 against 4,096, so only the
        // subject's paths are incomplete
        var root = CreateBranchingGraph("BudgetSpentAboveTheSubject", out _);
        var owner = root.Father!.Father!;
        var subject = owner.Father!;
        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, members: ["Father"], paths: ["/Nope/**"])]));

        // Act
        var decision = policy.ExplainVisibility(Registered(subject));
        var canReadOwnerFather = policy.CanRead(Member(owner, nameof(Person.Father)));

        // Assert
        Assert.True(canReadOwnerFather);
        Assert.Equal(new VisibilityDecision(isVisible: true, hasReadableMember: true, deniedProperty: null), decision);
    }

    [Fact]
    public void WhenTheSubjectIsTenThousandLevelsDeepUnderAPathDeny_ThenItsVisibilityIsAnsweredWithinSixtyFourMegabytes()
    {
        // Arrange: a deny with paths that no link of the chain matches, so every link check needs the paths of its owner
        var root = new Person(CreateContext());
        var current = root;
        for (var level = 0; level < 10_000; level++)
        {
            var next = new Person();
            current.Father = next;
            current = next;
        }

        var policy = Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Mother/**"])]));
        var bottom = Registered(current);

        // Act: on a small stack, as a deep walk must not recurse; without reusing the paths along the chain, building them per link allocates gigabytes here
        var canRead = false;
        var allocated = 0L;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                canRead = policy.IsVisible(bottom);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            catch (Exception exception)
            {
                error = exception;
            }
        }, maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();

        // Assert
        Assert.Null(error);
        Assert.True(canRead);
        Assert.InRange(allocated, 0, 64L * 1024 * 1024);
    }

    [Theory]
    [MemberData(nameof(BranchingGraphs))]
    public void WhenRulesHavePaths_ThenTheVisibilityOfEverySubjectAgreesWithItsMemberAndLinkChecks(string graph)
    {
        // Arrange
        var root = CreateBranchingGraph(graph, out var filler);
        var subjects = GetSubjects(Registered(root)).Except(filler.Select(Registered)).ToList();
        var policies = new[]
        {
            Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Father/Mother/**"])])),
            Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/**/Children[0]/Father"])])),
            Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, members: ["Father"], paths: ["/Mother/**"])])),
            Compile(Role("Reader", allow: [Rule(Read, paths: ["/Father/**"]), Rule(Read, paths: ["/Mother/Father/**"])])),
            Compile(Role("Reader", allow: [Rule(Read, paths: ["/**"])], deny: [Rule(Read, paths: ["/*/Father/Father/**"])])),
            Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Mother/Children/**"]), Rule(Read, paths: ["/Children[1]/Mother"])])),
            Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, paths: ["/Father/*"])])),
            Compile(Role("Reader", allow: [Rule(Read)], deny: [Rule(Read, members: ["Mother"], paths: ["/**"])]))
        };

        // Act
        var decisions = policies.SelectMany(policy => subjects.Select(subject => policy.ExplainVisibility(subject))).ToList();

        // Assert
        var expected = policies.SelectMany(policy => subjects.Select(subject => ExplainFromMembers(policy, subject))).ToList();
        Assert.Equal(expected, decisions);
        Assert.Contains(decisions, decision => decision.IsVisible);
        Assert.Contains(decisions, decision => decision.DeniedProperty is not null);
    }

    // Graphs of Persons with several parents, diamonds and cycles, attached under one root. The filler only spends the
    // budget of the path search and is left out of the comparison, which would otherwise take minutes.
    private static Person CreateBranchingGraph(string graph, out Person[] filler)
    {
        var root = new Person(CreateContext());
        var father = new Person();
        var mother = new Person();
        filler = [];
        switch (graph)
        {
            case "SecondParent":
                root.Father = father;
                root.Mother = mother;
                father.Father = new Person { Mother = new Person() };
                mother.Children = [new Person(), father.Father];
                break;
            case "NeighborCycle":
                root.Father = father;
                root.Mother = mother;
                father.Mother = mother;
                mother.Father = father;
                mother.Children = [new Person { Father = new Person() }];
                break;
            case "Diamond":
                var shared = new Person { Children = [new Person { Father = new Person() }] };
                father.Mother = shared;
                mother.Father = shared;
                root.Father = father;
                root.Mother = mother;
                break;
            case "DiamondAboveDeepChain":
                var top = new Person { Father = CreateFatherChain(40) };
                father.Father = top;
                mother.Children = [new Person(), top];
                root.Father = father;
                root.Mother = mother;
                break;
            case "DeepChainAboveDiamond":
                var chainBottom = new Person();
                var chainTop = CreateFatherChain(40);
                FindBottom(chainTop).Father = chainBottom;
                chainBottom.Father = father;
                chainBottom.Mother = mother;
                father.Father = new Person();
                mother.Father = father.Father;
                root.Father = chainTop;
                break;
            case "Cycle":
                var first = new Person();
                var second = new Person { Father = first };
                first.Mother = second;
                father.Father = first;
                root.Father = father;
                root.Mother = second;
                break;
            case "BudgetSpentAboveTheOwner":
                // Root.Father is the owner, owner.Father the subject; each filler is a child of the subject whose Father
                // is the owner, so every filler costs the owner's search three links and the subject's two.
                root.Father = father;
                father.Father = mother;
                filler = CreateFiller(1_500, father);
                mother.Children = filler;
                break;
            case "BudgetSpentAboveTheSubject":
                // Root.Father.Father is the owner, owner.Father the subject; each filler is a child of Root.Father whose
                // Father is Root.Father, so every filler costs both searches two links, which leaves the subject's
                // search 4,097 links and the owner's 4,096 of the budget of 4,096.
                root.Father = father;
                father.Father = mother;
                mother.Father = new Person();
                filler = CreateFiller(2_047, father);
                father.Children = filler;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(graph), graph, null);
        }

        return root;
    }

    private static Person[] CreateFiller(int count, Person father)
        => Enumerable.Range(0, count).Select(_ => new Person { Father = father }).ToArray();

    private static Person CreateFatherChain(int length)
    {
        var top = new Person();
        var current = top;
        for (var level = 1; level < length; level++)
        {
            current.Father = new Person();
            current = current.Father;
        }

        return top;
    }

    private static Person FindBottom(Person top)
    {
        var current = top;
        while (current.Father is not null)
        {
            current = current.Father;
        }

        return current;
    }

    private static List<RegisteredSubject> GetSubjects(RegisteredSubject root)
    {
        var subjects = new List<RegisteredSubject> { root };
        for (var i = 0; i < subjects.Count; i++)
        {
            var children = subjects[i].Properties.SelectMany(property => property.Children).Select(child => Registered(child.Subject));
            subjects.AddRange(children.Distinct().Except(subjects).ToList());
        }

        return subjects;
    }

    // The visibility from its definition, through member checks that each find the paths on their own: a readable
    // member and a chain of readable links to the root, the first unreadable link in depth-first order otherwise.
    private static VisibilityDecision ExplainFromMembers(AuthorizationPolicy policy, RegisteredSubject subject)
    {
        if (!subject.Properties.Any(policy.CanRead))
        {
            return new VisibilityDecision(isVisible: false, hasReadableMember: false, deniedProperty: null);
        }

        RegisteredSubjectProperty? deniedProperty = null;
        var isReachable = IsReachableFromMembers(policy, subject, [], ref deniedProperty);
        return new VisibilityDecision(isReachable, hasReadableMember: true, isReachable ? null : deniedProperty);
    }

    private static bool IsReachableFromMembers(
        AuthorizationPolicy policy, RegisteredSubject subject, HashSet<RegisteredSubject> explored, ref RegisteredSubjectProperty? deniedProperty)
    {
        if (subject.Parents.Length == 0)
        {
            return true;
        }

        if (!explored.Add(subject))
        {
            return false;
        }

        foreach (var parent in subject.Parents)
        {
            if (!policy.CanRead(parent.Property))
            {
                deniedProperty ??= parent.Property;
            }
            else if (IsReachableFromMembers(policy, parent.Property.Parent, explored, ref deniedProperty))
            {
                return true;
            }
        }

        return false;
    }
}
