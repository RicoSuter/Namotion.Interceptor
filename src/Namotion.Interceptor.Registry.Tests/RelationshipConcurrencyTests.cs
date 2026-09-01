using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry.Tests;

public class RelationshipConcurrencyTests
{
    [Theory]
    [InlineData(RelationshipConfiguration.LifecycleOnly)]
    [InlineData(RelationshipConfiguration.Parents)]
    [InlineData(RelationshipConfiguration.Registry)]
    [InlineData(RelationshipConfiguration.ParentsAndRegistry)]
    [Trait("Category", "Concurrency")]
    public async Task WhenConcurrentWritesTargetTheSameProperty_ThenTheFinalBackingGenerationWins(
        RelationshipConfiguration configuration)
    {
        // Replacing the backing re-read with either writer's captured value would let the losing invocation
        // publish a stale generation after both setters have returned.
        // Arrange
        var first = new Person { FirstName = "First" };
        var second = new Person { FirstName = "Second" };
        var third = new Person { FirstName = "Third" };
        var initial = new Person { FirstName = "Initial" };
        var firstGeneration = new[] { first, second, first };
        var secondGeneration = new[] { third, first, third };
        using var writeBarrier = new StructuralWriteBarrier(firstGeneration, secondGeneration);
        var context = CreateContext(configuration, writeBarrier);
        var container = new ConcurrentRelationshipContainer
        {
            Array = [initial]
        };
        Assert.True(((IInterceptorSubject)container).Context.AddFallbackContext(context));

        // Act
        await RunConcurrently(
            () => container.Array = firstGeneration,
            () => container.Array = secondGeneration);

        var finalGeneration = ReferenceEquals(container.Array, firstGeneration)
            ? new ExpectedProperty(
                nameof(ConcurrentRelationshipContainer.Array),
                [(first, 0), (second, 1), (first, 2)],
                () => EnumerateArray(container.Array))
            : new ExpectedProperty(
                nameof(ConcurrentRelationshipContainer.Array),
                [(third, 0), (first, 1), (third, 2)],
                () => EnumerateArray(container.Array));

        // Assert
        Assert.True(
            ReferenceEquals(container.Array, firstGeneration) ||
            ReferenceEquals(container.Array, secondGeneration));
        AssertGraphState(
            configuration,
            context,
            container,
            [finalGeneration],
            [initial, first, second, third]);
        AssertDetachClearsAndReattachConverges(
            configuration,
            context,
            container,
            [finalGeneration],
            [initial, first, second, third]);
    }

    [Theory]
    [InlineData(RelationshipConfiguration.LifecycleOnly)]
    [InlineData(RelationshipConfiguration.Parents)]
    [InlineData(RelationshipConfiguration.Registry)]
    [InlineData(RelationshipConfiguration.ParentsAndRegistry)]
    [Trait("Category", "Concurrency")]
    public async Task WhenConcurrentWritesTargetDifferentProperties_ThenBothBackingGenerationsWin(
        RelationshipConfiguration configuration)
    {
        // Serializing by property instead of by lifecycle authority would allow membership and processed
        // state updates for the two properties to interleave and lose one contribution.
        // Arrange
        var initialFirst = new Person { FirstName = "Initial first" };
        var initialSecond = new Person { FirstName = "Initial second" };
        var finalFirst = new Person { FirstName = "Final first" };
        var finalSecond = new Person { FirstName = "Final second" };
        using var writeBarrier = new StructuralWriteBarrier(finalFirst, finalSecond);
        var context = CreateContext(configuration, writeBarrier);
        var container = new ConcurrentRelationshipContainer
        {
            First = initialFirst,
            Second = initialSecond
        };
        Assert.True(((IInterceptorSubject)container).Context.AddFallbackContext(context));

        // Act
        await RunConcurrently(
            () => container.First = finalFirst,
            () => container.Second = finalSecond);

        // Assert
        AssertGraphState(
            configuration,
            context,
            container,
            [
                new ExpectedProperty(
                    nameof(ConcurrentRelationshipContainer.First),
                    [(finalFirst, null)],
                    () => EnumerateDirect(container.First)),
                new ExpectedProperty(
                    nameof(ConcurrentRelationshipContainer.Second),
                    [(finalSecond, null)],
                    () => EnumerateDirect(container.Second))
            ],
            [initialFirst, initialSecond, finalFirst, finalSecond]);
        AssertDetachClearsAndReattachConverges(
            configuration,
            context,
            container,
            [
                new ExpectedProperty(
                    nameof(ConcurrentRelationshipContainer.First),
                    [(finalFirst, null)],
                    () => EnumerateDirect(container.First)),
                new ExpectedProperty(
                    nameof(ConcurrentRelationshipContainer.Second),
                    [(finalSecond, null)],
                    () => EnumerateDirect(container.Second))
            ],
            [initialFirst, initialSecond, finalFirst, finalSecond]);
    }

