using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Randomly mutates value properties and optionally performs structural mutations
/// (collection add/remove, dictionary add/remove, object ref set/clear) on a TestNode graph.
/// Each participant (server + each client) gets its own MutationEngine.
/// Uses a shared global counter so every mutation produces a globally unique value,
/// preventing the equality interceptor from dropping changes and ensuring
/// source timestamps propagate correctly to all participants.
/// </summary>
public class MutationEngine : BackgroundService
{
    private const int MinCollectionSize = 10;
    private const int MaxCollectionSize = 30;
    private const int MaxDepth = 3;

    private static long _globalCounter;

    private readonly TestNode _root;
    private readonly ParticipantConfiguration _configuration;
    private readonly TestCycleCoordinator _coordinator;
    private readonly ILogger _logger;
    private readonly Random _random = new();
    private readonly object _nodeLock = new();

    private List<TestNode> _knownNodes = [];
    private List<TestNode> _structuralTargets = [];
    private readonly Dictionary<TestNode, int> _nodeDepths = new();

    private long _valueMutationCount;
    private long _structuralMutationCount;

    public string Name => _configuration.Name;
    public int MutationRate => _configuration.MutationRate;
    public int StructuralMutationRate => _configuration.StructuralMutationRate;
    public long ValueMutationCount => Interlocked.Read(ref _valueMutationCount);
    public long StructuralMutationCount => Interlocked.Read(ref _structuralMutationCount);

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
        Interlocked.Exchange(ref _valueMutationCount, 0);
        Interlocked.Exchange(ref _structuralMutationCount, 0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "MutationEngine [{Name}] started at {Rate} value mutations/sec, {StructuralRate} structural mutations/sec",
            _configuration.Name, _configuration.MutationRate, _configuration.StructuralMutationRate);

        RebuildKnownNodes();

        var tasks = new List<Task> { RunValueMutationsAsync(stoppingToken) };

        if (_configuration.StructuralMutationRate > 0)
        {
            tasks.Add(RunStructuralMutationsAsync(stoppingToken));
        }

        await Task.WhenAll(tasks);
    }

    private async Task RunValueMutationsAsync(CancellationToken stoppingToken)
    {
        var delayMilliseconds = 1000 / Math.Max(1, _configuration.MutationRate);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _coordinator.WaitIfPaused(stoppingToken);
                PerformValueMutation();
                Interlocked.Increment(ref _valueMutationCount);

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

    private async Task RunStructuralMutationsAsync(CancellationToken stoppingToken)
    {
        var delayMilliseconds = 1000 / Math.Max(1, _configuration.StructuralMutationRate);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _coordinator.WaitIfPaused(stoppingToken);
                PerformStructuralMutation();
                Interlocked.Increment(ref _structuralMutationCount);

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
        lock (_nodeLock)
        {
            _knownNodes = [];
            _structuralTargets = [];
            _nodeDepths.Clear();

            VisitNode(_root, 0);
        }
    }

    private void VisitNode(TestNode node, int depth)
    {
        _knownNodes.Add(node);
        _nodeDepths[node] = depth;

        if (depth < MaxDepth)
        {
            _structuralTargets.Add(node);
        }

        foreach (var child in node.Collection)
        {
            VisitNode(child, depth + 1);
        }

        foreach (var child in node.Items.Values)
        {
            VisitNode(child, depth + 1);
        }

        if (node.ObjectRef != null)
        {
            VisitNode(node.ObjectRef, depth + 1);
        }
    }

    private void PerformValueMutation()
    {
        TestNode node;
        lock (_nodeLock)
        {
            node = _knownNodes[_random.Next(_knownNodes.Count)];
        }

        var property = _random.Next(3);
        var counter = Interlocked.Increment(ref _globalCounter);

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

    private void PerformStructuralMutation()
    {
        TestNode target;
        lock (_nodeLock)
        {
            if (_structuralTargets.Count == 0)
            {
                return;
            }

            target = _structuralTargets[_random.Next(_structuralTargets.Count)];
        }

        // Pick random operation category: 0=Collection, 1=Dictionary, 2=ObjectRef
        var category = _random.Next(3);

        using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
        {
            switch (category)
            {
                case 0:
                    MutateCollection(target);
                    break;
                case 1:
                    MutateDictionary(target);
                    break;
                case 2:
                    MutateObjectRef(target);
                    break;
            }
        }

        RebuildKnownNodes();
    }

    private void MutateCollection(TestNode target)
    {
        var collection = target.Collection;
        var count = collection.Length;

        if (count >= MaxCollectionSize)
        {
            // Must remove
            RemoveFromCollection(target, collection);
        }
        else if (count <= MinCollectionSize)
        {
            // Must add
            AddToCollection(target, collection);
        }
        else
        {
            // Randomly add or remove
            if (_random.Next(2) == 0)
            {
                AddToCollection(target, collection);
            }
            else
            {
                RemoveFromCollection(target, collection);
            }
        }
    }

    private void AddToCollection(TestNode target, TestNode[] collection)
    {
        var newNode = CreateNewNode();
        target.Collection = [.. collection, newNode];
    }

    private void RemoveFromCollection(TestNode target, TestNode[] collection)
    {
        if (collection.Length == 0)
        {
            return;
        }

        var index = _random.Next(collection.Length);
        target.Collection = [.. collection[..index], .. collection[(index + 1)..]];
    }

    private void MutateDictionary(TestNode target)
    {
        var items = target.Items;
        var count = items.Count;

        if (count >= MaxCollectionSize)
        {
            RemoveFromDictionary(target, items);
        }
        else if (count <= MinCollectionSize)
        {
            AddToDictionary(target);
        }
        else
        {
            if (_random.Next(2) == 0)
            {
                AddToDictionary(target);
            }
            else
            {
                RemoveFromDictionary(target, items);
            }
        }
    }

    private void AddToDictionary(TestNode target)
    {
        var uniqueKey = $"item-{Interlocked.Increment(ref _globalCounter)}";
        var newItems = new Dictionary<string, TestNode>(target.Items)
        {
            [uniqueKey] = CreateNewNode()
        };
        target.Items = newItems;
    }

    private void RemoveFromDictionary(TestNode target, Dictionary<string, TestNode> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var keys = items.Keys.ToList();
        var key = keys[_random.Next(keys.Count)];
        var newItems = new Dictionary<string, TestNode>(items);
        newItems.Remove(key);
        target.Items = newItems;
    }

    private void MutateObjectRef(TestNode target)
    {
        if (target.ObjectRef != null && _random.Next(2) == 0)
        {
            target.ObjectRef = null;
        }
        else
        {
            target.ObjectRef = CreateNewNode();
        }
    }

    /// <summary>
    /// Creates a new TestNode and re-sets its default value properties to ensure
    /// write timestamps are recorded. Constructor property sets don't trigger
    /// the tracking interceptor, so without this, default values have no timestamp.
    /// </summary>
    private TestNode CreateNewNode()
    {
        var context = ((IInterceptorSubject)_root).Context;
        var node = new TestNode(context);

        // Re-assign defaults to trigger write interceptor and record timestamps.
        node.StringValue = string.Empty;
        node.DecimalValue = 0;
        node.IntValue = 0;

        return node;
    }
}
