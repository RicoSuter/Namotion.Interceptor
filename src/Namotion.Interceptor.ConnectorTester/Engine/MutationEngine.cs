using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Abstract base for mutation engines. Provides shared infrastructure:
/// global counter, graph traversal, node locking, cycle coordinator integration,
/// structural mutations, and mutation counters. Subclasses implement the value
/// mutation strategy via <see cref="RunValueMutationsAsync"/>.
/// </summary>
public abstract class MutationEngine : BackgroundService
{
    private readonly TestNode _root;
    private readonly ParticipantConfiguration _configuration;
    private readonly TestCycleCoordinator _coordinator;

    protected readonly MutationCounters Counters = new();
    protected readonly KnownNodeGraph Graph = new();
    protected readonly StructuralMutator StructuralMutator;
    protected readonly ILogger Logger;

    public string Name => _configuration.Name;
    public int ValueMutationRate => _configuration.ValueMutationRate;
    public int StructuralMutationRate => _configuration.StructuralMutationRate;
    public long ValueMutationCount => Counters.ValueMutationCount;
    public long StructuralMutationCount => Counters.StructuralMutationCount;

    protected TestNode Root => _root;
    protected IInterceptorSubjectContext Context => ((IInterceptorSubject)_root).Context;
    protected bool UseTransactions => _configuration.UseTransactions;
    protected TestCycleCoordinator Coordinator => _coordinator;

    protected MutationEngine(
        TestNode root,
        ParticipantConfiguration configuration,
        TestCycleCoordinator coordinator,
        ILogger logger)
    {
        _root = root;
        _configuration = configuration;
        _coordinator = coordinator;
        Logger = logger;
        StructuralMutator = new StructuralMutator(Graph);
    }

    public void ResetCounters() => Counters.Reset();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation(
            "MutationEngine [{Name}] started at {Rate} value mutations/sec, {StructuralRate} structural mutations/sec",
            _configuration.Name, _configuration.ValueMutationRate, _configuration.StructuralMutationRate);

        Graph.Rebuild(_root);

        var tasks = new List<Task> { RunValueMutationsAsync(stoppingToken) };

        if (_configuration.StructuralMutationRate > 0)
        {
            tasks.Add(RunStructuralMutationsAsync(stoppingToken));
        }

        await Task.WhenAll(tasks);
    }

    protected abstract Task RunValueMutationsAsync(CancellationToken stoppingToken);

    private async Task RunStructuralMutationsAsync(CancellationToken stoppingToken)
    {
        var (batchSize, delayMs) = TickPlan.From(_configuration.StructuralMutationRate);
        var rebuildCounter = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _coordinator.WaitIfPaused(stoppingToken);

                for (var i = 0; i < batchSize; i++)
                {
                    StructuralMutator.PerformMutation();
                    Counters.IncrementStructural();
                }

                rebuildCounter += batchSize;
                if (rebuildCounter >= 10)
                {
                    Graph.Rebuild(_root);
                    rebuildCounter = 0;
                }

                await Task.Delay(delayMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Structural mutation failed, continuing");
            }
        }
    }
}
