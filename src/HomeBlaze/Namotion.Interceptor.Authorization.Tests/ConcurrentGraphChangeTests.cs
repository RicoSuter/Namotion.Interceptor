using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Authorization.Tests.Models;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Xunit.Abstractions;
using static Namotion.Interceptor.Authorization.Tests.TestGraph;

namespace Namotion.Interceptor.Authorization.Tests;

public class ConcurrentGraphChangeTests(ITestOutputHelper output)
{
    private const int StressMutatorCycles = 20_000;
    private const int StressReaderCount = 4;

    // Widens the deny readers' window between reads, so a search that joins two states is likely; the allow readers do
    // not spin, so most of their evaluations complete while the parents move.
    private const int DenyReaderSpinIterations = 20;

    private static readonly PermissionAction[] KnownActions = [PermissionAction.Read, PermissionAction.Write];

    [Fact]
    public void WhenParentsMoveOnTwoLevelsDuringAPathSearch_ThenAPathDenyStillApplies()
    {
        // Arrange
        var pathProvider = new HookPathProvider();
        var policy = Compile(pathProvider, allowPaths: null, denyPaths: ["Mother.**"]);
        var graph = MoveGraph.Create();
        var member = Member(graph.S, nameof(Person.Children));
        // Guard: the deny already applies before the moves, so the later denial is not a side effect of the move.
        Assert.False(policy.CanRead(member));
        pathProvider.SetHook(graph.Step1To3);

        // Act
        var allowed = policy.CanRead(member);
        var allowedAfterTheMoves = policy.CanRead(member);

        // Assert
        Assert.False(allowed);
        Assert.False(allowedAfterTheMoves);
    }

    [Fact]
    public void WhenParentsMoveOnTwoLevelsDuringAPathSearch_ThenAPathAllowDoesNotMatchAPathThatNeverExisted()
    {
        // Arrange: S is under Priv.Father and under X.Father, and X is under Priv.Mother. The hook removes S from X.Father,
        // then attaches X to Pub.Mother (R.Father), then detaches X from Priv.Mother, so S is never under X while X is under
        // Pub. A search that joined S's parents read before the hook (X.Father) with X's parents read after it (Pub.Mother)
        // would list the path Father.Mother.Father, which the allow matches although that path never existed.
        var pathProvider = new HookPathProvider();
        var policy = Compile(pathProvider, allowPaths: ["Father.**"], denyPaths: null);
        var root = new Person(CreateContext());
        var priv = new Person();
        var pub = new Person();
        var x = new Person();
        var s = new Person();
        root.Mother = priv;
        root.Father = pub;
        priv.Mother = x;
        priv.Father = s;
        x.Father = s;
        var member = Member(s, nameof(Person.Children));
        // The first entry is the answer in the initial state; the hook adds the answer after each move.
        var states = new List<bool> { policy.CanRead(member) };
        pathProvider.SetHook(() =>
        {
            x.Father = null;
            states.Add(policy.CanRead(member));
            pub.Mother = x;
            states.Add(policy.CanRead(member));
            priv.Mother = null;
            states.Add(policy.CanRead(member));
        });

        // Act
        var allowed = policy.CanRead(member);

        // Assert
        Assert.Equal([false, false, false, false], states);
        Assert.False(allowed);
    }

    [Fact]
    public void WhenAPathProviderEvaluatesThePolicyDuringAPathSearch_ThenTheOuterEvaluationStillSeesTheGraphChange()
    {
        // Arrange: the nested evaluation reads the graph after the moves; the outer one must still notice that its own
        // reads went stale.
        var pathProvider = new HookPathProvider();
        var policy = Compile(pathProvider, allowPaths: null, denyPaths: ["Mother.**"]);
        var graph = MoveGraph.Create();
        var member = Member(graph.S, nameof(Person.Children));
        bool? nestedAllowed = null;
        pathProvider.SetHook(() =>
        {
            graph.Step1To3();
            nestedAllowed = policy.CanRead(member);
        });

        // Act
        var allowed = policy.CanRead(member);

        // Assert
        Assert.False(nestedAllowed);
        Assert.False(allowed);
    }

