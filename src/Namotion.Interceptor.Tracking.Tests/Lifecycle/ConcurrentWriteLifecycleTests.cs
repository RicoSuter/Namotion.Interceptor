using System.Collections;
using System.Reflection;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Tests that lifecycle tracking remains consistent when multiple threads
/// concurrently write to the same structural property on a subject.
/// Verifies the fix for a race condition in LifecycleInterceptor.WriteProperty
/// where stale currentValue captures caused orphaned subjects in _attachedSubjects.
/// </summary>
public class ConcurrentWriteLifecycleTests
{
    [Fact]
    public void WhenConcurrentCollectionWrites_ThenOnlyFinalSubjectsAreTracked()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithRegistry()
            .WithContextInheritance();

        var root = new Person(context) { FirstName = "Root" };
        var initialChildren = Enumerable.Range(0, 5)
            .Select(i => new Person { FirstName = $"Initial{i}" })
            .ToArray();
        root.Children = initialChildren;

        // Verify initial state
        foreach (var child in initialChildren)
        {
            Assert.Equal(1, child.GetReferenceCount());
        }

        // Act: Two threads concurrently set Children to different arrays.
        // Use a barrier to maximize the chance of overlap.
        var barrier = new Barrier(2);
        const int iterations = 200;

        var allCreatedByA = new List<Person>();
        var allCreatedByB = new List<Person>();

        var threadA = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var children = new[]
                {
                    new Person { FirstName = $"A{i}_0" },
                    new Person { FirstName = $"A{i}_1" },
                    new Person { FirstName = $"A{i}_2" }
                };

