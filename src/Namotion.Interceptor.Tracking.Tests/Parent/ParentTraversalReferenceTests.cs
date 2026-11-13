using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Parent;

/// <summary>
/// Compares <see cref="ParentsHandlerExtensions.TryGetFirstParent{TRoot}"/> on generated graphs with a
/// reference that is the contract written as the simplest search that fulfils it.
/// </summary>
public class ParentTraversalReferenceTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task WhenGraphHasUpToThreeSubjects_ThenResultEqualsBreadthFirstReference()
    {
        // Arrange
        var comparison = new Comparison();

        // Act
        await Task.Run(() =>
        {
            foreach (var (subjects, name) in EnumerateGraphsUpToThreeSubjects())
            {
                comparison.Compare(subjects, name);
            }
        }).WaitAsync(RunTimeout);

        // Assert
        Assert.Empty(comparison.Mismatches);
        Assert.Equal(4 + 64 + 4096, comparison.Graphs);
        Assert.True(comparison.Hits > 0 && comparison.Misses > 0);
    }

    [Fact]
    public async Task WhenGraphIsRandom_ThenResultEqualsBreadthFirstReference()
    {
        // Arrange
        var random = new Random(20261012);
        var comparison = new Comparison();

        // Act
        await Task.Run(() =>
        {
            for (var graph = 0; graph < 2000; graph++)
            {
                comparison.Compare(CreateRandomGraph(random, graph), $"graph={graph}");
            }
        }).WaitAsync(RunTimeout);

        // Assert
        Assert.Empty(comparison.Mismatches);
        Assert.Equal(2000, comparison.Graphs);
        Assert.True(comparison.Hits > 1000, $"Only {comparison.Hits} searches found a match.");
        Assert.True(comparison.Misses > 1000, $"Only {comparison.Misses} searches found nothing.");
    }

    private static IEnumerable<(IInterceptorSubject[] Subjects, string Name)> EnumerateGraphsUpToThreeSubjects()
    {
        for (var count = 1; count <= 3; count++)
        {
            var edgeCombinations = 1 << (count * count);
            var markCombinations = 1 << count;
            for (var edges = 0; edges < edgeCombinations; edges++)
            {
                for (var marks = 0; marks < markCombinations; marks++)
                {
                    yield return (CreateGraph(count, edges, marks), $"count={count} edges={edges} marks={marks}");
                }
            }
        }
    }

    private static IInterceptorSubject[] CreateGraph(int count, int edges, int marks)
    {
        var subjects = new IInterceptorSubject[count];
        for (var i = 0; i < count; i++)
        {
            subjects[i] = ParentTraversalGraph.CreateNode((marks & (1 << i)) != 0);
        }

        for (var child = 0; child < count; child++)
        {
            for (var parent = 0; parent < count; parent++)
            {
                if ((edges & (1 << (child * count + parent))) != 0)
                {
                    ParentTraversalGraph.Link(subjects[child], subjects[parent]);
                }
            }
        }

        return subjects;
    }

    private static IInterceptorSubject[] CreateRandomGraph(Random random, int graph)
    {
        var count = random.Next(4, 41);
        var branchShare = (graph % 3) switch { 0 => 0.0, 1 => 0.05, _ => 0.3 };
        var markShare = (graph / 3 % 3) switch { 0 => 0.0, 1 => 0.05, _ => 0.3 };

        var subjects = new IInterceptorSubject[count];
        for (var i = 0; i < count; i++)
        {
            subjects[i] = ParentTraversalGraph.CreateNode(random.NextDouble() < markShare);
        }

        for (var child = 0; child < count; child++)
        {
            LinkRandomParents(random, subjects, child, branchShare);
        }

        return subjects;
    }

    private static readonly string[] LinkProperties =
    [
        nameof(TraversalNode.Child), nameof(TraversalNode.SecondChild), nameof(TraversalNode.ThirdChild)
    ];

    private static void LinkRandomParents(Random random, IInterceptorSubject[] subjects, int child, double branchShare)
    {
        var parentCount = 1;
        if (random.NextDouble() < branchShare)
        {
            parentCount = random.Next(3) switch { 0 => 0, 1 => 2, _ => 3 };
        }

        // The property makes two links to the same parent two parent entries.
        for (var link = 0; link < parentCount; link++)
        {
            ParentTraversalGraph.Link(subjects[child], subjects[random.Next(subjects.Length)], LinkProperties[link]);
        }
    }

    private sealed class Comparison
    {
        public List<string> Mismatches { get; } = [];

        public int Graphs { get; private set; }

        public int Hits { get; private set; }

        public int Misses { get; private set; }

        public void Compare(IInterceptorSubject[] subjects, string graph)
        {
            Graphs++;
            for (var start = 0; start < subjects.Length; start++)
            {
                var expected = FindFirstParentBreadthFirstReference<ITraversalMarker>(subjects[start]);
                var actual = subjects[start].TryGetFirstParent<ITraversalMarker>();

                if (expected is null)
                {
                    Misses++;
                }
                else
                {
                    Hits++;
                }

                if (!ReferenceEquals(expected, actual) && Mismatches.Count < 20)
                {
                    Mismatches.Add(
                        $"{graph} start={start} expected={IndexOf(subjects, expected)} actual={IndexOf(subjects, actual)}");
                }
            }
        }

        private static int IndexOf(IInterceptorSubject[] subjects, object? subject)
        {
            for (var i = 0; i < subjects.Length; i++)
            {
                if (ReferenceEquals(subjects[i], subject))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// The contract of <see cref="ParentsHandlerExtensions.TryGetFirstParent{TRoot}"/>: breadth first over
    /// all parents in their order, every subject once, never the subject itself, entries without a
    /// subject skipped. Kept plain on purpose, so that it stays obviously correct while the real search
    /// is optimized.
    /// </summary>
    private static TRoot? FindFirstParentBreadthFirstReference<TRoot>(IInterceptorSubject subject)
        where TRoot : class
    {
        var visited = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<IInterceptorSubject>();
        queue.Enqueue(subject);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (!visited.Add(current))
            {
                continue;
            }

            if (current is TRoot root && !ReferenceEquals(current, subject))
            {
                return root;
            }

            foreach (var parent in current.GetParents())
            {
                if (parent.Property.Subject is not null)
                {
                    queue.Enqueue(parent.Property.Subject);
                }
            }
        }

        return null;
    }
}
