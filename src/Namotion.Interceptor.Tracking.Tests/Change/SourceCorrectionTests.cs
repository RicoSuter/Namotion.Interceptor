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

        // The correction carries a local assertion timestamp (when the assertion was made), not the
        // inbound scope time and not the value's last-change time, and it does NOT advance the
        // metadata: an unchanged value is not a new write, so the property keeps the value's real
        // last-change time from the arrange write.
        Assert.NotEqual(inboundTimestamp, correction.ChangedTimestamp);
        Assert.True(correction.ChangedTimestamp >= beforeCall);
        Assert.Equal(modelWriteTimestamp, property.TryGetWriteTimestamp());
    }

    [Fact]
    public void WhenInboundTimestampIsAheadOfLocalClock_ThenCorrectionKeepsTheLocalAssertionTimestamp()
    {
        // Arrange: model at 100; the source's clock runs an hour ahead of ours. The correction's stamp
        // is the local assertion time, so a source clock running ahead must not drag it with it.
        var context = CreateContext();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var source = new object();
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));
        var modelWriteTimestamp = property.TryGetWriteTimestamp();
        var aheadTimestamp = DateTimeOffset.UtcNow.AddHours(1);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: the source sends a diverging 105 stamped in our future; the clamp projects it onto
        // the stored 100 and the write is suppressed.
        property.SetValueFromSource(source, aheadTimestamp, null, 105);

        var changes = DrainWithSentinel(context, subscription);

        // Assert
        var correction = Assert.Single(changes, c => c.Property.Name == nameof(ClampingDevice.Value));
        Assert.Equal(ChangeOriginKind.Correction, correction.Origin.Kind);
        Assert.True(correction.ChangedTimestamp < aheadTimestamp);
        Assert.Equal(modelWriteTimestamp, property.TryGetWriteTimestamp());
    }

    [Fact]
    public void WhenSubjectAggregatesTwoTrackingContexts_ThenCorrectionReachesEveryQueue()
    {
        // Arrange: a subject attached to two full-tracking contexts. Ordinary writes already run
        // both queue interceptors, so a correction must fan out to both queues the same way; a
        // singular service lookup would throw on aggregation and neither queue could repair the
        // diverged source.
        var contextA = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var contextB = InterceptorSubjectContext.Create().WithFullPropertyTracking();

        var device = new ClampingDevice(contextA);
        ((IInterceptorSubject)device).Context.AddFallbackContext(contextB);
        device.Value = 100;
        var source = new object();

        using var subscriptionA = contextA.CreatePropertyChangeQueueSubscription();
        using var subscriptionB = contextB.CreatePropertyChangeQueueSubscription();

        // Act: an equality-suppressed diverging inbound apply must not throw and must synthesize
        // the correction into both queues.
        new PropertyReference(device, nameof(ClampingDevice.Value))
            .SetValueFromSource(source, null, null, 105);

        var changesA = DrainWithSentinel(contextA, subscriptionA);
        var changesB = DrainWithSentinel(contextB, subscriptionB);

        // Assert
        Assert.Single(changesA,
            c => ReferenceEquals(c.Property.Subject, device) && c.Origin.Kind == ChangeOriginKind.Correction);
        Assert.Single(changesB,
            c => ReferenceEquals(c.Property.Subject, device) && c.Origin.Kind == ChangeOriginKind.Correction);
    }

    [Fact]
    public async Task WhenFlowOwnsActiveTransactionAndInboundEqualsCommittedValue_ThenNothingIsPublished()
    {
        // Arrange: backing field at 100, an active transaction on this flow holding a pending 70.
        // The observable getter answers with the pending overlay on this flow, so synthesis must
        // compare against COMMITTED state: the sent value equals it, making this a pure echo. Reading
        // the overlay instead would misread the echo as divergence and assert the uncommitted 70 to
        // the source, which a rollback never commits.
        var context = CreateContext();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var source = new object();
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            device.Value = 70; // captured as the transaction's pending overlay

            // Act: an inbound value equal to the BACKING field is equality-suppressed before the
            // transaction captures it.
            property.SetValueFromSource(source, null, null, 100);

            // Dispose without committing: the pending 70 is discarded.
        }

        var changes = DrainWithSentinel(context, subscription);

        // Assert: pure echo, nothing published; no uncommitted value reaches the source.
        Assert.DoesNotContain(changes, c => c.Property.Name == nameof(ClampingDevice.Value));
        Assert.Equal(100, device.Value);
    }

    [Fact]
    public async Task WhenFlowOwnsActiveTransactionAndInboundDivergesFromCommittedValue_ThenCorrectionAssertsCommittedValue()
    {
        // Arrange: backing field at 100, an active transaction on this flow holding a pending 70. The
        // source sends a diverging 105 that the hook clamps onto the committed 100, so the write is
        // equality-suppressed and the source is left holding 105 while the committed model holds 100.
        // The divergence is real and must still be corrected while a transaction is pending: the
        // correction carries the committed 100, never the uncommitted overlay (70) and never the
        // dropped sent value (105).
        var context = CreateContext();
        var device = new ClampingDevice(context);
        device.Value = 100;
        var source = new object();
        var property = new PropertyReference(device, nameof(ClampingDevice.Value));

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            device.Value = 70; // captured as the transaction's pending overlay

            // Act
            property.SetValueFromSource(source, null, null, 105);

            // Dispose without committing: the pending 70 is discarded and the model stays at 100,
            // which is exactly the value the correction asserted.
        }

        var changes = DrainWithSentinel(context, subscription);

        // Assert
        var correction = Assert.Single(changes,
            c => c.Property.Name == nameof(ClampingDevice.Value) && c.Origin.Kind == ChangeOriginKind.Correction);
        Assert.Equal(100, correction.GetNewValue<int>());
        Assert.Same(source, correction.Origin.Source);
        Assert.Equal(100, device.Value);
    }

    [Fact]
    public void WhenInboundEnumValueArrivesAsUnderlyingTypeAndIsEqual_ThenItIsAPureEcho()
    {
        // Arrange: OPC UA encodes enumerations as Int32, so inbound enum applies arrive as boxed
        // integers. A numerically equal value is a pure echo: the type-strict boxed comparison
        // would misread it as divergence and synthesize a correction on every such apply
        // (self-sustaining with read-after-write), so divergence must mirror the setter's unbox.
        var context = CreateContext();
        var device = new ModeDevice(context);
        device.Mode = DeviceMode.Running;
        var source = new object();

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act: the source sends the underlying integral value of the stored enum.
        new PropertyReference(device, nameof(ModeDevice.Mode))
            .SetValueFromSource(source, null, null, (int)DeviceMode.Running);

        var changes = DrainWithSentinel(context, subscription);

        // Assert: nothing published (no change, no correction) and the model is unchanged.
        Assert.DoesNotContain(changes, c => ReferenceEquals(c.Property.Subject, device));
        Assert.Equal(DeviceMode.Running, device.Mode);
    }

    [Fact]
    public void WhenInboundEnumValueArrivesAsUnderlyingTypeAndChanges_ThenOriginSurvivesAsFromSource()
    {
        // Arrange: a genuinely changed enum value sent as its boxed underlying integer stores
        // faithfully through the setter's lenient unbox, so the origin must survive as FromSource
        // and stay echo-suppressible; a type-strict survival check would demote it to Local and
        // write every inbound enum change straight back to the server.
        var context = CreateContext();
        var device = new ModeDevice(context);
        device.Mode = DeviceMode.Idle;
        var source = new object();

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        new PropertyReference(device, nameof(ModeDevice.Mode))
            .SetValueFromSource(source, null, null, (int)DeviceMode.Fault);

        var changes = DrainWithSentinel(context, subscription);

        // Assert
        Assert.Equal(DeviceMode.Fault, device.Mode);
        var change = Assert.Single(changes, c => ReferenceEquals(c.Property.Subject, device));
        Assert.Equal(ChangeOriginKind.FromSource, change.Origin.Kind);
        Assert.Same(source, change.Origin.Source);
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
            // terminal write never lands. The captured value (80) differs from the stored 50, so this
            // is not an equality-suppressed write and no correction is synthesized.
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
        // value; no delivered correction carries the pre-race 100 (either dropped or carrying 90).
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