                allCreatedByA.AddRange(children);
                if (i == 0) barrier.SignalAndWait();
                root.Children = children;
            }
        });

        var threadB = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var children = new[]
                {
                    new Person { FirstName = $"B{i}_0" },
                    new Person { FirstName = $"B{i}_1" }
                };

                allCreatedByB.AddRange(children);
                if (i == 0) barrier.SignalAndWait();
                root.Children = children;
            }
        });

        threadA.Start();
        threadB.Start();
        threadA.Join();
        threadB.Join();

        // Assert: All subjects currently in Children must have ref count = 1.
        var winningSet = new HashSet<Person>(root.Children);
        foreach (var child in winningSet)
        {
            Assert.Equal(1, child.GetReferenceCount());
        }

        // Every subject NOT in the winning set (initial + all created by both threads)
        // must have ref count 0 — no orphaned subjects.
        foreach (var child in initialChildren.Concat(allCreatedByA).Concat(allCreatedByB))
        {
            if (!winningSet.Contains(child))
            {
                Assert.Equal(0, child.GetReferenceCount());
            }
        }

        // Verify registry only contains root + winning children (no orphaned subjects)
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Equal(1 + winningSet.Count, registry.KnownSubjects.Count);
    }

    [Fact]
    public void WhenConcurrentObjectRefWrites_ThenOnlyFinalSubjectIsTracked()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithRegistry()
            .WithContextInheritance();

        var root = new Person(context) { FirstName = "Root" };
        var initialFather = new Person { FirstName = "InitialFather" };
        root.Father = initialFather;
        Assert.Equal(1, initialFather.GetReferenceCount());

        // Act: Two threads concurrently set Father to different subjects.
        var barrier = new Barrier(2);
        const int iterations = 200;

        var allCreatedByA = new List<Person>();
        var allCreatedByB = new List<Person>();

        var threadA = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var father = new Person { FirstName = $"FatherA{i}" };
                allCreatedByA.Add(father);
                if (i == 0) barrier.SignalAndWait();
                root.Father = father;
            }
        });

        var threadB = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var father = new Person { FirstName = $"FatherB{i}" };
                allCreatedByB.Add(father);
                if (i == 0) barrier.SignalAndWait();
                root.Father = father;
            }
        });

        threadA.Start();
        threadB.Start();
        threadA.Join();
        threadB.Join();

        // Assert: The final Father must have ref count = 1.
        var finalFather = root.Father;
        Assert.NotNull(finalFather);
        Assert.Equal(1, finalFather.GetReferenceCount());

        // Every subject NOT currently referenced (initial + all created by both threads)
        // must have ref count 0 — no orphaned subjects.
        foreach (var father in allCreatedByA.Concat(allCreatedByB).Append(initialFather))
        {
            if (!ReferenceEquals(father, finalFather))
            {
                Assert.Equal(0, father.GetReferenceCount());
            }
        }

        // Verify registry only contains root + final father (no orphaned subjects)
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Equal(2, registry.KnownSubjects.Count);
    }

    [Fact]
    public void WhenManyThreadsSetCollectionRepeatedly_ThenNoOrphanedSubjectsRemain()
    {
        // Stress test: multiple threads, many iterations, verify no orphaned subjects.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithRegistry()
            .WithContextInheritance();

        var root = new Person(context) { FirstName = "Root" };

        const int threadCount = 4;
        const int iterations = 100;
        var barrier = new Barrier(threadCount);
        var allCreatedSubjects = new List<Person>[threadCount];
        var threads = new Thread[threadCount];

        for (var t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            allCreatedSubjects[threadIndex] = [];

            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < iterations; i++)
                {
                    var children = new[]
                    {
                        new Person { FirstName = $"T{threadIndex}_I{i}_0" },
                        new Person { FirstName = $"T{threadIndex}_I{i}_1" }
                    };

                    lock (allCreatedSubjects[threadIndex])
                    {
                        allCreatedSubjects[threadIndex].AddRange(children);
                    }

                    root.Children = children;
                }
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        // Assert: Only subjects currently in Children should have ref count > 0.
        var currentChildren = new HashSet<Person>(root.Children);

        foreach (var subjectList in allCreatedSubjects)
        {
            foreach (var subject in subjectList)
            {
                if (currentChildren.Contains(subject))
                {
                    Assert.Equal(1, subject.GetReferenceCount());
                }
                else
                {
                    Assert.Equal(0, subject.GetReferenceCount());
                }
            }
        }

        // Verify registry only contains root + current children (no orphaned subjects)
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Equal(1 + currentChildren.Count, registry.KnownSubjects.Count);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task WhenContextDetachRacesADescendantWrite_ThenDetachClearsAndReattachUsesTheBackingGeneration()
    {
        // Walking the descendant's newer backing value during detach would strand the old membership,
        // while committing the parked writer after detach would leak the replacement into lifecycle state.
        // Arrange
        var initial = new Person { FirstName = "Initial" };
        var replacement = new Person { FirstName = "Replacement" };
        var replacementGeneration = new[] { replacement };
        using var writeGate = new AfterCommitWriteGate(
            nameof(Person.Children),
            replacementGeneration);
        var context = InterceptorSubjectContext.Create();
        context.AddService<IWriteInterceptor>(writeGate);
        context.WithContextInheritance();

        var child = new Person
        {
            FirstName = "Child",
            Children = [initial]
        };
        var root = new Person
        {
            FirstName = "Root",
            Children = [child]
        };
        var rootContext = ((IInterceptorSubject)root).Context;
        Assert.True(rootContext.AddFallbackContext(context));
        var lifecycle = context.GetService<LifecycleInterceptor>();

        // Act: park the descendant write after backing commit and let context detach serialize first.
        var writer = Task.Factory.StartNew(
            () => child.Children = replacementGeneration,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        await AsyncTestHelpers.WaitUntilAsync(
            () => writeGate.BackingCommitted.IsSet || writer.IsCompleted,
            message: "The descendant write should commit before context detach.");
        Assert.True(writeGate.BackingCommitted.IsSet);
        Assert.False(writer.IsCompleted);

        Assert.True(rootContext.RemoveFallbackContext(context));
        writeGate.Release.Set();
        await writer;

        // Assert: no attached or processed state survives the detach, and the backing write remains authoritative.
        Assert.Same(replacementGeneration, child.Children);
        Assert.Equal(0, root.GetReferenceCount());
        Assert.Equal(0, child.GetReferenceCount());
        Assert.Equal(0, initial.GetReferenceCount());
        Assert.Equal(0, replacement.GetReferenceCount());
        AssertLifecycleStorageEmpty(lifecycle);

        // Act: reattach must enumerate the backing graph and recreate exactly its memberships.
        Assert.True(rootContext.AddFallbackContext(context));

        // Assert
        Assert.Same(child, Assert.Single(root.Children));
        Assert.Same(replacement, Assert.Single(child.Children));
        Assert.Equal(0, root.GetReferenceCount());
        Assert.Equal(1, child.GetReferenceCount());
        Assert.Equal(0, initial.GetReferenceCount());
        Assert.Equal(1, replacement.GetReferenceCount());

        Assert.True(rootContext.RemoveFallbackContext(context));
        AssertLifecycleStorageEmpty(lifecycle);
        Assert.Equal(0, child.GetReferenceCount());
        Assert.Equal(0, replacement.GetReferenceCount());
    }

    private static void AssertLifecycleStorageEmpty(LifecycleInterceptor lifecycle)
    {
        // Callers reach this helper only after the worker and the synchronous detach have completed.
        Assert.Empty(GetPrivateDictionary(lifecycle, "_attachedSubjects"));
        Assert.Empty(GetPrivateDictionary(lifecycle, "_processedProperties"));
    }

    private static IDictionary GetPrivateDictionary(LifecycleInterceptor lifecycle, string fieldName)
    {
        var field = typeof(LifecycleInterceptor).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsAssignableFrom<IDictionary>(field?.GetValue(lifecycle));
    }

    [RunsAfter(typeof(LifecycleInterceptor))]
    private sealed class AfterCommitWriteGate(string propertyName, object expectedValue)
        : IWriteInterceptor, IDisposable
    {
        public ManualResetEventSlim BackingCommitted { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            if (context.Property.Name != propertyName || !ReferenceEquals(context.NewValue, expectedValue))
            {
                return;
            }

            // ManualResetEventSlim owns publication between the writer and test thread.
            BackingCommitted.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The committed descendant write was not released.");
            }
        }

        public void Dispose()
        {
            BackingCommitted.Dispose();
            Release.Dispose();
        }
    }
}
