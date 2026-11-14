using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class SystemMetricsReaderTests
{
    [Fact]
    public void WhenReadingOnSupportedPlatform_ThenValuesArePlausible()
    {
        // Arrange
        var reader = SystemMetricsReaderFactory.CreateForCurrentPlatform();
        if (reader is null)
        {
            return; // unsupported platform, nothing to read
        }

        // Act
        var reading = reader.Read();

        // Assert
        Assert.True(reading.CpuTotalTime > 0);
        Assert.True(reading.CpuBusyTime <= reading.CpuTotalTime);
        Assert.True(reading.TotalMemory > 0);
        Assert.InRange(reading.AvailableMemory, 0L, reading.TotalMemory);
    }

    [Fact]
    public void WhenReadingOperatingSystemName_ThenItIsNotEmpty()
    {
        // Act
        var name = SystemInformation.ReadOperatingSystemName();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(name));
    }
}
