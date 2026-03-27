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
        Assert.Equal("Temp", metadata.Name);
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
        Assert.Null(metadata.Name);
        Assert.Equal(StateUnit.Default, metadata.Unit);
        Assert.Equal(0, metadata.Position);
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
}

[InterceptorSubject]
public partial class StateTestSubject
{
    [State(Name = "Temp", Unit = StateUnit.DegreeCelsius, Position = 1,
           IsCumulative = true, IsDiscrete = true, IsEstimated = true)]
    public partial decimal Temperature { get; set; }

    [State]
    public partial string? SimpleState { get; set; }

    public partial string? NotState { get; set; }
}
