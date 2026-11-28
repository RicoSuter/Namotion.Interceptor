using System.ComponentModel;
using HomeBlaze.Abstractions.Devices.Energy;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests;

public class BatteryStateDefaultTests
{
    [Fact]
    public void WhenASubjectReliesOnTheDefault_ThenIsBatteryLowIsRegisteredAsADiscreteDerivedStateAtTheContractPosition()
    {
        // Arrange
        var subject = new DefaultBatteryTestSubject(CreateContext());

        // Act
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(IBatteryState.IsBatteryLow))!;

        // Assert
        var metadata = property.GetStateMetadata();
        Assert.True(property.Reference.Metadata.IsDerived);
        Assert.NotNull(metadata);
        Assert.True(metadata.IsDiscrete);
        Assert.Equal(344, metadata.Position);
    }

    [Fact]
    public void WhenTheBatteryLevelDropsBelowTheThreshold_ThenIsBatteryLowChanges()
    {
        // Arrange
        var subject = new DefaultBatteryTestSubject(CreateContext()) { BatteryLevel = 0.5m };
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(nameof(IBatteryState.IsBatteryLow))!;
        Assert.Equal(false, property.GetValue());
        var changedProperties = new List<string>();
        ((INotifyPropertyChanged)subject).PropertyChanged += (_, arguments) => changedProperties.Add(arguments.PropertyName!);

        // Act
        subject.BatteryLevel = 0.05m;

        // Assert
        Assert.Equal(true, property.GetValue());
        Assert.Contains(nameof(IBatteryState.IsBatteryLow), changedProperties);
    }

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
    }
}

[InterceptorSubject]
public partial class DefaultBatteryTestSubject : IBatteryState
{
    public partial decimal? BatteryLevel { get; set; }
}
