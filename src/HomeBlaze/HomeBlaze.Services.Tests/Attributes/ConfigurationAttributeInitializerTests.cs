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

public class ConfigurationAttributeInitializerTests
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
    public void ConfigurationAttribute_RegistersConfigurationMetadata()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ConfigTestSubject(context);

        // Act
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(ConfigTestSubject.ApiKey))!;
        var attribute = property.TryGetAttribute(KnownAttributes.Configuration);

        // Assert
        Assert.NotNull(attribute);
        var metadata = attribute.GetValue() as ConfigurationMetadata;
        Assert.NotNull(metadata);
    }

    [Fact]
    public void PropertyWithoutConfigurationAttribute_HasNoConfigRegistryAttribute()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ConfigTestSubject(context);

        // Act
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(ConfigTestSubject.RuntimeState))!;
        var attribute = property.TryGetAttribute(KnownAttributes.Configuration);

        // Assert
        Assert.Null(attribute);
    }

    [Fact]
    public void WhenConfigurationIsSecret_ThenMetadataHasIsSecretTrue()
    {
        // Arrange
        var context = CreateContext();
        var subject = new SecretConfigTestSubject(context);

        // Act
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(SecretConfigTestSubject.SecretKey))!;
        var metadata = property.TryGetAttribute(KnownAttributes.Configuration)?.GetValue() as ConfigurationMetadata;

        // Assert
        Assert.NotNull(metadata);
        Assert.True(metadata.IsSecret);
    }

    [Fact]
    public void WhenConfigurationIsNotSecret_ThenMetadataHasIsSecretFalse()
    {
        // Arrange
        var context = CreateContext();
        var subject = new SecretConfigTestSubject(context);

        // Act
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty(nameof(SecretConfigTestSubject.PublicConfig))!;
        var metadata = property.TryGetAttribute(KnownAttributes.Configuration)?.GetValue() as ConfigurationMetadata;

        // Assert
        Assert.NotNull(metadata);
        Assert.False(metadata.IsSecret);
    }

    [Fact]
    public void WhenStateAndSecretConfiguration_ThenThrowsInvalidOperationException()
    {
        // Arrange
        var context = CreateContext();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => new InvalidSecretStateSubject(context));
        Assert.Contains("LeakySecret", exception.Message);
        Assert.Contains("[State]", exception.Message);
        Assert.Contains("[Configuration(IsSecret = true)]", exception.Message);
    }
}

[InterceptorSubject]
public partial class ConfigTestSubject
{
    [Configuration]
    public partial string? ApiKey { get; set; }

    public partial string? RuntimeState { get; set; }
}

[InterceptorSubject]
public partial class SecretConfigTestSubject
{
    [Configuration(IsSecret = true)]
    public partial string? SecretKey { get; set; }

    [Configuration]
    public partial string? PublicConfig { get; set; }
}

[InterceptorSubject]
public partial class InvalidSecretStateSubject
{
    [State]
    [Configuration(IsSecret = true)]
    public partial string? LeakySecret { get; set; }
}
