using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
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
            .WithService<IPropertyLifecycleHandler>(
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
    public void WhenStateAndSecretConfiguration_ThenFailsClosedWithoutExposingSecretAsState()
    {
        // Arrange
        var context = CreateContext();

        // Act: attaching must not throw (a throw would abort attach and can stop the host).
        var subject = new InvalidSecretStateSubject(context);

        // Assert: the secret configuration is registered, but the property is NOT exposed as state.
        var property = subject.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(InvalidSecretStateSubject.LeakySecret))!;
        var configuration = property.TryGetAttribute(KnownAttributes.Configuration)?.GetValue() as ConfigurationMetadata;
        Assert.NotNull(configuration);
        Assert.True(configuration.IsSecret);
        Assert.Null(property.TryGetAttribute(KnownAttributes.State));
    }

    [Fact]
    public void WhenStateAndSecretConfigurationAndLoggingAvailable_ThenWarns()
    {
        // Arrange: register a recording logger factory into the context, the way RootManager makes
        // host logging available to the context at startup.
        var context = CreateContext();
        var logger = new RecordingLogger();
        context.AddService<ILoggerFactory>(new RecordingLoggerFactory(logger));

        // Act
        _ = new InvalidSecretStateSubject(context);

        // Assert: the conflict is surfaced as a warning naming the offending property.
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("LeakySecret", warning);
        Assert.Contains("[State]", warning);
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