    [Fact]
    public void WhenTheGraphKeepsChangingDuringEveryAttempt_ThenThePathsAreIncompleteAndListNoPath()
    {
        // Arrange: S stays under R.Mother while R.Father gains and loses S at every segment lookup, so every state has
        // the path Mother, which the allow matches and the deny does not.
        var pathProvider = new HookPathProvider();
        var allowPolicy = Compile(pathProvider, allowPaths: ["**"], denyPaths: null);
        var denyPolicy = Compile(pathProvider, allowPaths: null, denyPaths: ["Nothing.**"]);
        var root = new Person(CreateContext());
        var s = new Person();
        root.Mother = s;
        var member = Member(s, nameof(Person.Children));
        // Guard: both policies answer for the stable graph, so the denials below come from the changing graph.
        Assert.True(allowPolicy.CanRead(member));
        Assert.True(denyPolicy.CanRead(member));
        pathProvider.SetRepeatingHook(() => root.Father = root.Father is null ? s : null);

        // Act
        var allowed = allowPolicy.CanRead(member);
        var notDenied = denyPolicy.CanRead(member);

        // Assert
        Assert.False(allowed);
        Assert.False(notDenied);
    }

    [Fact]
    public void WhenTheGraphChangesDuringTheFirstAttempt_ThenAPathAllowAnswersForTheChangedGraph()
    {
        // Arrange: S moves from R.Father to R.Mother, keeping its registered instance, after the first attempt read S.
        var pathProvider = new HookPathProvider();
        var policy = Compile(pathProvider, allowPaths: ["Mother.**"], denyPaths: null);
        var root = new Person(CreateContext());
        var s = new Person();
        root.Father = s;
        var member = Member(s, nameof(Person.Children));
        // Guard: the stable graph gives the answer opposite to the one expected after the move.
        Assert.False(policy.CanRead(member));
        pathProvider.SetHook(() =>
        {
            root.Mother = s;
            root.Father = null;
        });

        // Act
        var allowed = policy.CanRead(member);

        // Assert
        Assert.True(allowed);
    }

    [Fact]
    public void WhenTheGraphChangesDuringTheFirstAttempt_ThenAPathDenyAnswersForTheChangedGraph()
    {
        // Arrange: S moves from R.Father, which the deny names, to R.Mother after the first attempt read S.
        var pathProvider = new HookPathProvider();
        var policy = Compile(pathProvider, allowPaths: null, denyPaths: ["Father.**"]);
        var root = new Person(CreateContext());
        var s = new Person();
        root.Father = s;
        var member = Member(s, nameof(Person.Children));
        // Guard: the stable graph gives the answer opposite to the one expected after the move.
        Assert.False(policy.CanRead(member));
        pathProvider.SetHook(() =>
        {
            root.Mother = s;
            root.Father = null;
        });

        // Act
        var allowed = policy.CanRead(member);

        // Assert
        Assert.True(allowed);
    }

    [Fact]
    public void WhenParentsMoveConcurrentlyWithoutDetaching_ThenAPathDenyNeverLapsesAndEvaluationsComplete()
    {
        // Arrange: every state of the move cycle has a path of S below R.Mother, so a completed evaluation is denied by
        // the deny policy and allowed by the allow policy; only an evaluation that failed closed is denied by both.
        var denyPolicy = Compile(new HookPathProvider(DenyReaderSpinIterations), allowPaths: null, denyPaths: ["Mother.**"]);
        var allowPolicy = Compile(new HookPathProvider(), allowPaths: ["Mother.**"], denyPaths: null);
        var graph = MoveGraph.Create();
        var member = Member(graph.S, nameof(Person.Children));
        using var start = new ManualResetEventSlim();
        using var mutatorDone = new ManualResetEventSlim();
        var readers = Enumerable.Range(0, StressReaderCount)
            .Select(i => new StressReader(i % 2 == 0 ? () => denyPolicy.CanRead(member) : () => allowPolicy.CanRead(member), start, mutatorDone))
            .ToArray();
        var denyReaders = readers.Where((_, i) => i % 2 == 0).ToArray();
        var allowReaders = readers.Where((_, i) => i % 2 != 0).ToArray();

        // Act
        RunMutator(start, mutatorDone, readers, () =>
        {
            graph.Step1To3();
            graph.Step4To6();
        });

        // Assert
        output.WriteLine($"evaluations={readers.Sum(r => r.Evaluations)} completedAllows={allowReaders.Sum(r => r.Allowed)} wrongAllows={denyReaders.Sum(r => r.Allowed)}");
        Assert.All(readers, reader => Assert.Null(reader.Exception));
        Assert.Equal(0, denyReaders.Sum(reader => reader.Allowed));
        Assert.All(allowReaders, reader => Assert.True(reader.SettledAllowed, "The allow policy did not allow the settled graph."));
    }

