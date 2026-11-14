using System.Diagnostics;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class SubjectLifecycleAttachmentsTests
{
    [Fact]
    public void WhenSubjectLeavesThreeGraphsOneByOne_ThenItStaysAttachedUntilItLeftTheLast()
    {
        // Arrange
        var roots = Enumerable.Range(0, 3)
            .Select(_ => new Person(InterceptorSubjectContext.Create().WithLifecycle()))
            .ToArray();
        var child = new Person();
        foreach (var root in roots)
        {
            root.Father = child;
        }

        var states = new List<bool> { SubjectLifecycleAttachments.IsAttached(child) };

        // Act
        roots[1].Father = null;
        states.Add(SubjectLifecycleAttachments.IsAttached(child));
        roots[0].Father = null;
        states.Add(SubjectLifecycleAttachments.IsAttached(child));
        roots[2].Father = null;
        states.Add(SubjectLifecycleAttachments.IsAttached(child));

        // Assert
        Assert.Equal([true, true, true, false], states);
    }

    [Fact]
    public void WhenSubjectIsAttachedAgainAfterItLeftEveryGraph_ThenItIsAttached()
    {
        // Arrange
        var first = new Person(InterceptorSubjectContext.Create().WithLifecycle());
        var second = new Person(InterceptorSubjectContext.Create().WithLifecycle());
        var child = new Person();
        first.Father = child;
        first.Father = null;
        var isAttachedAfterLeaving = SubjectLifecycleAttachments.IsAttached(child);

        // Act
        second.Father = child;

        // Assert
        Assert.False(isAttachedAfterLeaving);
        Assert.True(SubjectLifecycleAttachments.IsAttached(child));
    }

    [Fact]
    public void WhenInterceptorsAreEqualButNotTheSame_ThenRemovingOneKeepsTheOther()
    {
        // Arrange
        var first = new EqualLifecycleInterceptor();
        var second = new EqualLifecycleInterceptor();
        var child = new Person();
        SubjectLifecycleAttachments.Add(child, first);
        SubjectLifecycleAttachments.Add(child, second);
        SubjectLifecycleAttachments.Remove(child, first);

        // Act
        SubjectLifecycleAttachments.Remove(child, first);

        // Assert
        Assert.True(SubjectLifecycleAttachments.IsAttached(child));
    }

    [Fact]
    public async Task WhenSubjectIsAttachedToSecondGraphWhileItLeavesTheFirst_ThenItStaysAttached()
    {
        // Arrange
        var first = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking());
        var second = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking());
        var child = new Person { FirstName = "a" };
        var firstName = new PropertyReference(child, nameof(Person.FirstName));
        var fullName = new PropertyReference(child, nameof(Person.FullName));

        // Act
        await RaceAsync(
            arrange: () =>
            {
                first.Father = child;
                second.Father = null;
            },
            first: () => second.Father = child,
            second: () => first.Father = null,
            isConsistent: () => SubjectLifecycleAttachments.IsAttached(child) && firstName.GetUsedByProperties().Items.ToArray().Contains(fullName));

        // Assert
        Assert.True(SubjectLifecycleAttachments.IsAttached(child));
        Assert.Contains(fullName, firstName.GetUsedByProperties().Items.ToArray());
    }

    [Fact]
    public async Task WhenSubjectLeavesBothOfItsGraphsAtOnce_ThenItIsDetached()
    {
        // Arrange
        var first = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking());
        var second = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking());
        var child = new Person { FirstName = "a" };
        var firstName = new PropertyReference(child, nameof(Person.FirstName));
        var fullName = new PropertyReference(child, nameof(Person.FullName));

        // Act
        await RaceAsync(
            arrange: () =>
            {
                first.Father = child;
                second.Father = child;
            },
            first: () => first.Father = null,
            second: () => second.Father = null,
            isConsistent: () => !SubjectLifecycleAttachments.IsAttached(child) && !firstName.GetUsedByProperties().Items.ToArray().Contains(fullName));

        // Assert
        Assert.False(SubjectLifecycleAttachments.IsAttached(child));
        Assert.DoesNotContain(fullName, firstName.GetUsedByProperties().Items.ToArray());
    }

    // Runs the two actions on two threads after each arrange, until the state is inconsistent or the iterations are
    // done. The window between a failed compare-and-swap of the attachments and the next one is a few instructions
    // wide, and a thread blocked in a barrier wakes microseconds after the last one arrives. So both threads spin
    // until a start time set when the barrier releases them, and the start of the first thread moves by up to two
    // microseconds against the second from one iteration to the next, so that some iterations line the changes up.
    private static async Task RaceAsync(Action arrange, Action first, Action second, Func<bool> isConsistent)
    {
        const int iterations = 5_000;
        const int offsetCount = 41;
        const int offsetStepNanoseconds = 50;
        var startTimestamp = 0L;
        var isDone = false;
        using var start = new Barrier(2, _ => startTimestamp = Stopwatch.GetTimestamp() + ToTimestampTicks(20_000));
        using var end = new Barrier(2);

        void Start(int offsetNanoseconds)
        {
            Synchronize(start);
            var startAt = startTimestamp + ToTimestampTicks(offsetNanoseconds);
            while (Stopwatch.GetTimestamp() < startAt)
            {
                Thread.SpinWait(1);
            }
        }

        var firstThread = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            for (var i = 0; i < iterations && !isDone; i++)
            {
                arrange();
                Start(i % offsetCount * offsetStepNanoseconds);
                first();
                Synchronize(end);
                isDone = !isConsistent();
            }

            isDone = true;
            Synchronize(start);
        });
        var secondThread = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
        {
            while (true)
            {
                Start(offsetCount / 2 * offsetStepNanoseconds);
                if (isDone)
                {
                    return;
                }

                second();
                Synchronize(end);
            }
        });

        await Task.WhenAll(firstThread, secondThread);
    }

    private static long ToTimestampTicks(int nanoseconds) => Stopwatch.Frequency * nanoseconds / 1_000_000_000;

    private static void Synchronize(Barrier barrier)
    {
        if (!barrier.SignalAndWait(TestTimeouts.Default))
        {
            throw new TimeoutException("The other race participant did not arrive.");
        }
    }

    private sealed class EqualLifecycleInterceptor : LifecycleInterceptor
    {
        public override bool Equals(object? obj) => obj is EqualLifecycleInterceptor;

        public override int GetHashCode() => 0;
    }
}
