using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests.Attributes;

public class StateAttributeInitializerTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
    }

    [Fact]
    public void StateAttribute_RegistersStateMetadata_WithAllFields()
    {
        // Arrange
        var context = CreateContext();
        var subject = new StateTestSubject(context)
        {
            Temperature = 23.5m
        };

        // Act
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(StateTestSubject.Temperature))!;
        var attribute = property.TryGetAttribute(KnownAttributes.State);

        // Assert
        Assert.NotNull(attribute);
        var metadata = attribute.GetValue() as StateMetadata;
        Assert.NotNull(metadata);
        Assert.Equal("Temp", metadata.Title);
        Assert.Equal(StateUnit.DegreeCelsius, metadata.Unit);
        Assert.Equal(1, metadata.Position);
        Assert.True(metadata.IsCumulative);
        Assert.True(metadata.IsDiscrete);
        Assert.True(metadata.IsEstimated);
    }

    [Fact]
    public void StateAttribute_RegistersStateMetadata_WithDefaults()
    {
        // Arrange
        var context = CreateContext();
        var subject = new StateTestSubject(context);

        // Act
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(StateTestSubject.SimpleState))!;
        var attribute = property.TryGetAttribute(KnownAttributes.State);

        // Assert
        Assert.NotNull(attribute);
        var metadata = attribute.GetValue() as StateMetadata;
        Assert.NotNull(metadata);
        Assert.Null(metadata.Title);
        Assert.Equal(StateUnit.Default, metadata.Unit);
        Assert.Equal(int.MaxValue, metadata.Position);
        Assert.False(metadata.IsCumulative);
        Assert.False(metadata.IsDiscrete);
        Assert.False(metadata.IsEstimated);
    }

    [Fact]
    public void PropertyWithoutStateAttribute_HasNoStateRegistryAttribute()
    {
        // Arrange
        var context = CreateContext();
        var subject = new StateTestSubject(context);

        // Act
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(StateTestSubject.NotState))!;
        var attribute = property.TryGetAttribute(KnownAttributes.State);

        // Assert
        Assert.Null(attribute);
    }

    [Fact]
    public void WhenClassAndInterfaceHaveStateAttribute_ThenFieldsAreMerged()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MergeTestSubject(context);

        // Act
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(MergeTestSubject.Power))!;
        var metadata = property.TryGetAttribute(KnownAttributes.State)?.GetValue() as StateMetadata;

        // Assert — class provides Position, interface provides Unit
        Assert.NotNull(metadata);
        Assert.Equal(5, metadata.Position);
        Assert.Equal(StateUnit.Watt, metadata.Unit);
        Assert.False(metadata.IsCumulative);
    }

    [Fact]
    public void WhenOnlyInterfaceHasStateAttribute_ThenInterfaceValuesUsed()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MergeTestSubject(context);

        // Act
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(MergeTestSubject.EnergyConsumed))!;
        var metadata = property.TryGetAttribute(KnownAttributes.State)?.GetValue() as StateMetadata;

        // Assert — all values from interface
        Assert.NotNull(metadata);
        Assert.Equal(StateUnit.WattHour, metadata.Unit);
        Assert.True(metadata.IsCumulative);
        Assert.Equal(int.MaxValue, metadata.Position); // default — no Position on interface
    }

    [Fact]
    public void WhenNoPositionSpecified_ThenDefaultsToIntMaxValue()
    {
        // Arrange
        var context = CreateContext();
        var subject = new StateTestSubject(context);

        // Act
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(StateTestSubject.SimpleState))!;
        var metadata = property.TryGetAttribute(KnownAttributes.State)?.GetValue() as StateMetadata;

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal(int.MaxValue, metadata.Position);
    }
}

[InterceptorSubject]
public partial class StateTestSubject
{
    [State(Title ="Temp", Unit = StateUnit.DegreeCelsius,
           IsCumulative = true, IsDiscrete = true, IsEstimated = true, Position = 1)]
    public partial decimal Temperature { get; set; }

    [State]
    public partial string? SimpleState { get; set; }

    public partial string? NotState { get; set; }
}

public interface IMergeTestPowerSensor
{
    [State(Unit = StateUnit.Watt)]
    decimal? Power { get; }

    [State(Unit = StateUnit.WattHour, IsCumulative = true)]
    decimal? EnergyConsumed { get; }
}

[InterceptorSubject]
public partial class MergeTestSubject : IMergeTestPowerSensor
{
    [State(Position = 5)]  // Only Position — Unit should come from interface
    public partial decimal? Power { get; set; }

    // No [State] on class — should inherit from interface
    public partial decimal? EnergyConsumed { get; set; }
}