    [Fact]
    public void WhenASubjectMovesBetweenTwoUnreadableStatesDuringItsEvaluation_ThenItIsNotReadable()
    {
        // Arrange: the move to state A runs when the member Father of S is matched against S's paths, which were
        // enumerated in state B, so the member checks see B (a readable member) and the walk sees A (a readable link).
        var pathProvider = new HookPathProvider();
        var policy = CompileVisibility(pathProvider);
        var graph = VisibilityGraph.Create();
        var subject = Registered(graph.S);
        // Guard: S is hidden in both states and again after returning to the first, so any later readable answer is spurious.
        Assert.False(policy.IsVisible(subject));
        graph.ToA();
        Assert.False(policy.IsVisible(subject));
        graph.ToB();
        Assert.False(policy.IsVisible(subject));
        pathProvider.SetHook(Member(graph.S, nameof(Person.Father)), graph.ToA);

        // Act
        var readable = policy.IsVisible(subject);
        var readableAfterTheMoves = policy.IsVisible(subject);

        // Assert
        Assert.False(readable);
        Assert.False(readableAfterTheMoves);
    }

    [Fact]
    public void WhenSingleParentsMoveOnTwoLevelsDuringTheWalk_ThenTheSubjectIsNotReadable()
    {
        // Arrange: without any path rule. Mother is unreadable. S moves from X.Father to X.Mother, then X from R.Mother
        // to R.Father, so S is hidden in every state; the moves run while the walk checks the link X.Father, after it read
        // S's parents and before it reads X's, so a walk that joins the two states finds R.Father.X.Father.
        var tagProvider = new HookTagProvider { IsHidden = member => member.Name == nameof(Person.Mother) };
        var policy = CompileWithHiddenMembers(tagProvider);
        var root = new Person(CreateContext());
        var x = new Person();
        var s = new Person();
        root.Mother = x;
        x.Father = s;
        var subject = Registered(s);
        // The first entry is the answer in the initial state; the hook adds the answer after each move.
        var states = new List<bool> { policy.IsVisible(subject) };
        tagProvider.SetHook(Member(x, nameof(Person.Father)), () =>
        {
            x.Mother = s;
            states.Add(policy.IsVisible(subject));
            x.Father = null;
            states.Add(policy.IsVisible(subject));
            root.Father = x;
            states.Add(policy.IsVisible(subject));
            root.Mother = null;
            states.Add(policy.IsVisible(subject));
        });

        // Act
        var readable = policy.IsVisible(subject);

        // Assert
        Assert.Equal([false, false, false, false, false], states);
        Assert.False(readable);
    }

    [Fact]
    public void WhenAParentMovesWhileItsLinkIsChecked_ThenTheSubjectIsNotReadable()
    {
        // Arrange: S is under P.Mother; P moves from R.Mother, which is denied, to R.Father, below which the link
        // Father.Mother is denied, so S is hidden in every state. The members of S are not hidden, so their checks read
        // no parents; the move runs after the link P.Mother was matched against P's paths in the first state and before
        // the walk reads P's parents, so a link check that does not share the walk's reads joins the two states.
        var pathProvider = new HookPathProvider();
        var root = new Person(CreateContext());
        var p = new Person();
        var s = new Person();
        root.Mother = p;
        p.Mother = s;
        var tagProvider = new HookTagProvider { IsHidden = member => member.Parent.Subject != s };
        var policy = CompileWithHiddenMembers(tagProvider, pathProvider, denyPaths: ["Father.Mother", "Mother"]);
        var subject = Registered(s);
        // The first entry is the answer in the initial state; the hook adds the answer after each move.
        var states = new List<bool> { policy.IsVisible(subject) };
        pathProvider.SetHook(Member(p, nameof(Person.Mother)), () =>
        {
            root.Father = p;
            states.Add(policy.IsVisible(subject));
            root.Mother = null;
            states.Add(policy.IsVisible(subject));
        });

        // Act
        var readable = policy.IsVisible(subject);

        // Assert
        Assert.Equal([false, false, false], states);
        Assert.False(readable);
    }

