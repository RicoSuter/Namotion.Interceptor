using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Value mutation strategy: picks a random node and random property, one at a time.
/// Used for chaos profiles (NumberOfBatches = 0).
/// When UseTransactions is enabled, each tick's batch of mutations is wrapped
/// in a single transaction.
/// </summary>
public class RandomMutationEngine : MutationEngine
{
    private readonly RandomValueMutationStrategy _strategy;

    public RandomMutationEngine(
        TestNode root,
        ParticipantConfiguration configuration,
        TestCycleCoordinator coordinator,
        ILogger logger)
        : base(root, configuration, coordinator, logger)
    {
        _strategy = new RandomValueMutationStrategy(Graph, coordinator, ((IInterceptorSubject)root).Context, Counters, configuration);
    }

    protected override Task RunValueMutationsAsync(CancellationToken stoppingToken) => _strategy.RunAsync(stoppingToken);
}
