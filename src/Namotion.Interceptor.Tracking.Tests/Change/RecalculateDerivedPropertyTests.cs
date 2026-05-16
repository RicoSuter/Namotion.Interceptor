using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class RecalculateDerivedPropertyTests
{
    [Fact]
    public void WhenRecalculateCalled_ThenGetterIsReEvaluatedAndNotificationFired()
    {
        // Arrange
        var externalValue = 10.0;
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var sensor = new ExternalSensor(context);
        sensor.ExternalValueProvider = () => externalValue;
        var property = new PropertyReference(sensor, nameof(ExternalSensor.CalibratedTemperature));
        property.RecalculateDerivedProperty();
        changes.Clear();

        // Act
        externalValue = 42.0;
        property.RecalculateDerivedProperty();

        // Assert
        Assert.Single(changes);
        Assert.Equal(nameof(ExternalSensor.CalibratedTemperature), changes[0].Property.Name);
        Assert.Equal(10.0, changes[0].GetOldValue<double>());
        Assert.Equal(42.0, changes[0].GetNewValue<double>());
    }

    [Fact]
    public void WhenRecalculateCalledAndValueUnchanged_ThenNoNotificationFired()
    {
        // Arrange
        var externalValue = 10.0;
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var sensor = new ExternalSensor(context);
        sensor.ExternalValueProvider = () => externalValue;
        var property = new PropertyReference(sensor, nameof(ExternalSensor.CalibratedTemperature));
        property.RecalculateDerivedProperty();
        changes.Clear();

        // Act
        property.RecalculateDerivedProperty();

        // Assert
        Assert.Empty(changes);
    }

    [Fact]
    public void WhenRecalculateCalledOnNonDerivedProperty_ThenNoOp()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var sensor = new ExternalSensor(context);

        // Act & Assert
        var property = new PropertyReference(sensor, nameof(ExternalSensor.Label));
        property.RecalculateDerivedProperty();
    }

    [Fact]
    public void WhenRecalculateCalledUnderExplicitTimestampScope_ThenChangeTimestampMatchesScope()
    {
        // Arrange
        var externalValue = 10.0;
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var sensor = new ExternalSensor(context);
        sensor.ExternalValueProvider = () => externalValue;
        var property = new PropertyReference(sensor, nameof(ExternalSensor.CalibratedTemperature));
        property.RecalculateDerivedProperty();
        changes.Clear();

        var explicitTimestamp = DateTimeOffset.UtcNow.AddDays(-100);

        // Act
        externalValue = 42.0;
        using (SubjectChangeContext.WithChangedTimestamp(explicitTimestamp))
        {
            property.RecalculateDerivedProperty();
        }

        // Assert
        var change = Assert.Single(changes);
        Assert.Equal(explicitTimestamp, change.ChangedTimestamp);
        Assert.Equal(explicitTimestamp, property.TryGetWriteTimestamp());
    }

    [Fact]
    public void WhenRecalculateCalledWithNoScope_ThenAllEventsShareSingleTimestamp()
    {
        // Arrange: install a thread-aware mock timestamp function. The mock returns
        // sequential values per call but only on the test thread, so any parallel test
        // running on another thread sees the real UtcNow pass-through unaffected.
        var testThreadId = Environment.CurrentManagedThreadId;
        var captureCount = 0;
        var mockBase = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var originalFn = SubjectChangeContext.GetTimestampFunction;
        SubjectChangeContext.GetTimestampFunction = () =>
            Environment.CurrentManagedThreadId == testThreadId
                ? mockBase.AddSeconds(Interlocked.Increment(ref captureCount))
                : DateTimeOffset.UtcNow;
        try
        {
            var externalValue = 10.0;
            var changes = new List<SubjectPropertyChange>();
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            context
                .GetPropertyChangeObservable(ImmediateScheduler.Instance)
                .Subscribe(changes.Add);

            var sensor = new ExternalSensor(context);
            sensor.ExternalValueProvider = () => externalValue;
            var property = new PropertyReference(sensor, nameof(ExternalSensor.CalibratedTemperature));
            property.RecalculateDerivedProperty();
            changes.Clear();
            captureCount = 0;

            // Act
            externalValue = 42.0;
            property.RecalculateDerivedProperty();

            // Assert: exactly one timestamp captured; every observed event used it.
            Assert.Equal(1, captureCount);
            var expected = mockBase.AddSeconds(1);
            var change = Assert.Single(changes);
            Assert.Equal(expected, change.ChangedTimestamp);
            Assert.Equal(expected, property.TryGetWriteTimestamp());
        }
        finally
        {
            SubjectChangeContext.GetTimestampFunction = originalFn;
        }
    }

    [Fact]
    public void WhenRecalculateCalledUnderNullScope_ThenStoredTimestampIsNullAndAllEventsShareSingleTimestamp()
    {
        // Arrange: thread-aware mock (see no-scope test for rationale).
        var testThreadId = Environment.CurrentManagedThreadId;
        var captureCount = 0;
        var mockBase = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var originalFn = SubjectChangeContext.GetTimestampFunction;
        SubjectChangeContext.GetTimestampFunction = () =>
            Environment.CurrentManagedThreadId == testThreadId
                ? mockBase.AddSeconds(Interlocked.Increment(ref captureCount))
                : DateTimeOffset.UtcNow;
        try
        {
            var externalValue = 10.0;
            var changes = new List<SubjectPropertyChange>();
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            context
                .GetPropertyChangeObservable(ImmediateScheduler.Instance)
                .Subscribe(changes.Add);

            var sensor = new ExternalSensor(context);
            sensor.ExternalValueProvider = () => externalValue;
            var property = new PropertyReference(sensor, nameof(ExternalSensor.CalibratedTemperature));
            property.RecalculateDerivedProperty();
            changes.Clear();
            captureCount = 0;

            // Act
            externalValue = 42.0;
            using (SubjectChangeContext.WithChangedTimestamp(null))
            {
                property.RecalculateDerivedProperty();
            }

            // Assert: storage stays null (never-written sentinel); publishing captured exactly
            // one timestamp which the single observed event published verbatim.
            Assert.Null(property.TryGetWriteTimestamp());
            Assert.Equal(1, captureCount);
            var expected = mockBase.AddSeconds(1);
            var change = Assert.Single(changes);
            Assert.Equal(expected, change.ChangedTimestamp);
        }
        finally
        {
            SubjectChangeContext.GetTimestampFunction = originalFn;
        }
    }


    [Fact]
    public void WhenRecalculateCalledConcurrently_ThenAllChangesAreSerializedAndNoNotificationsLost()
    {
        // Arrange
        var callCount = 0;
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                lock (changes) { changes.Add(change); }
            });

        var sensor = new ExternalSensor(context);
        sensor.ExternalValueProvider = () => Interlocked.Increment(ref callCount);
        var property = new PropertyReference(sensor, nameof(ExternalSensor.CalibratedTemperature));
        property.RecalculateDerivedProperty();

        lock (changes) { changes.Clear(); }
        Interlocked.Exchange(ref callCount, 0);

        // Act
        Parallel.For(0, 100, _ =>
        {
            property.RecalculateDerivedProperty();
        });

        // Assert
        // Thread-safety contract: concurrent calls must not deadlock, notifications must
        // arrive in order (no stale value after a newer one), and the final settled value
        // must be correct. The count is non-deterministic because IsRecalculating coalesces
        // concurrent calls, so fewer than 100 getter evaluations occur.
        lock (changes)
        {
            Assert.True(changes.Count > 0, "At least some recalculations should produce change notifications");

            for (var i = 1; i < changes.Count; i++)
            {
                var previous = changes[i - 1].GetNewValue<double>();
                var current = changes[i].GetNewValue<double>();
                Assert.True(current > previous,
                    $"Notifications must be monotonically increasing but got {previous} -> {current} at index {i}");
            }

            var finalNotifiedValue = changes[^1].GetNewValue<double>();
            Assert.Equal((double)callCount, finalNotifiedValue);
        }
    }
}
