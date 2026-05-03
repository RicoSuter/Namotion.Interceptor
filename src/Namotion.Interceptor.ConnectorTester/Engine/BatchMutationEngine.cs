using System.Diagnostics;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Value mutation strategy: cycles through all nodes in parallel batches.
/// Used for load profiles (NumberOfBatches > 0).
/// Mutates ValueMutationRate nodes per second, spread across NumberOfBatches
/// batches with even distribution via a PeriodicTimer at 110% tick rate.
/// Each participant mutates a single fixed property (participantIndex % 3)
/// to avoid OPC UA subscription coalescing.
/// </summary>
public class BatchMutationEngine : MutationEngine
{
    private readonly int _numberOfBatches;
    private readonly int _participantIndex;

    public BatchMutationEngine(
        TestNode root,
        ParticipantConfiguration configuration,
        TestCycleCoordinator coordinator,
        ILogger logger,
        int numberOfBatches,
        int participantIndex)
        : base(root, configuration, coordinator, logger)
    {
        _numberOfBatches = numberOfBatches;
        _participantIndex = participantIndex;
    }

    protected override async Task RunValueMutationsAsync(CancellationToken stoppingToken)
    {
        int nodeCount;
        lock (NodeLock)
        {
            nodeCount = KnownNodes.Count;
        }

        if (nodeCount == 0)
        {
            return;
        }

        var nodesPerBatch = (int)Math.Ceiling((double)ValueMutationRate / _numberOfBatches);
        var timerIntervalMs = 1000.0 / (_numberOfBatches * 1.1);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(timerIntervalMs));

        var nodeIndex = 0;
        var mutationsThisSecond = 0;
        var cycleStart = Stopwatch.GetTimestamp();
        var property = _participantIndex % 4;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            Coordinator.WaitIfPaused(stoppingToken);

            if (mutationsThisSecond >= ValueMutationRate)
            {
                var elapsed = Stopwatch.GetElapsedTime(cycleStart);
                if (elapsed < TimeSpan.FromSeconds(1))
                {
                    continue;
                }

                mutationsThisSecond = 0;
                cycleStart += Stopwatch.Frequency;
            }

            List<TestNode> nodes;
            lock (NodeLock)
            {
                nodes = KnownNodes;
                nodeCount = nodes.Count;
                if (nodeCount == 0)
                {
                    continue;
                }
            }

            var count = Math.Min(nodesPerBatch, ValueMutationRate - mutationsThisSecond);

            using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
            {
                Parallel.For(0, count, j =>
                {
                    var node = nodes[(nodeIndex + j) % nodeCount];
                    var counter = NextGlobalCounter();

                    switch (property)
                    {
                        case 0:
                            node.StringValue = counter.ToString("x8");
                            break;
                        case 1:
                            node.DecimalValue = counter / 100m;
                            break;
                        case 2:
                            node.IntValue = (int)(counter % int.MaxValue);
                            break;
                        case 3:
                            node.LongValue = counter;
                            break;
                    }

                    IncrementValueMutationCount();
                });
            }

            nodeIndex = (nodeIndex + count) % nodeCount;
            mutationsThisSecond += count;
        }
    }
}