    [Theory]
    [InlineData(RelationshipConfiguration.LifecycleOnly)]
    [InlineData(RelationshipConfiguration.Parents)]
    [InlineData(RelationshipConfiguration.Registry)]
    [InlineData(RelationshipConfiguration.ParentsAndRegistry)]
    [Trait("Category", "Concurrency")]
    public async Task WhenDuplicateOccurrenceChangesRaceAReplacement_ThenMembershipAndOccurrencesConverge(
        RelationshipConfiguration configuration)
    {
        // Collapsing duplicate occurrences into membership state, or diffing either captured setter value,
        // would leave the winning sequence with the wrong indexes or reference contribution.
        // Arrange
        var shared = new Person { FirstName = "Shared" };
        var other = new Person { FirstName = "Other" };
        var replacement = new Person { FirstName = "Replacement" };
        var initial = new Person { FirstName = "Initial" };
        var duplicateGeneration = new[] { shared, shared, other, shared };
        var replacementGeneration = new[] { replacement, shared, replacement };
        using var writeBarrier = new StructuralWriteBarrier(duplicateGeneration, replacementGeneration);
        var context = CreateContext(configuration, writeBarrier);
        var container = new ConcurrentRelationshipContainer
        {
            Array = [initial, shared, shared]
        };
        Assert.True(((IInterceptorSubject)container).Context.AddFallbackContext(context));

        // Act
        await RunConcurrently(
            () => container.Array = duplicateGeneration,
            () => container.Array = replacementGeneration);

        var finalGeneration = ReferenceEquals(container.Array, duplicateGeneration)
            ? new ExpectedProperty(
                nameof(ConcurrentRelationshipContainer.Array),
                [(shared, 0), (shared, 1), (other, 2), (shared, 3)],
                () => EnumerateArray(container.Array))
            : new ExpectedProperty(
                nameof(ConcurrentRelationshipContainer.Array),
                [(replacement, 0), (shared, 1), (replacement, 2)],
                () => EnumerateArray(container.Array));

        // Assert
        Assert.True(
            ReferenceEquals(container.Array, duplicateGeneration) ||
            ReferenceEquals(container.Array, replacementGeneration));
        AssertGraphState(
            configuration,
            context,
            container,
            [finalGeneration],
            [initial, shared, other, replacement]);
        AssertDetachClearsAndReattachConverges(
            configuration,
            context,
            container,
            [finalGeneration],
            [initial, shared, other, replacement]);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task WhenReadersSnapshotWhileWritersReorderAndRekey_ThenEveryArrayIsOneImmutableGeneration()
    {
        // Mutating published relationships in place, or publishing a cache before its entries are initialized,
        // would let at least one active-write snapshot combine old order with new indexes or contain defaults.
        // Arrange
        var arrayChild = new Person { FirstName = "Array child" };
        var arrayOther = new Person { FirstName = "Array other" };
        var dictionaryChild = new Person { FirstName = "Dictionary child" };
        var dictionaryOther = new Person { FirstName = "Dictionary other" };
        var alpha = new OpaqueKey("alpha");
        var beta = new OpaqueKey("beta");
        var gamma = new OpaqueKey("gamma");
        var delta = new OpaqueKey("delta");
        var epsilon = new OpaqueKey("epsilon");
        var zeta = new OpaqueKey("zeta");
        var oldArray = new[] { arrayChild, arrayOther, arrayChild };
        var newArray = new[] { arrayOther, arrayChild, arrayChild };
        var oldDictionary = new OpaqueReadOnlyDictionary(
            (alpha, dictionaryChild),
            (beta, dictionaryOther),
            (gamma, dictionaryChild));
        var newDictionary = new OpaqueReadOnlyDictionary(
            (delta, dictionaryChild),
            (epsilon, dictionaryChild),
            (zeta, dictionaryOther));
        var context = CreateContext(RelationshipConfiguration.ParentsAndRegistry);
        var container = new SnapshotRelationshipContainer
        {
            Array = oldArray,
            Directory = oldDictionary
        };
        Assert.True(((IInterceptorSubject)container).Context.AddFallbackContext(context));

        var registry = context.GetService<ISubjectRegistry>();
        var registeredContainer = registry.TryGetRegisteredSubject(container)!;
        var arrayProperty = registeredContainer.TryGetProperty(nameof(SnapshotRelationshipContainer.Array))!;
        var dictionaryProperty = registeredContainer.TryGetProperty(nameof(SnapshotRelationshipContainer.Directory))!;
        var oldArrayChildren = arrayProperty.Children;
        var oldArrayRegistryParents = registry.TryGetRegisteredSubject(arrayChild)!.Parents;
        var oldArrayTrackedParents = arrayChild.GetParents();
        var oldDictionaryChildren = dictionaryProperty.Children;
        var oldDictionaryRegistryParents = registry.TryGetRegisteredSubject(dictionaryChild)!.Parents;
        var oldDictionaryTrackedParents = dictionaryChild.GetParents();
        container.ArrayGate.Arm();
        container.DirectoryGate.Arm();

        // Act: the lifecycle writer lock means one callback parks after all built-in consumers publish,
        // while the second writer waits. Readers observe both phases without assuming cross-view atomicity.
        var arrayWrite = StartWorker(() => container.Array = newArray);
        var dictionaryWrite = StartWorker(() => container.Directory = newDictionary);
        ExceptionDispatchInfo? workerFailure = null;
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => container.ArrayGate.Entered.IsSet || container.DirectoryGate.Entered.IsSet ||
                      arrayWrite.IsCompleted || dictionaryWrite.IsCompleted,
                message: "One relationship writer should reach its publication gate.");

            var firstGate = container.ArrayGate.Entered.IsSet
                ? container.ArrayGate
                : container.DirectoryGate;
            var secondGate = ReferenceEquals(firstGate, container.ArrayGate)
                ? container.DirectoryGate
                : container.ArrayGate;
            var secondWrite = ReferenceEquals(secondGate, container.ArrayGate)
                ? arrayWrite
                : dictionaryWrite;
            Assert.True(firstGate.Entered.IsSet);
            Assert.False(arrayWrite.IsCompleted && dictionaryWrite.IsCompleted);
            AssertActiveSnapshots(
                registry,
                arrayProperty,
                arrayChild,
                [(arrayChild, 0), (arrayOther, 1), (arrayChild, 2)],
                [(arrayOther, 0), (arrayChild, 1), (arrayChild, 2)]);
            AssertActiveSnapshots(
                registry,
                dictionaryProperty,
                dictionaryChild,
                [(dictionaryChild, alpha), (dictionaryOther, beta), (dictionaryChild, gamma)],
                [(dictionaryChild, delta), (dictionaryChild, epsilon), (dictionaryOther, zeta)]);

            firstGate.Release.Set();
            await AsyncTestHelpers.WaitUntilAsync(
                () => secondGate.Entered.IsSet || secondWrite.IsCompleted,
                message: "The second relationship writer should reach its publication gate.");
            if (!secondGate.Entered.IsSet)
            {
                await secondWrite;
            }
            Assert.True(secondGate.Entered.IsSet);
            AssertActiveSnapshots(
                registry,
                arrayProperty,
                arrayChild,
                [(arrayChild, 0), (arrayOther, 1), (arrayChild, 2)],
                [(arrayOther, 0), (arrayChild, 1), (arrayChild, 2)]);
            AssertActiveSnapshots(
                registry,
                dictionaryProperty,
                dictionaryChild,
                [(dictionaryChild, alpha), (dictionaryOther, beta), (dictionaryChild, gamma)],
                [(dictionaryChild, delta), (dictionaryChild, epsilon), (dictionaryOther, zeta)]);
        }
        catch (Exception exception)
        {
            workerFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            container.ArrayGate.Release.Set();
            container.DirectoryGate.Release.Set();
            workerFailure = await ObserveWorkersAsync(
                [arrayWrite, dictionaryWrite],
                workerFailure,
                "Both relationship writers should complete after their publication gates are released.");
        }
        workerFailure?.Throw();

