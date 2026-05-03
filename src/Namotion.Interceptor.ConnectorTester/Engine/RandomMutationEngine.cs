using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Value mutation strategy: picks a random node and random property, one at a time.
/// Used for chaos profiles (BatchSize = 0).
/// When UseTransactions is enabled, each tick's batch of mutations is wrapped
/// in a single transaction.
/// </summary>
public class RandomMutationEngine : MutationEngine
{
    private readonly Random _valueMutationRandom = new();

    public RandomMutationEngine(
        TestNode root,
        ParticipantConfiguration configuration,
        TestCycleCoordinator coordinator,
        ILogger logger)
        : base(root, configuration, coordinator, logger)
    {
    }

    protected override async Task RunValueMutationsAsync(CancellationToken stoppingToken)
    {
        var mutationsPerMs = Math.Max(1, ValueMutationRate) / 1000.0;
        var batchSize = Math.Max(1, (int)Math.Ceiling(mutationsPerMs));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Coordinator.WaitIfPaused(stoppingToken);

                if (UseTransactions)
                {
                    using var transaction = await Context.BeginTransactionAsync(
                        TransactionFailureHandling.BestEffort);

                    for (var i = 0; i < batchSize; i++)
                    {
                        PerformValueMutation();
                        IncrementValueMutationCount();
                    }

                    await transaction.CommitAsync(stoppingToken);
                }
                else
                {
                    for (var i = 0; i < batchSize; i++)
                    {
                        PerformValueMutation();
                        IncrementValueMutationCount();
                    }
                }

                await Task.Delay(1, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void PerformValueMutation()
    {
        TestNode node;
        lock (NodeLock)
        {
            node = KnownNodes[_valueMutationRandom.Next(KnownNodes.Count)];
        }

        var property = _valueMutationRandom.Next(4);
        var counter = NextGlobalCounter();

        using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
        {
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
        }
    }
}
