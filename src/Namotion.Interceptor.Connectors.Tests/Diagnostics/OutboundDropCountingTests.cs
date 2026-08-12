using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Diagnostics;

/// <summary>
/// Pins that every outbound path which discards a write reports it, and that the three connector
/// buffers report their depth and their bound.
/// </summary>
public class OutboundDropCountingTests
{
    private const int OverflowChangeCount = 4;

    [Fact]
    public void WhenTheRetryQueueOverflows_ThenTheDroppedWritesAreCounted()
    {
        // Arrange
        var metrics = new SourceMetrics();
        using var queue = new WriteRetryQueue(maxQueueSize: 2, NullLogger.Instance, metrics.OutboundRetries);
        var diagnostics = new SourceDiagnostics(metrics);

        // Act
        queue.Enqueue(CreateChanges(count: 5));

        // Assert
        Assert.Equal(3, diagnostics.OutboundRetries.TotalDropped);
    }

    [Fact]
    public async Task WhenAQueuedWriteHasNoSetter_ThenItIsCountedAsDropped()
    {
        // Arrange: a write parked on a derived (getter-only) property, which the reconcile can neither
        // restore nor recognize as already current.
        var context = InterceptorSubjectContext.Create().WithRegistry().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var source = new TestSubjectSource(person, context, NullLogger.Instance);

        // Pins that this exercises the no-setter branch: without the guard, a FullName missing from the
        // property table would throw out of the reconcile and be counted by the catch instead.
        Assert.Null(new PropertyReference(person, nameof(Person.FullName)).Metadata.SetValue);

        source.WriteRetryQueue!.Enqueue(new[]
        {
            CreateChange(person, nameof(Person.FullName), oldValue: "old", newValue: "never-current")
        });

        // Act
        await source.ReconcileRetryQueueAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, source.Diagnostics.OutboundRetries.TotalDropped);
    }

    [Fact]
    public async Task WhenReconcileThrowsForAChange_ThenItIsCountedAsDropped()
    {
        // Arrange: a parked write whose restore throws, because the device rejects the value.
        var context = InterceptorSubjectContext.Create().WithRegistry().WithPropertyChangeSubscriptions();
        var device = new ThrowingDevice(context)
        {
            ThrowingEnabled = true,
            ShouldThrow = _ => true
        };

        using var source = new TestSubjectSource(device, context, NullLogger.Instance);

        // Pins that the restore is attempted at all, so the catch is reached through a throwing setter
        // rather than through the no-setter branch next to it.
        Assert.NotNull(new PropertyReference(device, nameof(ThrowingDevice.PropertyA)).Metadata.SetValue);

        source.WriteRetryQueue!.Enqueue(new[]
        {
            CreateChange(device, nameof(ThrowingDevice.PropertyA), oldValue: false, newValue: true)
        });

        // Act
        await source.ReconcileRetryQueueAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, source.Diagnostics.OutboundRetries.TotalDropped);
    }

    [Fact]
    public async Task WhenThereIsNoRetryQueueAndADirectWriteFails_ThenTheChangesAreCountedAsDropped()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithFullPropertyTracking();
        var person = new Person(context);

        var gate = new object();
        var failFirstName = false;
        var receivedWrites = new List<string>();

        using var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8), writeRetryQueueSize: 0)
        {
            WriteChangesOverride = (changes, _) =>
            {
                lock (gate)
                {
                    SubjectPropertyChange[] failed = failFirstName
                        ? changes.ToArray().Where(change => change.Property.Name == nameof(Person.FirstName)).ToArray()
                        : [];

                    foreach (var change in changes.ToArray())
                    {
                        if (change.Property.Name != nameof(Person.FirstName) || failed.Length == 0)
                        {
                            receivedWrites.Add(change.Property.Name);
                        }
                    }

                    return failed.Length == 0
                        ? ValueTask.FromResult(WriteResult.Success)
                        : ValueTask.FromResult(WriteResult.Failure(failed, new InvalidOperationException("boom")));
                }
            }
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            // The probe is re-written on each poll because writes captured before the connected phase
            // are drained rather than written, so a single write proves nothing about the pump.
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                person.LastName = "Probe" + probeValue++;
                lock (gate)
                {
                    return receivedWrites.Contains(nameof(Person.LastName));
                }
            }, message: "The pump did not reach the connected phase.");

            // Act
            lock (gate)
            {
                failFirstName = true;
            }

            person.FirstName = "John";
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.TotalDropped > 0,
                message: "The discarded direct write was not counted.");

            // Assert
            Assert.Equal(1, source.Diagnostics.OutboundRetries.TotalDropped);
            Assert.Equal(0, source.Diagnostics.OutboundRetries.Capacity);
            Assert.Equal(0, source.Diagnostics.OutboundRetries.Depth);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A graceful stop that catches a write in flight is not a loss the operator can act on, and
    /// <c>WriteChangesInBatchesAsync</c> reports that cancellation as a failed result rather than
    /// throwing it. Counted, the drop counter would jump at every restart until an operator learns to
    /// ignore it.
    /// </summary>
    [Fact]
    public async Task WhenAWriteIsCancelledByTheStop_ThenItIsNotCountedAsDropped()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithFullPropertyTracking();
        var person = new Person(context);

        using var writeStarted = new ManualResetEventSlim(false);
        using var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8), writeRetryQueueSize: 0)
        {
            WriteChangesOverride = async (_, cancellationToken) =>
            {
                writeStarted.Set();

                // Held until the stop cancels the token, so the write is in flight when it lands.
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return WriteResult.Success;
            }
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);

        // The probe is re-written on each poll because writes captured before the connected phase are
        // drained rather than written.
        var probeValue = 0;
        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            person.FirstName = "v" + probeValue++;
            return writeStarted.IsSet;
        }, message: "The pump never reached a write.");

        // Act
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, source.Diagnostics.OutboundRetries.TotalDropped);
    }

    [Fact]
    public void WhenTheDisabledQueueDrainRuns_ThenNothingIsCounted()
    {
        // Arrange: a source configured without a retry queue, and a change on a property this source
        // does not own, which the unfiltered drain discards.
        var context = InterceptorSubjectContext.Create().WithRegistry().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var source = new TestSubjectSource(person, context, NullLogger.Instance, writeRetryQueueSize: 0);
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        person.FirstName = "unowned";
        Assert.True(subscription.Count > 0, "The change was not captured, so the drain would be a no-op.");

        // Act
        source.DrainOwnedWritesToRetryQueue(subscription);

        // Assert
        Assert.Equal(0, subscription.Count);
        Assert.Equal(0, source.Diagnostics.OutboundRetries.TotalDropped);
    }

    [Fact]
    public void WhenABufferedLoadIsSuperseded_ThenTheDiscardedUpdatesAreCounted()
    {
        // Arrange
        var metrics = new SourceMetrics();
        var diagnostics = new SourceDiagnostics(metrics);
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        using var source = new TestSubjectSource(
            new Person(context), context, NullLogger.Instance, writeRetryQueueSize: 0);
        var writer = new SubjectPropertyWriter(source, NullLogger.Instance, metrics.InboundBuffer);

        writer.StartBuffering();
        writer.Write(0, static _ => { });
        writer.Write(1, static _ => { });
        Assert.Equal(2, diagnostics.InboundBuffer.Depth);

        // Act
        writer.StartBuffering();

        // Assert
        Assert.Equal(2, diagnostics.InboundBuffer.TotalDropped);
        Assert.Equal(0, diagnostics.InboundBuffer.Depth);
    }

    [Fact]
    public async Task WhenTheProcessorIsRecreated_ThenTheAccumulatedDropCountSurvives()
    {
        // Arrange: a bounded processor registered against the metrics, dropping into it, then handed
        // over. The in-repo connectors all pass maxQueueDepth: null, so this drives QueueMetrics and
        // ChangeQueueProcessor directly rather than through a source.
        var metrics = new SourceMetrics();
        var diagnostics = new SourceDiagnostics(metrics);
        var context = InterceptorSubjectContext.Create().WithRegistry().WithPropertyChangeSubscriptions();
        var person = new Person(context);

        var first = CreateBoundedProcessor(context, maxQueueDepth: 1);
        metrics.OutboundChanges.Register(() => first.QueueDepth, () => first.DropCount, capacity: 1);
        await OverflowAsync(first, person, tag: "one");
        var afterFirst = diagnostics.OutboundChanges.TotalDropped;

        // Act
        metrics.OutboundChanges.Deregister();
        first.Dispose();
        using var second = CreateBoundedProcessor(context, maxQueueDepth: 1);
        metrics.OutboundChanges.Register(() => second.QueueDepth, () => second.DropCount, capacity: 1);
        await OverflowAsync(second, person, tag: "two");

        // Assert
        Assert.True(afterFirst > 0);
        Assert.Equal(afterFirst * 2, diagnostics.OutboundChanges.TotalDropped);
    }

    [Fact]
    public async Task WhenASourceIsRunning_ThenItsOutboundChangeQueueIsRegisteredAsUnbounded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var source = CreateSourceWithStalledOutboundQueue(person, context);

        await source.StartAsync(CancellationToken.None);
        try
        {
            // Act
            await WaitForBufferedOutboundChangeAsync(source, person);

            // Assert
            // The depth comes first because it is the only read that tells a live registration from no
            // registration at all: an unregistered QueueMetrics reports a null capacity too, so the
            // capacity below only means "registered as unbounded" once the depth has proven the
            // registration is live.
            Assert.True(source.Diagnostics.OutboundChanges.Depth > 0);
            Assert.Null(source.Diagnostics.OutboundChanges.Capacity);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenARunningSourceStops_ThenItsOutboundChangeQueueIsDeregistered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var source = CreateSourceWithStalledOutboundQueue(person, context);

        await source.StartAsync(CancellationToken.None);
        await WaitForBufferedOutboundChangeAsync(source, person);

        // Act
        await source.StopAsync(CancellationToken.None);

        // Assert
        // Nothing drains the processor's queue on the way out, so its count is still non-zero. A
        // non-zero depth here would therefore mean the depth provider outlived the processor.
        Assert.Equal(0, source.Diagnostics.OutboundChanges.Depth);
    }

    [Fact]
    public async Task WhenWritesAreParkedInTheRetryQueue_ThenTheOutboundRetriesDepthReportsThem()
    {
        // Arrange: every write fails, so the connected phase parks the changes in the retry queue.
        var context = InterceptorSubjectContext.Create().WithRegistry().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8))
        {
            WriteChangesOverride = (changes, _) => ValueTask.FromResult(
                WriteResult.Failure(changes, new InvalidOperationException("boom")))
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            // Act
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                person.FirstName = "v" + probeValue++;
                return source.Diagnostics.OutboundRetries.Depth > 0;
            }, message: "Parked writes were not reported by the registered depth provider.");

            // Assert
            Assert.True(source.Diagnostics.OutboundRetries.Depth > 0);
            Assert.Equal(1000, source.Diagnostics.OutboundRetries.Capacity);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A source whose buffer time outlasts the test, so a change captured by the connected phase stays
    /// in the processor's queue instead of being flushed away before the depth can be read.
    /// </summary>
    private static TestSubjectSource CreateSourceWithStalledOutboundQueue(
        Person person, IInterceptorSubjectContext context)
    {
        var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMinutes(5));

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        return source;
    }

    private static Task WaitForBufferedOutboundChangeAsync(TestSubjectSource source, Person person)
    {
        // The probe is re-written on each poll because writes captured before the connected phase are
        // parked in the retry queue rather than buffered by the processor.
        var probeValue = 0;
        return AsyncTestHelpers.WaitUntilAsync(() =>
        {
            person.FirstName = "v" + probeValue++;
            return source.Diagnostics.OutboundChanges.Depth > 0;
        }, message: "The outbound change queue never reported a depth.");
    }

    private static ChangeQueueProcessor CreateBoundedProcessor(IInterceptorSubjectContext context, int maxQueueDepth)
    {
        // A buffer time far longer than the test, so nothing flushes the queue and every change past
        // the bound overflows deterministically. The source sentinel is a non-null object no change can
        // carry as its origin: with null, every local change would match the echo check and be skipped.
        return new ChangeQueueProcessor(
            source: new object(),
            context,
            _ => true,
            (_, _) => ValueTask.CompletedTask,
            ChangeDeliveryRule.SourceValuesMayBeStale,
            bufferTime: TimeSpan.FromMinutes(5),
            maxQueueDepth: maxQueueDepth,
            logger: NullLogger.Instance);
    }

    /// <summary>
    /// Commits <see cref="OverflowChangeCount"/> changes into a processor bounded at 1, so it drops all
    /// but the newest. Each change is on its own property and carries a value the model has not held
    /// before, so none supersedes another and the resulting drop count is exact rather than racy.
    /// </summary>
    private static async Task OverflowAsync(ChangeQueueProcessor processor, Person person, string tag)
    {
        using var cancellation = new CancellationTokenSource();

        // Started before the writes: ProcessAsync snapshots what was already queued and drops whatever
        // the model has moved past, so writes made first would not all reach the bounded queue.
        var processing = processor.ProcessAsync(cancellation.Token);

        person.FirstName = "a" + tag;
        person.LastName = "b" + tag;
        person.FirstName_MaxLength_Unit = "c" + tag;
        person.FirstName_MaxLength++;

        await AsyncTestHelpers.WaitUntilAsync(
            () => processor.DropCount >= OverflowChangeCount - 1,
            message: "The bounded queue did not overflow.");

        await cancellation.CancelAsync();
        await processing;
    }

    private static SubjectPropertyChange CreateChange<TValue>(
        IInterceptorSubject subject, string propertyName, TValue oldValue, TValue newValue)
    {
        // Boxed rather than typed, because the reconcile reads every parked change as object.
        return SubjectPropertyChange.Create<object?>(
            new PropertyReference(subject, propertyName),
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            null,
            oldValue,
            newValue);
    }

    private static ReadOnlyMemory<SubjectPropertyChange> CreateChanges(int count)
    {
        var subjectMock = new Mock<IInterceptorSubject>();
        var changes = new SubjectPropertyChange[count];
        for (var i = 0; i < count; i++)
        {
            changes[i] = CreateChange<object?>(subjectMock.Object, $"Property{i}", i, i + 1);
        }

        return changes;
    }
}
