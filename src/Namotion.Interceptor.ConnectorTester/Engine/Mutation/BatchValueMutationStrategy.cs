using System.Diagnostics;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// Value mutation strategy: cycles through all nodes in parallel batches.
/// Used for load profiles (NumberOfBatches > 0).
/// Mutates ValueMutationRate nodes per second, spread across NumberOfBatches
/// batches with even distribution via a PeriodicTimer at 110% tick rate.
/// Each participant mutates a single fixed property (participantIndex % 4)
/// to avoid OPC UA subscription coalescing.
/// When UseTransactions is enabled, each batch is wrapped in a transaction
/// (sequential, since transactions are not thread-safe with Parallel.For).
/// </summary>
public sealed class BatchValueMutationStrategy : IValueMutationStrategy
{
    private readonly KnownNodeGraph _graph;
    private readonly TestCycleCoordinator _coordinator;
    private readonly IInterceptorSubjectContext _context;
    private readonly MutationCounters _counters;
    private readonly bool _useTransactions;
    private readonly int _valueMutationRate;
    private readonly int _numberOfBatches;
    private readonly int _participantIndex;

    public BatchValueMutationStrategy(
        KnownNodeGraph graph,
        TestCycleCoordinator coordinator,
        IInterceptorSubjectContext context,
        MutationCounters counters,
        ParticipantConfiguration participantConfiguration,
        int numberOfBatches,
        int participantIndex)
    {
        _graph = graph;
        _coordinator = coordinator;
        _context = context;
        _counters = counters;
        _useTransactions = participantConfiguration.UseTransactions;
        _valueMutationRate = participantConfiguration.ValueMutationRate;
        _numberOfBatches = numberOfBatches;
        _participantIndex = participantIndex;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        int nodeCount;
        lock (_graph.NodeLock)
        {
            nodeCount = _graph.KnownNodes.Count;
        }

        if (nodeCount == 0)
        {
            return;
        }

        var nodesPerBatch = (int)Math.Ceiling((double)_valueMutationRate / _numberOfBatches);
        var timerIntervalMs = 1000.0 / (_numberOfBatches * 1.1);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(timerIntervalMs));

        var nodeIndex = 0;
        var mutationsThisSecond = 0;
        var cycleStart = Stopwatch.GetTimestamp();
        var property = _participantIndex % 4;

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (_coordinator.WaitIfPaused(cancellationToken))
            {
                // Convergence pause leaves cycleStart frozen while wall-clock advances.
                // Without resync, the rate limit's idle branch (elapsed < 1s) never fires
                // for the first ~3 minutes of the next mutate phase: the loop bursts at the
                // timer's max throughput (~22k/sec at NumberOfBatches=50, timer*1.1) until
                // cycleStart slowly catches wall (gaining ~0.06s per 0.94s window). Resync
                // here so every cycle starts at exactly the configured ValueMutationRate.
                cycleStart = Stopwatch.GetTimestamp();
                mutationsThisSecond = 0;
            }

            if (mutationsThisSecond >= _valueMutationRate)
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
            lock (_graph.NodeLock)
            {
                nodes = _graph.KnownNodes;
                nodeCount = nodes.Count;
                if (nodeCount == 0)
                {
                    continue;
                }
            }

            var count = Math.Min(Math.Min(nodesPerBatch, _valueMutationRate - mutationsThisSecond), nodeCount);

            if (_useTransactions)
            {
                await MutateBatchWithTransactionAsync(nodes, nodeCount, nodeIndex, count, property, cancellationToken);
            }
            else
            {
                MutateBatchParallel(nodes, nodeCount, nodeIndex, count, property);
            }

            nodeIndex = (nodeIndex + count) % nodeCount;
            mutationsThisSecond += count;
        }
    }

    private async Task MutateBatchWithTransactionAsync(
        List<TestNode> nodes, int nodeCount, int nodeIndex, int count, int property,
        CancellationToken cancellationToken)
    {
        using var transaction = await _context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort);

        using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
        {
            for (var j = 0; j < count; j++)
            {
                MutateNode(nodes[(nodeIndex + j) % nodeCount], property);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private void MutateBatchParallel(
        List<TestNode> nodes, int nodeCount, int nodeIndex, int count, int property)
    {
        // Bug fix #1: SubjectChangeContext._current is [ThreadStatic], so the scope opened on the
        // orchestrator thread is invisible to Parallel.For workers. Capture the batch timestamp here
        // and re-enter the scope inside each worker action so every mutation observes the same value.
        var batchTimestamp = DateTimeOffset.UtcNow;
        Parallel.For(0, count, j =>
        {
            using (SubjectChangeContext.WithChangedTimestamp(batchTimestamp))
            {
                MutateNode(nodes[(nodeIndex + j) % nodeCount], property);
            }
        });
    }

    private void MutateNode(TestNode node, int property)
    {
        var counter = GlobalMutationCounter.Next();

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

        _counters.IncrementValue();
    }
}
