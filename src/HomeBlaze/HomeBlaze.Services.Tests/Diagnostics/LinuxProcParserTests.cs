using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class LinuxProcParserTests
{
    [Fact]
    public void WhenProcStatHasAllFields_ThenGuestFieldsAreExcludedFromTotal()
    {
        // Arrange: user nice system idle iowait irq softirq steal guest guest_nice
        var content = "cpu  100 20 30 800 50 0 0 0 999 999\ncpu0 1 2 3 4 5 6 7 8 9 10\n"u8;

        // Act
        var success = LinuxProcParser.TryParseProcStat(content, out var busyTime, out var totalTime);

        // Assert
        Assert.True(success);
        Assert.Equal(1000UL, totalTime);
        Assert.Equal(150UL, busyTime);
    }

    [Fact]
    public void WhenProcStatHasOnlyFourFields_ThenParsesThem()
    {
        // Arrange
        var content = "cpu 10 0 10 80\n"u8;

        // Act
        var success = LinuxProcParser.TryParseProcStat(content, out var busyTime, out var totalTime);

        // Assert
        Assert.True(success);
        Assert.Equal(100UL, totalTime);
        Assert.Equal(20UL, busyTime);
    }

    [Fact]
    public void WhenProcStatDoesNotStartWithCpu_ThenFails()
    {
        // Arrange
        var content = "intr 1 2 3\n"u8;

        // Act
        var success = LinuxProcParser.TryParseProcStat(content, out _, out _);

        // Assert
        Assert.False(success);
    }

    [Fact]
    public void WhenProcStatHasTooFewFields_ThenFails()
    {
        // Arrange
        var content = "cpu 10 20\n"u8;

        // Act
        var success = LinuxProcParser.TryParseProcStat(content, out _, out _);

        // Assert
        Assert.False(success);
    }

    [Fact]
    public void WhenMemInfoHasTotalAndAvailable_ThenConvertsKibibytesToBytes()
    {
        // Arrange
        var content = "MemTotal:       16000000 kB\nMemFree:         1000000 kB\nMemAvailable:    8000000 kB\n"u8;

        // Act
        var success = LinuxProcParser.TryParseMemInfo(content, out var totalBytes, out var availableBytes);

        // Assert
        Assert.True(success);
        Assert.Equal(16_000_000L * 1024, totalBytes);
        Assert.Equal(8_000_000L * 1024, availableBytes);
    }

    [Fact]
    public void WhenMemInfoLacksAvailable_ThenFails()
    {
        // Arrange
        var content = "MemTotal:       16000000 kB\nMemFree:         1000000 kB\n"u8;

        // Act
        var success = LinuxProcParser.TryParseMemInfo(content, out _, out _);

        // Assert
        Assert.False(success);
    }

    [Theory]
    [InlineData("NAME=\"Debian GNU/Linux\"\nPRETTY_NAME=\"Debian GNU/Linux 12 (bookworm)\"\n", "Debian GNU/Linux 12 (bookworm)")]
    [InlineData("PRETTY_NAME='Alpine Linux v3.20'\n", "Alpine Linux v3.20")]
    [InlineData("PRETTY_NAME=Ubuntu\n", "Ubuntu")]
    [InlineData("NAME=Ubuntu\n", null)]
    [InlineData("PRETTY_NAME=\"\"\n", null)]
    public void WhenOsReleaseIsParsed_ThenReturnsPrettyName(string content, string? expected)
    {
        // Act
        var prettyName = LinuxProcParser.ParseOsReleasePrettyName(content);

        // Assert
        Assert.Equal(expected, prettyName);
    }

    [Theory]
    [InlineData("536870912\n", 17_179_869_184L, 536_870_912L)]
    [InlineData("max\n", 17_179_869_184L, null)]
    [InlineData("-1\n", 17_179_869_184L, null)]
    [InlineData("9223372036854710272\n", 17_179_869_184L, null)]
    [InlineData("17179869184\n", 17_179_869_184L, null)]
    [InlineData("536870912\n", null, 536_870_912L)]
    [InlineData(null, 17_179_869_184L, null)]
    public void WhenCgroupMemoryLimitIsParsed_ThenReturnsLimitOrNull(string? content, long? totalMemory, long? expected)
    {
        // Act
        var limit = LinuxProcParser.ParseCgroupMemoryLimit(content, totalMemory);

        // Assert
        Assert.Equal(expected, limit);
    }

    [Theory]
    [InlineData("200000 100000\n", "2")]
    [InlineData("150000 100000\n", "1.5")]
    [InlineData("max 100000\n", null)]
    [InlineData("garbage", null)]
    [InlineData(null, null)]
    public void WhenCgroupV2CpuMaxIsParsed_ThenReturnsCoresOrNull(string? content, string? expected)
    {
        // Act
        var cores = LinuxProcParser.ParseCgroupV2CpuMax(content);

        // Assert
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), cores);
    }

    [Theory]
    [InlineData("50000\n", "100000\n", "0.5")]
    [InlineData("-1\n", "100000\n", null)]
    [InlineData(null, "100000\n", null)]
    [InlineData("50000\n", null, null)]
    public void WhenCgroupV1CpuLimitIsParsed_ThenReturnsCoresOrNull(string? quota, string? period, string? expected)
    {
        // Act
        var cores = LinuxProcParser.ParseCgroupV1CpuLimit(quota, period);

        // Assert
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), cores);
    }
}