    [Fact]
    public void WhenTheGraphChangesDuringTheFirstVisibilityAttempt_ThenTheAnswerIsForTheChangedGraph()
    {
        // Arrange: S moves from R.Mother to R.Father, which is unreadable, while the first attempt checks the link R.Mother.
        var tagProvider = new HookTagProvider { IsHidden = member => member.Name == nameof(Person.Father) };
        var policy = CompileWithHiddenMembers(tagProvider);
        var root = new Person(CreateContext());
        var s = new Person();
        root.Mother = s;
        var subject = Registered(s);
        // Guard: S is readable in the stable graph, so the expected answer below comes from the move.
        Assert.True(policy.IsVisible(subject));
        tagProvider.SetHook(Member(root, nameof(Person.Mother)), () =>
        {
            root.Father = s;
            root.Mother = null;
        });

        // Act
        var decision = policy.ExplainVisibility(subject);

        // Assert
        Assert.Equal(new VisibilityDecision(isVisible: false, hasReadableMember: true, Member(root, nameof(Person.Father))), decision);
    }

    [Fact]
    public void WhenTheGraphKeepsChangingDuringEveryVisibilityAttempt_ThenTheSubjectIsHiddenWithoutAReason()
    {
        // Arrange: S stays under R.Mother, which is readable, while R.Father gains and loses S at every tag lookup.
        var tagProvider = new HookTagProvider { IsHidden = member => member.Name == nameof(Person.Father) };
        var policy = CompileWithHiddenMembers(tagProvider);
        var root = new Person(CreateContext());
        var s = new Person();
        root.Mother = s;
        var subject = Registered(s);
        // Guard: S is readable in the stable graph, so the expected answer below comes from the move.
        Assert.True(policy.IsVisible(subject));
        tagProvider.SetRepeatingHook(() => root.Father = root.Father is null ? s : null);

        // Act
        var decision = policy.ExplainVisibility(subject);

        // Assert
        Assert.Equal(new VisibilityDecision(isVisible: false, hasReadableMember: false, deniedProperty: null), decision);
    }

    [Fact]
    public void WhenASubjectMovesConcurrentlyBetweenTwoUnreadableStates_ThenItIsNeverReadableAndEvaluationsComplete()
    {
        // Arrange: every state of the move cycle hides S from the visibility policy and shows it to the allow policy;
        // only an evaluation that failed closed hides it from both.
        var visibilityPolicy = CompileVisibility(new HookPathProvider(DenyReaderSpinIterations));
        var allowPolicy = Compile(new HookPathProvider(), allowPaths: ["**"], denyPaths: null);
        var graph = VisibilityGraph.Create();
        var subject = Registered(graph.S);
        // Guard: the allow policy shows S in the initial state, so a hidden answer from it means a failed-closed evaluation.
        Assert.True(allowPolicy.IsVisible(subject));
        using var start = new ManualResetEventSlim();
        using var mutatorDone = new ManualResetEventSlim();
        var readers = Enumerable.Range(0, StressReaderCount)
            .Select(i => i % 2 == 0
                ? new StressReader(() => visibilityPolicy.IsVisible(subject), start, mutatorDone, () => visibilityPolicy.ExplainVisibility(subject).HasReadableMember)
                : new StressReader(() => allowPolicy.IsVisible(subject), start, mutatorDone))
            .ToArray();
        var visibilityReaders = readers.Where((_, i) => i % 2 == 0).ToArray();
        var allowReaders = readers.Where((_, i) => i % 2 != 0).ToArray();

        // Act
        RunMutator(start, mutatorDone, readers, () =>
        {
            graph.ToA();
            graph.ToB();
        });

        // Assert
        output.WriteLine($"evaluations={readers.Sum(r => r.Evaluations)} completedAllows={allowReaders.Sum(r => r.Allowed)} wrongAllows={visibilityReaders.Sum(r => r.Allowed)} completedVisibility={visibilityReaders.Sum(r => r.Completed)}");
        Assert.All(readers, reader => Assert.Null(reader.Exception));
        Assert.Equal(0, visibilityReaders.Sum(reader => reader.Allowed));
        Assert.All(allowReaders, reader => Assert.True(reader.SettledAllowed, "The allow policy did not allow the settled graph."));
        Assert.All(visibilityReaders, reader => Assert.True(reader.SettledComplete, "The visibility evaluation did not complete on the settled graph."));
    }

