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
}
