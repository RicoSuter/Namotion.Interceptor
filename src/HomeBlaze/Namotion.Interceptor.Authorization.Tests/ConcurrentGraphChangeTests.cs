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
        Assert.False(policy.CanRead(member));
        pathProvider.SetHook(graph.Step1To3);

        // Act
        var allowed = policy.CanRead(member);

        // Assert
        Assert.False(allowed);
        Assert.False(policy.CanRead(member));
    }

    [Fact]
    public void WhenParentsMoveOnTwoLevelsDuringAPathSearch_ThenAPathAllowDoesNotMatchAPathThatNeverExisted()
    {
        // Arrange: S under Priv.Father and X.Father, X under Priv.Mother; the moves pass X to Pub.Mother, so S is under
        // Father only through Priv and through Pub.Mother.X, never through R.Father.X.Father.
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
            .Select(i => new StressReader(i % 2 == 0 ? denyPolicy : allowPolicy, member, start, mutatorDone))
            .ToArray();
        var denyReaders = readers.Where((_, i) => i % 2 == 0).ToArray();
        var allowReaders = readers.Where((_, i) => i % 2 != 0).ToArray();

        // Act
        start.Set();
        for (var cycle = 0; cycle < StressMutatorCycles; cycle++)
        {
            graph.Step1To3();
            graph.Step4To6();
        }

        mutatorDone.Set();
        foreach (var reader in readers)
        {
            reader.Join();
        }

        // Assert
        output.WriteLine($"evaluations={readers.Sum(r => r.Evaluations)} completedAllows={allowReaders.Sum(r => r.Allowed)} wrongAllows={denyReaders.Sum(r => r.Allowed)}");
        Assert.All(readers, reader => Assert.Null(reader.Exception));
        Assert.All(readers, reader => Assert.True(reader.Evaluations > 0));
        Assert.Equal(0, denyReaders.Sum(reader => reader.Allowed));
        Assert.True(allowReaders.Sum(reader => reader.Allowed) > 0, "No evaluation of the allow policy completed while the parents moved.");
    }

    private static AuthorizationPolicy Compile(PathProviderBase pathProvider, string[]? allowPaths, string[]? denyPaths)
        => AuthorizationPolicy.Compile(
            [Policies.Role(
                "Role",
                allow: [Policies.Rule([PermissionAction.Read], paths: allowPaths)],
                deny: denyPaths is null ? null : [Policies.Rule([PermissionAction.Read], paths: denyPaths)])],
            ["Role"], [], KnownActions, TestTags.Default, pathProvider);

    /// <summary>
    /// Asks the policy about the member on its own thread from the start signal until the mutator is done, at least once,
    /// and keeps its counts and any exception for the test thread to read after <see cref="Join"/>.
    /// </summary>
    private sealed class StressReader
    {
        private readonly AuthorizationPolicy _policy;
        private readonly RegisteredSubjectProperty _member;
        private readonly ManualResetEventSlim _start;
        private readonly ManualResetEventSlim _mutatorDone;
        private readonly Thread _thread;

        public StressReader(AuthorizationPolicy policy, RegisteredSubjectProperty member, ManualResetEventSlim start, ManualResetEventSlim mutatorDone)
        {
            _policy = policy;
            _member = member;
            _start = start;
            _mutatorDone = mutatorDone;
            _thread = new Thread(Run);
            _thread.Start();
        }

        public int Evaluations { get; private set; }

        public int Allowed { get; private set; }

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
                    if (_policy.CanRead(_member))
                    {
                        Allowed++;
                    }
                }
                while (!_mutatorDone.IsSet);
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
        private bool _isRepeating;

        public void SetHook(Action hook) => _hook = hook;

        public void SetRepeatingHook(Action hook)
        {
            _isRepeating = true;
            _hook = hook;
        }

        public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
        {
            var hook = _isRepeating ? _hook : Interlocked.Exchange(ref _hook, null);
            hook?.Invoke();
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
}
