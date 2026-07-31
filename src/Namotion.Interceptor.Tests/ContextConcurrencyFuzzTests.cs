using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests;

/// <summary>
/// Randomized, model based concurrency fuzzing for <see cref="InterceptorSubjectContext"/>.
///
/// Each round builds a random fallback graph (including cycles, self references and multi parent
/// shapes), hammers it from several threads with topology mutations, service registrations,
/// service queries and intercepted property and method access, and then checks quiescent
/// consistency: once every worker joined, what each context resolves must equal what a
/// single threaded walk of the final graph says it should resolve. A mismatch means a cache
/// survived a topology change, which is the defect class that silently drops an interceptor from
/// a compiled chain.
///
/// The subject executors take part in the graph as ordinary nodes, so the caches of
/// <see cref="InterceptorExecutor"/> are fuzzed the same way as the caches of a plain context.
///
/// One shape is deliberately excluded: a delegation cycle in which every context is empty
/// recurses forever by design (see issue #401), so a context without services never gets a
/// fallback edge to another context without services.
/// </summary>
public class ContextConcurrencyFuzzTests
{
    // Bounded for CI: many short rounds rather than few long ones, because each round is a fresh
    // random topology and topology diversity finds more than depth within one graph.
    //
    // The deep sweep this was validated with is Rounds = 1000, WorkerCount = 16 and
    // OperationsPerWorker = 600, which is 8000 topologies and roughly 77 million operations in
    // about nine minutes. Raise the three constants to re-run it; nothing else has to change.
    private const int Rounds = 30;
    private const int WorkerCount = 8;
    private const int OperationsPerWorker = 200;

    private const int MaxContextCount = 12;
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The oracle that catches a permanently stale cache: after quiescence every context has to
    /// resolve exactly the services the final fallback graph contains, and a write, a read and a
    /// method call on a subject bound to it have to reach exactly the interceptors of that same
    /// graph. Service caches and compiled chain caches are separate, so both are asserted.
    /// </summary>
    [Theory]
    [InlineData(13)]
    [InlineData(1729)]
    [InlineData(65537)]
    [InlineData(271828)]
    [InlineData(-999999)]
    [InlineData(20260731)]
    [InlineData(-2147483647)]
    [InlineData(7654321)]
    public async Task WhenTopologyAndServicesAreMutatedConcurrently_ThenQuiescentResolutionMatchesFinalTopology(int seed)
    {
        for (var round = 0; round < Rounds; round++)
        {
            // Arrange: the round seed fully determines the topology and every worker operation, so
            // a reported failure can be replayed by running this seed alone.
            var roundSeed = unchecked(seed * 7919 + round);
            var topology = BuildTopology(new Random(roundSeed));

            using var start = new ManualResetEventSlim(false);
            var workers = Enumerable
                .Range(0, WorkerCount)
                .Select(workerIndex => Task.Factory.StartNew(
                    () => RunWorker(topology, workerIndex, new Random(unchecked(roundSeed * 31 + workerIndex)), start),
                    TaskCreationOptions.LongRunning))
                .ToArray();

            // Act
            start.Set();
            await AsyncTestHelpers.WaitUntilAsync(
                () => workers.All(worker => worker.IsCompleted),
                WorkerTimeout,
                TimeSpan.FromMilliseconds(2),
                $"Concurrent context mutations did not finish. {topology.Describe(roundSeed)}");
            await Task.WhenAll(workers);

            // Assert
            AssertResolutionMatchesTopology(topology, roundSeed);
            AssertInterceptionMatchesTopology(topology, roundSeed);
        }
    }

    private static void AssertResolutionMatchesTopology(Topology topology, int roundSeed)
    {
        foreach (var node in topology.Nodes)
        {
            var reachableNodes = topology.ComputeReachableNodes(node);

            // Every service instance is unique and services are never removed, so the expected
            // count is simply the sum over the reachable part of the final graph.
            var expectedMarkerCount = reachableNodes.Sum(reachableNode => reachableNode.MarkerCount);
            var actualMarkers = node.Context.GetServices<MarkerService>();
            Assert.True(actualMarkers.Length == expectedMarkerCount,
                $"Context {node.Name} resolved {actualMarkers.Length} marker services but the final topology " +
                $"contains {expectedMarkerCount}. {topology.Describe(roundSeed)}");

            var expectedInterceptors = ExpectedInterceptors(reachableNodes);
            var actualInterceptors = node.Context.GetServices<IWriteInterceptor>();
            Assert.True(
                actualInterceptors.Length == expectedInterceptors.Count &&
                actualInterceptors.All(interceptor => expectedInterceptors.Contains((RecordingInterceptor)interceptor)),
                $"Context {node.Name} resolved the write interceptors {Format(actualInterceptors)} but the final " +
                $"topology contains {Format(expectedInterceptors)}. {topology.Describe(roundSeed)}");

            var expectsProbe = reachableNodes.Contains(topology.ProbeNode);
            var actualProbe = node.Context.TryGetService<SingletonProbeService>();
            Assert.True(expectsProbe == (actualProbe is not null),
                $"Context {node.Name} {(actualProbe is null ? "did not resolve" : "resolved")} the probe service " +
                $"but the final topology says it is {(expectsProbe ? "reachable" : "unreachable")}. " +
                topology.Describe(roundSeed));
        }
    }

