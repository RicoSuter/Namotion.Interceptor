using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Change;

/// <summary>
/// Pins the correction-detection contract folded into <c>SetValueFromOrigin</c>: an inbound
/// (<see cref="ChangeOriginKind.FromSource"/>) write whose projected value is equality-suppressed
/// yet differs from the value the source sent means the source silently dropped the model's value,
/// so a <see cref="ChangeOriginKind.Correction"/> change is synthesized to flow the authoritative
/// model value back. Detection never runs for local, confirmed, or transaction-captured writes.
/// </summary>
public class SourceCorrectionTests
{
    [Fact]
    public void WhenStoredValueChanges_ThenNormalChangeIsPublished()
    {
        // Arrange: model at 50.
        var context = CreateContext();
        var device = new ClampingDevice(context);
        device.Value = 50;
        var source = new object();

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: the source sends 105; the hook clamps to 100, so the stored value actually changes.
        new PropertyReference(device, nameof(ClampingDevice.Value))
            .SetValueFromSource(source, null, null, 105);

        var changes = DrainWithSentinel(context, subscription);

        // Assert: one normal change, demoted to Local by the survival check (transformed value),
        // new value 100. No correction.
        var valueChanges = changes.Where(c => c.Property.Name == nameof(ClampingDevice.Value)).ToList();
        Assert.Single(valueChanges);
        Assert.Equal(ChangeOriginKind.Local, valueChanges[0].Origin.Kind);
        Assert.Equal(100, valueChanges[0].GetNewValue<int>());
        Assert.Equal(100, device.Value);
    }

