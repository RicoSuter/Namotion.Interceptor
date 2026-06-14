using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// Default value-mutation strategy: picks a random node and a random property
/// per tick, honoring TestCycleCoordinator pauses and optional transaction wrapping.
/// </summary>
public sealed class RandomValueMutationStrategy : IValueMutationStrategy
{
    private readonly KnownNodeGraph _graph;
    private readonly TestCycleCoordinator _coordinator;
    private readonly IInterceptorSubjectContext _context;
    private readonly MutationCounters _counters;
    private readonly bool _useTransactions;
    private readonly int _valueMutationRate;
    private readonly Random _random = new();

    public RandomValueMutationStrategy(
        KnownNodeGraph graph,
        TestCycleCoordinator coordinator,
        IInterceptorSubjectContext context,
        MutationCounters counters,
        ParticipantConfiguration participantConfiguration)
    {
        _graph = graph;
        _coordinator = coordinator;
        _context = context;
        _counters = counters;
        _useTransactions = participantConfiguration.UseTransactions;
        _valueMutationRate = participantConfiguration.ValueMutationRate;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var (batchSize, delayMs) = TickPlan.From(_valueMutationRate);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _coordinator.WaitIfPaused(cancellationToken);

                if (_useTransactions)
                {
                    using var transaction = await _context.BeginTransactionAsync(
                        TransactionFailureHandling.BestEffort);

                    for (var i = 0; i < batchSize; i++)
                    {
                        PerformValueMutation();
                        _counters.IncrementValue();
                    }

                    await transaction.CommitAsync(cancellationToken);
                }
                else
                {
                    for (var i = 0; i < batchSize; i++)
                    {
                        PerformValueMutation();
                        _counters.IncrementValue();
                    }
                }

                await Task.Delay(delayMs, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void PerformValueMutation()
    {
        TestNode node;
        lock (_graph.NodeLock)
        {
            node = _graph.KnownNodes[_random.Next(_graph.KnownNodes.Count)];
        }

        var property = _random.Next(4);
        var counter = GlobalMutationCounter.Next();

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