    private static void AssertInterceptionMatchesTopology(Topology topology, int roundSeed)
    {
        var writtenValue = 0;
        foreach (var node in topology.SubjectNodes)
        {
            var subject = node.Subject!;
            var expectedInterceptors = ExpectedInterceptors(topology.ComputeReachableNodes(node));

            AssertInterceptorsAreCalledOnce(topology, node, expectedInterceptors, roundSeed, "int write",
                interceptor => interceptor.WriteCount, () => subject.Value = ++writtenValue);

            AssertInterceptorsAreCalledOnce(topology, node, expectedInterceptors, roundSeed, "string write",
                interceptor => interceptor.WriteCount, () => subject.Text = roundSeed.ToString());

            AssertInterceptorsAreCalledOnce(topology, node, expectedInterceptors, roundSeed, "read",
                interceptor => interceptor.ReadCount, () => _ = subject.Value);

            AssertInterceptorsAreCalledOnce(topology, node, expectedInterceptors, roundSeed, "method",
                interceptor => interceptor.MethodCount, () => subject.Echo(1));
        }
    }

    /// <summary>
    /// Runs one intercepted operation on the subject of the given executor and asserts that exactly
    /// the interceptors of the final topology observed it, each of them exactly once.
    /// </summary>
    private static void AssertInterceptorsAreCalledOnce(
        Topology topology,
        ContextNode node,
        HashSet<RecordingInterceptor> expectedInterceptors,
        int roundSeed,
        string operation,
        Func<RecordingInterceptor, int> counter,
        Action trigger)
    {
        var countsBefore = topology.Interceptors.ToDictionary(interceptor => interceptor, counter);

        trigger();

        foreach (var interceptor in topology.Interceptors)
        {
            var expectedCalls = expectedInterceptors.Contains(interceptor) ? 1 : 0;
            var actualCalls = counter(interceptor) - countsBefore[interceptor];
            Assert.True(actualCalls == expectedCalls,
                $"An intercepted {operation} on the subject of {node.Name} reached the interceptor of context " +
                $"{interceptor.Index} {actualCalls} times instead of {expectedCalls}, so its compiled chain " +
                $"disagrees with the final topology. {topology.Describe(roundSeed)}");
        }
    }

    private static HashSet<RecordingInterceptor> ExpectedInterceptors(HashSet<ContextNode> reachableNodes)
    {
        return reachableNodes
            .Select(reachableNode => reachableNode.Interceptor)
            .OfType<RecordingInterceptor>()
            .ToHashSet();
    }

    private static string Format(IEnumerable<object> interceptors)
    {
        var indices = interceptors.OfType<RecordingInterceptor>().Select(interceptor => interceptor.Index).Order();
        return $"[{string.Join(", ", indices)}]";
    }

