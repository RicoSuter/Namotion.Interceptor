using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Cross-checks the ownership model against an independent reimplementation: after every mutation of
/// a randomly built graph, the set of attached subjects must equal a forward reachability mark from
/// the anchored subjects, and every reference count and parent entry must match the occurrences that
/// mark walks over.
/// </summary>
/// <remarks>
/// The oracle is deliberately the algorithm the implementation does not use. The lifecycle answers
/// reachability with a backward search from the questioned subject and maintains counts and parents
/// incrementally; this recomputes everything forward from scratch through public APIs only, so the
/// two cannot share a bug. The seeds are fixed so a failure is reproducible.
/// </remarks>
public class OwnershipOracleTests
{
    private static readonly int[] CommittedSeeds = [1, 2, 3, 5, 8, 13, 21, 34];

    public static TheoryData<int> Seeds
    {
        get
        {
            var data = new TheoryData<int>();
            foreach (var seed in CommittedSeeds)
            {
                data.Add(seed);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void WhenAGraphIsMutatedRandomly_ThenOwnershipMatchesForwardReachability(int seed)
    {
        RunSeed(seed);
    }

    /// <summary>
    /// A truncated run is a silent loss of coverage: the theory case still passes, having exercised
    /// however few steps preceded the cycle, and a divergence later in that seed becomes unreachable.
    /// This pins that none of the committed seeds is truncated, so the skip stays free.
    /// </summary>
    [Fact]
    public void WhenTheCommittedSeedsRun_ThenNoneIsTruncatedByThePreExistingFallbackCycle()
    {
        // Arrange & Act
        var truncated = CommittedSeeds.Where(seed => !new OwnershipOracleTests().RunSeed(seed)).ToArray();

        // Assert
        Assert.Empty(truncated);
    }

    /// <summary>
    /// Runs one seed. Returns false when the pre-existing fallback delegation cycle cut it short, so
    /// a caller can tell a completed run from a truncated one.
    /// </summary>
    public bool RunSeed(int seed)
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithParents();

        var random = new Random(seed);
        var universe = new List<Person>();
        for (var i = 0; i < 10; i++)
        {
            // A third of the subjects start as constructor roots, so provisional anchors, adoption
            // and orphaning all occur.
            var subject = i % 3 == 0 ? new Person(context) : new Person();
            _names[subject] = $"S{i}";
            universe.Add(subject);
        }

        // Act & Assert
        var log = new List<string>();
        for (var step = 0; step < 60; step++)
        {
            try
            {
                log.Add(Mutate(context, universe, random));

                // Inside the same guard as the mutation: the oracle's own reads go through the
                // interceptor chain too, so a cycle can surface here rather than there.
                AssertOwnershipMatchesOracle(context, universe, log);
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("delegation cycle"))
            {
                // Pre-existing and unrelated to ownership: the context-inheritance handler composes
                // each subject onto the executor of the parent that first pulled it in, so two
                // subjects that each become the other's first parent compose a fallback cycle and
                // every later property read on them throws. It reproduces identically without any of
                // the ownership state, and the whole composed fallback graph is removed by the
                // transitional-API removal stage. Measured over 700 seeds it cuts 21 of them short,
                // 3.0 percent, costing 0.9 percent of the planned steps, and none of the committed
                // seeds; the fact above is what keeps that true.
                return false;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"step {step} threw: {exception.Message}\nafter\n{string.Join("\n", log)}", exception);
            }
        }

        return true;
    }

    /// <summary>Applies one random mutation and returns it, so a failure reports how to get there.</summary>
    private string Mutate(IInterceptorSubjectContext context, List<Person> universe, Random random)
    {
        var subject = universe[random.Next(universe.Count)];
        switch (random.Next(6))
        {
            case 0:
            {
                var value = PickReference(universe, random);
                subject.Father = value;
                return $"{Name(subject)}.Father = {(value is null ? "null" : Name(value))}";
            }

            case 1:
            {
                var value = PickReference(universe, random);
                subject.Mother = value;
                return $"{Name(subject)}.Mother = {(value is null ? "null" : Name(value))}";
            }

            case 2:
            {
                var count = random.Next(4);
                var children = new Person[count];
                for (var i = 0; i < count; i++)
                {
                    // Repeats are drawn from a small pool on purpose: duplicate occurrences and
                    // colliding indices are where occurrence identity is decided.
                    children[i] = universe[random.Next(universe.Count)];
                }

                subject.Children = children;
                return $"{Name(subject)}.Children = [{string.Join(", ", children.Select(Name))}]";
            }

            case 3:
                subject.Children = [];
                return $"{Name(subject)}.Children = []";

            case 4:
                if (subject.TryGetContext() is null || ((IInterceptorSubject)subject).Executor.Anchor != SubjectAnchorKind.Explicit)
                {
                    subject.AttachToContext(context);
                    return $"{Name(subject)}.AttachToContext()";
                }

                return $"({Name(subject)} already explicit)";

            default:
                if (((IInterceptorSubject)subject).Executor.Anchor == SubjectAnchorKind.Explicit)
                {
                    subject.DetachFromContext(context);
                    return $"{Name(subject)}.DetachFromContext()";
                }

                return $"({Name(subject)} not explicit)";
        }
    }

    private static Person? PickReference(List<Person> universe, Random random)
    {
        return random.Next(4) == 0 ? null : universe[random.Next(universe.Count)];
    }

    private void AssertOwnershipMatchesOracle(
        IInterceptorSubjectContext context, List<Person> universe, List<string> log)
    {
        var trace = string.Join("\n", log);
        var occurrences = new List<(Person Parent, string Property, object? Index, Person Child)>();
        var reachable = MarkForward(context, universe, occurrences);

        var attached = universe.Where(subject => subject.TryGetContext() is not null).ToHashSet();
        Assert.True(
            reachable.SetEquals(attached),
            $"attached [{Describe(attached)}] but reachable [{Describe(reachable)}] after\n{trace}");

        foreach (var subject in attached)
        {
            var expected = occurrences.Count(occurrence => ReferenceEquals(occurrence.Child, subject));

            var expectedParents = occurrences
                .Where(occurrence => ReferenceEquals(occurrence.Child, subject))
                .Select(occurrence => (Parent: Name(occurrence.Parent), occurrence.Property, Index: occurrence.Index?.ToString()))
                .OrderBy(entry => $"{entry.Parent}.{entry.Property}[{entry.Index}]")
                .ToList();

            var actualParents = ((IInterceptorSubject)subject).GetParents()
                .Select(parent => (Parent: Name(parent.Property.Subject), Property: parent.Property.Name, Index: parent.Index?.ToString()))
                .OrderBy(entry => $"{entry.Parent}.{entry.Property}[{entry.Index}]")
                .ToList();

            Assert.True(
                expectedParents.SequenceEqual(actualParents),
                $"{Name(subject)} parents [{string.Join(", ", actualParents)}], expected [{string.Join(", ", expectedParents)}] after\n{trace}");

            Assert.True(
                expected == subject.GetReferenceCount(),
                $"{Name(subject)} counts {subject.GetReferenceCount()}, expected {expected} after\n{trace}");
        }
    }

    /// <summary>
    /// The independent half: walks forward from every anchored subject over the values its
    /// structural properties currently hold, collecting the occurrences on the way.
    /// </summary>
    private static HashSet<Person> MarkForward(
        IInterceptorSubjectContext context,
        List<Person> universe,
        List<(Person Parent, string Property, object? Index, Person Child)> occurrences)
    {
        var marked = new HashSet<Person>();
        var pending = new Stack<Person>();
        foreach (var subject in universe)
        {
            var executor = ((IInterceptorSubject)subject).Executor;
            if (executor.Anchor != SubjectAnchorKind.None && ReferenceEquals(executor.AttachedContext, context))
            {
                pending.Push(subject);
            }
        }

        while (pending.Count > 0)
        {
            var subject = pending.Pop();
            if (!marked.Add(subject))
            {
                continue;
            }

            foreach (var (property, index, child) in ReadStructuralValues(subject))
            {
                occurrences.Add((subject, property, index, child));
                pending.Push(child);
            }
        }

        // Occurrences of subjects that are not themselves reachable are not edges of the graph.
        occurrences.RemoveAll(occurrence => !marked.Contains(occurrence.Parent));
        return marked;
    }

    private static IEnumerable<(string Property, object? Index, Person Child)> ReadStructuralValues(Person subject)
    {
        if (subject.Father is { } father)
        {
            yield return (nameof(Person.Father), null, father);
        }

        if (subject.Mother is { } mother)
        {
            yield return (nameof(Person.Mother), null, mother);
        }

        for (var i = 0; i < subject.Children.Length; i++)
        {
            yield return (nameof(Person.Children), i, subject.Children[i]);
        }
    }

    /// <summary>
    /// Stable names captured outside the graph. Reading a name back off the subject would resolve the
    /// interceptor chain, and the composed fallback graph these tests build can be cyclic. Per
    /// instance, so a run does not retain every subject of every other run.
    /// </summary>
    private readonly Dictionary<IInterceptorSubject, string> _names = new();

    private string Name(IInterceptorSubject subject)
    {
        return _names.GetValueOrDefault(subject, "?");
    }

    private string Describe(IEnumerable<Person> subjects)
    {
        return string.Join(", ", subjects.Select(Name).OrderBy(name => name));
    }
}
