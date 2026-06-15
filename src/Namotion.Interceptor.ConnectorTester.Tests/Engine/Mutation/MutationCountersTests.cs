using Xunit;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;

public class MutationCountersTests
{
    [Fact]
    public void WhenIncrementValueCalled_ThenValueMutationCountIncreases()
    {
        // Arrange
        var counters = new MutationCounters();

        // Act
        counters.IncrementValue();
        counters.IncrementValue();

        // Assert
        Assert.Equal(2, counters.ValueMutationCount);
        Assert.Equal(0, counters.StructuralMutationCount);
    }

    [Fact]
    public void WhenIncrementStructuralCalled_ThenStructuralMutationCountIncreases()
    {
        // Arrange
        var counters = new MutationCounters();

        // Act
        counters.IncrementStructural();
        counters.IncrementStructural();
        counters.IncrementStructural();

        // Assert
        Assert.Equal(3, counters.StructuralMutationCount);
        Assert.Equal(0, counters.ValueMutationCount);
    }

    [Fact]
    public void WhenResetCalled_ThenBothCountersReturnToZero()
    {
        // Arrange
        var counters = new MutationCounters();
        counters.IncrementValue();
        counters.IncrementStructural();

        // Act
        counters.Reset();

        // Assert
        Assert.Equal(0, counters.ValueMutationCount);
        Assert.Equal(0, counters.StructuralMutationCount);
    }

    [Fact]
    public void WhenIncrementedFromManyThreads_ThenCountIsAccurate()
    {
        // Arrange
        var counters = new MutationCounters();
        const int threadCount = 8;
        const int incrementsPerThread = 10_000;
        using var startGate = new ManualResetEventSlim(false);
        var threads = new Thread[threadCount];

        // Act
        for (var t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                startGate.Wait();
                for (var i = 0; i < incrementsPerThread; i++)
                {
                    counters.IncrementValue();
                }
            });
            threads[t].Start();
        }
        startGate.Set();
        foreach (var thread in threads) thread.Join();

        // Assert
        Assert.Equal((long)threadCount * incrementsPerThread, counters.ValueMutationCount);
    }
}
