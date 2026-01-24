using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for OpcUaClientConfiguration validation logic.
/// </summary>
public class OpcUaClientConfigurationTests
{
    private static OpcUaClientConfiguration CreateValidConfiguration(
        TimeSpan? maxReconnectDuration = null)
    {
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            PathProvider = new AttributeBasedPathProvider("opc"),
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
        };

        if (maxReconnectDuration.HasValue)
        {
            config.MaxReconnectDuration = maxReconnectDuration.Value;
        }

        return config;
    }

    [Fact]
    public void Validate_WithMaxReconnectDurationLessThan5Seconds_ThrowsArgumentException()
    {
        // Arrange
        var config = CreateValidConfiguration(TimeSpan.FromSeconds(4));

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => config.Validate());
        Assert.Contains("MaxReconnectDuration", exception.Message);
        Assert.Contains("at least 5 seconds", exception.Message);
    }

    [Fact]
    public void Validate_WithMaxReconnectDurationExactly5Seconds_DoesNotThrow()
    {
        // Arrange
        var config = CreateValidConfiguration(TimeSpan.FromSeconds(5));

        // Act & Assert - should not throw
        config.Validate();
    }

    [Fact]
    public void Validate_WithMaxReconnectDurationDefault30Seconds_DoesNotThrow()
    {
        // Arrange
        var config = CreateValidConfiguration();

        // Act & Assert - should not throw
        config.Validate();
        Assert.Equal(TimeSpan.FromSeconds(30), config.MaxReconnectDuration);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4.9)]
    public void Validate_WithMaxReconnectDurationBelowMinimum_ThrowsArgumentException(double seconds)
    {
        // Arrange
        var config = CreateValidConfiguration(TimeSpan.FromSeconds(seconds));

        // Act & Assert
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(300)]
    public void Validate_WithMaxReconnectDurationAtOrAboveMinimum_DoesNotThrow(double seconds)
    {
        // Arrange
        var config = CreateValidConfiguration(TimeSpan.FromSeconds(seconds));

        // Act & Assert - should not throw
        config.Validate();
    }

    [Fact]
    public void Validate_WithNegativeConsistencyReadBuffer_ThrowsArgumentException()
    {
        // Arrange
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            PathProvider = new AttributeBasedPathProvider("opc"),
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            ConsistencyReadBuffer = TimeSpan.FromMilliseconds(-1)
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => config.Validate());
        Assert.Contains("ConsistencyReadBuffer", exception.Message);
    }

    [Fact]
    public void Validate_WithZeroConsistencyReadBuffer_Succeeds()
    {
        // Arrange - Zero is valid (no buffer)
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            PathProvider = new AttributeBasedPathProvider("opc"),
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            ConsistencyReadBuffer = TimeSpan.Zero
        };

        // Act & Assert - Should not throw
        config.Validate();
    }

    [Fact]
    public void Validate_WithValidConsistencyReadSettings_Succeeds()
    {
        // Arrange
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            PathProvider = new AttributeBasedPathProvider("opc"),
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            ConsistencyReadBuffer = TimeSpan.FromMilliseconds(100)
        };

        // Act & Assert - Should not throw
        config.Validate();
    }
}
