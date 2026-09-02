using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

[Collection(TerminalBoundaryCoordinatorCollection.Name)]
public class NormalizingSetterDerivedRaceTests
{
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenDirectAliasObservesFaithfulStoreBeforeGraphPublication_ThenItWaitsAndConverges()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithDerivedPropertyChangeDetection();
        var parent = new SubstitutingDevice();
        ((IInterceptorSubject)parent).AttachToContext(context);
        var child = new SubstitutingDevice();
        var projectionRead = new ManualResetEventSlim(false);
        var probe = new DerivedProjectionProbe
        {
            Projection = () =>
            {
                projectionRead.Set();
                return parent.ChildWithoutInterception;
            }
        };
        ((IInterceptorSubject)probe).AttachToContext(context);
        projectionRead.Reset();

        var rawStored = new ManualResetEventSlim(false);
        var releaseStore = new ManualResetEventSlim(false);
        parent.OnRawValueWritten = value =>
        {
            if (!ReferenceEquals(value, child))
            {
                return;
            }

            rawStored.Set();
            if (!releaseStore.Wait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                throw new TimeoutException("Timed out waiting to finish the faithful raw store.");
            }
        };

        Exception? writeException = null;
        var writer = new Thread(() => writeException = Record.Exception(() => parent.Child = child))
        {
            IsBackground = true
        };
        Exception? recalculationException = null;
        var recalculator = new Thread(
            () => recalculationException = Record.Exception(() => probe.Name = "trigger"))
        {
            IsBackground = true
        };

        // Act
        writer.Start();
        Assert.True(rawStored.Wait(WriteProtocolAcceptance.RendezvousTimeout));
        recalculator.Start();
        Assert.True(projectionRead.Wait(WriteProtocolAcceptance.RendezvousTimeout));
        releaseStore.Set();

        // Assert
        Assert.True(writer.Join(WriteProtocolAcceptance.RendezvousTimeout));
        Assert.True(recalculator.Join(WriteProtocolAcceptance.RendezvousTimeout));
        Assert.Null(writeException);
        Assert.Null(recalculationException);
        Assert.Same(context, child.TryGetContext());
        Assert.Same(child, probe.Projected);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenDirectAliasObservesTwoConsecutiveReservedValues_ThenItConvergesToTheLatest()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithDerivedPropertyChangeDetection();
        var parent = new SubstitutingDevice();
        ((IInterceptorSubject)parent).AttachToContext(context);
        var first = new SubstitutingDevice();
        var second = new SubstitutingDevice();
        var firstStored = new ManualResetEventSlim(false);
        var releaseFirst = new ManualResetEventSlim(false);
        var secondStored = new ManualResetEventSlim(false);
        var releaseSecond = new ManualResetEventSlim(false);
        parent.OnRawValueWritten = value =>
        {
            var stored = ReferenceEquals(value, first) ? firstStored : secondStored;
            var release = ReferenceEquals(value, first) ? releaseFirst : releaseSecond;
            stored.Set();
            if (!release.Wait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                throw new TimeoutException("Timed out waiting to finish a faithful raw store.");
            }
        };

        var armed = false;
        var evaluations = 0;
        var secondWriterStarted = 0;
        var firstObserved = new ManualResetEventSlim(false);
        Thread secondWriter = null!;
        var probe = new DerivedProjectionProbe
        {
            Projection = () =>
            {
                var value = parent.ChildWithoutInterception;
                if (!armed)
                {
                    return value;
                }

                Interlocked.Increment(ref evaluations);
                if (ReferenceEquals(value, first))
                {
                    firstObserved.Set();
                }

                if (ReferenceEquals(value, first) &&
                    ReferenceEquals(first.TryGetContext(), context) &&
                    Interlocked.CompareExchange(ref secondWriterStarted, 1, 0) == 0)
                {
                    secondWriter.Start();
                    if (!secondStored.Wait(WriteProtocolAcceptance.RendezvousTimeout))
                    {
                        throw new TimeoutException("The second writer did not reach its raw store.");
                    }

                    value = parent.ChildWithoutInterception;
                }

                return value;
            }
        };
        ((IInterceptorSubject)probe).AttachToContext(context);

        Exception? firstWriteException = null;
        var firstWriter = new Thread(() => firstWriteException = Record.Exception(() => parent.Child = first))
        {
            IsBackground = true
        };
        Exception? secondWriteException = null;
        secondWriter = new Thread(() => secondWriteException = Record.Exception(() => parent.Child = second))
        {
            IsBackground = true
        };
        Exception? recalculationException = null;
        var recalculator = new Thread(
            () => recalculationException = Record.Exception(() => probe.Name = "trigger"))
        {
            IsBackground = true
        };

        // Act
        firstWriter.Start();
        Assert.True(firstStored.Wait(WriteProtocolAcceptance.RendezvousTimeout));
        armed = true;
        recalculator.Start();
        Assert.True(firstObserved.Wait(WriteProtocolAcceptance.RendezvousTimeout));
        releaseFirst.Set();
        Assert.True(secondStored.Wait(WriteProtocolAcceptance.RendezvousTimeout));
        releaseSecond.Set();

        // Assert
        Assert.True(firstWriter.Join(WriteProtocolAcceptance.RendezvousTimeout));
        Assert.True(secondWriter.Join(WriteProtocolAcceptance.RendezvousTimeout));
        Assert.True(recalculator.Join(WriteProtocolAcceptance.RendezvousTimeout));
        Assert.Null(firstWriteException);
        Assert.Null(secondWriteException);
        Assert.Null(recalculationException);
        Assert.Same(context, second.TryGetContext());
        Assert.Null(first.TryGetContext());
        Assert.Same(second, probe.Projected);
        Assert.True(evaluations >= 2);
    }
}