    private static AuthorizationPolicy Compile(PathProviderBase pathProvider, string[]? allowPaths, string[]? denyPaths)
        => AuthorizationDefinition.Create(
            [Policies.Role(
                "Role",
                allow: [Policies.Rule([PermissionAction.Read], paths: allowPaths)],
                deny: denyPaths is null ? null : [Policies.Rule([PermissionAction.Read], paths: denyPaths)])], [],
            KnownActions, pathProvider, TestTags.Default).Compile(["Role"]);

    // Members of S are denied below Priv.Father (state A) and the link Pub.Mother is denied (state B), so S is hidden in
    // both states for a different reason.
    private static AuthorizationPolicy CompileVisibility(PathProviderBase pathProvider)
        => Compile(pathProvider, allowPaths: null, denyPaths: ["Mother.Father.*", "Father.Mother"]);

    private static AuthorizationPolicy CompileWithHiddenMembers(
        HookTagProvider tagProvider, PathProviderBase? pathProvider = null, string[]? denyPaths = null)
        => AuthorizationDefinition.Create(
            [Policies.Role(
                "Role",
                allow: [Policies.Rule([PermissionAction.Read])],
                deny: [Policies.Rule([PermissionAction.Read], paths: denyPaths, tags: [HookTagProvider.HiddenTag])])], [],
            KnownActions, pathProvider ?? DefaultPathProvider.Instance, tagProvider).Compile(["Role"]);

    // Runs the mutator's cycles, then always signals the readers, so a failing mutator cannot leave them running.
    private static void RunMutator(ManualResetEventSlim start, ManualResetEventSlim mutatorDone, StressReader[] readers, Action cycle)
    {
        start.Set();
        try
        {
            for (var i = 0; i < StressMutatorCycles; i++)
            {
                cycle();
            }
        }
        finally
        {
            mutatorDone.Set();
            foreach (var reader in readers)
            {
                reader.Join();
            }
        }
    }

    /// <summary>
    /// Asks a question of the policy on its own thread from the start signal until the mutator is done, at least once,
    /// and keeps its counts and any exception for the test thread to read after <see cref="Join"/>.
    /// </summary>
    private sealed class StressReader
    {
        private readonly Func<bool> _isAllowed;
        private readonly Func<bool>? _isComplete;
        private readonly ManualResetEventSlim _start;
        private readonly ManualResetEventSlim _mutatorDone;
        private readonly Thread _thread;

        public StressReader(Func<bool> isAllowed, ManualResetEventSlim start, ManualResetEventSlim mutatorDone, Func<bool>? isComplete = null)
        {
            _isAllowed = isAllowed;
            _isComplete = isComplete;
            _start = start;
            _mutatorDone = mutatorDone;
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
        }

        public int Evaluations { get; private set; }

        public int Allowed { get; private set; }

        /// <summary>Gets how often the completion check held during the run; only evaluations that did not fail closed pass it.</summary>
        public int Completed { get; private set; }

        /// <summary>Gets whether the evaluation on the settled graph, after the mutator was done, allowed.</summary>
        public bool SettledAllowed { get; private set; }

        /// <summary>Gets whether the completion check held on the settled graph, after the mutator was done.</summary>
        public bool SettledComplete { get; private set; }

        public Exception? Exception { get; private set; }

        public void Join() => _thread.Join();

        private void Run()
        {
            try
            {
                _start.Wait();
                do
                {
                    Evaluations++;
                    if (_isAllowed())
                    {
                        Allowed++;
                    }

                    if (_isComplete?.Invoke() == true)
                    {
                        Completed++;
                    }
                }
                while (!_mutatorDone.IsSet);

                // The graph no longer changes, so this evaluation always completes.
                SettledAllowed = _isAllowed();
                SettledComplete = _isComplete?.Invoke() == true;
            }
            catch (Exception exception)
            {
                Exception = exception;
            }
        }
    }

    /// <summary>A path provider that runs a callback when a segment is looked up, to change the graph in the middle of a path search.</summary>
    private sealed class HookPathProvider(int spinIterations = 0) : DefaultPathProvider('.')
    {
        private Action? _hook;
        private RegisteredSubjectProperty? _hookProperty;
        private bool _isRepeating;

