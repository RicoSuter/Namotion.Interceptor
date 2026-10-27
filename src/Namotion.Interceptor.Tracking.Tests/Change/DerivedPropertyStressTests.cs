using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyStressTests
{
    private const int RoundCount = 10;
    private const int CheckpointCount = 10;
    private const int OperationsPerCheckpoint = 100;
    private const int WriterCount = 2;

    [Fact]
    public void WhenNodesMoveWhileSourcesAreWritten_ThenDerivedTrackingMatchesAFreshGraph()
    {
        for (var round = 0; round < RoundCount; round++)
        {
            // Arrange
            var seed = round * 100;
            var graph = StressGraph.Create(seed);
            var mismatches = new List<string>();

            // Act
            RunConcurrently(graph, seed + StressGraph.MoverCount, () => Verify(graph, mismatches));

            // Assert
            Assert.True(mismatches.Count == 0, $"Round {round}, seed {seed}:{Environment.NewLine}{string.Join(Environment.NewLine, mismatches.Distinct().Take(30))}");
        }
    }

    /// <summary>
    /// Compares the graph with a fresh copy, then writes each source in both and compares the published changes.
    /// Returns false when a mismatch was found.
    /// </summary>
    private static bool Verify(StressGraph graph, List<string> mismatches)
    {
        if (graph.Widener.OverlapCount > 0)
        {
            var property = graph.Widener.FirstOverlap!.Value;
            mismatches.Add($"{graph.Label(property.Subject)}.{property.Name}: {graph.Widener.OverlapCount} publications overlapped another publication of the same property");
        }

        var fresh = graph.BuildFreshCopy();
        CompareGraphs(graph, fresh, mismatches);
        CompareChangesAfterWrites(graph, fresh, mismatches);
        CompareGraphs(graph, fresh, mismatches);
        return mismatches.Count == 0;
    }

    /// <summary>
    /// Runs the movers and writers in batches. The barrier holds all of them between batches, so each
    /// checkpoint sees the graph quiescent.
    /// </summary>
    private static void RunConcurrently(StressGraph graph, int writerSeed, Func<bool> checkpoint)
    {
        var threadCount = StressGraph.MoverCount + WriterCount;
        var keepRunning = new StrongBox<bool>(true);
        using var barrier = new Barrier(threadCount, _ => keepRunning.Value = checkpoint());
        var failures = new ConcurrentQueue<Exception>();
        var threads = new Thread[threadCount];
        for (var index = 0; index < threadCount; index++)
        {
            var runBatch = index < StressGraph.MoverCount
                ? graph.Movers[index].RunBatch
                : CreateWriteBatch(graph, writerSeed + index);

            threads[index] = new Thread(() => RunBatches(barrier, runBatch, keepRunning, failures)) { IsBackground = true };
            threads[index].Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "A stress thread did not finish, possibly a deadlock.");
        }

        Assert.Empty(failures);
    }

    private static Action CreateWriteBatch(StressGraph graph, int seed)
    {
        var random = new Random(seed);
        return () => graph.WriteBatch(random);
    }

    private static void RunBatches(Barrier barrier, Action runBatch, StrongBox<bool> keepRunning, ConcurrentQueue<Exception> failures)
    {
        try
        {
            barrier.SignalAndWait();
            for (var index = 0; index < CheckpointCount && keepRunning.Value; index++)
            {
                runBatch();
                barrier.SignalAndWait();
            }
        }
        catch (Exception exception)
        {
            failures.Enqueue(exception);
            barrier.RemoveParticipant();
        }
    }

    private static void CompareGraphs(StressGraph graph, FreshGraph fresh, List<string> mismatches)
    {
        var lifecycle = graph.Context.TryGetLifecycleInterceptor()!;
        foreach (var subject in graph.Sources)
        {
            if (lifecycle.TryRunWhileAttached(subject, static () => { }) != fresh.Map.ContainsKey(subject))
            {
                mismatches.Add($"{graph.Label(subject)}: attached state differs from reachability");
            }
        }

        foreach (var (original, copy) in fresh.Map)
        {
            foreach (var name in original.Properties.Keys)
            {
                var originalProperty = new PropertyReference(original, name);
                var copyProperty = new PropertyReference(copy, name);
                var label = $"{graph.Label(original)}.{name}";

                if (originalProperty.Metadata.IsDerived)
                {
                    CompareDerivedState(label, originalProperty, copyProperty, mismatches);
                }

                CompareReferences(label + " required", originalProperty.GetRequiredProperties().ToArray(), copyProperty.GetRequiredProperties().ToArray(), graph, fresh, mismatches);
                CompareReferences(label + " used-by", originalProperty.GetUsedByProperties().Items.ToArray(), copyProperty.GetUsedByProperties().Items.ToArray(), graph, fresh, mismatches);
            }
        }
    }

    private static void CompareDerivedState(string label, PropertyReference originalProperty, PropertyReference copyProperty, List<string> mismatches)
    {
        var originalData = originalProperty.TryGetDerivedPropertyData();
        if (originalData is null || !originalData.IsAttached || !originalData.IsDerived)
        {
            mismatches.Add($"{label}: no attached derived tracking data");
            return;
        }

        var expectedValue = copyProperty.TryGetDerivedPropertyData()?.LastKnownValue;
        if (!Equals(originalData.LastKnownValue, expectedValue))
        {
            mismatches.Add($"{label}: tracked value {originalData.LastKnownValue}, expected {expectedValue}");
        }
    }

    private static void CompareReferences(
        string label,
        PropertyReference[] originalReferences,
        PropertyReference[] copyReferences,
        StressGraph graph,
        FreshGraph fresh,
        List<string> mismatches)
    {
        var actual = new List<string>();
        foreach (var reference in originalReferences)
        {
            if (!fresh.Map.ContainsKey(reference.Subject))
            {
                mismatches.Add($"{label}: points to {graph.Label(reference.Subject)}.{reference.Name}, which is not attached");
            }

            actual.Add($"{graph.Label(reference.Subject)}.{reference.Name}");
        }

        var expected = copyReferences.Select(reference => $"{fresh.Label(reference.Subject)}.{reference.Name}");
        var actualText = string.Join(", ", actual.Order(StringComparer.Ordinal));
        var expectedText = string.Join(", ", expected.Order(StringComparer.Ordinal));
        if (actualText != expectedText)
        {
            mismatches.Add($"{label}: [{actualText}], expected [{expectedText}]");
        }
    }

    private static void CompareChangesAfterWrites(StressGraph graph, FreshGraph fresh, List<string> mismatches)
    {
        var originalChanges = new List<string>();
        var copyChanges = new List<string>();
        using var originalSubscription = graph.Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => originalChanges.Add(DescribeChange(change, graph.Label)));
        using var copySubscription = fresh.Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => copyChanges.Add(DescribeChange(change, fresh.Label)));

        foreach (var (original, copy) in fresh.Map)
        {
            if (original is not StressNode originalNode)
            {
                continue;
            }

            for (var source = 0; source < StressGraph.SourceCount; source++)
            {
                StressGraph.WriteNextValue(originalNode, source);
                StressGraph.WriteNextValue((StressNode)copy, source);

                var actualText = string.Join(", ", originalChanges.Order(StringComparer.Ordinal));
                var expectedText = string.Join(", ", copyChanges.Order(StringComparer.Ordinal));
                if (actualText != expectedText)
                {
                    mismatches.Add($"Write {source} to {graph.Label(original)} published [{actualText}], expected [{expectedText}]");
                }

                originalChanges.Clear();
                copyChanges.Clear();
            }
        }
    }

    private static string DescribeChange(SubjectPropertyChange change, Func<IInterceptorSubject, string> label) =>
        $"{label(change.Property.Subject)}.{change.Property.Name}={change.GetNewValue<object?>()}";

    /// <summary>
    /// Widens race windows: on a writer thread, the publication of a derived property waits until the movers
    /// complete a few moves, unless no mover is running. Also counts publications of a derived property that
    /// overlap another publication of the same property, which its recalculation serializes, also across a move.
    /// </summary>
    [RunsBefore(typeof(PropertyChangeInterceptor))]
    private sealed class RaceWidener : IWriteInterceptor
    {
        // Long enough for a move that detaches and attaches a subject again, and a write after it.
        private const int MovesPerWait = 3;

        [ThreadStatic]
        private static bool _isWriterThread;

        private readonly ConcurrentDictionary<PropertyReference, StrongBox<int>> _inFlight = new(PropertyReference.Comparer);
        private int _activeMovers;
        private int _completedMoves;
        private int _overlapCount;
        private StrongBox<PropertyReference>? _firstOverlap;

        public int OverlapCount => Volatile.Read(ref _overlapCount);

        public PropertyReference? FirstOverlap => Volatile.Read(ref _firstOverlap)?.Value;

        public static void MarkWriterThread() => _isWriterThread = true;

        public void BeginMoves() => Interlocked.Increment(ref _activeMovers);

        public void CompleteMove() => Interlocked.Increment(ref _completedMoves);

        public void EndMoves() => Interlocked.Decrement(ref _activeMovers);

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            // A derived property without a setter is written only by the publication of its recalculation.
            var metadata = context.Property.Metadata;
            if (!metadata.IsDerived || metadata.SetValue is not null)
            {
                next(ref context);
                return;
            }

            var inFlight = _inFlight.GetOrAdd(context.Property, static _ => new StrongBox<int>());
            if (Interlocked.Increment(ref inFlight.Value) > 1)
            {
                Interlocked.Increment(ref _overlapCount);
                Interlocked.CompareExchange(ref _firstOverlap, new StrongBox<PropertyReference>(context.Property), null);
            }

            try
            {
                WaitForMoves();
                next(ref context);
            }
            finally
            {
                Interlocked.Decrement(ref inFlight.Value);
            }
        }

        private void WaitForMoves()
        {
            if (!_isWriterThread)
            {
                return;
            }

            var completedMoves = Volatile.Read(ref _completedMoves);
            var spinWait = new SpinWait();
            while (Volatile.Read(ref _completedMoves) - completedMoves < MovesPerWait && Volatile.Read(ref _activeMovers) > 0)
            {
                spinWait.SpinOnce(sleep1Threshold: -1);
            }
        }
    }

    private enum SlotKind
    {
        None,
        First,
        Second,
        Items,
        Child
    }

    private readonly record struct Slot(SlotKind Kind, int Index);

    /// <summary>
    /// Three roots holding six nodes, and three leaves held by the nodes. Each mover owns half of the nodes and
    /// leaves and its own slots, and places only its subjects in its slots, so a subject never has two parents,
    /// not even while a write of another mover is between its store and the lifecycle reconciliation. A subject
    /// that had two parents keeps the context of the first one as its fallback (#801), and attaching it under that
    /// parent again does not attach its children.
    /// </summary>
    private sealed class StressGraph
    {
        public const int SourceCount = 4;
        private const int MaxItemCount = 3;

        // Both movers write slots of the second root.
        private static readonly Slot[][] RootSlotsPerMover =
        [
            [new Slot(SlotKind.First, 0), new Slot(SlotKind.Second, 0), new Slot(SlotKind.First, 1), new Slot(SlotKind.Items, 0)],
            [new Slot(SlotKind.Second, 1), new Slot(SlotKind.First, 2), new Slot(SlotKind.Second, 2), new Slot(SlotKind.Items, 2)]
        ];

        private readonly Dictionary<IInterceptorSubject, string> _labels = new(ReferenceEqualityComparer.Instance);

        private StressGraph(IInterceptorSubjectContext context)
        {
            Context = context;
            context.WithService(() => Widener);
            Roots = Enumerable.Range(0, 3).Select(_ => new StressRoot(context)).ToArray();
            Nodes = Enumerable.Range(0, 6).Select(index => new StressNode(context) { Value = index }).ToArray();
            Leaves = Enumerable.Range(0, 3).Select(index => new StressNode(context) { Value = 10 + index }).ToArray();
            Sources = [.. Nodes, .. Leaves];

            AddLabels("root", Roots);
            AddLabels("node", Nodes);
            AddLabels("leaf", Leaves);
        }

        public static int MoverCount => RootSlotsPerMover.Length;

        public IInterceptorSubjectContext Context { get; }

        public RaceWidener Widener { get; } = new();

        public StressRoot[] Roots { get; }

        public StressNode[] Nodes { get; }

        public StressNode[] Leaves { get; }

        public StressNode[] Sources { get; }

        public Mover[] Movers { get; private set; } = [];

        public static StressGraph Create(int seed)
        {
            var graph = new StressGraph(CreateContext());

            // Each mover gives its subjects a parent, so that a subject is attached exactly when it is reachable from a root.
            graph.Movers = Enumerable
                .Range(0, MoverCount)
                .Select(index => new Mover(graph, RootSlotsPerMover[index], index, new Random(seed + index)))
                .ToArray();

            return graph;
        }

        public static IInterceptorSubjectContext CreateContext() =>
            InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

        public string Label(IInterceptorSubject subject) => _labels.GetValueOrDefault(subject, "unknown");

        public void WriteBatch(Random random)
        {
            RaceWidener.MarkWriterThread();
            for (var index = 0; index < OperationsPerCheckpoint; index++)
            {
                WriteRandomSource(random);
            }
        }

        private void WriteRandomSource(Random random)
        {
            var node = Sources[random.Next(Sources.Length)];
            var value = random.Next(100);
            switch (random.Next(SourceCount))
            {
                case 0:
                    node.Value = value;
                    break;
                case 1:
                    node.Alternate = value;
                    break;
                case 2:
                    node.UseAlternate = value % 2 == 0;
                    break;
                default:
                    node.Offset = value;
                    break;
            }
        }

        public static void WriteNextValue(StressNode node, int source)
        {
            switch (source)
            {
                case 0:
                    node.Value++;
                    break;
                case 1:
                    node.Alternate++;
                    break;
                case 2:
                    node.UseAlternate = !node.UseAlternate;
                    break;
                default:
                    node.Offset++;
                    break;
            }
        }

        /// <summary>Removes the subject from the slot if it is still there; its owner may have replaced it since.</summary>
        public void Remove(StressNode subject, Slot slot)
        {
            switch (slot.Kind)
            {
                case SlotKind.First when Roots[slot.Index].First == subject:
                    Roots[slot.Index].First = null;
                    break;
                case SlotKind.Second when Roots[slot.Index].Second == subject:
                    Roots[slot.Index].Second = null;
                    break;
                case SlotKind.Items when Roots[slot.Index].Items.Contains(subject):
                    Roots[slot.Index].Items = Roots[slot.Index].Items.Where(item => item != subject).ToArray();
                    break;
                case SlotKind.Child when Nodes[slot.Index].Child == subject:
                    Nodes[slot.Index].Child = null;
                    break;
            }
        }

        /// <summary>Places the subject in the slot, which removes the subject that was there.</summary>
        public void Place(StressNode subject, Slot slot)
        {
            switch (slot.Kind)
            {
                case SlotKind.First:
                    Roots[slot.Index].First = subject;
                    break;
                case SlotKind.Second:
                    Roots[slot.Index].Second = subject;
                    break;
                case SlotKind.Items:
                    var items = Roots[slot.Index].Items;
                    Roots[slot.Index].Items = items.Length < MaxItemCount ? [.. items, subject] : [.. items[1..], subject];
                    break;
                case SlotKind.Child:
                    Nodes[slot.Index].Child = subject;
                    break;
            }
        }

        public FreshGraph BuildFreshCopy()
        {
            var fresh = new FreshGraph(CreateContext());
            foreach (var root in Roots)
            {
                var copy = new StressRoot(fresh.Context)
                {
                    First = CopyNode(root.First, fresh),
                    Second = CopyNode(root.Second, fresh),
                    Items = root.Items.Select(item => CopyNode(item, fresh)!).ToArray()
                };
                fresh.Add(root, copy, Label(root));
            }

            return fresh;
        }

        private StressNode? CopyNode(StressNode? node, FreshGraph fresh)
        {
            if (node is null)
            {
                return null;
            }

            if (fresh.Map.TryGetValue(node, out var existing))
            {
                return (StressNode)existing;
            }

            var copy = new StressNode(fresh.Context)
            {
                Value = node.Value,
                Alternate = node.Alternate,
                UseAlternate = node.UseAlternate,
                Offset = node.Offset
            };
            fresh.Add(node, copy, Label(node));
            copy.Child = CopyNode(node.Child, fresh);
            return copy;
        }

        private void AddLabels(string prefix, IEnumerable<IInterceptorSubject> subjects)
        {
            var index = 0;
            foreach (var subject in subjects)
            {
                _labels[subject] = $"{prefix}{index++}";
            }
        }
    }

    /// <summary>
    /// Moves its nodes between its root slots, and its leaves between the child slots of its nodes. It first
    /// removes a subject from where it put it last, so a subject never has two parents.
    /// </summary>
    private sealed class Mover
    {
        private readonly StressGraph _graph;
        private readonly Random _random;
        private readonly StressNode[] _owned;
        private readonly Slot[] _rootSlots;
        private readonly Slot[] _childSlots;
        private readonly Dictionary<StressNode, Slot> _slots = new(ReferenceEqualityComparer.Instance);

        public Mover(StressGraph graph, Slot[] rootSlots, int moverIndex, Random random)
        {
            _graph = graph;
            _random = random;
            _rootSlots = rootSlots;

            var nodeIndexes = Enumerable.Range(0, graph.Nodes.Length).Where(index => index % StressGraph.MoverCount == moverIndex).ToArray();
            var leaves = graph.Leaves.Where((_, index) => index % StressGraph.MoverCount == moverIndex).ToArray();
            _childSlots = nodeIndexes.Select(index => new Slot(SlotKind.Child, index)).ToArray();
            _owned = [.. nodeIndexes.Select(index => graph.Nodes[index]), .. leaves];

            for (var index = 0; index < nodeIndexes.Length; index++)
            {
                MoveTo(graph.Nodes[nodeIndexes[index]], rootSlots[index]);
            }

            for (var index = 0; index < leaves.Length; index++)
            {
                MoveTo(leaves[index], _childSlots[index]);
            }
        }

        public void RunBatch()
        {
            _graph.Widener.BeginMoves();
            try
            {
                for (var index = 0; index < OperationsPerCheckpoint; index++)
                {
                    MoveOwnedSubject();
                    _graph.Widener.CompleteMove();
                }
            }
            finally
            {
                _graph.Widener.EndMoves();
            }
        }

        private void MoveOwnedSubject()
        {
            var subject = _owned[_random.Next(_owned.Length)];
            var slots = Array.IndexOf(_graph.Leaves, subject) >= 0 ? _childSlots : _rootSlots;

            // Moving back into the same slot detaches the subject and attaches it again.
            var target = _random.Next(5) switch
            {
                0 => new Slot(SlotKind.None, 0),
                1 => _slots[subject],
                _ => slots[_random.Next(slots.Length)]
            };
            MoveTo(subject, target);
        }

        private void MoveTo(StressNode subject, Slot target)
        {
            if (_slots.TryGetValue(subject, out var slot))
            {
                _graph.Remove(subject, slot);
            }

            _graph.Place(subject, target);
            _slots[subject] = target;
        }
    }

    /// <summary>
    /// A graph built single-threaded from the attached part of a stress graph, keyed by the original subjects.
    /// </summary>
    private sealed class FreshGraph(IInterceptorSubjectContext context)
    {
        private readonly Dictionary<IInterceptorSubject, string> _labels = new(ReferenceEqualityComparer.Instance);

        public IInterceptorSubjectContext Context { get; } = context;

        public Dictionary<IInterceptorSubject, IInterceptorSubject> Map { get; } = new(ReferenceEqualityComparer.Instance);

        public void Add(IInterceptorSubject original, IInterceptorSubject copy, string label)
        {
            Map[original] = copy;
            _labels[copy] = label;
        }

        public string Label(IInterceptorSubject subject) => _labels.GetValueOrDefault(subject, "unknown");
    }
}
