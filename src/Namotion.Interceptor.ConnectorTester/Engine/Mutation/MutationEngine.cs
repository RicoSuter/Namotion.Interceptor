using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// BackgroundService that drives one participant's value-mutation strategy and an
/// optional structural-mutation loop. Composes <see cref="KnownNodeGraph"/> +
/// <see cref="IValueMutationStrategy"/> + <see cref="StructuralMutator"/> +
/// <see cref="MutationCounters"/>. Honors <see cref="TestCycleCoordinator"/> pauses.
/// </summary>
public sealed class MutationEngine : BackgroundService
{
    private const int RebuildEveryStructuralMutations = 10;

    private readonly TestNode _root;
    private readonly KnownNodeGraph _graph;
    private readonly StructuralMutator _structuralMutator;
    private readonly IValueMutationStrategy _valueStrategy;
    private readonly TestCycleCoordinator _coordinator;
    private readonly MutationCounters _counters;
    private readonly WriteDurabilityLedger? _ledger;
    private readonly ILogger _logger;
    private readonly int _structuralMutationRate;

    public string Name { get; }
    public int ValueMutationRate { get; }
    public int StructuralMutationRate => _structuralMutationRate;
    public long ValueMutationCount => _counters.ValueMutationCount;
    public long StructuralMutationCount => _counters.StructuralMutationCount;
    public long CommitCount => _counters.CommitCount;
    public long ConflictCommitCount => _counters.ConflictCommitCount;
    public long SourceWriteFailedCommitCount => _counters.SourceWriteFailedCommitCount;
    public long UnexpectedFailedCommitCount => _counters.UnexpectedFailedCommitCount;

    public void ResetCounters() => _counters.Reset();

    /// <summary>Clears the write-durability ledger, if any.</summary>
    public void ResetDurabilityLedger() => _ledger?.Reset();

    /// <summary>
    /// Gets whether the participant's source is synchronized (see <see cref="WriteDurabilityLedger.IsSourceSynchronized"/>),
    /// and <c>true</c> when the engine has no write-durability ledger.
    /// </summary>
    public bool IsSourceSynchronized => _ledger?.IsSourceSynchronized ?? true;

    /// <summary>
    /// Checks the writes the ledger recorded against this participant's model. The result lists one line per write
    /// the model no longer holds, prefixed with the participant name, and is empty when the engine has no ledger.
    /// </summary>
    public WriteDurabilityResult VerifyWriteDurability()
    {
        if (_ledger is null)
        {
            return new WriteDurabilityResult(Name, [], DroppedWriteCount: 0);
        }

        // The known nodes lag structural changes, local and remote, until the next rebuild.
        _graph.Rebuild(_root);
        List<TestNode> knownNodes;
        lock (_graph.NodeLock)
        {
            knownNodes = _graph.KnownNodes;
        }

        var violations = _ledger.Verify(knownNodes)
            .Select(violation => $"{Name}: {violation}")
            .ToList();

        // Read after the model, so a drop that changed what the model holds is counted.
        return new WriteDurabilityResult(Name, violations, _ledger.DroppedWriteCount);
    }

    public MutationEngine(
        TestNode root,
        ParticipantConfiguration participantConfiguration,
        TestCycleCoordinator coordinator,
        IValueMutationStrategy valueStrategy,
        KnownNodeGraph graph,
        MutationCounters counters,
        ILogger logger,
        WriteDurabilityLedger? ledger = null)
    {
        _root = root;
        _graph = graph;
        _structuralMutator = new StructuralMutator(_graph);
        _valueStrategy = valueStrategy;
        _coordinator = coordinator;
        _counters = counters;
        _ledger = ledger;
        _logger = logger;
        Name = participantConfiguration.Name;
        ValueMutationRate = participantConfiguration.ValueMutationRate;
        _structuralMutationRate = participantConfiguration.StructuralMutationRate;
    }

    public static MutationEngine CreateRandom(
        TestNode root,
        ParticipantConfiguration participantConfiguration,
        TestCycleCoordinator coordinator,
        ILogger logger,
        bool verifyWriteDurability = false,
        ISubjectSource? source = null)
    {
        var graph = new KnownNodeGraph();
        var counters = new MutationCounters();
        var ledger = verifyWriteDurability ? new WriteDurabilityLedger(source) : null;
        var context = ((IInterceptorSubject)root).Context;
        var strategy = new RandomValueMutationStrategy(graph, coordinator, context, counters, participantConfiguration, logger, ledger);
        return new MutationEngine(root, participantConfiguration, coordinator, strategy, graph, counters, logger, ledger);
    }

    public static MutationEngine CreateBatch(
        TestNode root,
        ParticipantConfiguration participantConfiguration,
        TestCycleCoordinator coordinator,
        ILogger logger,
        int numberOfBatches,
        int participantIndex,
        bool verifyWriteDurability = false,
        ISubjectSource? source = null)
    {
        var graph = new KnownNodeGraph();
        var counters = new MutationCounters();
        var ledger = verifyWriteDurability ? new WriteDurabilityLedger(source) : null;
        var context = ((IInterceptorSubject)root).Context;
        var strategy = new BatchValueMutationStrategy(graph, coordinator, context, counters, participantConfiguration, numberOfBatches, participantIndex, logger, ledger);
        return new MutationEngine(root, participantConfiguration, coordinator, strategy, graph, counters, logger, ledger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "MutationEngine [{Name}] started at {Rate} value mutations/sec, {StructuralRate} structural mutations/sec",
            Name, ValueMutationRate, _structuralMutationRate);

        _graph.Rebuild(_root);

        var tasks = new List<Task> { _valueStrategy.RunAsync(stoppingToken) };

        if (_structuralMutationRate > 0)
        {
            tasks.Add(RunStructuralLoopAsync(stoppingToken));
        }

        await Task.WhenAll(tasks);
    }

    private async Task RunStructuralLoopAsync(CancellationToken cancellationToken)
    {
        var (batchSize, delayMs) = TickPlan.From(_structuralMutationRate);
        var rebuildCounter = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _coordinator.WaitIfPaused(cancellationToken);

                for (var i = 0; i < batchSize; i++)
                {
                    _structuralMutator.PerformMutation();
                    _counters.IncrementStructural();
                }

                rebuildCounter += batchSize;
                if (rebuildCounter >= RebuildEveryStructuralMutations)
                {
                    _graph.Rebuild(_root);
                    rebuildCounter = 0;
                }

                await Task.Delay(delayMs, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Structural mutation failed, continuing");
            }
        }
    }
}
