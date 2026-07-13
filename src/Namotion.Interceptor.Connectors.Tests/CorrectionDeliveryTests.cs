using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Pins PR 2 correction delivery in <see cref="ChangeQueueProcessor"/>: corrections bypass the
/// own-source dequeue skip, normal changes beat corrections in flush dedup regardless of order,
/// immediate mode drops corrections, and buffered corrections are revalidated before and after each
/// write (with a bounded post-write follow-up loop).
/// </summary>
public class CorrectionDeliveryTests
{
    // === Delivery routing (dequeue-time own-source skip) ===

    [Fact]
    public async Task WhenCorrectionOriginSourceEqualsProcessorSource_ThenCorrectionIsStillDelivered()
    {
        // Arrange: a correction records which source diverged, not a delivery target. Even when the
        // processor's own source equals the correction's source, it must be delivered (the owner
        // writes the authoritative value back). This is the test the bypass exists for.
        var context = CreateContext();
        var source = new object();
        var device = new ClampingDevice(context);
        device.Value = 100;

        var (written, gate) = CreateCollector();
        using var processor = CreateProcessor(source, context, written, gate);

        // Act: the source sends 105; the hook clamps to the stored 100, which the equality check
        // suppresses while it diverges from the sent 105, so a Correction(source) is synthesized.
        var delivered = await DriveAndCollectAsync(processor, context, written, gate,
            () => new PropertyReference(device, nameof(ClampingDevice.Value)).SetValueFromSource(source, null, null, 105));

        // Assert: the correction was delivered despite origin source == processor source.
        var corrections = delivered
            .Where(c => ReferenceEquals(c.Property.Subject, device) && c.Origin.Kind == ChangeOriginKind.Correction)
            .ToList();
        Assert.Single(corrections);
        Assert.Same(source, corrections[0].Origin.Source);
        Assert.Equal(100, corrections[0].GetNewValue<int>());
    }

    [Fact]
    public async Task WhenCorrectionOriginSourceDiffersFromProcessorSource_ThenCorrectionIsDelivered()
    {
        // Arrange: the WebSocket shape. The origin source is a per-connection object; the processor
        // identifies as a different handler object. Corrections must not be dropped by identity
        // mismatch.
        var context = CreateContext();
        var connection = new object();
        var handler = new object();
        var device = new ClampingDevice(context);
        device.Value = 100;

        var (written, gate) = CreateCollector();
        using var processor = CreateProcessor(handler, context, written, gate);

        // Act
        var delivered = await DriveAndCollectAsync(processor, context, written, gate,
            () => new PropertyReference(device, nameof(ClampingDevice.Value)).SetValueFromSource(connection, null, null, 105));

        // Assert
        var corrections = delivered
            .Where(c => ReferenceEquals(c.Property.Subject, device) && c.Origin.Kind == ChangeOriginKind.Correction)
            .ToList();
        Assert.Single(corrections);
        Assert.Same(connection, corrections[0].Origin.Source);
    }

    [Fact]
    public async Task WhenFromSourceOriginEqualsProcessorSource_ThenChangeIsSkipped()
    {
        // Arrange: a normal inbound value that survives is FromSource(source) and keeps today's echo
        // suppression: it is skipped for its own source's processor.
        var context = CreateContext();
        var source = new object();
        var device = new ClampingDevice(context);
        device.Value = 0;

        var (written, gate) = CreateCollector();
        using var processor = CreateProcessor(source, context, written, gate);

        // Act: 60 survives (no clamp, differs from 0), so it publishes as FromSource(source).
        var delivered = await DriveAndCollectAsync(processor, context, written, gate,
            () => new PropertyReference(device, nameof(ClampingDevice.Value)).SetValueFromSource(source, null, null, 60));

        // Assert: the FromSource(source) change is skipped by its own source's processor.
        Assert.DoesNotContain(delivered, c => ReferenceEquals(c.Property.Subject, device));
    }

