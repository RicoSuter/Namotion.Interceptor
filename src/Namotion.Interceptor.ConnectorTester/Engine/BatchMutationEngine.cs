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

    public BatchMutationEngine(
        TestNode root,
        ParticipantConfiguration configuration,
        TestCycleCoordinator coordinator,
        ILogger logger,
        int batchSize,
        int batchIntervalMs)
        : base(root, configuration, coordinator, logger)
    {
        _batchSize = batchSize;
        _batchIntervalMs = batchIntervalMs;
    }

    protected override async Task RunValueMutationsAsync(CancellationToken stoppingToken)
    {
        var batchIndex = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Coordinator.WaitIfPaused(stoppingToken);

                List<TestNode> batch;
                lock (NodeLock)
                {
                    if (KnownNodes.Count == 0)
                    {
                        await Task.Delay(_batchIntervalMs, stoppingToken);
                        continue;
                    }

                    var startIndex = (batchIndex * _batchSize) % KnownNodes.Count;
                    var count = Math.Min(_batchSize, KnownNodes.Count - startIndex);
                    batch = KnownNodes.GetRange(startIndex, count);
                    batchIndex++;

                    if (startIndex + count >= KnownNodes.Count)
                    {
                        batchIndex = 0;
                    }
                }

                using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
                {
                    Parallel.ForEach(batch, node =>
                    {
                        var counter = NextGlobalCounter();
                        var property = (int)(counter % 3);

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

                await Task.Delay(_batchIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