        // Assert: captured arrays stay frozen and every quiescent view agrees with the backing values.
        AssertGeneration(oldArrayChildren, [(arrayChild, 0), (arrayOther, 1), (arrayChild, 2)]);
        AssertParentGeneration(oldArrayRegistryParents, arrayProperty,
            [(arrayChild, 0), (arrayChild, 2)]);
        AssertTrackedParentGeneration(oldArrayTrackedParents, container,
            nameof(SnapshotRelationshipContainer.Array), [(arrayChild, 0), (arrayChild, 2)]);
        AssertGeneration(oldDictionaryChildren,
            [(dictionaryChild, alpha), (dictionaryOther, beta), (dictionaryChild, gamma)]);
        AssertParentGeneration(oldDictionaryRegistryParents, dictionaryProperty,
            [(dictionaryChild, alpha), (dictionaryChild, gamma)]);
        AssertTrackedParentGeneration(oldDictionaryTrackedParents, container,
            nameof(SnapshotRelationshipContainer.Directory),
            [(dictionaryChild, alpha), (dictionaryChild, gamma)]);

        var expected = new[]
        {
            new ExpectedProperty(
                nameof(SnapshotRelationshipContainer.Array),
                [(arrayOther, 0), (arrayChild, 1), (arrayChild, 2)],
                () => EnumerateArray(container.Array)),
            new ExpectedProperty(
                nameof(SnapshotRelationshipContainer.Directory),
                [(dictionaryChild, delta), (dictionaryChild, epsilon), (dictionaryOther, zeta)],
                () => EnumerateDictionary(container.Directory))
        };
        AssertGraphState(
            RelationshipConfiguration.ParentsAndRegistry,
            context,
            container,
            expected,
            [arrayChild, arrayOther, dictionaryChild, dictionaryOther]);
        AssertDetachClearsAndReattachConverges(
            RelationshipConfiguration.ParentsAndRegistry,
            context,
            container,
            expected,
            [arrayChild, arrayOther, dictionaryChild, dictionaryOther]);
    }

    private static IInterceptorSubjectContext CreateContext(
        RelationshipConfiguration configuration,
        IWriteInterceptor? writeInterceptor = null)
    {
        var context = InterceptorSubjectContext.Create();
        if (writeInterceptor is not null)
        {
            context.AddService(writeInterceptor);
        }

        return configuration switch
        {
            RelationshipConfiguration.LifecycleOnly => context.WithLifecycle(),
            RelationshipConfiguration.Parents => context.WithParents(),
            RelationshipConfiguration.Registry => context.WithRegistry(),
            RelationshipConfiguration.ParentsAndRegistry => context.WithParents().WithRegistry(),
            _ => throw new ArgumentOutOfRangeException(nameof(configuration), configuration, null)
        };
    }

    private static async Task RunConcurrently(Action first, Action second)
    {
        var workers = new[] { StartWorker(first), StartWorker(second) };
        var workerFailure = await ObserveWorkersAsync(
            workers,
            null,
            "Both concurrent structural writers should complete.");
        workerFailure?.Throw();
    }

    private static Task StartWorker(Action action) => Task.Factory.StartNew(
        action,
        CancellationToken.None,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default);

    private static async Task<ExceptionDispatchInfo?> ObserveWorkersAsync(
        IReadOnlyList<Task> workers,
        ExceptionDispatchInfo? primaryFailure,
        string message)
    {
        ExceptionDispatchInfo? coordinationFailure = null;
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => workers.All(worker => worker.IsCompleted),
                message: message);
        }
        catch (Exception exception)
        {
            coordinationFailure = ExceptionDispatchInfo.Capture(exception);
        }

        foreach (var worker in workers)
        {
            if (!worker.IsCompleted)
            {
                continue;
            }

            try
            {
                await worker;
            }
            catch (Exception exception)
            {
                primaryFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        return primaryFailure ?? coordinationFailure;
    }

    private static void AssertGraphState<TContainer>(
        RelationshipConfiguration configuration,
        IInterceptorSubjectContext context,
        TContainer container,
        IReadOnlyList<ExpectedProperty> expectedProperties,
        IReadOnlyList<Person> candidates)
        where TContainer : class, IInterceptorSubject
    {
        foreach (var expectedProperty in expectedProperties)
        {
            AssertRelationshipSequence(expectedProperty.Relationships, expectedProperty.ReadBacking());
        }

        var expectedReferenceCounts = new Dictionary<Person, int>(ReferenceEqualityComparer.Instance);
        foreach (var candidate in candidates)
        {
            expectedReferenceCounts.TryAdd(candidate, 0);
        }

        foreach (var expectedProperty in expectedProperties)
        {
            foreach (var child in expectedProperty.Relationships
                         .Select(relationship => relationship.Child)
                         .Distinct((IEqualityComparer<Person>)ReferenceEqualityComparer.Instance))
            {
                expectedReferenceCounts[child] = expectedReferenceCounts.GetValueOrDefault(child) + 1;
            }
        }

        foreach (var candidate in candidates)
        {
            Assert.Equal(expectedReferenceCounts[candidate], candidate.GetReferenceCount());
        }
        Assert.Equal(0, container.GetReferenceCount());

        var hasRegistry = configuration is RelationshipConfiguration.Registry or
            RelationshipConfiguration.ParentsAndRegistry;
        var hasParents = configuration is RelationshipConfiguration.Parents or
            RelationshipConfiguration.ParentsAndRegistry;
        var registry = hasRegistry ? context.GetService<ISubjectRegistry>() : null;
        var registeredProperties = new Dictionary<string, RegisteredSubjectProperty>();

        if (registry is not null)
        {
            var expectedKnownSubjects = expectedReferenceCounts
                .Where(entry => entry.Value > 0)
                .Select(entry => (IInterceptorSubject)entry.Key)
                .Append(container)
                .ToArray();
            Assert.Equal(expectedKnownSubjects.Length, registry.KnownSubjects.Count);
            foreach (var expectedSubject in expectedKnownSubjects)
            {
                Assert.Equal(1, registry.KnownSubjects.Keys.Count(
                    known => ReferenceEquals(known, expectedSubject)));
            }

            var registeredContainer = registry.TryGetRegisteredSubject(container)!;
            foreach (var expectedProperty in expectedProperties)
            {
                var registeredProperty = registeredContainer.TryGetProperty(expectedProperty.Name)!;
                registeredProperties.Add(expectedProperty.Name, registeredProperty);
                AssertGeneration(registeredProperty.Children, expectedProperty.Relationships);
            }
        }

        foreach (var candidate in candidates)
        {
            var expectedRelationships = expectedProperties
                .SelectMany(property => property.Relationships.Select(
                    relationship => (Property: property, Relationship: relationship)))
                .Where(item => ReferenceEquals(item.Relationship.Child, candidate))
                .ToArray();

            if (registry is not null)
            {
                var registeredCandidate = registry.TryGetRegisteredSubject(candidate);
                if (expectedRelationships.Length == 0)
                {
                    Assert.Null(registeredCandidate);
                }
                else
                {
                    Assert.NotNull(registeredCandidate);
                    Assert.Equal(expectedReferenceCounts[candidate], registeredCandidate.ReferenceCount);
                    Assert.Equal(expectedRelationships.Length, registeredCandidate.Parents.Length);
                    foreach (var propertyGroup in expectedRelationships.GroupBy(item => item.Property.Name))
                    {
                        AssertParentGeneration(
                            registeredCandidate.Parents,
                            registeredProperties[propertyGroup.Key],
                            propertyGroup.Select(item => item.Relationship).ToArray());
                    }
                }
            }

            if (hasParents)
            {
                var trackedParents = candidate.GetParents();
                Assert.Equal(expectedRelationships.Length, trackedParents.Length);
                foreach (var propertyGroup in expectedRelationships.GroupBy(item => item.Property.Name))
                {
                    AssertTrackedParentGeneration(
                        trackedParents,
                        container,
                        propertyGroup.Key,
                        propertyGroup.Select(item => item.Relationship).ToArray());
                }
            }
            else
            {
                Assert.Empty(candidate.GetParents());
            }

            if (registry is not null && expectedRelationships.Length > 0)
            {
                var first = expectedRelationships[0];
                var property = registeredProperties[first.Property.Name];
                var expectedPath = first.Relationship.Index is null
                    ? $"{property.Name}.{nameof(Person.FirstName)}"
                    : $"{property.Name}[{first.Relationship.Index}].{nameof(Person.FirstName)}";
                var nameProperty = registry.TryGetRegisteredSubject(candidate)!
                    .TryGetProperty(nameof(Person.FirstName))!;
                Assert.Equal(expectedPath,
                    nameProperty.TryGetPath(DefaultPathProvider.Instance, container));
            }
        }
    }

    private static void AssertDetachClearsAndReattachConverges<TContainer>(
        RelationshipConfiguration configuration,
        IInterceptorSubjectContext context,
        TContainer container,
        IReadOnlyList<ExpectedProperty> expectedProperties,
        IReadOnlyList<Person> candidates)
        where TContainer : class, IInterceptorSubject
    {
        var hasRegistry = configuration is RelationshipConfiguration.Registry or
            RelationshipConfiguration.ParentsAndRegistry;
        var registry = hasRegistry ? context.GetService<ISubjectRegistry>() : null;
        var capturedProperties = registry is null
            ? []
            : expectedProperties.Select(expected => registry.TryGetRegisteredSubject(container)!
                .TryGetProperty(expected.Name)!).ToArray();
        var capturedSubjects = registry is null
            ? []
            : candidates.Select(registry.TryGetRegisteredSubject).Where(subject => subject is not null).ToArray();

        // Act: detaching must clear canonical processed state and every published group.
        Assert.True(container.Context.RemoveFallbackContext(context));

        // Assert
        AssertLifecycleStorageEmpty(context.GetService<LifecycleInterceptor>());
        Assert.All(candidates, candidate => Assert.Equal(0, candidate.GetReferenceCount()));
        Assert.All(candidates, candidate => Assert.Empty(candidate.GetParents()));
        if (registry is not null)
        {
            Assert.Empty(registry.KnownSubjects);
            Assert.All(capturedProperties, property => Assert.Empty(property.Children));
            Assert.All(capturedSubjects, subject => Assert.Empty(subject!.Parents));
            Assert.All(candidates, candidate => Assert.Null(registry.TryGetRegisteredSubject(candidate)));
        }

        // Act: reattachment must enumerate the successful backing values and reconstruct exactly one generation.
        Assert.True(container.Context.AddFallbackContext(context));

        // Assert
        AssertGraphState(configuration, context, container, expectedProperties, candidates);
    }

    private static void AssertLifecycleStorageEmpty(LifecycleInterceptor lifecycle)
    {
        // Callers reach this helper only after all workers and the synchronous detach have completed.
        var attachedSubjects = GetPrivateDictionary(lifecycle, "_attachedSubjects");
        var processedProperties = GetPrivateDictionary(lifecycle, "_processedProperties");
        Assert.Empty(attachedSubjects);
        Assert.Empty(processedProperties);
    }

    private static IDictionary GetPrivateDictionary(LifecycleInterceptor lifecycle, string fieldName)
    {
        var field = typeof(LifecycleInterceptor).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsAssignableFrom<IDictionary>(field?.GetValue(lifecycle));
    }

    private static void AssertActiveSnapshots(
        ISubjectRegistry registry,
        RegisteredSubjectProperty property,
        Person child,
        IReadOnlyList<(Person Child, object? Index)> oldGeneration,
        IReadOnlyList<(Person Child, object? Index)> newGeneration)
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var children = property.Children;
            var registryParents = registry.TryGetRegisteredSubject(child)!.Parents;
            var trackedParents = child.GetParents();

            AssertOneOf(children, oldGeneration, newGeneration);
            AssertOneOfRegistryParents(registryParents, property, oldGeneration, newGeneration, child);
            AssertOneOfTrackedParents(
                trackedParents,
                property.Subject,
                property.Name,
                oldGeneration,
                newGeneration,
                child);
        }
    }

    private static void AssertOneOf(
        ImmutableArray<SubjectPropertyChild> snapshot,
        IReadOnlyList<(Person Child, object? Index)> oldGeneration,
        IReadOnlyList<(Person Child, object? Index)> newGeneration)
    {
        Assert.All(snapshot, relationship => Assert.NotNull(relationship.Subject));
        Assert.True(
            IsRelationshipSequence(snapshot.Select(item => (item.Subject, item.Index)), oldGeneration) ||
            IsRelationshipSequence(snapshot.Select(item => (item.Subject, item.Index)), newGeneration),
            "The child snapshot combined two relationship generations.");
    }

    private static void AssertOneOfRegistryParents(
        ImmutableArray<SubjectPropertyParent> snapshot,
        RegisteredSubjectProperty property,
        IReadOnlyList<(Person Child, object? Index)> oldGeneration,
        IReadOnlyList<(Person Child, object? Index)> newGeneration,
        Person child)
    {
        Assert.All(snapshot, relationship => Assert.NotNull(relationship.Property));
        var actual = snapshot
            .Where(parent => ReferenceEquals(parent.Property, property))
            .Select(parent => ((IInterceptorSubject)child, parent.Index));
        var expectedOld = oldGeneration.Where(item => ReferenceEquals(item.Child, child)).ToArray();
        var expectedNew = newGeneration.Where(item => ReferenceEquals(item.Child, child)).ToArray();
        Assert.True(
            IsRelationshipSequence(actual, expectedOld) || IsRelationshipSequence(actual, expectedNew),
            "The Registry parent snapshot combined two relationship generations.");
    }

    private static void AssertOneOfTrackedParents(
        ImmutableArray<SubjectParent> snapshot,
        IInterceptorSubject parent,
        string propertyName,
        IReadOnlyList<(Person Child, object? Index)> oldGeneration,
        IReadOnlyList<(Person Child, object? Index)> newGeneration,
        Person child)
    {
        Assert.All(snapshot, relationship => Assert.NotNull(relationship.Property.Subject));
        var actual = snapshot
            .Where(item => ReferenceEquals(item.Property.Subject, parent) && item.Property.Name == propertyName)
            .Select(item => ((IInterceptorSubject)child, item.Index));
        var expectedOld = oldGeneration.Where(item => ReferenceEquals(item.Child, child)).ToArray();
        var expectedNew = newGeneration.Where(item => ReferenceEquals(item.Child, child)).ToArray();
        Assert.True(
            IsRelationshipSequence(actual, expectedOld) || IsRelationshipSequence(actual, expectedNew),
            "The tracked-parent snapshot combined two relationship generations.");
    }

    private static void AssertGeneration(
        ImmutableArray<SubjectPropertyChild> actual,
        IReadOnlyList<(Person Child, object? Index)> expected) =>
        AssertRelationshipSequence(
            expected,
            actual.Select(item => ((Person)item.Subject, item.Index)).ToArray());

    private static void AssertParentGeneration(
        ImmutableArray<SubjectPropertyParent> actual,
        RegisteredSubjectProperty property,
        IReadOnlyList<(Person Child, object? Index)> expected)
    {
        var filtered = actual
            .Where(parent => ReferenceEquals(parent.Property, property))
            .Select(parent => (expected[0].Child, parent.Index))
            .ToArray();
        AssertRelationshipSequence(expected, filtered);
    }

    private static void AssertTrackedParentGeneration<TContainer>(
        ImmutableArray<SubjectParent> actual,
        TContainer parent,
        string propertyName,
        IReadOnlyList<(Person Child, object? Index)> expected)
        where TContainer : class, IInterceptorSubject
    {
        var filtered = actual
            .Where(item => ReferenceEquals(item.Property.Subject, parent) && item.Property.Name == propertyName)
            .Select(item => (expected[0].Child, item.Index))
            .ToArray();
        AssertRelationshipSequence(expected, filtered);
    }

    private static void AssertRelationshipSequence(
        IReadOnlyList<(Person Child, object? Index)> expected,
        IReadOnlyList<(Person Child, object? Index)> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Same(expected[index].Child, actual[index].Child);
            AssertIndex(expected[index].Index, actual[index].Index);
        }
    }

    private static bool IsRelationshipSequence(
        IEnumerable<(IInterceptorSubject Subject, object? Index)> actual,
        IReadOnlyList<(Person Child, object? Index)> expected)
    {
        var actualArray = actual.ToArray();
        if (actualArray.Length != expected.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!ReferenceEquals(expected[index].Child, actualArray[index].Subject) ||
                !IndexMatches(expected[index].Index, actualArray[index].Index))
            {
                return false;
            }
        }

        return true;
    }

    private static void AssertIndex(object? expected, object? actual)
    {
        if (expected is OpaqueKey)
        {
            Assert.Same(expected, actual);
        }
        else
        {
            Assert.Equal(expected, actual);
        }
    }

    private static bool IndexMatches(object? expected, object? actual) => expected is OpaqueKey
        ? ReferenceEquals(expected, actual)
        : Equals(expected, actual);

    private static IReadOnlyList<(Person Child, object? Index)> EnumerateArray(Person[] array) =>
        array.Select((child, index) => (child, (object?)index)).ToArray();

    private static IReadOnlyList<(Person Child, object? Index)> EnumerateDirect(Person? child) =>
        child is null ? [] : [(child, null)];

    private static IReadOnlyList<(Person Child, object? Index)> EnumerateDictionary(
        IReadOnlyDictionary<OpaqueKey, Person> dictionary) =>
        dictionary.Select(item => (item.Value, (object?)item.Key)).ToArray();

    public enum RelationshipConfiguration
    {
        LifecycleOnly,
        Parents,
        Registry,
        ParentsAndRegistry
    }

    private sealed record ExpectedProperty(
        string Name,
        IReadOnlyList<(Person Child, object? Index)> Relationships,
        Func<IReadOnlyList<(Person Child, object? Index)>> ReadBacking);

    [RunsAfter(typeof(LifecycleInterceptor))]
    private sealed class StructuralWriteBarrier(object firstValue, object secondValue) : IWriteInterceptor, IDisposable
    {
        // Barrier owns all publication for its participant state; the two value references are immutable.
        private readonly Barrier _barrier = new(2);

        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.NewValue, firstValue) || ReferenceEquals(context.NewValue, secondValue))
            {
                if (!_barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Both structural writers did not reach the barrier.");
                }
            }

            next(ref context);
        }

        public void Dispose() => _barrier.Dispose();
    }
}