    private static Topology BuildTopology(Random random)
    {
        var contextCount = random.Next(2, MaxContextCount + 1);
        var nodes = new List<ContextNode>();

        for (var index = 0; index < contextCount; index++)
        {
            // A context that keeps a service of its own never becomes a delegation target, which
            // is what keeps cycles in the generated graph legal (see the class comment).
            var hasOwnService = index == 0 || random.Next(4) != 0;
            var context = InterceptorSubjectContext.Create();

            RecordingInterceptor? interceptor = null;
            if (hasOwnService)
            {
                interceptor = new RecordingInterceptor(index);
                context.AddService(interceptor);
            }

            if (index == 0)
            {
                // Exactly one instance exists in the whole graph, so TryGetService can never see
                // two of them and its arity check doubles as an in flight duplicate detector.
                context.AddService(new SingletonProbeService());
            }

            nodes.Add(new ContextNode($"c{index}", context, interceptor, hasOwnService, null));
        }

        // The executor of a subject is a context too, so it joins the graph as a node whose
        // fallbacks are fuzzed like any other. Nothing ever points at an executor, so an executor
        // can never sit on a cycle and may therefore delegate into a context without services.
        var contextNodes = nodes.ToArray();
        var subjectCount = random.Next(1, contextCount + 1);
        for (var index = 0; index < subjectCount; index++)
        {
            var subject = new FuzzSubject();
            var executor = (InterceptorSubjectContext)((IInterceptorSubject)subject).Context;
            nodes.Add(new ContextNode($"s{index}", executor, null, false, subject));
        }

        var edges = new List<Edge>();
        var declaredEdges = new HashSet<(ContextNode Source, ContextNode Target)>();

        void DeclareEdge(ContextNode source, ContextNode target, bool isPresent)
        {
            // Never let a plain context without services delegate into another one without
            // services: that is the pure delegation cycle that recurses forever by design.
            if (source.Subject is null && !source.HasOwnService && !target.HasOwnService)
            {
                return;
            }

            if (declaredEdges.Add((source, target)))
            {
                edges.Add(new Edge(source, target, isPresent));
            }
        }

        // Force at least one back reference so that every round exercises a cycle, which is the
        // shape the registry produces for parent links.
        var servingNodes = contextNodes.Where(node => node.HasOwnService).ToArray();
        if (servingNodes.Length >= 2)
        {
            var first = servingNodes[random.Next(servingNodes.Length)];
            var second = servingNodes[random.Next(servingNodes.Length)];
            if (first != second)
            {
                DeclareEdge(first, second, true);
                DeclareEdge(second, first, true);
            }
        }

        // Every subject starts out bound to one context, like a subject constructed with a context.
        for (var index = contextNodes.Length; index < nodes.Count; index++)
        {
            DeclareEdge(nodes[index], contextNodes[random.Next(contextNodes.Length)], true);
        }

        var candidateCount = random.Next(nodes.Count, nodes.Count * 2 + 1);
        for (var candidate = 0; candidate < candidateCount; candidate++)
        {
            var source = nodes[random.Next(nodes.Count)];
            var target = contextNodes[random.Next(contextNodes.Length)];
            if (source == target && random.Next(10) != 0)
            {
                continue;
            }

            DeclareEdge(source, target, random.Next(5) != 0);
        }

        // Each edge belongs to at most one worker, so no two threads toggle the same edge and the
        // final edge set stays exactly known without weakening the concurrency.
        foreach (var edge in edges)
        {
            edge.Owner = random.Next(10) < 7 ? random.Next(WorkerCount) : -1;
            if (edge.IsPresent)
            {
                edge.Source.Context.AddFallbackContext(edge.Target.Context);
            }
        }

        return new Topology(nodes.ToArray(), edges, contextNodes[0]);
    }

    private static void RunWorker(Topology topology, int workerIndex, Random random, ManualResetEventSlim start)
    {
        var ownedEdges = topology.Edges.Where(edge => edge.Owner == workerIndex).ToArray();
        var nodes = topology.Nodes;
        var subjectNodes = topology.SubjectNodes;

        start.Wait();

        for (var operation = 0; operation < OperationsPerWorker; operation++)
        {
            var node = nodes[random.Next(nodes.Length)];
            var subject = subjectNodes[random.Next(subjectNodes.Length)].Subject!;
            var choice = random.Next(100);

            if (choice < 18 && ownedEdges.Length != 0)
            {
                var edge = ownedEdges[random.Next(ownedEdges.Length)];
                if (random.Next(2) == 0)
                {
                    edge.Source.Context.AddFallbackContext(edge.Target.Context);
                    edge.IsPresent = true;
                }
                else
                {
                    edge.Source.Context.RemoveFallbackContext(edge.Target.Context);
                    edge.IsPresent = false;
                }
            }
            else if (choice < 30)
            {
                node.Context.AddService(new MarkerService());
                Interlocked.Increment(ref node.MarkerCount);
            }
            else if (choice < 38)
            {
                // The outcome depends on the concurrent topology and is therefore not modeled; the
                // service type is one the oracles ignore, and an extra service can only remove a
                // delegation shortcut, never add one.
                node.Context.TryAddService(() => new TransientProbeService(), _ => true);
            }
            else if (choice < 55)
            {
                _ = node.Context.GetServices<MarkerService>();
            }
            else if (choice < 68)
            {
                _ = node.Context.GetServices<IWriteInterceptor>();
            }
            else if (choice < 74)
            {
                _ = node.Context.TryGetService<SingletonProbeService>();
            }
            else if (choice < 84)
            {
                subject.Value = operation;
            }
            else if (choice < 90)
            {
                subject.Text = null;
            }
            else if (choice < 96)
            {
                _ = subject.Value;
            }
            else
            {
                _ = subject.Echo(operation);
            }
        }
    }

