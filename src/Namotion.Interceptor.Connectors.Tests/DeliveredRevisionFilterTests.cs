using System.Runtime.CompilerServices;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class DeliveredRevisionFilterTests
{
    [Fact]
    public void WhenACommitWasAlreadyDelivered_ThenAnOlderCommitForThatPropertyIsSuppressed()
    {
        // Arrange: the cross-flush inversion. A writer preempted between committing and enqueuing can
        // land revision 8 in the batch after the one that carried revision 10.
        var tracker = new DeliveredRevisionFilter();
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act
        var newer = tracker.TryAdmit(CreateChange(property, revision: 10));
        var older = tracker.TryAdmit(CreateChange(property, revision: 8));

        // Assert
        Assert.True(newer);
        Assert.False(older);
    }

    [Fact]
    public void WhenANewerCommitArrives_ThenItIsDeliveredAndBecomesTheNewBaseline()
    {
        // Arrange
        var tracker = new DeliveredRevisionFilter();
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act
        tracker.TryAdmit(CreateChange(property, revision: 10));

        // Assert
        Assert.True(tracker.TryAdmit(CreateChange(property, revision: 12)));
        Assert.False(tracker.TryAdmit(CreateChange(property, revision: 12)));
        Assert.False(tracker.TryAdmit(CreateChange(property, revision: 11)));
    }

    [Fact]
    public void WhenAChangeHasNoRevision_ThenItIsNeverSuppressedAndNeverBecomesABaseline()
    {
        // Arrange: revision 0 orders against nothing, so nothing can establish it as superseded, and
        // it must not suppress the real commits that follow it.
        var tracker = new DeliveredRevisionFilter();
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act & Assert
        Assert.True(tracker.TryAdmit(CreateChange(property, revision: 0)));
        Assert.True(tracker.TryAdmit(CreateChange(property, revision: 0)));
        Assert.True(tracker.TryAdmit(CreateChange(property, revision: 5)));
        Assert.True(tracker.TryAdmit(CreateChange(property, revision: 0)));
    }

    [Fact]
    public void WhenPropertiesDiffer_ThenTheirBaselinesAreIndependent()
    {
        // Arrange: revisions are per subject, so two properties of one subject interleave. Suppressing
        // across properties would drop legitimate changes.
        var tracker = new DeliveredRevisionFilter();
        var subject = new Person();
        var first = new PropertyReference(subject, nameof(Person.FirstName));
        var last = new PropertyReference(subject, nameof(Person.LastName));
        var otherSubject = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act
        tracker.TryAdmit(CreateChange(first, revision: 10));

        // Assert
        Assert.True(tracker.TryAdmit(CreateChange(last, revision: 8)));
        Assert.True(tracker.TryAdmit(CreateChange(otherSubject, revision: 8)));
    }

    [Fact]
    public void WhenASubjectIsCollected_ThenItsDeliveryStateGoesWithIt()
    {
        // Arrange: the state lives in the subject's own property data rather than in a map owned by the
        // filter, so nothing has to evict it. A filter-side map keyed by PropertyReference would hold
        // these subjects strongly for as long as the processor lived.
        var filter = new DeliveredRevisionFilter();
        var abandoned = RecordAndAbandon(filter);

        // Act
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert
        Assert.All(abandoned, subject => Assert.False(subject.IsAlive,
            "the filter must not keep a subject alive after everything else has dropped it"));
    }

    [Fact]
    public void WhenAnEchoIsRecorded_ThenAnOlderLocalCommitIsSuppressed()
    {
        // Arrange: the source pushed a value in, so it already holds it. A local commit that predates
        // that echo must not be written back over it.
        var filter = new DeliveredRevisionFilter();
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act
        filter.RecordDelivered(CreateChange(property, revision: 20));

        // Assert
        Assert.False(filter.TryAdmit(CreateChange(property, revision: 18)));
        Assert.True(filter.TryAdmit(CreateChange(property, revision: 21)));
    }

    [Fact]
    public void WhenTheSourceHoldsTheNewestValue_ThenNoWriteBackIsNeeded()
    {
        // Arrange: nothing this processor wrote is newer, so the source still holds what it was given.
        var filter = new DeliveredRevisionFilter();
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act
        filter.RecordDelivered(CreateChange(property, revision: 10));

        // Assert
        Assert.False(filter.WasWrittenOut(property));
    }

    [Fact]
    public void WhenThisProcessorWroteTheProperty_ThenAWriteBackIsNeeded()
    {
        // Arrange: our write may have landed on the source after a transaction's, so a later
        // confirmation has to be sent out to restore the confirmed value.
        var filter = new DeliveredRevisionFilter();
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act
        Assert.True(filter.TryAdmit(CreateChange(property, revision: 10)));

        // Assert
        Assert.True(filter.WasWrittenOut(property));
    }

    [Fact]
    public void WhenAConfirmationIsWrittenBack_ThenTheNextConfirmationIsAlsoWrittenBack()
    {
        // Arrange: a written-back confirmation is an ordinary write on the wire and can itself land on
        // the source after a later transaction's direct write, so it must keep asking for the next
        // repair. Clearing here is the chain that loses a committed transaction: repair for T1 delayed,
        // T2 writes the source, T1's repair lands last, T2's confirmation sees no write-out and is
        // skipped, and the source keeps T1's value for good.
        var filter = new DeliveredRevisionFilter();
        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        filter.TryAdmit(CreateChange(property, revision: 10));
        Assert.True(filter.WasWrittenOut(property));

        // Act
        var confirmation = SubjectPropertyChange.Create(
            property, ChangeOrigin.Confirmed(new object()), DateTimeOffset.UnixEpoch, null, "old", "new", 11L);
        Assert.True(filter.TryAdmit(confirmation));

        // Assert
        Assert.True(filter.WasWrittenOut(property));
    }

    [Fact]
    public void WhenAnEchoFollowsOurOwnWrite_ThenAConfirmationIsStillWrittenBack()
    {
        // Arrange: an echo proves the source emitted a value, not that our write has landed. A write
        // still in flight can overtake a transaction's direct write, so the written-out bit must survive
        // the echo or the confirmation that would repair the overwrite is skipped.
        var filter = new DeliveredRevisionFilter();
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));

        Assert.True(filter.TryAdmit(CreateChange(property, revision: 10)));

        // Act: the source pushes a value while our write may still be in flight.
        filter.RecordDelivered(CreateChange(property, revision: 11));

        // Assert
        Assert.True(filter.WasWrittenOut(property));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RecordAndAbandon(DeliveredRevisionFilter filter)
    {
        var abandoned = new WeakReference[500];
        for (var index = 0; index < abandoned.Length; index++)
        {
            var subject = new Person();
            abandoned[index] = new WeakReference(subject);
            filter.TryAdmit(CreateChange(new PropertyReference(subject, nameof(Person.FirstName)), index + 1));
        }

        return abandoned;
    }


    [Fact]
    public void WhenTwoSourcesServeOneProperty_ThenTheirBaselinesAreIndependent()
    {
        // Arrange: a model exposed over two protocols gives one property two processors. Sharing a
        // baseline would let the first one's delivery suppress the second's, so its clients would
        // silently never receive the value.
        var first = new DeliveredRevisionFilter(new object());
        var second = new DeliveredRevisionFilter(new object());
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act
        var deliveredByFirst = first.TryAdmit(CreateChange(property, revision: 10));
        var deliveredBySecond = second.TryAdmit(CreateChange(property, revision: 10));

        // Assert: both deliver the newest commit, and both still suppress an older one of their own.
        Assert.True(deliveredByFirst);
        Assert.True(deliveredBySecond);
        Assert.False(first.TryAdmit(CreateChange(property, revision: 9)));
        Assert.False(second.TryAdmit(CreateChange(property, revision: 9)));
    }

    [Fact]
    public void WhenTwoThreadsAdmitOneProperty_ThenEachRevisionIsAdmittedAtMostOnce()
    {
        // Arrange: the dequeue thread and the flush task consult the same property with no lock between
        // them, so the compare-and-exchange is the only thing keeping the baseline monotonic. Admitting
        // one revision twice would write the same value out twice; losing one would drop a delivery.
        const int revisions = 20_000;
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));
        var filter = new DeliveredRevisionFilter(new object());
        var admittedBy = new int[revisions + 1];

        var barrier = new Barrier(2);
        Exception? failure = null;

        void Admit(int worker)
        {
            try
            {
                barrier.SignalAndWait();
                for (var revision = 1; revision <= revisions; revision++)
                {
                    if (filter.TryAdmit(CreateChange(property, revision)))
                    {
                        Interlocked.Increment(ref admittedBy[revision]);
                    }
                }
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref failure, exception, null);
            }
        }

        var one = new Thread(() => Admit(1));
        var two = new Thread(() => Admit(2));

        // Act
        one.Start();
        two.Start();
        one.Join();
        two.Join();

        // Assert: a revision is admitted at most once, and the baseline ends on the newest.
        Assert.Null(failure);
        Assert.All(admittedBy.AsEnumerable().Skip(1), count => Assert.True(count <= 1,
            "a revision was admitted by both threads, so the same value would be written out twice"));
        Assert.False(filter.TryAdmit(CreateChange(property, revisions)));
    }

    private static SubjectPropertyChange CreateChange(PropertyReference property, long revision) =>
        SubjectPropertyChange.Create(property, ChangeOrigin.Local, DateTimeOffset.UnixEpoch, null, "old", "new", revision);

    [Fact]
    public void WhenTwoSourcesFirstTouchOnePropertyConcurrently_ThenNeitherSlotIsLost()
    {
        // Arrange: adding a source swaps a copy-on-write array. A plain assignment in place of the
        // compare-and-exchange drops whichever add loses the race, and the loser's baseline silently
        // never takes effect, so its superseded commits start being written out.
        const int rounds = 20_000;
        var properties = new PropertyReference[rounds];
        for (var index = 0; index < rounds; index++)
        {
            properties[index] = new PropertyReference(new Person(), nameof(Person.FirstName));
        }

        var first = new DeliveredRevisionFilter(new object());
        var second = new DeliveredRevisionFilter(new object());
        var barrier = new Barrier(2);
        Exception? failure = null;

        void Admit(DeliveredRevisionFilter filter)
        {
            try
            {
                barrier.SignalAndWait();
                for (var index = 0; index < rounds; index++)
                {
                    filter.TryAdmit(CreateChange(properties[index], revision: 10));
                }
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref failure, exception, null);
            }
        }

        var one = new Thread(() => Admit(first));
        var two = new Thread(() => Admit(second));

        // Act
        one.Start();
        two.Start();
        one.Join();
        two.Join();

        // Assert: both sources kept a slot on every property, so each still suppresses its own older
        // commit. A lost slot shows up as that source admitting revision 9.
        Assert.Null(failure);
        for (var index = 0; index < rounds; index++)
        {
            Assert.False(first.TryAdmit(CreateChange(properties[index], revision: 9)),
                $"the first source lost its slot on property {index}");
            Assert.False(second.TryAdmit(CreateChange(properties[index], revision: 9)),
                $"the second source lost its slot on property {index}");
        }
    }

    [Fact]
    public void WhenAFilterIsReleased_ThenTheSubjectStopsHoldingItsSource()
    {
        // Arrange: a slot holds its source, and the subject holds the slot, so a connector rebuilt
        // against a live graph would stay reachable from that graph forever. HomeBlaze rebuilds an OPC UA
        // server on every configuration save, so this is a real shape rather than a hypothetical one.
        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));
        var abandoned = RecordAndRelease(property, release: true);

        // Act
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert
        Assert.All(abandoned, source => Assert.False(source.IsAlive,
            "a released connector must not stay reachable from the subject it delivered to"));
    }

    [Fact]
    public void WhenAFilterIsReleased_ThenAnotherSourceKeepsItsBaseline()
    {
        // Arrange: release must take out this source's slot only, or a connector still running would
        // start admitting the commits it had already delivered.
        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));
        var leaving = new DeliveredRevisionFilter(new object());
        var staying = new DeliveredRevisionFilter(new object());

        Assert.True(leaving.TryAdmit(CreateChange(property, revision: 10)));
        Assert.True(staying.TryAdmit(CreateChange(property, revision: 10)));

        // Act
        leaving.Release([subject]);

        // Assert
        Assert.False(staying.TryAdmit(CreateChange(property, revision: 9)));
    }

    [Fact]
    public void WhenReleaseRacesAFirstTouch_ThenNoSlotIsStranded()
    {
        // Arrange: TryAdvance checks the released flag and then publishes a slot. A release running
        // entirely inside that window sweeps before the slot exists, and nothing later removes it: a
        // new processor for the same source finds the slot pre-existing and never learns it owns the
        // leak. The publication is fenced against the flag, so either the sweep sees the slot or the
        // touching thread sees the release and takes its own slot back out.
        const int rounds = 200;
        const int propertiesPerRound = 200;

        for (var round = 0; round < rounds; round++)
        {
            var source = new object();
            var filter = new DeliveredRevisionFilter(source);
            var subjects = new IInterceptorSubject[propertiesPerRound];
            var properties = new PropertyReference[propertiesPerRound];
            for (var index = 0; index < propertiesPerRound; index++)
            {
                var subject = new Person();
                subjects[index] = subject;
                properties[index] = new PropertyReference(subject, nameof(Person.FirstName));
            }

            using var barrier = new Barrier(2);
            var toucher = new Thread(() =>
            {
                // ReSharper disable once AccessToDisposedClosure
                barrier.SignalAndWait();
                for (var index = 0; index < propertiesPerRound; index++)
                {
                    filter.TryAdmit(CreateChange(properties[index], revision: 1));
                }
            });

            toucher.Start();
            barrier.SignalAndWait();
            filter.Release(subjects);
            toucher.Join();

            // Assert: no property still holds a slot for the released source.
            for (var index = 0; index < propertiesPerRound; index++)
            {
                if (properties[index].TryGetPropertyData("ni.drev", out var value)
                    && value is DeliveredRevisionSlots slots)
                {
                    Assert.False(slots.TryGetPacked(source, out _),
                        $"round {round}: property {index} kept a slot for the released source");
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RecordAndRelease(PropertyReference property, bool release)
    {
        var abandoned = new WeakReference[50];
        for (var index = 0; index < abandoned.Length; index++)
        {
            var source = new object();
            abandoned[index] = new WeakReference(source);

            var filter = new DeliveredRevisionFilter(source);
            filter.TryAdmit(CreateChange(property, revision: index + 1));

            if (release)
            {
                filter.Release([property.Subject]);
            }
        }

        return abandoned;
    }
}
