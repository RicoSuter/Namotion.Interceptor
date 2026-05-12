using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Engine;

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
public class BatchMutationEngine : MutationEngine
{
    private readonly BatchValueMutationStrategy _strategy;

    public BatchMutationEngine(
        TestNode root,
        ParticipantConfiguration configuration,
        TestCycleCoordinator coordinator,
        ILogger logger,
        int numberOfBatches,
        int participantIndex)
        : base(root, configuration, coordinator, logger)
    {
        _strategy = new BatchValueMutationStrategy(
            Graph,
            coordinator,
            ((IInterceptorSubject)root).Context,
            Counters,
            configuration,
            numberOfBatches,
            participantIndex);
    }

    protected override Task RunValueMutationsAsync(CancellationToken stoppingToken)
        => _strategy.RunAsync(stoppingToken);
}
