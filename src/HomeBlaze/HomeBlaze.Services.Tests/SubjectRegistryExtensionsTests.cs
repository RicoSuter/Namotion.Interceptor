using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests;

public class SubjectRegistryExtensionsTests
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
    public void IsConfigurationProperty_ReturnsTrue_ForConfigurationProperty()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ExtensionsTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(ExtensionsTestSubject.Config))!;

        // Act & Assert
        Assert.True(property.IsConfigurationProperty());
    }

    [Fact]
    public void IsConfigurationProperty_ReturnsFalse_ForNonConfigurationProperty()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ExtensionsTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(ExtensionsTestSubject.Temperature))!;

        // Act & Assert
        Assert.False(property.IsConfigurationProperty());
    }

    [Fact]
    public void GetStateMetadata_ReturnsMetadata_ForStateProperty()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ExtensionsTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(ExtensionsTestSubject.Temperature))!;

        // Act
        var metadata = property.GetStateMetadata();

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal("Temp", metadata.Title);
        Assert.Equal(StateUnit.DegreeCelsius, metadata.Unit);
        Assert.Equal(1, metadata.Position);
    }

    [Fact]
    public void GetStateMetadata_ReturnsNull_ForNonStateProperty()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ExtensionsTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(ExtensionsTestSubject.Config))!;

        // Act & Assert
        Assert.Null(property.GetStateMetadata());
    }

    [Fact]
    public void GetDisplayName_ReturnsStateName_WhenSet()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ExtensionsTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(ExtensionsTestSubject.Temperature))!;

        // Act & Assert
        Assert.Equal("Temp", property.GetDisplayName());
    }

    [Fact]
    public void GetDisplayName_ReturnsPropertyName_WhenNoStateName()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ExtensionsTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(ExtensionsTestSubject.SimpleState))!;

        // Act & Assert
        Assert.Equal("SimpleState", property.GetDisplayName());
    }

    [Fact]
    public void GetDisplayPosition_ReturnsPosition_FromStateMetadata()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ExtensionsTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(ExtensionsTestSubject.Temperature))!;

        // Act & Assert
        Assert.Equal(1, property.GetDisplayPosition());
    }

    [Fact]
    public void GetDisplayPosition_ReturnsMaxValue_WhenNoStateAttribute()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ExtensionsTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(ExtensionsTestSubject.Config))!;

        // Act & Assert
        Assert.Equal(int.MaxValue, property.GetDisplayPosition());
    }

    [Fact]
    public void GetConfigurationProperties_ReturnsOnlyConfigProperties()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ExtensionsTestSubject(context);

        // Act
        var configProperties = subject.GetConfigurationProperties().ToList();

        // Assert
        Assert.Single(configProperties);
        Assert.Equal(nameof(ExtensionsTestSubject.Config), configProperties[0].Name);
    }

    [Fact]
    public void GetStateProperties_ReturnsOnlyStateProperties()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ExtensionsTestSubject(context);

        // Act
        var stateProperties = subject.GetStateProperties().ToList();

        // Assert
        Assert.Equal(2, stateProperties.Count);
        Assert.Contains(stateProperties, p => p.Name == nameof(ExtensionsTestSubject.Temperature));
        Assert.Contains(stateProperties, p => p.Name == nameof(ExtensionsTestSubject.SimpleState));
    }

    [Fact]
    public void HasConfigurationProperties_ReturnsTrue_WhenConfigPropertiesExist()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ExtensionsTestSubject(context);

        // Act & Assert
        Assert.True(subject.HasConfigurationProperties());
    }

    [Fact]
    public void HasConfigurationProperties_ReturnsFalse_WhenNoConfigProperties()
    {
        // Arrange
        var context = CreateContext();
        var subject = new StateOnlyTestSubject(context);

        // Act & Assert
        Assert.False(subject.HasConfigurationProperties());
    }

    [Fact]
    public void DynamicSubject_WithProgrammaticAttributes_WorksIdentically()
    {
        // Arrange
        var context = CreateContext();
        var subject = new DynamicTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(DynamicTestSubject.Value))!;

        // Act
        property.AddAttribute(KnownAttributes.State, typeof(StateMetadata),
            _ => new StateMetadata { Title = "Dynamic Value", Unit = StateUnit.Watt, Position = 5 },
            null);

        // Assert
        var metadata = property.GetStateMetadata();
        Assert.NotNull(metadata);
        Assert.Equal("Dynamic Value", metadata.Title);
        Assert.Equal(StateUnit.Watt, metadata.Unit);
        Assert.Equal(5, metadata.Position);
    }
}

[InterceptorSubject]
public partial class ExtensionsTestSubject
{
    [State(Title = "Temp", Unit = StateUnit.DegreeCelsius, Position = 1)]
    public partial decimal Temperature { get; set; }

    [State]
    public partial string? SimpleState { get; set; }

    [Configuration]
    public partial string? Config { get; set; }
}

[InterceptorSubject]
public partial class StateOnlyTestSubject
{
    [State]
    public partial string? Value { get; set; }
}

[InterceptorSubject]
public partial class DynamicTestSubject
{
    public partial decimal Value { get; set; }
}
