namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// One value-mutation strategy. The host calls RunAsync on a background task; the
/// strategy is responsible for honoring TestCycleCoordinator pauses, mutation
/// counter increments, optional transaction wrapping, and cancellation.
/// </summary>
public interface IValueMutationStrategy
{
    Task RunAsync(CancellationToken cancellationToken);
}
