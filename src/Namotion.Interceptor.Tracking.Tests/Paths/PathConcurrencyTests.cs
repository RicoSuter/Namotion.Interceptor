using System;
using System.Collections.Generic;
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
}