    [Fact]
    public async Task WhenFromSourceOriginDiffersFromProcessorSource_ThenChangeIsDelivered()
    {
        // Arrange
        var context = CreateContext();
        var source = new object();
        var otherSource = new object();
        var device = new ClampingDevice(context);
        device.Value = 0;

        var (written, gate) = CreateCollector();
        using var processor = CreateProcessor(otherSource, context, written, gate);

        // Act
        var delivered = await DriveAndCollectAsync(processor, context, written, gate,
            () => new PropertyReference(device, nameof(ClampingDevice.Value)).SetValueFromSource(source, null, null, 60));

        // Assert: a FromSource(source) change is delivered to every other bound source's processor.
        var changes = delivered.Where(c => ReferenceEquals(c.Property.Subject, device)).ToList();
        Assert.Single(changes);
        Assert.Equal(ChangeOriginKind.FromSource, changes[0].Origin.Kind);
        Assert.Equal(60, changes[0].GetNewValue<int>());
    }

    [Fact]
    public async Task WhenCorrectionIsDelivered_ThenItCarriesTheFreshWriteTimestampMetadata()
    {
        // Arrange
        var context = CreateContext();
        var source = new object();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));
        var inboundTimestamp = DateTimeOffset.UtcNow.AddHours(-1);

        var (written, gate) = CreateCollector();
        using var processor = CreateProcessor(source, context, written, gate);

        // Act
        var delivered = await DriveAndCollectAsync(processor, context, written, gate,
            () => property.SetValueFromSource(source, inboundTimestamp, null, 105));

        // Assert: the delivered correction carries the fresh local timestamp synthesis stamped on the
        // property's write-timestamp metadata, not the inbound scope timestamp.
        var correction = Assert.Single(delivered,
            c => ReferenceEquals(c.Property.Subject, device) && c.Origin.Kind == ChangeOriginKind.Correction);
        Assert.NotEqual(inboundTimestamp, correction.ChangedTimestamp);
        Assert.Equal(property.TryGetWriteTimestamp(), correction.ChangedTimestamp);
    }

    [Fact]
    public async Task WhenCorrectionIsSynthesized_ThenItNeverReachesThePropertyChangeObservable()
    {
        // Arrange: a correction is not a model change, so app-level observable subscribers never see it.
        var context = CreateContext();
        var source = new object();
        var device = new ClampingDevice(context);
        device.Value = 100;

        var observed = new ConcurrentQueue<SubjectPropertyChange>();
        using var observableSubscription = context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(observed.Enqueue);

        using var queueSubscription = context.CreatePropertyChangeQueueSubscription();

        // Act: divergence synthesizes a Correction(source).
        new PropertyReference(device, nameof(ClampingDevice.Value)).SetValueFromSource(source, null, null, 105);

        // A sentinel model change on a fresh subject flows through both the queue and the observable.
        var sentinel = new ClampingDevice(context);
        sentinel.Value = 7;
        await AsyncTestHelpers.WaitUntilAsync(
            () => observed.Any(c => ReferenceEquals(c.Property.Subject, sentinel)),
            timeout: TimeSpan.FromSeconds(10));

        // Assert: the queue saw the correction, the observable did not.
        var queued = DrainQueue(queueSubscription, sentinel);
        Assert.Contains(queued, c => ReferenceEquals(c.Property.Subject, device) && c.Origin.Kind == ChangeOriginKind.Correction);
        Assert.DoesNotContain(observed, c => ReferenceEquals(c.Property.Subject, device));
    }

    // === Flush dedup (normal beats correction regardless of queue order) ===

    [Fact]
    public async Task WhenCorrectionThenNormalForSameProperty_ThenNormalWinsWithOwnOldValue()
    {
        // Arrange
        var context = CreateContext();
        var source = new object();
        var device = new ClampingDevice(context);
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));

        var (written, gate) = CreateCollector();
        using var processor = CreateBufferedProcessor(context, written, gate);

        // Act: a correction (old == new == 100) enqueued before a normal change (old 105, new 110).
        InjectChange(processor, Correction(property, source, 100));
        InjectChange(processor, Normal(property, oldValue: 105, newValue: 110));
        await TriggerFlushAsync(processor);

        // Assert: the normal change wins and keeps its own old value as the diff baseline.
        var change = Assert.Single(written);
        Assert.Equal(ChangeOriginKind.Local, change.Origin.Kind);
        Assert.Equal(105, change.GetOldValue<int>());
        Assert.Equal(110, change.GetNewValue<int>());
    }

    [Fact]
    public async Task WhenNormalThenCorrectionForSameProperty_ThenNormalWins()
    {
        // Arrange
        var context = CreateContext();
        var source = new object();
        var device = new ClampingDevice(context);
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));

        var (written, gate) = CreateCollector();
        using var processor = CreateBufferedProcessor(context, written, gate);

        // Act: a normal change enqueued before a correction. A correction never replaces a queued
        // normal change.
        InjectChange(processor, Normal(property, oldValue: 105, newValue: 110));
        InjectChange(processor, Correction(property, source, 110));
        await TriggerFlushAsync(processor);

        // Assert
        var change = Assert.Single(written);
        Assert.Equal(ChangeOriginKind.Local, change.Origin.Kind);
        Assert.Equal(105, change.GetOldValue<int>());
        Assert.Equal(110, change.GetNewValue<int>());
    }

    [Fact]
    public async Task WhenTwoCorrectionsForSameProperty_ThenTheyCoalesceIntoOne()
    {
        // Arrange: the model already holds the corrected value, so both send-time revalidations pass.
        var context = CreateContext();
        var source = new object();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));

        var (written, gate) = CreateCollector();
        using var processor = CreateBufferedProcessor(context, written, gate);

        // Act: two corrections for one property carry the same value by construction.
        InjectChange(processor, Correction(property, source, 100));
        InjectChange(processor, Correction(property, source, 100));
        await TriggerFlushAsync(processor);

        // Assert: they coalesce into a single delivered correction.
        var change = Assert.Single(written);
        Assert.Equal(ChangeOriginKind.Correction, change.Origin.Kind);
        Assert.Equal(100, change.GetNewValue<int>());
    }

    // === Immediate-mode drop (buffered-only correction delivery) ===

    [Fact]
    public async Task WhenImmediateMode_ThenCorrectionIsDroppedWithWarningButNormalIsWritten()
    {
        // Arrange: an immediate-mode processor (bufferTime <= 0) has no dedup, so a stale correction
        // could push the source to a wrong value. Corrections are dropped with a warning; a normal
        // change on the same processor is still written.
        var context = CreateContext();
        var source = new object();
        var correctionDevice = new ClampingDevice(context);
        correctionDevice.Value = 100;
        var normalDevice = new ClampingDevice(context);

        var logger = new CapturingLogger();
        var (written, gate) = CreateCollector();
        using var processor = new ChangeQueueProcessor(
            source: source,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) => Record(written, gate, changes),
            bufferTime: TimeSpan.Zero,
            maxQueueDepth: null,
            logger: logger);

        using var cts = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cts.Token);

        // Act: produce a Correction(source) via divergence, then a normal (Local) change.
        new PropertyReference(correctionDevice, nameof(ClampingDevice.Value)).SetValueFromSource(source, null, null, 105);
        normalDevice.Value = 42;

        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            lock (gate) return written.Any(c => ReferenceEquals(c.Property.Subject, normalDevice));
        }, timeout: TimeSpan.FromSeconds(10));

        await cts.CancelAsync();
        await processing;

        // Assert: the normal change was written, the correction was not, and a warning was logged.
        lock (gate)
        {
            Assert.Contains(written, c => ReferenceEquals(c.Property.Subject, normalDevice));
            Assert.DoesNotContain(written, c => ReferenceEquals(c.Property.Subject, correctionDevice));
        }
        Assert.True(logger.HasWarningContaining("correction"));
    }

    // === Send-time revalidation, pre-write drop ===

    [Fact]
    public async Task WhenModelMovedBeforeCorrectionWrite_ThenCorrectionIsDroppedByPreWriteRevalidation()
    {
        // Arrange: a stale correction (value 100) that survived synthesis and dedup, but the model has
        // since moved to 90 before the correction's write starts.
        var context = CreateContext();
        var source = new object();
        var staleDevice = new ClampingDevice(context);
        staleDevice.Value = 90; // model moved to 90; the getter no longer returns 100
        var normalDevice = new ClampingDevice(context);

        var staleProperty = new PropertyReference(staleDevice, nameof(ClampingDevice.Value));
        var normalProperty = new PropertyReference(normalDevice, nameof(ClampingDevice.Value));

        var (written, gate) = CreateCollector();
        using var processor = CreateBufferedProcessor(context, written, gate);

        // Act
        InjectChange(processor, Correction(staleProperty, source, 100));
        InjectChange(processor, Normal(normalProperty, oldValue: 0, newValue: 5));
        await TriggerFlushAsync(processor);

        // Assert: the stale correction is dropped (getter returns 90 != 100), the normal change is written.
        Assert.DoesNotContain(written, c => ReferenceEquals(c.Property.Subject, staleDevice));
        Assert.Contains(written, c => ReferenceEquals(c.Property.Subject, normalDevice));
    }

    // === Send-time revalidation, in-flight window (post-write follow-up) ===

    [Fact]
    public async Task WhenInboundApplyLandsDuringCorrectionWrite_ThenFollowUpWriteConvergesSource()
    {
        // Arrange: model at 100, an injected Correction(source, 100). The fake source write is held
        // open with a gate so an inbound apply (model -> 90) lands between the pre-write revalidation
        // and the completion of the correction write. This is the echo-suppressed inbound-from-S
        // window that pre-write revalidation alone cannot cover.
        var context = CreateContext();
        var source = new object();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));

        var sourceState = new ConcurrentDictionary<string, object?>();
        var firstWriteStarted = new ManualResetEventSlim(false);
        var inboundApplied = new ManualResetEventSlim(false);
        var correctionWrites = 0;

        using var processor = new ChangeQueueProcessor(
            source: source,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                var span = changes.Span;
                for (var i = 0; i < span.Length; i++)
                {
                    var change = span[i];
                    if (change.Origin.Kind == ChangeOriginKind.Correction &&
                        Interlocked.Increment(ref correctionWrites) == 1)
                    {
                        // Hold the first correction write open until the inbound apply lands.
                        firstWriteStarted.Set();
                        inboundApplied.Wait(TimeSpan.FromSeconds(10));
                    }

                    sourceState[change.Property.Name] = change.GetNewValue<object?>();
                }

                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(50),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        InjectChange(processor, Correction(property, source, 100));

        // Act: flush on a background task; the first correction write blocks inside the write handler.
        var flush = Task.Run(async () => await TriggerFlushAsync(processor));

        firstWriteStarted.Wait(TimeSpan.FromSeconds(10));

        // The inbound-from-source value lands while the correction write is held open.
        device.Value = 90;
        inboundApplied.Set();

        await flush.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: the post-write recheck wrote a follow-up correction converging the source to 90,
        // so no stale 100 remains on the source.
        Assert.True(sourceState.TryGetValue(nameof(ClampingDevice.Value), out var finalValue));
        Assert.Equal(90, finalValue);
        Assert.True(correctionWrites >= 2, "A follow-up correction write should have converged the source.");
    }

    // === Send-time revalidation, bounded-loop exhaustion ===

    [Fact]
    public async Task WhenModelKeepsMovingDuringPostWriteRecheck_ThenCorrectionIsDroppedWithResyncWarning()
    {
        // Arrange: the model keeps moving on every correction write, so the post-write recheck never
        // converges and the bounded loop exhausts its attempt limit.
        var context = CreateContext();
        var source = new object();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));

        var logger = new CapturingLogger();
        var correctionWrites = 0;

        using var processor = new ChangeQueueProcessor(
            source: source,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                var span = changes.Span;
                for (var i = 0; i < span.Length; i++)
                {
                    if (span[i].Origin.Kind == ChangeOriginKind.Correction)
                    {
                        var count = Interlocked.Increment(ref correctionWrites);
                        // Move the model to a distinct value below the clamp on every write so the
                        // post-write recheck always mismatches.
                        device.Value = 100 - count;
                    }
                }

                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(50),
            maxQueueDepth: null,
            logger: logger);

        InjectChange(processor, Correction(property, source, 100));

        // Act
        await TriggerFlushAsync(processor);

        // Assert: the loop terminated (bounded), and a warning naming RequestResynchronization/#342
        // was logged.
        Assert.True(correctionWrites >= 2, "The bounded loop should have retried at least once.");
        Assert.True(correctionWrites <= 32, "The bounded loop must terminate.");
        Assert.True(logger.HasWarningContaining("RequestResynchronization"));
        Assert.True(logger.HasWarningContaining("342"));
    }

    // === Helpers ===

    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

    private static (List<SubjectPropertyChange> Written, object Gate) CreateCollector() => ([], new object());

    private static ChangeQueueProcessor CreateProcessor(
        object? source, IInterceptorSubjectContext context, List<SubjectPropertyChange> written, object gate) =>
        new(
            source: source,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) => Record(written, gate, changes),
            bufferTime: TimeSpan.FromMilliseconds(15),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

    private static ChangeQueueProcessor CreateBufferedProcessor(
        IInterceptorSubjectContext context, List<SubjectPropertyChange> written, object gate) =>
        new(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) => Record(written, gate, changes),
            bufferTime: TimeSpan.FromMinutes(10), // large so only the explicit TriggerFlush drains the queue
            maxQueueDepth: null,
            logger: NullLogger.Instance);

    private static ValueTask Record(List<SubjectPropertyChange> written, object gate, ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        lock (gate)
        {
            written.AddRange(changes.ToArray());
        }

        return ValueTask.CompletedTask;
    }

    private static async Task<List<SubjectPropertyChange>> DriveAndCollectAsync(
        ChangeQueueProcessor processor,
        IInterceptorSubjectContext context,
        List<SubjectPropertyChange> written,
        object gate,
        Action produce)
    {
        using var cts = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cts.Token);

        produce();

        // A fresh subject's local write always survives and is delivered by a non-null-source
        // processor. Because the subscription is FIFO, when the sentinel lands the produced change
        // has already been dequeued (delivered or skipped), regardless of flush batch boundaries.
        var sentinel = new ClampingDevice(context);
        sentinel.Value = 7;

        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            lock (gate) return written.Any(c => ReferenceEquals(c.Property.Subject, sentinel));
        }, timeout: TimeSpan.FromSeconds(10));

        await cts.CancelAsync();
        await processing;

        lock (gate)
        {
            return written.Where(c => !ReferenceEquals(c.Property.Subject, sentinel)).ToList();
        }
    }

    private static List<SubjectPropertyChange> DrainQueue(PropertyChangeQueueSubscription subscription, IInterceptorSubject sentinel)
    {
        var changes = new List<SubjectPropertyChange>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (subscription.TryDequeue(out var change, timeout.Token))
        {
            if (ReferenceEquals(change.Property.Subject, sentinel))
            {
                return changes;
            }

            changes.Add(change);
        }

        throw new TimeoutException("Sentinel change was not received within 10 seconds.");
    }

    private static SubjectPropertyChange Correction(PropertyReference property, object source, int value) =>
        SubjectPropertyChange.Create(property, ChangeOrigin.Correction(source), DateTimeOffset.UtcNow, null, value, value);

    private static SubjectPropertyChange Normal(PropertyReference property, int oldValue, int newValue) =>
        SubjectPropertyChange.Create(property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, oldValue, newValue);

    private static void InjectChange(ChangeQueueProcessor processor, SubjectPropertyChange change)
    {
        var changesField = typeof(ChangeQueueProcessor)
            .GetField("_changes", BindingFlags.NonPublic | BindingFlags.Instance);
        var queue = (ConcurrentQueue<SubjectPropertyChange>)changesField!.GetValue(processor)!;
        queue.Enqueue(change);
    }

    private static async Task TriggerFlushAsync(ChangeQueueProcessor processor)
    {
        var tryFlushMethod = typeof(ChangeQueueProcessor)
            .GetMethod("TryFlushAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (ValueTask)tryFlushMethod!.Invoke(processor, [CancellationToken.None])!;
        await task;
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];
        private readonly object _gate = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }

        public bool HasWarningContaining(string substring)
        {
            lock (_gate)
            {
                return _entries.Any(e =>
                    e.Level == LogLevel.Warning &&
                    e.Message.Contains(substring, StringComparison.OrdinalIgnoreCase));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
