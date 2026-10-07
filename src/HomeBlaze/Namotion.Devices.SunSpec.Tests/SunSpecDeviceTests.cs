using HomeBlaze.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.SunSpec.Tests.Testing;
using Namotion.Interceptor.Modbus.Client;

namespace Namotion.Devices.SunSpec.Tests;

public class SunSpecDeviceTests
{
    [Fact]
    public void WhenConstructed_ThenDefaultsAreSet()
    {
        // Act
        var device = TestHost.CreateAttachedDevice();

        // Assert
        Assert.Equal(502, device.Port);
        Assert.Equal(new[] { 1 }, device.UnitIds);
        Assert.Equal(TimeSpan.FromSeconds(10), device.PollingInterval);
        Assert.Empty(device.Units);
        Assert.Equal("SunSpec Device", device.Title);
    }

    [Fact]
    public void WhenNameIsSet_ThenNameIsTheTitle()
    {
        // Arrange
        var device = TestHost.CreateAttachedDevice();

        // Act
        device.Name = "Inverter";

        // Assert
        Assert.Equal("Inverter", device.Title);
    }

    [Theory]
    [InlineData(100, 1000)]
    [InlineData(10000, 10000)]
    [InlineData(7200000, 3600000)]
    public void WhenPollingIntervalIsConfigured_ThenTheEffectiveIntervalIsClampedToItsLimits(int configuredMilliseconds, int expectedMilliseconds)
    {
        // Arrange
        var device = TestHost.CreateAttachedDevice();

        // Act
        device.PollingInterval = TimeSpan.FromMilliseconds(configuredMilliseconds);

        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), device.GetEffectivePollingInterval());
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 1, 248 })]
    public void WhenUnitIdsAreInvalid_ThenTheyAreRejected(int[] unitIds)
    {
        // Act
        var isValid = SunSpecDevice.TryGetUnitIds(unitIds, out _, out var error);

        // Assert
        Assert.False(isValid);
        Assert.NotNull(error);
    }

    [Fact]
    public void WhenUnitIdsRepeat_ThenEachIsUsedOnce()
    {
        // Act
        var isValid = SunSpecDevice.TryGetUnitIds([2, 1, 2], out var unitIds, out _);

        // Assert
        Assert.True(isValid);
        Assert.Equal(new byte[] { 2, 1 }, unitIds);
    }

    [Fact]
    public void WhenNoPathIsConfigured_ThenDefinitionsAreInTheDataDirectory()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "sunspec-data");
        var device = TestHost.CreateAttachedDevice(dataDirectory);

        // Act
        var directory = device.GetModelDefinitionsDirectory();

        // Assert
        Assert.Equal(Path.Combine(dataDirectory, "SunSpec", "Models"), directory);
    }

    [Fact]
    public void WhenRelativePathIsConfigured_ThenItStartsAtTheDataDirectory()
    {
        // Arrange
        var dataDirectory = Path.Combine(Path.GetTempPath(), "sunspec-data");
        var device = TestHost.CreateAttachedDevice(dataDirectory);

        // Act
        device.ModelDefinitionsPath = "Vendor";

        // Assert
        Assert.Equal(Path.Combine(dataDirectory, "Vendor"), device.GetModelDefinitionsDirectory());
    }

    [Fact]
    public void WhenAbsolutePathIsConfigured_ThenItIsUsedAsIs()
    {
        // Arrange
        var device = TestHost.CreateAttachedDevice(Path.Combine(Path.GetTempPath(), "sunspec-data"));
        var path = Path.Combine(Path.GetTempPath(), "sunspec-vendor");

        // Act
        device.ModelDefinitionsPath = path;

        // Assert
        Assert.Equal(path, device.GetModelDefinitionsDirectory());
    }

    [Fact]
    public void WhenThereIsNoDataDirectory_ThenThereIsNoDefaultDefinitionsDirectory()
    {
        // Act
        var device = TestHost.CreateAttachedDevice();

        // Assert
        Assert.Null(device.GetModelDefinitionsDirectory());
    }

    [Fact]
    public void WhenSourceHasNotConnectedYet_ThenStatusIsStarting()
    {
        // Arrange
        var device = TestHost.CreateAttachedDevice();
        using var source = device.CreateModbusClientSource(new ModbusClientConfiguration { Host = "127.0.0.1" }, NullLogger.Instance);

        // Act
        device.UpdateStatus(source.Diagnostics);

        // Assert
        Assert.False(device.IsConnected);
        Assert.Equal(ServiceStatus.Starting, device.Status);
        Assert.Equal("Connecting...", device.StatusMessage);
    }
}
