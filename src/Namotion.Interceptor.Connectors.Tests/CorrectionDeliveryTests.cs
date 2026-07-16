using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Pins PR 2 correction delivery in <see cref="ChangeQueueProcessor"/>: corrections bypass the
/// own-source dequeue skip, normal changes beat corrections in flush dedup regardless of order,
/// both immediate and buffered modes deliver corrections through send-time revalidation, and each
/// correction is revalidated before and after the write (with a bounded post-write follow-up loop).
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
    public async Task WhenCorrectionIsDelivered_ThenItCarriesAFreshAssertionTimestampWithoutAdvancingMetadata()
    {
        // Arrange
        var context = CreateContext();
        var source = new object();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));
        var modelWriteTimestamp = property.TryGetWriteTimestamp();
        var inboundTimestamp = DateTimeOffset.UtcNow.AddHours(-1);
        var beforeAct = DateTimeOffset.UtcNow;

        var (written, gate) = CreateCollector();
        using var processor = CreateProcessor(source, context, written, gate);

        // Act
        var delivered = await DriveAndCollectAsync(processor, context, written, gate,
            () => property.SetValueFromSource(source, inboundTimestamp, null, 105));

        // Assert: the delivered correction carries a local send-time assertion timestamp, not the
        // inbound scope timestamp, while the property's write-timestamp metadata keeps the value's
        // real last-change time.
        var correction = Assert.Single(delivered,
            c => ReferenceEquals(c.Property.Subject, device) && c.Origin.Kind == ChangeOriginKind.Correction);
        Assert.NotEqual(inboundTimestamp, correction.ChangedTimestamp);
        Assert.True(correction.ChangedTimestamp >= beforeAct);
        Assert.Equal(modelWriteTimestamp, property.TryGetWriteTimestamp());
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
        var queued = ChangeQueueTestHelpers.DrainUntilSubject(queueSubscription, sentinel);
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

    [Fact]
    public async Task WhenFreshCorrectionSitsBehindStaleNormalInFlush_ThenBothAreDeliveredAndSourceConverges()
    {
        // Arrange: the reachable interleaving where dedup order lies about freshness. A local write
        // to v1 queues a normal change; an inbound from the source then stores v2 (echo-suppressed,
        // never queued); a suppressed diverging inbound synthesizes Correction(v2). The flush sees
        // [N(v1), C(v2)] with the model at v2: dropping the correction would leave the source at v1
        // until its next inbound event, so the displaced correction must instead be revalidated
        // (model == v2 -> delivered after the normal batch) and the source converges.
        var context = CreateContext();
        var source = new object();
        var device = new ClampingDevice(context);
        device.Value = 90; // the live model already holds the corrected value v2 = 90
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));

        var (written, gate) = CreateCollector();
        using var processor = CreateBufferedProcessor(context, written, gate);

        // Act: the stale normal (v1 = 50) queued before the fresh correction (v2 = 90).
        InjectChange(processor, Normal(property, oldValue: 0, newValue: 50));
        InjectChange(processor, Correction(property, source, 90));
        await TriggerFlushAsync(processor);

        // Assert: the normal batch goes first, then the revalidated correction converges the source.
        Assert.Equal(2, written.Count);
        Assert.Equal(ChangeOriginKind.Local, written[0].Origin.Kind);
        Assert.Equal(50, written[0].GetNewValue<int>());
        Assert.Equal(ChangeOriginKind.Correction, written[1].Origin.Kind);
        Assert.Equal(90, written[1].GetNewValue<int>());
    }

    [Fact]
    public async Task WhenStaleCorrectionPrecedesNormalInFlush_ThenRevalidationDropsIt()
    {
        // Arrange: the opposite order. The correction (100) was queued before a normal write moved
        // the model to 50; routing the displaced correction through revalidation must not resurrect
        // it (the model no longer holds 100), so only the normal change is delivered.
        var context = CreateContext();
        var source = new object();
        var device = new ClampingDevice(context);
        device.Value = 50;
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));

        var (written, gate) = CreateCollector();
        using var processor = CreateBufferedProcessor(context, written, gate);

        // Act
        InjectChange(processor, Correction(property, source, 100));
        InjectChange(processor, Normal(property, oldValue: 100, newValue: 50));
        await TriggerFlushAsync(processor);

        // Assert
        var change = Assert.Single(written);
        Assert.Equal(ChangeOriginKind.Local, change.Origin.Kind);
        Assert.Equal(50, change.GetNewValue<int>());
    }

    [Fact]
    public async Task WhenTwoCorrectionsCarryDifferentValues_ThenLaterCorrectionIsKeptWhole()
    {
        // Arrange: the model moved between two syntheses, so the queued corrections carry different
        // values. Dedup must keep the later correction whole: merging would graft the earlier
        // correction's old value onto the newer one and break the documented old == new invariant.
        var context = CreateContext();
        var source = new object();
        var device = new ClampingDevice(context);
        device.Value = 90; // the later correction (90) passes send-time revalidation
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));

        var (written, gate) = CreateCollector();
        using var processor = CreateBufferedProcessor(context, written, gate);

        // Act
        InjectChange(processor, Correction(property, source, 100));
        InjectChange(processor, Correction(property, source, 90));
        await TriggerFlushAsync(processor);

        // Assert: one delivered correction with old == new == 90.
        var change = Assert.Single(written);
        Assert.Equal(ChangeOriginKind.Correction, change.Origin.Kind);
        Assert.Equal(90, change.GetOldValue<int>());
        Assert.Equal(90, change.GetNewValue<int>());
    }

    [Fact]
    public async Task WhenBufferedFlushMixesCorrectionsAndNormals_ThenNormalBatchIsWrittenBeforeIndividualCorrections()
    {
        // Arrange: the correction is queued before two normal changes for other properties. Buffered
        // delivery preserves the normal write as one batch and sends corrections individually after it.
        var context = CreateContext();
        var source = new object();
        var correctionDevice = new ClampingDevice(context);
        correctionDevice.Value = 100;
        var firstNormalDevice = new ClampingDevice(context);
        var secondNormalDevice = new ClampingDevice(context);
        var batches = new List<SubjectPropertyChange[]>();

        using var processor = new ChangeQueueProcessor(
            source: source,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                batches.Add(changes.ToArray());
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMinutes(10),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        // Act
        InjectChange(processor, Correction(
            new PropertyReference(correctionDevice, nameof(ClampingDevice.Value)), source, 100));
        InjectChange(processor, Normal(
            new PropertyReference(firstNormalDevice, nameof(ClampingDevice.Value)), oldValue: 0, newValue: 1));
        InjectChange(processor, Normal(
            new PropertyReference(secondNormalDevice, nameof(ClampingDevice.Value)), oldValue: 0, newValue: 2));
        await TriggerFlushAsync(processor);

        // Assert
        Assert.Equal(2, batches.Count);
        Assert.Equal(2, batches[0].Length);
        Assert.All(batches[0], change => Assert.Equal(ChangeOriginKind.Local, change.Origin.Kind));
        var correction = Assert.Single(batches[1]);
        Assert.Equal(ChangeOriginKind.Correction, correction.Origin.Kind);
        Assert.Same(correctionDevice, correction.Property.Subject);
    }

    // === Immediate-mode delivery (send-time revalidation, no dedup) ===

    [Fact]
    public async Task WhenImmediateMode_ThenCorrectionIsDeliveredViaRevalidation()
    {
        // Arrange: an immediate-mode processor (bufferTime <= 0). Dedup is absent here, but dedup was
        // never the safety mechanism for corrections: send-time revalidation is, and it works in
        // immediate mode too. The correction (model still holds 100) is revalidated and delivered; a
        // normal change on the same processor is also written.
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

        // Assert: both the normal change and the revalidated correction were written, no drop warning.
        lock (gate)
        {
            Assert.Contains(written, c => ReferenceEquals(c.Property.Subject, normalDevice));
            var correction = Assert.Single(written,
                c => ReferenceEquals(c.Property.Subject, correctionDevice)
                     && c.Origin.Kind == ChangeOriginKind.Correction);
            Assert.Equal(100, correction.GetNewValue<int>());
        }
        Assert.False(logger.HasWarningContaining("Dropping correction"));
    }

    [Fact]
    public async Task WhenRevalidationGetterThrowsInImmediateMode_ThenCorrectionIsDroppedAndProcessorSurvives()
    {
        // Arrange: a read interceptor that throws for the correction device's property once armed. In
        // immediate mode the revalidation runs directly in the dequeue loop, so an escaping getter
        // exception would terminate the processor; it must instead drop the correction, log an error,
        // and keep delivering subsequent changes.
        var thrower = new ArmedThrowingReadInterceptor(nameof(ClampingDevice.Value));
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => thrower)
            .WithFullPropertyTracking();

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

        // Produce the correction while the interceptor is unarmed (synthesis reads the getter), then
        // arm it BEFORE the processor starts consuming, so the revalidation read is the one that throws.
        new PropertyReference(correctionDevice, nameof(ClampingDevice.Value)).SetValueFromSource(source, null, null, 105);
        thrower.Arm();

        using var cts = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cts.Token);

        // Act: a subsequent normal change must still be delivered by the surviving loop. The write
        // targets a different subject, so the armed thrower never sees it.
        normalDevice.Value = 42;

        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            lock (gate) return written.Any(c => ReferenceEquals(c.Property.Subject, normalDevice));
        }, timeout: TimeSpan.FromSeconds(10));

        await cts.CancelAsync();
        await processing;

        // Assert: the correction was dropped with an error log, the normal change was delivered.
        lock (gate)
        {
            Assert.DoesNotContain(written, c => ReferenceEquals(c.Property.Subject, correctionDevice));
            Assert.Contains(written, c => ReferenceEquals(c.Property.Subject, normalDevice));
        }
        Assert.True(logger.HasErrorContaining("dropping the correction"));
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

    [Fact]
    public async Task WhenDeliveryRevalidatesUnderActiveTransaction_ThenItReadsCommittedStateNotThePendingOverlay()
    {
        // Arrange: committed model 100 and a queued Correction(100). A correction asserts committed
        // state; the processing loop normally carries no transaction, but here the flush runs on a flow
        // that owns one holding a pending overlay of 70. If revalidation read the overlay it would see
        // 70 != 100 and drop a valid correction, so the read must detach the transaction and see the
        // committed 100 (symmetric with synthesis).
        var context = InterceptorSubjectContext
            .Create()
            .WithTransactions()
            .WithFullPropertyTracking();
        var source = new object();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));

        var (written, gate) = CreateCollector();
        using var processor = CreateBufferedProcessor(context, written, gate);
        InjectChange(processor, Correction(property, source, 100));

        // Act: flush on a flow that owns an active transaction holding a pending 70.
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            device.Value = 70; // captured as the transaction's pending overlay
            await TriggerFlushAsync(processor);
        }

        // Assert: the correction was delivered (revalidation compared committed 100, not overlay 70).
        var change = Assert.Single(written);
        Assert.Equal(ChangeOriginKind.Correction, change.Origin.Kind);
        Assert.Equal(100, change.GetNewValue<int>());
    }

    // === Send-time revalidation, in-flight window (post-write follow-up) ===

    [Fact]
    public async Task WhenInboundApplyLandsDuringCorrectionWrite_ThenFollowUpWriteConvergesSource()
    {
        // Arrange (the PR 2 release-gate scenario): model at 100; a real inbound 105 is clamped
        // onto the stored 100 and synthesizes a Correction(source). The fake source write is held
        // open with a gate so an inbound FromSource(source) apply (model -> 90) lands between the
        // pre-write revalidation and the completion of the correction write. That inbound change is
        // echo-suppressed by this processor's own dequeue-time skip, so NO normal write can ever
        // repair the source afterwards; only the post-write recheck's follow-up correction can.
        // The live ProcessAsync path is used so the echo suppression actually runs.
        var context = CreateContext();
        var source = new object();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));
        var beforeAct = DateTimeOffset.UtcNow;

        var sourceState = new ConcurrentDictionary<string, object?>();
        var deliveredCorrections = new ConcurrentQueue<SubjectPropertyChange>();
        var deliveredDeviceNonCorrections = new ConcurrentQueue<SubjectPropertyChange>();
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
                    if (change.Origin.Kind == ChangeOriginKind.Correction)
                    {
                        deliveredCorrections.Enqueue(change);
                        if (Interlocked.Increment(ref correctionWrites) == 1)
                        {
                            // Hold the first correction write open until the inbound apply lands.
                            firstWriteStarted.Set();
                            inboundApplied.Wait(TimeSpan.FromSeconds(10));
                        }
                    }
                    else if (ReferenceEquals(change.Property.Subject, device))
                    {
                        deliveredDeviceNonCorrections.Enqueue(change);
                    }

                    sourceState[change.Property.Name] = change.GetNewValue<object?>();
                }

                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(20),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cts.Token);

        // Act: the source sends 105; the clamp projects it onto the stored 100, the write is
        // suppressed, and a Correction(source) is synthesized. The live processor dequeues it
        // (corrections bypass the own-source skip) and blocks inside the held-open write.
        property.SetValueFromSource(source, null, null, 105);
        firstWriteStarted.Wait(TimeSpan.FromSeconds(10));

        // The inbound-from-source value lands while the correction write is held open. It is stored
        // as FromSource(source) and echo-suppressed on this processor's dequeue path.
        property.SetValueFromSource(source, null, null, 90);
        inboundApplied.Set();

        await AsyncTestHelpers.WaitUntilAsync(
            () => Equals(sourceState.GetValueOrDefault(nameof(ClampingDevice.Value)), 90),
            timeout: TimeSpan.FromSeconds(10),
            message: "The follow-up correction should have converged the source to 90.");

        await cts.CancelAsync();
        await processing;

        // Assert: the post-write recheck wrote a follow-up correction converging the source to 90,
        // and the inbound FromSource(source) change was echo-suppressed, so no normal write backed
        // the follow-up up: the correction loop alone repaired the source.
        Assert.Equal(90, sourceState[nameof(ClampingDevice.Value)]);
        Assert.True(correctionWrites >= 2, "A follow-up correction write should have converged the source.");
        Assert.Empty(deliveredDeviceNonCorrections);

        // Every attempt is stamped at send time: each delivered stamp is at or after the act, rather
        // than inheriting a synthesis-time or model timestamp. No ordering between attempts is asserted
        // (the contract makes no monotonic guarantee; the clock may return equal or decreasing values).
        var corrections = deliveredCorrections.ToArray();
        Assert.Equal(100, corrections.First().GetNewValue<int>());
        Assert.Equal(90, corrections.Last().GetNewValue<int>());
        Assert.All(corrections, correction => Assert.True(correction.ChangedTimestamp >= beforeAct));
    }

    [Fact]
    public async Task WhenCorrectionValueCyclesBackDuringRevalidation_ThenEveryAttemptIsStampedAtSendTime()
    {
        // Arrange: drive the bounded loop through 100 -> 90 -> 100. The final value equals the
        // original correction again; reusing the queued stamp would describe the last assertion by
        // the time the first one was synthesized, so every attempt carries its own send-time stamp.
        var context = CreateContext();
        var source = new object();
        var device = new ClampingDevice(context);
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));
        var initialTimestamp = DateTimeOffset.UtcNow.AddMinutes(-3);
        var intermediateTimestamp = initialTimestamp.AddMinutes(1);
        var finalTimestamp = initialTimestamp.AddMinutes(2);
        var staleCorrectionTimestamp = initialTimestamp.AddMinutes(-1);

        using (SubjectChangeContext.WithChangedTimestamp(initialTimestamp))
        {
            device.Value = 100;
        }

        var deliveredCorrections = new List<SubjectPropertyChange>();
        var correctionWrites = 0;
        using var processor = new ChangeQueueProcessor(
            source: source,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                var correction = Assert.Single(changes.ToArray());
                deliveredCorrections.Add(correction);

                switch (++correctionWrites)
                {
                    case 1:
                        using (SubjectChangeContext.WithChangedTimestamp(intermediateTimestamp))
                        {
                            device.Value = 90;
                        }
                        break;
                    case 2:
                        using (SubjectChangeContext.WithChangedTimestamp(finalTimestamp))
                        {
                            device.Value = 100;
                        }
                        break;
                }

                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMinutes(10),
            maxQueueDepth: null,
            logger: NullLogger.Instance);

        InjectChange(processor, Correction(property, source, 100, staleCorrectionTimestamp));

        // Act
        await TriggerFlushAsync(processor);

        // Assert: all three values were delivered, each stamped at send time rather than reusing the
        // stale queued stamp or a model timestamp, so every delivered timestamp is newer than every
        // (past) model write and the original correction stamp. No ordering between attempts is
        // asserted: the contract provides no monotonic guarantee.
        Assert.Equal(3, deliveredCorrections.Count);
        Assert.Equal([100, 90, 100], deliveredCorrections.Select(change => change.GetNewValue<int>()));
        Assert.All(deliveredCorrections, change => Assert.True(change.ChangedTimestamp > finalTimestamp));
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

    private static SubjectPropertyChange Correction(
        PropertyReference property, object source, int value, DateTimeOffset? changedTimestamp = null) =>
        SubjectPropertyChange.Create(
            property, ChangeOrigin.Correction(source), changedTimestamp ?? DateTimeOffset.UtcNow, null, value, value);

    /// <summary>Throws on chain reads of the named property once armed; unarmed reads pass through.</summary>
    private sealed class ArmedThrowingReadInterceptor : IReadInterceptor
    {
        private readonly string _propertyName;
        private volatile bool _armed;

        public ArmedThrowingReadInterceptor(string propertyName) => _propertyName = propertyName;

        public void Arm() => _armed = true;

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
        {
            if (_armed && context.Property.Name == _propertyName)
            {
                throw new InvalidOperationException("Getter failure injected by test.");
            }

            return next(ref context);
        }
    }

    private static SubjectPropertyChange Normal(PropertyReference property, int oldValue, int newValue) =>
        SubjectPropertyChange.Create(property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, oldValue, newValue);

    private static void InjectChange(ChangeQueueProcessor processor, SubjectPropertyChange change) =>
        processor.EnqueueForTesting(change);

    private static async Task TriggerFlushAsync(ChangeQueueProcessor processor) =>
        await processor.TryFlushAsync(CancellationToken.None);

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

        public bool HasWarningContaining(string substring) => HasEntryContaining(LogLevel.Warning, substring);

        public bool HasErrorContaining(string substring) => HasEntryContaining(LogLevel.Error, substring);

        private bool HasEntryContaining(LogLevel level, string substring)
        {
            lock (_gate)
            {
                return _entries.Any(e =>
                    e.Level == level &&
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
