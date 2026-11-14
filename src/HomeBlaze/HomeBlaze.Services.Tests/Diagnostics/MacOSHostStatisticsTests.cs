using System.Runtime.CompilerServices;
using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class MacOSHostStatisticsTests
{
    [Fact]
    public void WhenCpuLoadInfoIsMeasured_ThenSizeMatchesHostCpuLoadInfoCount()
    {
        // Act
        var size = Unsafe.SizeOf<MacOSHostCpuLoadInfo>();

        // Assert
        Assert.Equal((int)(MacOSHostCpuLoadInfo.Count * sizeof(int)), size);
    }

    [Fact]
    public void WhenVmStatisticsIsMeasured_ThenSizeMatchesHostVmInfo64Count()
    {
        // Act
        var size = Unsafe.SizeOf<MacOSVmStatistics64>();

        // Assert
        Assert.Equal(152, size);
        Assert.Equal((int)(MacOSVmStatistics64.Count * sizeof(int)), size);
    }
}
