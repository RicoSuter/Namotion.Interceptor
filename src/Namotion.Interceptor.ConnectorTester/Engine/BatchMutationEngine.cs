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
    private readonly int _batchIntervalMs;
    private readonly int _participantIndex;

    public BatchMutationEngine(
        TestNode root,
        ParticipantConfiguration configuration,
        TestCycleCoordinator coordinator,
        ILogger logger,
        int batchSize,
        int batchIntervalMs,
        int participantIndex)
        : base(root, configuration, coordinator, logger)
    {
        _batchSize = batchSize;
        _batchIntervalMs = batchIntervalMs;
        _participantIndex = participantIndex;
    }

    protected override async Task RunValueMutationsAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_batchIntervalMs));
        var batchIndex = 0;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            Coordinator.WaitIfPaused(stoppingToken);

            int startIndex;
            int count;
            List<TestNode> nodes;
            lock (NodeLock)
            {
                nodes = KnownNodes;
                if (nodes.Count == 0)
                {
                    continue;
                }

                startIndex = (batchIndex * _batchSize) % nodes.Count;
                count = Math.Min(_batchSize, nodes.Count - startIndex);
                batchIndex++;

                if (startIndex + count >= nodes.Count)
                {
                    batchIndex = 0;
                }
            }

            using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
            {
                var property = _participantIndex % 3;

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
        }
    }
}
