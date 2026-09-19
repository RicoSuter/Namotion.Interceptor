using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
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
    private readonly WriteDurabilityLedger? _ledger;
    private readonly bool _useTransactions;
    private readonly int _valueMutationRate;
    private readonly int? _fixedProperty;
    private readonly Random _random = new();

    /// <summary>
    /// When <paramref name="ledger"/> is given, writes only the value property at the participant's
    /// <see cref="ParticipantConfiguration.Index"/> instead of a random one and records every applied write in it.
    /// </summary>
    public RandomValueMutationStrategy(
        KnownNodeGraph graph,
        TestCycleCoordinator coordinator,
        IInterceptorSubjectContext context,
        MutationCounters counters,
        ParticipantConfiguration participantConfiguration,
        WriteDurabilityLedger? ledger = null)
    {
        _graph = graph;
        _coordinator = coordinator;
        _context = context;
        _counters = counters;
        _ledger = ledger;
        _useTransactions = participantConfiguration.UseTransactions;
        _valueMutationRate = participantConfiguration.ValueMutationRate;
        _fixedProperty = ledger is null ? null : participantConfiguration.Index;
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

                    var uncommittedWrites = _ledger is null
                        ? null
                        : new List<(TestNode Node, int Property, long Counter)>(batchSize);
                    for (var i = 0; i < batchSize; i++)
                    {
                        var write = PerformValueMutation();
                        uncommittedWrites?.Add(write);
                        _counters.IncrementValue();
                    }

                    await transaction.CommitAsync(cancellationToken);
                    if (_ledger is not null && uncommittedWrites is not null)
                    {
                        foreach (var (node, property, counter) in uncommittedWrites)
                        {
                            _ledger.Record(node, property, counter);
                        }
                    }
                }
                else
                {
                    for (var i = 0; i < batchSize; i++)
                    {
                        var (node, property, counter) = PerformValueMutation();
                        _ledger?.Record(node, property, counter);
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

    private (TestNode Node, int Property, long Counter) PerformValueMutation()
    {
        TestNode node;
        lock (_graph.NodeLock)
        {
            node = _graph.KnownNodes[_random.Next(_graph.KnownNodes.Count)];
        }

        var property = _fixedProperty ?? _random.Next(TestNode.ValuePropertyCount);
        var counter = GlobalMutationCounter.Next();

        using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
        {
            TestNode.WriteValueProperty(node, property, counter);
        }

        return (node, property, counter);
    }
}