[InterceptorSubject]
public partial class ConcurrentRelationshipContainer
{
    public ConcurrentRelationshipContainer()
    {
        Array = [];
    }

    public partial Person? First { get; set; }

    public partial Person? Second { get; set; }

    public partial Person[] Array { get; set; }
}

[InterceptorSubject]
public partial class SnapshotRelationshipContainer : IPropertyRelationshipHandler
{
    public SnapshotRelationshipContainer()
    {
        Array = [];
        Directory = new OpaqueReadOnlyDictionary();
    }

    public RelationshipPublicationGate ArrayGate { get; } = new(nameof(Array));

    public RelationshipPublicationGate DirectoryGate { get; } = new(nameof(Directory));

    public partial Person[] Array { get; set; }

    public partial IReadOnlyDictionary<OpaqueKey, Person> Directory { get; set; }

    public void ReconcileChildRelationships(
        PropertyReference property,
        ReadOnlySpan<SubjectPropertyRelationship> relationships)
    {
        if (property.Name == nameof(Array))
        {
            ArrayGate.EnterIfArmed();
        }
        else if (property.Name == nameof(Directory))
        {
            DirectoryGate.EnterIfArmed();
        }
    }
}

public sealed class RelationshipPublicationGate(string propertyName)
{
    private int _isArmed;

