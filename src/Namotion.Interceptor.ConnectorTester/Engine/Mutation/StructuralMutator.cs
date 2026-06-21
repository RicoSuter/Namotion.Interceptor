using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// Performs one structural mutation against a target picked from KnownNodeGraph.StructuralTargets.
/// Caller is responsible for the loop, counter increments, and rebuild scheduling.
/// </summary>
public sealed class StructuralMutator
{
    private const int MinCollectionSize = 10;
    private const int MaxCollectionSize = 30;
    private const int MaxTotalNodes = 500;

    private readonly KnownNodeGraph _graph;
    private readonly Random _random = new();

    public StructuralMutator(KnownNodeGraph graph)
    {
        _graph = graph;
    }

    public void PerformMutation()
    {
        TestNode target;
        int totalNodeCount;
        lock (_graph.NodeLock)
        {
            if (_graph.StructuralTargets.Count == 0)
            {
                return;
            }

            target = _graph.StructuralTargets[_random.Next(_graph.StructuralTargets.Count)];
            totalNodeCount = _graph.KnownNodes.Count;
        }

        var category = _random.Next(3);

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
        target.Collection = [.. collection, new TestNode()];
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
        var uniqueKey = $"item-{GlobalMutationCounter.Next()}";
        var newItems = new Dictionary<string, TestNode>(target.Items)
        {
            [uniqueKey] = new TestNode()
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

    private void MutateObjectRef(TestNode target, int totalNodeCount)
    {
        var atNodeLimit = totalNodeCount >= MaxTotalNodes;

        if (target.ObjectRef != null && (_random.Next(2) == 0 || atNodeLimit))
        {
            target.ObjectRef = null;
        }
        else if (!atNodeLimit)
        {
            target.ObjectRef = new TestNode();
        }
    }
}