        public void SetHook(Action hook) => _hook = hook;

        /// <summary>Runs the callback once, when the segment of the given property is looked up.</summary>
        public void SetHook(RegisteredSubjectProperty property, Action hook)
        {
            _hookProperty = property;
            _hook = hook;
        }

        public void SetRepeatingHook(Action hook)
        {
            _isRepeating = true;
            _hook = hook;
        }

        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            if (_isRepeating)
            {
                _hook?.Invoke();
            }
            else if (_hookProperty is null || ReferenceEquals(property, _hookProperty))
            {
                Interlocked.Exchange(ref _hook, null)?.Invoke();
            }

            if (spinIterations > 0)
            {
                Thread.SpinWait(spinIterations);
            }

            return base.TryGetPropertySegment(property, out segment);
        }
    }

    /// <summary>
    /// R.Mother is Priv and R.Father is Pub; X is under Priv.Mother and Pub.Mother, S under Pub.Father and X.Father.
    /// Each step adds the new parent before it removes the old one, so every registered instance stays attached, and
    /// S has a path below R.Mother in every state.
    /// </summary>
    private sealed class MoveGraph
    {
        public required Person R { get; init; }

        public required Person Priv { get; init; }

        public required Person Pub { get; init; }

        public required Person X { get; init; }

        public required Person S { get; init; }

        public static MoveGraph Create()
        {
            var root = new Person(CreateContext());
            var priv = new Person();
            var pub = new Person();
            var x = new Person();
            var s = new Person();
            root.Mother = priv;
            root.Father = pub;
            priv.Mother = x;
            pub.Mother = x;
            pub.Father = s;
            x.Father = s;
            return new MoveGraph { R = root, Priv = priv, Pub = pub, X = x, S = s };
        }

        public void Step1To3()
        {
            Priv.Father = S;
            X.Father = null;
            Priv.Mother = null;
        }

        public void Step4To6()
        {
            Priv.Mother = X;
            X.Father = S;
            Priv.Father = null;
        }
    }

    /// <summary>
    /// Tags the members that <see cref="IsHidden"/> selects hidden and runs a callback when the tags of a given member are
    /// looked up, to change the graph in the middle of an evaluation.
    /// </summary>
    private sealed class HookTagProvider : IMemberTagProvider
    {
        public const string HiddenTag = "hidden";

        private static readonly ImmutableArray<string> HiddenTags = [HiddenTag];

        private RegisteredSubjectProperty? _hookMember;
        private Action? _hook;
        private bool _isRepeating;

        public Func<RegisteredSubjectProperty, bool> IsHidden { get; set; } = _ => false;

        public void SetHook(RegisteredSubjectProperty member, Action hook)
        {
            _hookMember = member;
            _hook = hook;
        }

        public void SetRepeatingHook(Action hook)
        {
            _isRepeating = true;
            _hook = hook;
        }

        public ImmutableArray<string> GetTags(RegisteredSubjectProperty member)
        {
            if (_isRepeating)
            {
                _hook?.Invoke();
            }
            else if (ReferenceEquals(member, _hookMember))
            {
                Interlocked.Exchange(ref _hook, null)?.Invoke();
            }

            return IsHidden(member) ? HiddenTags : [];
        }
    }

    /// <summary>
    /// R.Mother is Priv and R.Father is Pub; S moves between Pub.Mother (state B) and Priv.Father (state A), each move
    /// adding the new parent before removing the old one, so S keeps its registered instance.
    /// </summary>
    private sealed class VisibilityGraph
    {
        public required Person R { get; init; }

        public required Person Priv { get; init; }

        public required Person Pub { get; init; }

        public required Person S { get; init; }

        public static VisibilityGraph Create()
        {
            var root = new Person(CreateContext());
            var priv = new Person();
            var pub = new Person();
            var s = new Person();
            root.Mother = priv;
            root.Father = pub;
            pub.Mother = s;
            return new VisibilityGraph { R = root, Priv = priv, Pub = pub, S = s };
        }

        public void ToA()
        {
            Priv.Father = S;
            Pub.Mother = null;
        }

        public void ToB()
        {
            Pub.Mother = S;
            Priv.Father = null;
        }
    }
}
