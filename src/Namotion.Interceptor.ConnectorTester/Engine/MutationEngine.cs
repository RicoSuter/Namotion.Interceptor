using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Model;

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
    private const int MaxTotalNodes = 500;

    private static long _globalCounter;

    private readonly TestNode _root;
    private readonly ParticipantConfiguration _configuration;
    private readonly TestCycleCoordinator _coordinator;
    private readonly ILogger _logger;
    private readonly Random _valueMutationRandom = new();
    private readonly Random _structuralMutationRandom = new();
    private readonly Lock _nodeLock = new();

    private List<TestNode> _knownNodes = [];
    private List<TestNode> _structuralTargets = [];

    private long _valueMutationCount;
    private long _structuralMutationCount;

    public string Name => _configuration.Name;
    public int ValueMutationRate => _configuration.ValueMutationRate;
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
            _configuration.Name, _configuration.ValueMutationRate, _configuration.StructuralMutationRate);

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
        var delayMilliseconds = 1000 / Math.Max(1, _configuration.ValueMutationRate);

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

            VisitNode(_root, 0);
        }
    }

    private void VisitNode(TestNode node, int depth, HashSet<TestNode>? visited = null)
    {
        visited ??= [];
        if (!visited.Add(node))
        {
            return;
        }

        _knownNodes.Add(node);

        if (depth < MaxDepth)
        {
            _structuralTargets.Add(node);
        }

        foreach (var child in node.Collection)
        {
            VisitNode(child, depth + 1, visited);
        }

        foreach (var child in node.Items.Values)
        {
            VisitNode(child, depth + 1, visited);
        }

        if (node.ObjectRef != null)
        {
            VisitNode(node.ObjectRef, depth + 1, visited);
        }
    }

    private void PerformValueMutation()
    {
        TestNode node;
        lock (_nodeLock)
        {
            node = _knownNodes[_valueMutationRandom.Next(_knownNodes.Count)];
        }

        var property = _valueMutationRandom.Next(3);
        var counter = Interlocked.Increment(ref _globalCounter);

        // Note: The node is selected under _nodeLock but mutated outside it.
        // A concurrent structural mutation could remove this node from the graph.
        // This is acceptable: the property assignment still succeeds on the CLR object,
        // and the node will simply no longer be tracked after the next RebuildKnownNodes().
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
        int totalNodeCount;
        lock (_nodeLock)
        {
            if (_structuralTargets.Count == 0)
            {
                return;
            }

            target = _structuralTargets[_structuralMutationRandom.Next(_structuralTargets.Count)];
            totalNodeCount = _knownNodes.Count;
        }

        // Pick random operation category: 0=Collection, 1=Dictionary, 2=ObjectRef
        var category = _structuralMutationRandom.Next(3);

        using (SubjectChangeContext.WithChangedTimestamp(DateTimeOffset.UtcNow))
        {
            switch (category)
            {
                case 0:
                    MutateCollection(target, totalNodeCount);
                    break;
                case 1:
                    MutateDictionary(target, totalNodeCount);
                    break;
                case 2:
                    MutateObjectRef(target, totalNodeCount);
                    break;
            }
        }

        RebuildKnownNodes();
    }

    private void MutateCollection(TestNode target, int totalNodeCount)
    {
        var collection = target.Collection;
        var count = collection.Length;
        var atNodeLimit = totalNodeCount >= MaxTotalNodes;

        if (count >= MaxCollectionSize || (atNodeLimit && count > MinCollectionSize))
        {
            RemoveFromCollection(target, collection);
        }
        else if (count <= MinCollectionSize && !atNodeLimit)
        {
            AddToCollection(target, collection);
        }
        else if (atNodeLimit)
        {
            RemoveFromCollection(target, collection);
        }
        else
        {
            if (_structuralMutationRandom.Next(2) == 0)
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

        var index = _structuralMutationRandom.Next(collection.Length);
        target.Collection = [.. collection[..index], .. collection[(index + 1)..]];
    }

    private void MutateDictionary(TestNode target, int totalNodeCount)
    {
        var items = target.Items;
        var count = items.Count;
        var atNodeLimit = totalNodeCount >= MaxTotalNodes;

        if (count >= MaxCollectionSize || (atNodeLimit && count > MinCollectionSize))
        {
            RemoveFromDictionary(target, items);
        }
        else if (count <= MinCollectionSize && !atNodeLimit)
        {
            AddToDictionary(target);
        }
        else if (atNodeLimit)
        {
            RemoveFromDictionary(target, items);
        }
        else
        {
            if (_structuralMutationRandom.Next(2) == 0)
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
        var key = keys[_structuralMutationRandom.Next(keys.Count)];
        var newItems = new Dictionary<string, TestNode>(items);
        newItems.Remove(key);
        target.Items = newItems;
    }

    private void MutateObjectRef(TestNode target, int totalNodeCount)
    {
        var atNodeLimit = totalNodeCount >= MaxTotalNodes;

        if (target.ObjectRef != null && (_structuralMutationRandom.Next(2) == 0 || atNodeLimit))
        {
            target.ObjectRef = null;
        }
        else if (!atNodeLimit)
        {
            target.ObjectRef = CreateNewNode();
        }
    }

    private TestNode CreateNewNode()
    {
        return new TestNode();
    }
}
