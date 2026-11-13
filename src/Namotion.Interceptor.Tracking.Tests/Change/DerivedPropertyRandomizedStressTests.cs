using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class DerivedPropertyRandomizedStressTests
{
    public DerivedPropertyRandomizedStressTests() => PropertyChangeSubscriptions.ResetForTests();

    private const int PersonCount = 6;
    private const int WorkerCount = 4;
    private const int RoundCount = 3000;
    private const int OperationsPerWorkerAndRound = 20;

    private static readonly string[] DerivedNames = [nameof(StressPerson.MotherName), nameof(StressPerson.Line)];

    [Fact]
    [Trait("Category", "Stress")]
    public async Task WhenThreadsWriteAttachAndDetachConcurrently_ThenLastPublishedValueEqualsGetter()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new StressPerson(context);
        var people = Enumerable.Range(0, PersonCount).Select(i => new StressPerson { Name = $"p{i}" }).ToArray();
        var lastPublished = new Dictionary<PropertyReference, object?>();
        var subscriptions = people
            .SelectMany(person => DerivedNames.Select(name => new PropertyReference(person, name).SubscribeInline(
                (in SubjectPropertyChange change) =>
                {
                    lock (lastPublished)
                    {
                        lastPublished[change.Property] = change.GetNewValue<object?>();
                    }
                })))
            .ToArray();
        var mismatches = new List<string>();
        var roundsWithAttachedPeople = 0;

        try
        {
            // Act
            for (var round = 0; round < RoundCount; round++)
            {
                var seed = round * WorkerCount;
                var workers = Enumerable.Range(0, WorkerCount)
                    .Select(worker => DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
                    {
                        var random = new Random(seed + worker);
                        if (worker == 0)
                        {
                            MoveRandomly(root, people, random);
                        }
                        else
                        {
                            WriteRandomly(people, random);
                        }
                    }));
                await Task.WhenAll(workers).WaitAsync(TestTimeouts.Default);
                if (VerifySettledState(round, people, lastPublished, mismatches) > 0)
                {
                    roundsWithAttachedPeople++;
                }
            }

            // Assert
            Assert.True(roundsWithAttachedPeople > 0);
            Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches.Take(30)));
        }
        finally
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }
    }

    /// <summary>
    /// Attaches and detaches people. Structural writes run on one thread: concurrent structural writes can leave a
    /// subject attached by the lifecycle without its inherited context, which no derived property can publish through.
    /// </summary>
    private static void MoveRandomly(StressPerson root, StressPerson[] people, Random random)
    {
        for (var i = 0; i < OperationsPerWorkerAndRound; i++)
        {
            var index = random.Next(people.Length);
            if (random.Next(2) == 0)
            {
                root.Children = people.Where(_ => random.Next(2) == 0).ToArray();
            }
            else
            {
                // Only a person with a lower index, so there are no cycles: the lifecycle keeps a cycle that is no
                // longer reachable attached through its own references, and a person that becomes its own mother
                // then is attached without a context.
                people[index].Mother = random.Next(4) == 0 || index == 0 ? null : people[random.Next(index)];
            }
        }
    }

    private static void WriteRandomly(StressPerson[] people, Random random)
    {
        for (var i = 0; i < OperationsPerWorkerAndRound; i++)
        {
            people[random.Next(people.Length)].Name = $"n{random.Next(100)}";
        }
    }

    /// <summary>
    /// Once the writes settled, compares every attached derived property's committed value with its getter, then
    /// gives every attached person a new name and compares the last published values with the getters. Detached
    /// people keep no links. Returns the number of attached people.
    /// </summary>
    /// <remarks>
    /// The last publication of a round is not comparable by itself: an attach commits its first value without
    /// publishing it, and a write that does not change a value publishes nothing. The committed value is compared
    /// directly, and the publications only after the settle write, which changes every value it compares.
    /// </remarks>
    private static int VerifySettledState(
        int round, StressPerson[] people, Dictionary<PropertyReference, object?> lastPublished, List<string> mismatches)
    {
        var attached = people.Where(SubjectLifecycleAttachments.IsAttached).ToArray();
        foreach (var property in attached.SelectMany(GetDerivedProperties))
        {
            AddIfDifferent(mismatches, round, property, "committed", property.Metadata.GetValue!(property.Subject), GetLastKnownValue(property));
        }

        lock (lastPublished)
        {
            lastPublished.Clear();
        }

        for (var i = 0; i < attached.Length; i++)
        {
            attached[i].Name = $"r{round}p{i}";
        }

        foreach (var person in attached)
        {
            // A person without a mother keeps the same mother name, which is not published again.
            foreach (var property in GetDerivedProperties(person).Where(p => person.Mother is not null || p.Name == nameof(StressPerson.Line)))
            {
                lastPublished.TryGetValue(property, out var published);
                AddIfDifferent(mismatches, round, property, "published", property.Metadata.GetValue!(person), published);
            }
        }

        var sources = people
            .SelectMany(person => new[] { nameof(StressPerson.Name), nameof(StressPerson.Mother), nameof(StressPerson.MotherName) }
                .Select(name => new PropertyReference(person, name)))
            .ToArray();
        foreach (var property in people.Except(attached).SelectMany(GetDerivedProperties))
        {
            if (property.GetRequiredProperties().Length > 0 ||
                sources.Any(source => source.GetUsedByProperties().Items.ToArray().Contains(property)))
            {
                mismatches.Add($"Round {round}: {property.Name} of detached {property.Subject} keeps a link.");
            }
        }

        return attached.Length;
    }

    private static IEnumerable<PropertyReference> GetDerivedProperties(StressPerson person)
    {
        return DerivedNames.Select(name => new PropertyReference(person, name));
    }

    private static void AddIfDifferent(List<string> mismatches, int round, PropertyReference property, string kind, object? expected, object? actual)
    {
        if (!Equals(expected, actual))
        {
            mismatches.Add($"Round {round}: {kind} {property.Name} of {property.Subject} is '{actual}', the getter returns '{expected}'.");
        }
    }

    private static object? GetLastKnownValue(PropertyReference property)
    {
        return property.TryGetDerivedPropertyData()?.LastKnownValue;
    }
}

/// <summary>
/// A person whose derived properties read another subject through a tracked property, so their dependencies change
/// with <see cref="Mother"/>.
/// </summary>
[InterceptorSubject]
public partial class StressPerson
{
    public StressPerson()
    {
        Children = [];
    }

    public partial string? Name { get; set; }

    public partial StressPerson? Mother { get; set; }

    public partial StressPerson[] Children { get; set; }

    [Derived]
    public string MotherName
    {
        get
        {
            var name = Mother?.Name;

            // Widens the window between the reads and the commit of an evaluation, in which other threads write.
            Thread.SpinWait(1000);
            return name ?? "-";
        }
    }

    [Derived]
    public string Line => $"{Name} of {MotherName}";

    public override string ToString() => Name ?? "?";
}
