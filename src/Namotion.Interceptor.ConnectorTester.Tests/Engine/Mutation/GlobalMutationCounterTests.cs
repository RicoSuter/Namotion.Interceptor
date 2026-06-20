using System.Collections.Concurrent;
using Xunit;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;

public class GlobalMutationCounterTests
{
    [Fact]
    public void WhenNextCalled_ThenReturnsMonotonicallyIncreasingValues()
    {
        // Arrange / Act
        var first = GlobalMutationCounter.Next();
        var second = GlobalMutationCounter.Next();
        var third = GlobalMutationCounter.Next();

        // Assert
        Assert.True(second > first);
        Assert.True(third > second);
    }

    [Fact]
    public void WhenCalledFromManyThreads_ThenAllValuesAreUnique()
    {
        // Arrange
        const int threadCount = 16;
        const int callsPerThread = 1000;
        var observed = new ConcurrentBag<long>();
        using var startGate = new ManualResetEventSlim(false);
        var threads = new Thread[threadCount];

        // Act
        for (var t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                startGate.Wait();
                for (var i = 0; i < callsPerThread; i++)
                {
                    observed.Add(GlobalMutationCounter.Next());
                }
            });
            threads[t].Start();
        }
        startGate.Set();
        foreach (var thread in threads) thread.Join();

        // Assert
        var distinct = new HashSet<long>(observed);
        Assert.Equal(threadCount * callsPerThread, distinct.Count);
    }
}
