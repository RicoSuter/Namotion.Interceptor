using Namotion.Interceptor.Modbus.Client;

namespace Namotion.Interceptor.Modbus.Tests.Client;

public class ModbusClientConfigurationTests
{
    [Fact]
    public void WhenConfigurationUsesDefaults_ThenValidationPasses()
    {
        // Arrange
        var configuration = new ModbusClientConfiguration { Host = "192.168.1.10" };

        // Act
        configuration.Validate();

        // Assert
        Assert.Equal(502, configuration.Port);
        Assert.Equal(1, configuration.UnitId);
        Assert.Equal(TimeSpan.FromSeconds(2), configuration.PollingInterval);
        Assert.Equal(0, configuration.MaximumRegisterGap);
    }

    public static TheoryData<ModbusClientConfiguration> InvalidConfigurations => new()
    {
        new ModbusClientConfiguration { Host = " " },
        new ModbusClientConfiguration { Host = "host", Port = 0 },
        new ModbusClientConfiguration { Host = "host", Port = 65536 },
        new ModbusClientConfiguration { Host = "host", PollingInterval = TimeSpan.Zero },
        new ModbusClientConfiguration { Host = "host", RequestTimeout = TimeSpan.Zero },
        new ModbusClientConfiguration { Host = "host", RetryTime = TimeSpan.Zero },
        new ModbusClientConfiguration { Host = "host", BufferTime = TimeSpan.FromMilliseconds(-1) },
        new ModbusClientConfiguration { Host = "host", PollingInterval = TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1) },
        new ModbusClientConfiguration { Host = "host", RequestTimeout = TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1) },
        new ModbusClientConfiguration { Host = "host", RetryTime = TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1) },
        new ModbusClientConfiguration { Host = "host", BufferTime = TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1) },
        new ModbusClientConfiguration { Host = "host", MaximumRegisterGap = -1 },
        new ModbusClientConfiguration { Host = "host", MaximumRegisterGap = 125 },
    };

    [Fact]
    public void WhenDelaysAreAtTheirLimits_ThenValidationPasses()
    {
        // Arrange
        var maximumDelay = TimeSpan.FromMinutes(10);
        var configuration = new ModbusClientConfiguration
        {
            Host = "host",
            PollingInterval = maximumDelay,
            RequestTimeout = maximumDelay,
            RetryTime = maximumDelay,
            BufferTime = TimeSpan.Zero
        };

        // Act
        var exception = Record.Exception(configuration.Validate);

        // Assert
        Assert.Null(exception);
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void WhenConfigurationIsInvalid_ThenValidateThrows(ModbusClientConfiguration configuration)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(configuration.Validate);
    }
}