    public ManualResetEventSlim Entered { get; } = new();

    public ManualResetEventSlim Release { get; } = new();

    public void Arm()
    {
        Entered.Reset();
        Release.Reset();
        Volatile.Write(ref _isArmed, 1);
    }

    public void EnterIfArmed()
    {
        // The volatile arm publishes the reset events; Interlocked gives the single writer ownership.
        if (Interlocked.Exchange(ref _isArmed, 0) == 0)
        {
            return;
        }

        Entered.Set();
        if (!Release.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException($"The {propertyName} relationship writer was not released.");
        }
    }
}

public sealed class OpaqueKey(string value)
{
    public override bool Equals(object? obj) =>
        throw new InvalidOperationException("Key equality is forbidden.");

    public override int GetHashCode() =>
        throw new InvalidOperationException("Key hashing is forbidden.");

    public override string ToString() => value;
}

public sealed class OpaqueReadOnlyDictionary(params (OpaqueKey Key, Person Value)[] items)
    : IReadOnlyDictionary<OpaqueKey, Person>
{
    public int Count => items.Length;

    public IEnumerable<OpaqueKey> Keys => items.Select(item => item.Key);

    public IEnumerable<Person> Values => items.Select(item => item.Value);

    public Person this[OpaqueKey key] => throw new NotSupportedException();

    public bool ContainsKey(OpaqueKey key) => throw new NotSupportedException();

    public bool TryGetValue(OpaqueKey key, out Person value) => throw new NotSupportedException();

    public IEnumerator<KeyValuePair<OpaqueKey, Person>> GetEnumerator() =>
        items.Select(item => KeyValuePair.Create(item.Key, item.Value)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
