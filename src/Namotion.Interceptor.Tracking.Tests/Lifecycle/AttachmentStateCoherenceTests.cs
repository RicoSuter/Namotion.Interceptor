using System.Diagnostics;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// <c>IsAnchoredRoot</c> reads the anchor alone and documents that a non-None anchor implies an
/// attached context. The executor publishes the two as separate volatile stores, context first and
/// anchor second, so a lock-free reader can land between them.
/// </summary>
public class AttachmentStateCoherenceTests
{
    private static readonly TimeSpan HammerDuration = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reproduces the finding that the attachment context and the anchor are not one coherent
    /// state. Reproduces naturally: nothing holds a window open, the reader simply observes the
    /// pair while a second thread transitions it. The window is two adjacent stores wide, so this
    /// hammers for a bounded time rather than a fixed iteration count.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAttachmentIsTransitionedConcurrently_ThenIsAnchoredRootNeverOutlivesTheContext()
    {
        // Arrange: a lifecycle-free context, so a detach is exactly the pair of stores under test
        // with no topology work in between.
        var context = InterceptorSubjectContext.Create();
        var subject = (IInterceptorSubject)new Person { FirstName = "S" };

        var stop = 0;
        var incoherentObservations = 0;
        var transitions = 0;
        var reads = 0;

        var reader = new Thread(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                if (subject.IsAnchoredRoot() && subject.TryGetContext() is null)
                {
                    Interlocked.Increment(ref incoherentObservations);
                }

                Interlocked.Increment(ref reads);
            }
        })
        {
            IsBackground = true
        };

        var transitioner = new Thread(() =>
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < HammerDuration && Volatile.Read(ref incoherentObservations) == 0)
            {
                subject.AttachToContext(context);
                subject.DetachFromContext(context);
                Interlocked.Increment(ref transitions);
            }

            Volatile.Write(ref stop, 1);
        })
        {
            IsBackground = true
        };

        // Act
        reader.Start();
        transitioner.Start();
        var transitionerCompleted = transitioner.Join(HammerDuration + TimeSpan.FromSeconds(20));
        Volatile.Write(ref stop, 1);
        var readerCompleted = reader.Join(TimeSpan.FromSeconds(20));

        // Assert: both threads did real work, so a green result cannot come from an idle run.
        Assert.True(transitionerCompleted && readerCompleted, "a hammer thread never finished");
        Assert.True(Volatile.Read(ref transitions) > 0, "no attachment transition ran");
        Assert.True(Volatile.Read(ref reads) > 0, "no attachment read ran");

        // The documented invariant of IsAnchoredRoot: a non-None anchor implies an attached context.
        Assert.True(Volatile.Read(ref incoherentObservations) == 0,
            $"observed {Volatile.Read(ref incoherentObservations)} attachment states with a non-None " +
            $"anchor and no context in {Volatile.Read(ref transitions)} transitions and " +
            $"{Volatile.Read(ref reads)} reads: the context and the anchor are published as two " +
            "separate stores, so a lock-free reader can land between them");
    }
}
