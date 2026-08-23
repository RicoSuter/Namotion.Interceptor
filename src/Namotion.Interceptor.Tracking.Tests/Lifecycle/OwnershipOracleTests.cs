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
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(21)]
    [InlineData(34)]
    public void WhenAGraphIsMutatedRandomly_ThenOwnershipMatchesForwardReachability(int seed)
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
            universe.Add(i % 3 == 0 ? new Person(context) { FirstName = $"S{i}" } : new Person { FirstName = $"S{i}" });
        }

        // Act & Assert
        var log = new List<string>();
        for (var step = 0; step < 60; step++)
        {
            log.Add(Mutate(context, universe, random));
            AssertOwnershipMatchesOracle(context, universe, log);
        }
    }

    /// <summary>Applies one random mutation and returns it, so a failure reports how to get there.</summary>
    private static string Mutate(IInterceptorSubjectContext context, List<Person> universe, Random random)
    {
        var subject = universe[random.Next(universe.Count)];
        switch (random.Next(6))
        {
            case 0:
            {
                var value = PickReference(universe, random);
                subject.Father = value;
                return $"{subject.FirstName}.Father = {value?.FirstName ?? "null"}";
            }

            case 1:
            {
                var value = PickReference(universe, random);
                subject.Mother = value;
                return $"{subject.FirstName}.Mother = {value?.FirstName ?? "null"}";
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
                return $"{subject.FirstName}.Children = [{string.Join(", ", children.Select(child => child.FirstName))}]";
            }

            case 3:
                subject.Children = [];
                return $"{subject.FirstName}.Children = []";

            case 4:
                if (subject.TryGetContext() is null || ((IInterceptorSubject)subject).Executor.Anchor != SubjectAnchorKind.Explicit)
                {
                    subject.AttachToContext(context);
                    return $"{subject.FirstName}.AttachToContext()";
                }

                return $"({subject.FirstName} already explicit)";

            default:
                if (((IInterceptorSubject)subject).Executor.Anchor == SubjectAnchorKind.Explicit)
                {
                    subject.DetachFromContext(context);
                    return $"{subject.FirstName}.DetachFromContext()";
                }

                return $"({subject.FirstName} not explicit)";
        }
    }

    private static Person? PickReference(List<Person> universe, Random random)
    {
        return random.Next(4) == 0 ? null : universe[random.Next(universe.Count)];
    }

    private static void AssertOwnershipMatchesOracle(
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
                .Select(occurrence => (occurrence.Parent.FirstName, occurrence.Property, Index: occurrence.Index?.ToString()))
                .OrderBy(entry => $"{entry.FirstName}.{entry.Property}[{entry.Index}]")
                .ToList();

            var actualParents = ((IInterceptorSubject)subject).GetParents()
                .Select(parent => (((Person)parent.Property.Subject).FirstName, parent.Property.Name, Index: parent.Index?.ToString()))
                .OrderBy(entry => $"{entry.FirstName}.{entry.Name}[{entry.Index}]")
                .ToList();

            Assert.True(
                expectedParents.SequenceEqual(actualParents),
                $"{subject.FirstName} parents [{string.Join(", ", actualParents)}], expected [{string.Join(", ", expectedParents)}] after\n{trace}");

            Assert.True(
                expected == subject.GetReferenceCount(),
                $"{subject.FirstName} counts {subject.GetReferenceCount()}, expected {expected} after\n{trace}");
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

    private static string Describe(IEnumerable<Person> subjects)
    {
        return string.Join(", ", subjects.Select(subject => subject.FirstName).OrderBy(name => name));
    }
}
