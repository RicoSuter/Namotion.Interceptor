using Xunit;
using Namotion.Interceptor.ConnectorTester.Performance;

namespace Namotion.Interceptor.ConnectorTester.Tests.Performance;

public class PerformanceWindowTests
{
    [Fact]
    public void WhenLatenciesProvided_ThenPercentilesComputed()
    {
        // Arrange: 100 evenly-spaced samples 1..100.
        var latencies = Enumerable.Range(1, 100).Select(i => (double)i).ToList();

        // Act
        var window = PerformanceWindow.FromSorted(latencies);

        // Assert (nearest-rank, matches today's Percentile formula).
        Assert.Equal(50, window.P50);
        Assert.Equal(90, window.P90);
        Assert.Equal(95, window.P95);
        Assert.Equal(99, window.P99);
        Assert.Equal(100, window.P999);
        Assert.Equal(100, window.Max);
    }

    [Fact]
    public void WhenLatenciesEmpty_ThenPercentilesAreNaN()
    {
        // Arrange / Act
        var window = PerformanceWindow.FromSorted([]);

        // Assert
        Assert.True(double.IsNaN(window.P50));
        Assert.True(double.IsNaN(window.Max));
    }
}
