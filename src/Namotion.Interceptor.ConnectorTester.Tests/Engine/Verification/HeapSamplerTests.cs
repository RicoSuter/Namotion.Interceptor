using System.Runtime.InteropServices;
using Xunit;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Verification;

[CollectionDefinition(nameof(HeapSamplerTests), DisableParallelization = true)]
public class HeapSamplerCollection;

/// <summary>
/// Runs alone because tests compare two heap samples, which tests allocating in parallel would shift.
/// </summary>
[Collection(nameof(HeapSamplerTests))]
public class HeapSamplerTests
{
    [Fact]
    public void WhenCompactAndSampleCalled_ThenReturnsPositiveHeapAndProcessSizes()
    {
        // Arrange
        var sampler = new HeapSampler();

        // Act
        var (heapMb, processMb) = sampler.CompactAndSample();

        // Assert
        Assert.True(heapMb > 0);
        Assert.True(processMb > 0);
        Assert.True(processMb >= heapMb, "Process working set should be >= managed heap.");
    }

    [Fact]
    public void WhenCompactAndSampleCalled_ThenForcesGen2Collection()
    {
        // Arrange
        var sampler = new HeapSampler();
        var gen2Before = GC.CollectionCount(2);

        // Act
        _ = sampler.CompactAndSample();

        // Assert
        var gen2After = GC.CollectionCount(2);
        Assert.True(gen2After > gen2Before, $"Expected at least one Gen2 collection. Before: {gen2Before}, after: {gen2After}.");
    }

    [Fact]
    public void WhenObjectsArePinnedAcrossSeveralGen0Regions_ThenTheSampledHeapGrowsByThem()
    {
        // Arrange
        var sampler = new HeapSampler();

        // Act
        var measurement = MeasurePinnedArraysWhileTheRestOfTheHeapStaysUnchanged(sampler);

        // Assert
        Assert.True(measurement.PinnedHeapMb - measurement.BaselineHeapMb >= measurement.PinnedMb / 2,
            $"Expected the heap to grow by about {measurement.PinnedMb:F2} MB of pinned arrays. Before: {measurement.BaselineHeapMb:F2} MB, " +
            $"pinned: {measurement.PinnedHeapMb:F2} MB, released: {measurement.ReleasedHeapMb:F2} MB.");
        Assert.True(measurement.ProcessMb >= measurement.PinnedHeapMb, "Process working set should be >= managed heap.");
    }

    // Objects that other tests of the assembly leave behind can die between two samples, and their release offsets the
    // growth by the pinned arrays. An attempt counts only when the heap returns to its baseline once the arrays are
    // released, which a sampler that misses the pinned arrays passes as well. The tolerance of a quarter of the pinned
    // arrays keeps the growth that a correct sampler reports above the asserted half.
    private static PinnedArraysMeasurement MeasurePinnedArraysWhileTheRestOfTheHeapStaysUnchanged(HeapSampler sampler)
    {
        const int maximumAttempts = 5;
        var attempts = new List<PinnedArraysMeasurement>();
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            var measurement = MeasurePinnedArrays(sampler);
            if (Math.Abs(measurement.ReleasedHeapMb - measurement.BaselineHeapMb) <= measurement.PinnedMb / 4)
            {
                return measurement;
            }

            attempts.Add(measurement);
        }

        Assert.Fail("The rest of the heap changed in every attempt: " + string.Join("; ", attempts.Select(measurement =>
            $"before {measurement.BaselineHeapMb:F2} MB, pinned {measurement.PinnedHeapMb:F2} MB, released {measurement.ReleasedHeapMb:F2} MB")));
        return default;
    }

    private static PinnedArraysMeasurement MeasurePinnedArrays(HeapSampler sampler)
    {
        const int arraySize = 1000;
        var (baselineHeapMb, _) = sampler.CompactAndSample();
        var handles = new List<GCHandle>();
        double pinnedHeapMb, processMb;
        try
        {
            // One pinned array in every 64 of a 32 MB allocation stream keeps several gen0 regions
            // in place through the compacting collections.
            for (var index = 0; index < 32 * 1024; index++)
            {
                var array = new byte[arraySize];
                if (index % 64 == 0)
                {
                    handles.Add(GCHandle.Alloc(array, GCHandleType.Pinned));
                }
            }

            (pinnedHeapMb, processMb) = sampler.CompactAndSample();
        }
        finally
        {
            foreach (var handle in handles)
            {
                handle.Free();
            }
        }

        var (releasedHeapMb, _) = sampler.CompactAndSample();
        var pinnedMb = handles.Count * arraySize / (1024.0 * 1024.0);
        return new PinnedArraysMeasurement(pinnedMb, baselineHeapMb, pinnedHeapMb, releasedHeapMb, processMb);
    }

    private readonly record struct PinnedArraysMeasurement(
        double PinnedMb, double BaselineHeapMb, double PinnedHeapMb, double ReleasedHeapMb, double ProcessMb);
}
