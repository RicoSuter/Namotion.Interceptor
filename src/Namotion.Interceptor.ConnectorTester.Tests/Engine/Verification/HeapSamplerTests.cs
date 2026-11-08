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
        const int arraySize = 1000;
        var sampler = new HeapSampler();
        var (baselineHeapMb, _) = sampler.CompactAndSample();
        var handles = new List<GCHandle>();
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

            var pinnedMb = handles.Count * arraySize / (1024.0 * 1024.0);

            // Act
            var (heapMb, processMb) = sampler.CompactAndSample();

            // Assert
            Assert.True(heapMb - baselineHeapMb >= pinnedMb / 2,
                $"Expected the heap to grow by about {pinnedMb:F2} MB of pinned arrays. Before: {baselineHeapMb:F2} MB, after: {heapMb:F2} MB.");
            Assert.True(processMb >= heapMb, "Process working set should be >= managed heap.");
        }
        finally
        {
            foreach (var handle in handles)
            {
                handle.Free();
            }
        }
    }
}