    [Fact]
    public void WhenProjectedValueEqualsStoredValue_ThenCorrectionIsPublished()
    {
        // Arrange: model at 100.
        var context = CreateContext();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var source = new object();
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));
        var modelWriteTimestamp = property.TryGetWriteTimestamp();

        // An old inbound timestamp so "not the inbound scope time" is a real assertion.
        var inboundTimestamp = DateTimeOffset.UtcNow.AddHours(-1);

        var raisedProperties = new List<string?>();
        device.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        var beforeCall = DateTimeOffset.UtcNow;

        // Act: the source sends 105; the hook projects it onto the stored 100, which the equality
        // check suppresses. The sent 105 was silently dropped, so a correction is synthesized.
        property.SetValueFromSource(source, inboundTimestamp, null, 105);

        var changes = DrainWithSentinel(context, subscription);

        // Assert: exactly one queued Value change, a Correction carrying the source and old == new == 100.
        var valueChanges = changes.Where(c => c.Property.Name == nameof(ClampingDevice.Value)).ToList();
        Assert.Single(valueChanges);
        var correction = valueChanges[0];
        Assert.Equal(ChangeOriginKind.Correction, correction.Origin.Kind);
        Assert.Same(source, correction.Origin.Source);
        Assert.Equal(100, correction.GetOldValue<int>());
        Assert.Equal(100, correction.GetNewValue<int>());

        // The model was not mutated and no PropertyChanged fired for the correction.
        Assert.Equal(100, device.Value);
        Assert.DoesNotContain(nameof(ClampingDevice.Value), raisedProperties);

        // The correction carries a fresh assertion timestamp (a timestamp-ordering endpoint must
        // never reject it as stale), not the inbound scope time, and it does NOT advance the
        // metadata: an unchanged value is not a new write, so the property keeps the value's real
        // last-change time from the arrange write.
        Assert.NotEqual(inboundTimestamp, correction.ChangedTimestamp);
        Assert.True(correction.ChangedTimestamp >= beforeCall);
        Assert.Equal(modelWriteTimestamp, property.TryGetWriteTimestamp());
    }

    [Fact]
    public void WhenInboundTimestampIsAheadOfLocalClock_ThenCorrectionTimestampExceedsIt()
    {
        // Arrange: model at 100; the source's clock runs an hour ahead of ours.
        var context = CreateContext();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var source = new object();
        var aheadTimestamp = DateTimeOffset.UtcNow.AddHours(1);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: the source sends a diverging 105 stamped in our future; the clamp projects it onto
        // the stored 100 and the write is suppressed.
        new PropertyReference(device, nameof(ClampingDevice.Value))
            .SetValueFromSource(source, aheadTimestamp, null, 105);

        var changes = DrainWithSentinel(context, subscription);

        // Assert: the correction is stamped strictly after the inbound timestamp it corrects
        // (Lamport lower bound), so a timestamp-ordering endpoint cannot reject it as older than
        // the diverged value, even under source clock skew.
        var correction = Assert.Single(changes, c => c.Property.Name == nameof(ClampingDevice.Value));
        Assert.Equal(ChangeOriginKind.Correction, correction.Origin.Kind);
        Assert.True(correction.ChangedTimestamp > aheadTimestamp);
    }

    [Fact]
    public void WhenInboundTimestampIsMaxValue_ThenSynthesisSaturatesInsteadOfThrowing()
    {
        // Arrange: model at 100; a hostile or broken source stamps its diverging value with
        // DateTimeOffset.MaxValue. The Lamport bound has no representable successor, so synthesis
        // must saturate at the ceiling rather than throw out of the inbound apply; delivery is the
        // single logged decision point that drops the unadvanceable correction.
        var context = CreateContext();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var source = new object();

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: must not throw.
        new PropertyReference(device, nameof(ClampingDevice.Value))
            .SetValueFromSource(source, DateTimeOffset.MaxValue, null, 105);

        var changes = DrainWithSentinel(context, subscription);

        // Assert: the correction is synthesized, saturated at the ceiling.
        var correction = Assert.Single(changes, c => c.Property.Name == nameof(ClampingDevice.Value));
        Assert.Equal(ChangeOriginKind.Correction, correction.Origin.Kind);
        Assert.Equal(DateTimeOffset.MaxValue, correction.ChangedTimestamp);
    }

    [Fact]
    public void WhenInboundTimestampCarriesOffsetAtClockCeiling_ThenSynthesisNormalizesInsteadOfThrowing()
    {
        // Arrange: an inbound stamp whose CLOCK time sits at DateTime.MaxValue but whose instant is
        // an hour below the ceiling. DateTimeOffset arithmetic operates on the clock time while
        // comparisons use the instant, so without UTC normalization the Lamport advancement would
        // throw out of the inbound apply despite the instant being advanceable.
        var context = CreateContext();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var source = new object();
        var offsetCeilingTimestamp = new DateTimeOffset(DateTime.MaxValue, TimeSpan.FromHours(1));

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: must not throw.
        new PropertyReference(device, nameof(ClampingDevice.Value))
            .SetValueFromSource(source, offsetCeilingTimestamp, null, 105);

        var changes = DrainWithSentinel(context, subscription);

        // Assert: synthesized, UTC-normalized, strictly after the inbound instant.
        var correction = Assert.Single(changes, c => c.Property.Name == nameof(ClampingDevice.Value));
        Assert.Equal(ChangeOriginKind.Correction, correction.Origin.Kind);
        Assert.Equal(TimeSpan.Zero, correction.ChangedTimestamp.Offset);
        Assert.True(correction.ChangedTimestamp > offsetCeilingTimestamp.ToUniversalTime());
    }

    [Fact]
    public void WhenSentValueEqualsStoredValue_ThenNothingIsPublished()
    {
        // Arrange: model at 100.
        var context = CreateContext();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var source = new object();

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: the source echoes the stored value; the projection is stable and equal to the sent
        // value, so there is no divergence.
        new PropertyReference(device, nameof(ClampingDevice.Value))
            .SetValueFromSource(source, null, null, 100);

        var changes = DrainWithSentinel(context, subscription);

        // Assert: pure echo, nothing published (no change, no correction).
        Assert.DoesNotContain(changes, c => c.Property.Name == nameof(ClampingDevice.Value));
        Assert.Equal(100, device.Value);
    }

    [Fact]
    public void WhenConfirmedWriteIsSuppressed_ThenNoCorrectionIsPublished()
    {
        // Arrange: model at 100.
        var context = CreateContext();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var source = new object();

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: a commit-confirmed apply whose projected value equals the stored value is suppressed.
        new PropertyReference(device, nameof(ClampingDevice.Value))
            .SetValueFromOrigin(ChangeOrigin.Confirmed(source), null, null, 105);

        var changes = DrainWithSentinel(context, subscription);

        // Assert: detection runs only for FromSource, so a suppressed Confirmed apply is never a
        // correction candidate (the commit protocol already guarantees the source state).
        Assert.DoesNotContain(changes, c => c.Property.Name == nameof(ClampingDevice.Value));
    }

    [Fact]
    public async Task WhenInboundStampedWriteIsCapturedByTransaction_ThenNoCorrectionIsPublished()
    {
        // Arrange: model at 50, an active transaction on the context.
        var context = CreateContext();
        var device = new ClampingDevice(context);
        device.Value = 50;
        var source = new object();
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        IReadOnlyList<SubjectPropertyChange> pending;
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            // Act: the inbound value is captured by the transaction and the chain stops, so the
            // terminal write never lands. The equality handler ran [RunsFirst] and saw differing
            // values (50 vs 80), so the outcome's valueUnchanged is false and no correction is synthesized.
            property.SetValueFromSource(source, null, null, 80);
            pending = transaction.GetPendingChanges();
        }

        var changes = DrainWithSentinel(context, subscription);

        // Assert: the value is pending in the transaction, and no correction was synthesized.
        Assert.Contains(pending, c => c.Property.Name == nameof(ClampingDevice.Value) && c.GetNewValue<int>() == 80);
        Assert.DoesNotContain(changes,
            c => c.Property.Name == nameof(ClampingDevice.Value) && c.Origin.Kind == ChangeOriginKind.Correction);
    }

    [Fact]
    public void WhenReadInterceptorTransformsValue_ThenCorrectionCarriesObservableValue()
    {
        // Arrange: model with a read interceptor that offsets Value reads by +1, so the observable
        // value (getter) differs from the raw backing field.
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new OffsetReadInterceptor(nameof(ClampingDevice.Value), offset: 1))
            .WithFullPropertyTracking();

        var device = new ClampingDevice(context);
        device.Value = 100; // backing field 100, observable value 101
        var source = new object();

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: the source sends 105; the hook clamps to the backing 100, which the equality check
        // suppresses (it compares the raw backing field, not the observable value).
        new PropertyReference(device, nameof(ClampingDevice.Value))
            .SetValueFromSource(source, null, null, 105);

        var changes = DrainWithSentinel(context, subscription);

        // Assert: the correction carries the read-interceptor-observed value (101), not the raw
        // backing field (100), pinning that corrections assert the observable value.
        var valueChanges = changes.Where(c => c.Property.Name == nameof(ClampingDevice.Value)).ToList();
        Assert.Single(valueChanges);
        Assert.Equal(ChangeOriginKind.Correction, valueChanges[0].Origin.Kind);
        Assert.Equal(101, valueChanges[0].GetNewValue<int>());
        Assert.Equal(101, device.Value);
    }

    [Fact]
    public void WhenSynthesisGetterThrows_ThenExceptionPropagatesAndNoCorrectionIsEnqueued()
    {
        // Arrange: a read interceptor that throws once armed. The synthesis-time observable read is
        // the only chain read in this flow, so arming after the arrange write targets exactly it.
        var thrower = new ArmedThrowingReadInterceptor(nameof(ClampingDevice.Value));
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => thrower)
            .WithFullPropertyTracking();

        var device = new ClampingDevice(context);
        device.Value = 100;
        var source = new object();

        using var subscription = context.CreatePropertyChangeQueueSubscription();
        thrower.Arm();

        // Act & Assert: the throw propagates to the caller deliberately. Stamped setters are already
        // a throwing API (validators throw through them), and a broken read path is a defect that
        // must surface; swallowing it would leave an undiagnosable, silently diverged source.
        Assert.Throws<InvalidOperationException>(() =>
            new PropertyReference(device, nameof(ClampingDevice.Value))
                .SetValueFromSource(source, null, null, 105));

        thrower.Disarm();

        // The suppressed write left the model intact and no correction was enqueued.
        Assert.Equal(100, device.Value);
        var changes = DrainWithSentinel(context, subscription);
        Assert.DoesNotContain(changes, c => c.Property.Name == nameof(ClampingDevice.Value));
    }

    [Fact]
    public async Task WhenConcurrentWriteRacesTheSuppressedApply_ThenCorrectionIsNeverStale()
    {
        // Arrange: model at 100, a gate read interceptor that parks the synthesis-time getter read
        // so a second thread can write 90 between the equality decision and synthesis.
        var gate = new SynthesisGate(nameof(ClampingDevice.Value));
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => gate)
            .WithFullPropertyTracking();

        var device = new ClampingDevice(context);
        device.Value = 100;
        var source = new object();
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        gate.Arm();

        var secondWrite = Task.Run(() =>
        {
            gate.WaitUntilSynthesisReached();
            device.Value = 90; // concurrent local write while thread 1 is parked in synthesis
            gate.ReleaseSynthesis();
        });

        // Act: thread 1 applies 105 (clamped to the stored 100, suppressed). During synthesis the
        // gate trips on the getter read, letting thread 2 write 90 before the write-timestamp compare.
        property.SetValueFromSource(source, null, null, 105);

        await secondWrite.WaitAsync(TimeSpan.FromSeconds(10));

        var changes = DrainWithSentinel(context, subscription);

        // Assert: with distinct write-timestamps, synthesis drops rather than enqueueing the stale
        // value. The timestamp check is advisory because timestamps can alias; the delivery path owns
        // the drop-or-fresh guarantee through send-time model revalidation.
        var corrections = changes
            .Where(c => c.Property.Name == nameof(ClampingDevice.Value)
                        && c.Origin.Kind == ChangeOriginKind.Correction)
            .ToList();
        Assert.All(corrections, c => Assert.NotEqual(100, c.GetNewValue<int>()));
        Assert.Equal(90, device.Value);
    }

    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext
            .Create()
            .WithTransactions()
            .WithFullPropertyTracking();

    // Writes a sentinel change on a fresh subject and drains the subscription up to it (excluded),
    // returning everything published before the sentinel.
    private static List<SubjectPropertyChange> DrainWithSentinel(
        IInterceptorSubjectContext context, PropertyChangeQueueSubscription subscription)
    {
        var sentinel = new ClampingDevice(context);
        sentinel.Value = 7;
        return ChangeQueueTestHelpers.DrainUntilSubject(subscription, sentinel);
    }

    /// <summary>Throws on chain reads of the named property while armed; unarmed reads pass through.</summary>
    private sealed class ArmedThrowingReadInterceptor : IReadInterceptor
    {
        private readonly string _propertyName;
        private volatile bool _armed;

        public ArmedThrowingReadInterceptor(string propertyName) => _propertyName = propertyName;

        public void Arm() => _armed = true;

        public void Disarm() => _armed = false;

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
        {
            if (_armed && context.Property.Name == _propertyName)
            {
                throw new InvalidOperationException("Getter failure injected by test.");
            }

            return next(ref context);
        }
    }

    /// <summary>Offsets reads of one int property so the observable value differs from the backing field.</summary>
    private sealed class OffsetReadInterceptor : IReadInterceptor
    {
        private readonly string _propertyName;
        private readonly int _offset;

        public OffsetReadInterceptor(string propertyName, int offset)
        {
            _propertyName = propertyName;
            _offset = offset;
        }

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
        {
            var value = next(ref context);
            if (context.Property.Name == _propertyName && value is int current)
            {
                return (TProperty)(object)(current + _offset);
            }

            return value;
        }
    }

    /// <summary>
    /// Parks the first read of the target property after arming (the synthesis-time getter read),
    /// signalling a second thread to write and waiting for it before letting the read proceed.
    /// </summary>
    private sealed class SynthesisGate : IReadInterceptor
    {
        private readonly string _propertyName;
        private readonly ManualResetEventSlim _synthesisReached = new(false);
        private readonly ManualResetEventSlim _proceed = new(false);
        private int _armed;

        public SynthesisGate(string propertyName) => _propertyName = propertyName;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void WaitUntilSynthesisReached() => _synthesisReached.Wait(TimeSpan.FromSeconds(10));

        public void ReleaseSynthesis() => _proceed.Set();

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
        {
            if (context.Property.Name == _propertyName && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _synthesisReached.Set();
                _proceed.Wait(TimeSpan.FromSeconds(10));
            }

            return next(ref context);
        }
    }
}
