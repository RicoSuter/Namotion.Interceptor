using Moq;
using Namotion.Interceptor.Connectors.TwinCAT.Client;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Client;

public class AdsClientConfigurationTests
{
    private static AdsClientConfiguration CreateValidConfiguration()
    {
        return new AdsClientConfiguration
        {
            Host = "192.168.1.100",
            AmsNetId = "192.168.1.100.1.1",
            PathProvider = Mock.Of<IPathProvider>()
        };
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var configuration = CreateValidConfiguration();

        // Assert
        Assert.Equal(851, configuration.AmsPort);
        Assert.Equal(TimeSpan.FromSeconds(5), configuration.Timeout);
        Assert.Equal(AdsReadMode.Auto, configuration.DefaultReadMode);
        Assert.Equal(100, configuration.DefaultCycleTime);
        Assert.Equal(0, configuration.DefaultMaxDelay);
        Assert.Equal(500, configuration.MaxNotifications);
        Assert.Equal(TimeSpan.FromMilliseconds(100), configuration.PollingInterval);
        Assert.Equal(1000, configuration.WriteRetryQueueSize);
        Assert.Equal(TimeSpan.FromSeconds(5), configuration.HealthCheckInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(8), configuration.BufferTime);
        Assert.Equal(TimeSpan.FromSeconds(1), configuration.RetryTime);
        Assert.Equal(5, configuration.CircuitBreakerFailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(60), configuration.CircuitBreakerCooldown);
        Assert.NotNull(configuration.ValueConverter);
    }

    [Fact]
    public void Validate_WithValidConfiguration_DoesNotThrow()
    {
        // Arrange
        var configuration = CreateValidConfiguration();

        // Act & Assert - should not throw
        configuration.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Validate_WithInvalidHost_ThrowsArgumentException(string? host)
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.Host = host!;

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => configuration.Validate());
        Assert.Contains("Host", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Validate_WithInvalidAmsNetId_ThrowsArgumentException(string? amsNetId)
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.AmsNetId = amsNetId!;

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => configuration.Validate());
        Assert.Contains("AmsNetId", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_WithInvalidAmsPort_ThrowsArgumentOutOfRangeException(int port)
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.AmsPort = port;

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => configuration.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_WithInvalidMaxNotifications_ThrowsArgumentOutOfRangeException(int maxNotifications)
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.MaxNotifications = maxNotifications;

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithZeroPollingInterval_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.PollingInterval = TimeSpan.Zero;

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithNegativePollingInterval_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.PollingInterval = TimeSpan.FromMilliseconds(-1);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithCustomValues_DoesNotThrow()
    {
        // Arrange
        var configuration = new AdsClientConfiguration
        {
            Host = "10.0.0.1",
            AmsNetId = "10.0.0.1.1.1",
            AmsPort = 852,
            PathProvider = Mock.Of<IPathProvider>(),
            DefaultReadMode = AdsReadMode.Polled,
            MaxNotifications = 1000,
            PollingInterval = TimeSpan.FromMilliseconds(500)
        };

        // Act & Assert - should not throw
        configuration.Validate();
    }
}
