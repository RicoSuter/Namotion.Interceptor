using System.Text.Json;
using HomeBlaze.History.Abstractions;
using HomeBlaze.History.InMemory;

namespace HomeBlaze.History.InMemory.Tests;

public class InMemoryHistoryStoreCoreOversizeAndMetricsTests
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private static InMemoryHistoryStoreCore NewCore(int maxJsonSize = 8192, int maxPoints = 1000) =>
        new(maxPointsPerProperty: maxPoints, maxAge: TimeSpan.FromHours(1),
            maxJsonSize: maxJsonSize, getUtcNow: () => Base.AddHours(1));

    [Fact]
    public void WhenStringExceedsMaxJsonSize_ThenPlaceholderStoredAndCounted()
    {
        // Arrange - cap at 16 chars; record a 100-char string
        var core = NewCore(maxJsonSize: 16);
        var big = new string('x', 100);
        core.Record("/a/Name", Base.AddSeconds(1), big, typeof(string));

        // Act
        var point = core.Query(new HistoryQuery("/a/Name", Base, Base.AddSeconds(10))).Points.Single();

        // Assert - placeholder row, timeline preserved, OversizeCount incremented
        Assert.Equal(JsonValueKind.Object, point.Json!.Value.ValueKind);
        Assert.True(point.Json!.Value.GetProperty("$oversize").GetBoolean());
        Assert.True(point.Json!.Value.GetProperty("size").GetInt32() >= 100);
        Assert.Equal(1, core.OversizeCount);
    }

    [Fact]
    public void WhenStringWithinCap_ThenStoredVerbatimAndNotCounted()
    {
        // Arrange
        var core = NewCore(maxJsonSize: 1024);
        core.Record("/a/Name", Base.AddSeconds(1), "small", typeof(string));

        // Act
        var point = core.Query(new HistoryQuery("/a/Name", Base, Base.AddSeconds(10))).Points.Single();

        // Assert
        Assert.Equal("small", point.Json!.Value.GetString());
        Assert.Equal(0, core.OversizeCount);
    }

    [Fact]
    public void WhenSamplesRecorded_ThenCountMetricsReflectThem()
    {
        // Arrange
        var core = NewCore();
        core.Record("/a/V", Base.AddSeconds(1), 1d, typeof(double));
        core.Record("/a/V", Base.AddSeconds(2), 2d, typeof(double));
        core.Record("/b/V", Base.AddSeconds(1), 3d, typeof(double));

        // Act & Assert
        Assert.Equal(3, core.RecordedCount);
        Assert.Equal(2, core.TrackedPropertyCount);
        Assert.Equal(3, core.TotalSampleCount);
        Assert.True(core.EstimatedMemoryBytes > 0);
    }

    [Fact]
    public void WhenCapacityEvicts_ThenEvictedCountAccumulates()
    {
        // Arrange - capacity 2, append 5 -> 3 evicted
        var core = NewCore(maxPoints: 2);
        for (var i = 0; i < 5; i++)
        {
            core.Record("/a/V", Base.AddSeconds(i), (double)i, typeof(double));
        }

        // Act & Assert
        Assert.Equal(3, core.EvictedCount);
        Assert.Equal(2, core.TotalSampleCount);
    }
}
