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
    private const int MinCollectionSize = 10;
    private const int MaxCollectionSize = 30;
    private const int MaxTotalNodes = 500;

    private readonly TestNode _root;
    private readonly ParticipantConfiguration _configuration;
    private readonly TestCycleCoordinator _coordinator;
    private readonly Random _structuralMutationRandom = new();

    protected readonly MutationCounters Counters = new();
    protected readonly KnownNodeGraph Graph = new();
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
                    PerformStructuralMutation();
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

    private void PerformStructuralMutation()
    {
        TestNode target;
        int totalNodeCount;
        lock (Graph.NodeLock)
        {
            if (Graph.StructuralTargets.Count == 0)
            {
                return;
            }

            target = Graph.StructuralTargets[_structuralMutationRandom.Next(Graph.StructuralTargets.Count)];
            totalNodeCount = Graph.KnownNodes.Count;
        }

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
        var uniqueKey = $"item-{GlobalMutationCounter.Next()}";
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
