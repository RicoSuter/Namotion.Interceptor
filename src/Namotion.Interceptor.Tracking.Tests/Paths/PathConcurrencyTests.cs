using System;
using System.Collections.Generic;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Tests.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

[Collection(PerPropertySubscriptionCollection.Name)]
public class PathConcurrencyTests
{
    public PathConcurrencyTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenCallbackWritesAnotherWatchedSegment_ThenNestedEventDeliveredAfterCurrentCallbackAndTotallyOrdered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };

        // The callback records its own entry and exit around a single nested write to a watched segment.
        // Inline (re-entrant) delivery would interleave as enter:Jack, enter:Zed, exit:Zed, exit:Jack;
        // flattened delivery keeps each callback whole and runs the nested event after the current returns.
        var log = new List<string>();
        var nestedWriteDone = false;
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> change) =>
        {
            var value = change.New.GetValueOrDefault();
            log.Add("enter:" + value);
            if (!nestedWriteDone)
            {
                nestedWriteDone = true;
                father.FirstName = "Zed"; // nested write to the watched leaf during the active drain
            }

            log.Add("exit:" + value);
        });

        // Act
        father.FirstName = "Jack";

        // Assert: the nested event is flattened (delivered after the current callback returns, not inline)
        // and the total delivery order is the FIFO of the writes.
        Assert.Equal(new[] { "enter:Jack", "exit:Jack", "enter:Zed", "exit:Zed" }, log);
    }

    [Fact]
    public void WhenCallbackThrows_ThenQueuedBacklogStrandedAndDeliveredOnNextWriteWithChainedTransitions()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };

        var delivered = new List<SubjectPathChange<string?>>();
        var nestedWriteDone = false;
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> change) =>
        {
            delivered.Add(change);
            if (change.New.GetValueOrDefault() == "Jack")
            {
                if (!nestedWriteDone)
                {
                    nestedWriteDone = true;
                    father.FirstName = "Zed"; // enqueues Jack->Zed into the drain while this callback is active
                }

                throw new InvalidOperationException("boom");
            }
        });

        // Act & Assert: the throwing callback propagates through the triggering write and abandons the drain.
        Assert.Throws<InvalidOperationException>(() => father.FirstName = "Jack");

        // Only the first event was delivered; the nested Jack->Zed stays stranded in the queue.
        Assert.Single(delivered);
        Assert.Equal("Jack", delivered[0].New.GetValueOrDefault());

        // Act: a later write recovers the stranded backlog first, then delivers its own event.
        father.FirstName = "Max";

        // Assert: the stranded Jack->Zed drains first, then Zed->Max, with chained transitions (N+1.Old == N.New).
        Assert.Equal(3, delivered.Count);
        Assert.Equal("Jack", delivered[1].Old.GetValueOrDefault());
        Assert.Equal("Zed", delivered[1].New.GetValueOrDefault());
        Assert.Equal("Zed", delivered[2].Old.GetValueOrDefault());
        Assert.Equal("Max", delivered[2].New.GetValueOrDefault());
    }

    [Fact]
    public async Task WhenGetterWritesWatchedSegmentDuringWalk_ThenReentrancyIsDeferredAndDeliveredInTotalOrderWithoutDeadlock()
    {
        // Arrange: a two-segment path (x => x.Father!.FirstName) so both the intermediate and the leaf are
        // watched segments. A read interceptor performs, exactly once and only after arming, a single write
        // to the watched leaf DURING the leaf read of the tracker's own walk. That write dispatches
        // synchronously on the walking thread and re-enters ProcessSegmentCallback while the thread already
        // holds the subscription lock. Without the thread-affine guard the re-entrant call computes a nested
        // event under the held lock and corrupts the outer walk: the outer walk's already-captured leaf value
        // ("Jack") is delivered LAST, so the final delivered value disagrees with the settled graph
        // ("SideEffect"). With the guard the re-entrant write is deferred and delivered as a fresh, chained
        // event after the outer walk, keeping delivery totally ordered and quiescently consistent.
        var father = new Person { FirstName = "Joe" };
        var sideEffect = new SideEffectReadInterceptor(father, nameof(Person.FirstName), "SideEffect");
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => sideEffect, _ => false);
        var person = new Person(context) { Father = father };

        var deliveredLock = new object();
        var delivered = new List<SubjectPathChange<string?>>();
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> change) =>
        {
            lock (deliveredLock)
            {
                delivered.Add(change);
            }
        });

        // Act: arm the one-shot side effect, then trigger the outer walk with a watched-leaf write. The write
        // runs on a worker thread bounded by a timeout: a regression that reintroduces the AB-BA deadlock
        // makes WaitAsync throw TimeoutException and fails the test fast instead of hanging the whole suite.
        // On the happy path the write completes synchronously in microseconds.
        sideEffect.Arm();
        await Task.Run(() => father.FirstName = "Jack").WaitAsync(TimeSpan.FromSeconds(10));

        SubjectPathChange<string?>[] observed;
        lock (deliveredLock)
        {
            observed = delivered.ToArray();
        }

        // Assert
        // Quiescent consistency: once writes settle, the graph, the subscription's Current and the last
        // delivered New all agree on the side-effect value.
        Assert.Equal("SideEffect", father.FirstName);
        Assert.Equal("SideEffect", subscription.Current.GetValueOrDefault());
        Assert.NotEmpty(observed);
        Assert.Equal("SideEffect", observed[^1].New.GetValueOrDefault());

        // Total order: transitions are chained from the seed value (each event's Old is the prior event's
        // New), so the deferred side-effect event is delivered AFTER the triggering event, never inline.
        Assert.Equal("Joe", observed[0].Old.GetValueOrDefault());
        for (var i = 1; i < observed.Length; i++)
        {
            Assert.Equal(observed[i - 1].New.GetValueOrDefault(), observed[i].Old.GetValueOrDefault());
        }
    }

    /// <summary>
    /// A read interceptor that, exactly once and only after <see cref="Arm"/>, writes a watched leaf while it
    /// is being read. Registered so its write lands DURING the tracker's own event walk, re-entering the path
    /// subscription on the walking (lock-holding) thread. Reads before arming (the seed walk) and reads after
    /// firing (later re-walks, derived recalculations) do nothing, so the side effect fires exactly once.
    /// </summary>
    private sealed class SideEffectReadInterceptor : IReadInterceptor
    {
        private readonly IInterceptorSubject _target;
        private readonly string _triggerPropertyName;
        private readonly string? _sideEffectValue;
        private int _armed;
        private int _fired;

        public SideEffectReadInterceptor(Person target, string triggerPropertyName, string? sideEffectValue)
        {
            _target = target;
            _triggerPropertyName = triggerPropertyName;
            _sideEffectValue = sideEffectValue;
        }

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
        {
            var result = next(ref context);

            if (Volatile.Read(ref _armed) == 1
                && ReferenceEquals(context.Property.Subject, _target)
                && context.Property.Name == _triggerPropertyName
                && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                // Write the watched leaf mid-read so the write dispatches synchronously and re-enters the
                // path subscription on this same lock-holding thread. Setting the value AFTER next() returned
                // means the outer walk already captured the pre-side-effect value, so the deferred revalidation
                // has a genuinely distinct value to deliver.
                ((Person)_target).FirstName = _sideEffectValue;
            }

            return result;
        }
    }
}