    private sealed class Topology(ContextNode[] nodes, List<Edge> edges, ContextNode probeNode)
    {
        internal ContextNode[] Nodes { get; } = nodes;

        internal ContextNode[] SubjectNodes { get; } = nodes.Where(node => node.Subject is not null).ToArray();

        internal List<Edge> Edges { get; } = edges;

        /// <summary>The one context holding the singleton probe service.</summary>
        internal ContextNode ProbeNode { get; } = probeNode;

        internal RecordingInterceptor[] Interceptors { get; } = nodes
            .Select(node => node.Interceptor)
            .OfType<RecordingInterceptor>()
            .ToArray();

        /// <summary>
        /// The single threaded model of the resolution semantics: the services a context resolves
        /// are the own services of every context reachable over the present fallback edges. A
        /// delegating context contributes nothing of its own, so modeling delegation separately
        /// would produce the same set and is left out.
        /// </summary>
        internal HashSet<ContextNode> ComputeReachableNodes(ContextNode node)
        {
            var reachableNodes = new HashSet<ContextNode>();
            var pending = new Stack<ContextNode>();
            pending.Push(node);

            while (pending.Count != 0)
            {
                var current = pending.Pop();
                if (!reachableNodes.Add(current))
                {
                    continue;
                }

                foreach (var edge in Edges)
                {
                    if (edge.IsPresent && edge.Source == current)
                    {
                        pending.Push(edge.Target);
                    }
                }
            }

            return reachableNodes;
        }

        internal string Describe(int roundSeed)
        {
            var shape = string.Join("; ", Nodes.Select(node =>
                $"{node.Name}{(node.HasOwnService ? "" : "*")}+{node.MarkerCount}->" +
                $"[{string.Join(",", Edges.Where(edge => edge.IsPresent && edge.Source == node).Select(edge => edge.Target.Name))}]"));

            return $"Seed {roundSeed}, final topology ('c' is a context, 's' a subject executor, " +
                   $"'*' a node seeded without services, '+n' its added marker services): {shape}";
        }
    }

    private sealed class ContextNode(
        string name,
        InterceptorSubjectContext context,
        RecordingInterceptor? interceptor,
        bool hasOwnService,
        FuzzSubject? subject)
    {
        internal int MarkerCount;

        internal string Name { get; } = name;

        internal InterceptorSubjectContext Context { get; } = context;

        internal RecordingInterceptor? Interceptor { get; } = interceptor;

        internal bool HasOwnService { get; } = hasOwnService;

        /// <summary>Set when this node is the executor of a subject, otherwise <c>null</c>.</summary>
        internal FuzzSubject? Subject { get; } = subject;
    }

    private sealed class Edge(ContextNode source, ContextNode target, bool isPresent)
    {
        internal ContextNode Source { get; } = source;

        internal ContextNode Target { get; } = target;

        /// <summary>Written only by the owning worker and read after it joined.</summary>
        internal bool IsPresent { get; set; } = isPresent;

        /// <summary>Index of the worker that may toggle this edge, or -1 when it stays fixed.</summary>
        internal int Owner { get; set; } = -1;
    }

    private sealed class RecordingInterceptor(int index) : IReadInterceptor, IWriteInterceptor, IMethodInterceptor
    {
        private int _readCount;
        private int _writeCount;
        private int _methodCount;

        internal int Index { get; } = index;

        internal int ReadCount => Volatile.Read(ref _readCount);

        internal int WriteCount => Volatile.Read(ref _writeCount);

        internal int MethodCount => Volatile.Read(ref _methodCount);

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref _readCount);
            return next(ref context);
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref _writeCount);
            next(ref context);
        }

        public object? InvokeMethod(MethodInvocationContext context, InvokeMethodInterceptionDelegate next)
        {
            Interlocked.Increment(ref _methodCount);
            return next(ref context);
        }
    }

    private sealed class MarkerService;

    private sealed class SingletonProbeService;

    private sealed class TransientProbeService;
}

[InterceptorSubject]
public partial class FuzzSubject
{
    public partial int Value { get; set; }

    public partial string? Text { get; set; }

    protected int EchoWithoutInterceptor(int input) => input;
}
