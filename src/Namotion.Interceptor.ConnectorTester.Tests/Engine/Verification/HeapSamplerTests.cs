using Xunit;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Verification;

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
}
