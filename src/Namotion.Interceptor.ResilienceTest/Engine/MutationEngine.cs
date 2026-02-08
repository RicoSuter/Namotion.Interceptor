using Namotion.Interceptor.ResilienceTest.Configuration;
using Namotion.Interceptor.ResilienceTest.Model;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.ResilienceTest.Engine;

/// <summary>
/// Randomly mutates value properties on a TestNode graph.
/// Each participant (server + each client) gets its own MutationEngine.
/// Uses a shared global counter so every mutation produces a globally unique value,
/// preventing the equality interceptor from dropping changes and ensuring
/// source timestamps propagate correctly to all participants.
/// </summary>
public class MutationEngine : BackgroundService
{
    private static long _globalCounter;

    private readonly TestNode _root;
    private readonly ParticipantConfiguration _configuration;
    private readonly TestCycleCoordinator _coordinator;
    private readonly ILogger _logger;
    private readonly Random _random = new();

    private List<TestNode> _knownNodes = [];

    public string Name => _configuration.Name;
    public int MutationRate => _configuration.MutationRate;
    public long ValueMutationCount { get; private set; }

    public MutationEngine(
        TestNode root,
        ParticipantConfiguration configuration,
        TestCycleCoordinator coordinator,
        ILogger logger)
    {
        _root = root;
        _configuration = configuration;
        _coordinator = coordinator;
        _logger = logger;
    }

    /// <summary>Resets counters at the start of each cycle.</summary>
    public void ResetCounters()
    {
        ValueMutationCount = 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MutationEngine [{Name}] started at {Rate} mutations/sec",
            _configuration.Name, _configuration.MutationRate);

        RebuildKnownNodes();

        var delayMilliseconds = 1000 / Math.Max(1, _configuration.MutationRate);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _coordinator.WaitIfPaused(stoppingToken);
                PerformValueMutation();
                ValueMutationCount++;

                if (delayMilliseconds > 0)
                {
                    await Task.Delay(delayMilliseconds, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void RebuildKnownNodes()
    {
        _knownNodes = [_root];
        _knownNodes.AddRange(_root.Collection);
        _knownNodes.AddRange(_root.Items.Values);

        // Include non-null ObjectRefs from all nodes
        foreach (var node in _knownNodes.ToList())
        {
            if (node.ObjectRef != null)
            {
                _knownNodes.Add(node.ObjectRef);
            }
        }
    }

    private void PerformValueMutation()
    {
        var node = _knownNodes[_random.Next(_knownNodes.Count)];
        var property = _random.Next(3);
        var counter = Interlocked.Increment(ref _globalCounter);

        // Use explicit timestamp scope so that all interceptors and change queue
        // observers see the same timestamp for this mutation.
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
            }
        }
    }
}
