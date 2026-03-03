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

        Person[]? threadAFinalChildren = null;
        Person[]? threadBFinalChildren = null;

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

                if (i == 0) barrier.SignalAndWait();
                root.Children = children;
                threadAFinalChildren = children;
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

                if (i == 0) barrier.SignalAndWait();
                root.Children = children;
                threadBFinalChildren = children;
            }
        });

        threadA.Start();
        threadB.Start();
        threadA.Join();
        threadB.Join();

        // Assert: The property has one of the final arrays.
        // All subjects in the final array must have ref count = 1.
        // All subjects NOT in the final array must have ref count = 0.
        var finalChildren = root.Children;
        foreach (var child in finalChildren)
        {
            Assert.Equal(1, child.GetReferenceCount());
        }

        // The initial children should all have ref count 0 (properly detached)
        foreach (var child in initialChildren)
        {
            Assert.Equal(0, child.GetReferenceCount());
        }

        // Verify the non-winning final set also has ref count 0.
        // Note: threadAFinalChildren/threadBFinalChildren always hold the last iteration's array
        // for their respective thread. Since each iteration creates fresh Person objects, the
        // "losing" thread's final array contains subjects that were never the actual winner,
        // so checking ref count 0 on non-winning subjects is safe.
        var losingSet = ReferenceEquals(root.Children, threadAFinalChildren)
            ? threadBFinalChildren!
            : threadAFinalChildren!;

        // Only check subjects NOT in the winning set
        var winningSet = new HashSet<Person>(root.Children);
        foreach (var child in losingSet)
        {
            if (!winningSet.Contains(child))
            {
                Assert.Equal(0, child.GetReferenceCount());
            }
        }
    }

    [Fact]
    public void WhenConcurrentObjectRefWrites_ThenOnlyFinalSubjectIsTracked()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithContextInheritance();

        var root = new Person(context) { FirstName = "Root" };
        var initialFather = new Person { FirstName = "InitialFather" };
        root.Father = initialFather;
        Assert.Equal(1, initialFather.GetReferenceCount());

        // Act: Two threads concurrently set Father to different subjects.
        var barrier = new Barrier(2);
        const int iterations = 200;
        Person? threadAFinal = null;
        Person? threadBFinal = null;

        var threadA = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var father = new Person { FirstName = $"FatherA{i}" };
                if (i == 0) barrier.SignalAndWait();
                root.Father = father;
                threadAFinal = father;
            }
        });

        var threadB = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var father = new Person { FirstName = $"FatherB{i}" };
                if (i == 0) barrier.SignalAndWait();
                root.Father = father;
                threadBFinal = father;
            }
        });

        threadA.Start();
        threadB.Start();
        threadA.Join();
        threadB.Join();

        // Assert
        var finalFather = root.Father;
        Assert.NotNull(finalFather);
        Assert.Equal(1, finalFather.GetReferenceCount());
        Assert.Equal(0, initialFather.GetReferenceCount());

        // The non-winning final subject should have ref count 0
        var loser = ReferenceEquals(root.Father, threadAFinal) ? threadBFinal! : threadAFinal!;
        if (!ReferenceEquals(loser, root.Father))
        {
            Assert.Equal(0, loser.GetReferenceCount());
        }
    }

    [Fact]
    public void WhenManyThreadsSetCollectionRepeatedly_ThenNoOrphanedSubjectsRemain()
    {
        // Stress test: multiple threads, many iterations, verify no orphaned subjects.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
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
        var orphanCount = 0;

        foreach (var subjectList in allCreatedSubjects)
        {
            foreach (var subject in subjectList)
            {
                var refCount = subject.GetReferenceCount();
                if (currentChildren.Contains(subject))
                {
                    Assert.Equal(1, refCount);
                }
                else if (refCount > 0)
                {
                    orphanCount++;
                }
            }
        }

        Assert.Equal(0, orphanCount);
    }
}
