using System.Diagnostics;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Value mutation strategy: cycles through all nodes in parallel batches.
/// Used for load profiles (BatchSize > 0).
/// </summary>
public class BatchMutationEngine : MutationEngine
{
    private readonly int _batchSize;
    private readonly int _participantIndex;

    public BatchMutationEngine(
        TestNode root,
        ParticipantConfiguration configuration,
        TestCycleCoordinator coordinator,
        ILogger logger,
        int batchSize,
        int participantIndex)
        : base(root, configuration, coordinator, logger)
    {
        _batchSize = batchSize;
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

        var totalBatches = (int)Math.Ceiling((double)nodeCount / _batchSize);
        var timerIntervalMs = 1000.0 / (totalBatches * 1.1);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(timerIntervalMs));

        var batchIndex = 0;
        var cycleStart = Stopwatch.GetTimestamp();
        var property = _participantIndex % 3;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            Coordinator.WaitIfPaused(stoppingToken);

            if (batchIndex >= totalBatches)
            {
                // All batches done for this second — check if next second started
                var elapsed = Stopwatch.GetElapsedTime(cycleStart);
                if (elapsed < TimeSpan.FromSeconds(1))
                {
                    continue;
                }

                batchIndex = 0;
                cycleStart = Stopwatch.GetTimestamp();
            }

            List<TestNode> nodes;
            int startIndex;
            int count;
            lock (NodeLock)
            {
                nodes = KnownNodes;
                nodeCount = nodes.Count;
                if (nodeCount == 0)
                {
                    continue;
                }

                startIndex = (batchIndex * _batchSize) % nodeCount;
                count = Math.Min(_batchSize, nodeCount - startIndex);
            }

            using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
            {
                Parallel.For(startIndex, startIndex + count, i =>
                {
                    var node = nodes[i];
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
                    }

                    IncrementValueMutationCount();
                });
            }

            batchIndex++;
        }
    }
}
