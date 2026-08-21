using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// Default value-mutation strategy: picks a random node and a random property
/// per tick, honoring TestCycleCoordinator pauses and optional transaction wrapping.
/// When DisjointProperties is enabled, the property is fixed to the participant's
/// own index instead of being picked at random, so the write-durability oracle
/// never sees two participants writing the same property.
/// </summary>
public sealed class RandomValueMutationStrategy : IValueMutationStrategy
{
    private readonly KnownNodeGraph _graph;
    private readonly TestCycleCoordinator _coordinator;
    private readonly IInterceptorSubjectContext _context;
    private readonly MutationCounters _counters;
    private readonly WriteDurabilityLedger _ledger;
    private readonly bool _useTransactions;
    private readonly int _valueMutationRate;
    private readonly int _participantIndex;
    private readonly bool _disjointProperties;
    private readonly Random _random = new();

    public RandomValueMutationStrategy(
        KnownNodeGraph graph,
        TestCycleCoordinator coordinator,
        IInterceptorSubjectContext context,
        MutationCounters counters,
        ParticipantConfiguration participantConfiguration,
        int participantIndex,
        bool disjointProperties,
        WriteDurabilityLedger ledger)
    {
        _graph = graph;
        _coordinator = coordinator;
        _context = context;
        _counters = counters;
        _ledger = ledger;
        _useTransactions = participantConfiguration.UseTransactions;
        _valueMutationRate = participantConfiguration.ValueMutationRate;
        _participantIndex = participantIndex;
        _disjointProperties = disjointProperties;
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

                    var batch = new List<(TestNode Node, int Property, object? Value)>(batchSize);
                    for (var i = 0; i < batchSize; i++)
                    {
                        batch.Add(PerformValueMutation());
                        _counters.IncrementValue();
                    }

                    try
                    {
                        await transaction.CommitAsync(cancellationToken);
                        foreach (var (node, property, value) in batch)
                        {
                            _ledger.Record(node, property, value);
                        }
                    }
                    catch (SubjectTransactionException)
                    {
                        // A commit failure is legitimate under BestEffort with a dying transport: the
                        // failed properties never applied locally either, so model and peer still agree.
                        _counters.IncrementFailedCommit();
                        foreach (var (node, property, _) in batch)
                        {
                            _ledger.Forget(node, property);
                        }
                    }
                }
                else
                {
                    for (var i = 0; i < batchSize; i++)
                    {
                        var (node, property, value) = PerformValueMutation();
                        _ledger.Record(node, property, value);
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

    private (TestNode Node, int Property, object? Value) PerformValueMutation()
    {
        TestNode node;
        lock (_graph.NodeLock)
        {
            node = _graph.KnownNodes[_random.Next(_graph.KnownNodes.Count)];
        }

        var property = _disjointProperties ? _participantIndex % 4 : _random.Next(4);
        var counter = GlobalMutationCounter.Next();
        object value;

        using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
        {
            switch (property)
            {
                case 0:
                    var stringValue = counter.ToString("x8");
                    node.StringValue = stringValue;
                    value = stringValue;
                    break;
                case 1:
                    var decimalValue = counter / 100m;
                    node.DecimalValue = decimalValue;
                    value = decimalValue;
                    break;
                case 2:
                    var intValue = (int)(counter % int.MaxValue);
                    node.IntValue = intValue;
                    value = intValue;
                    break;
                default:
                    node.LongValue = counter;
                    value = counter;
                    break;
            }
        }

        return (node, property, value);
    }
}
