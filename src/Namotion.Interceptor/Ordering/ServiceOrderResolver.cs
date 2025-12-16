using System.Collections.Concurrent;
using System.Reflection;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Ordering;

internal readonly struct ServiceOrderInfo
{
    public Type[] RunsBefore { get; }
    public Type[] RunsAfter { get; }
    public bool RunsFirst { get; }
    public bool RunsLast { get; }

    public ServiceOrderInfo(Type[] runsBefore, Type[] runsAfter, bool runsFirst, bool runsLast)
    {
        RunsBefore = runsBefore;
        RunsAfter = runsAfter;
        RunsFirst = runsFirst;
        RunsLast = runsLast;
    }
}

/// <summary>
/// Resolves service execution order based on ordering attributes.
/// Uses Kahn's algorithm for topological sorting with three-group partitioning (First, Middle, Last).
/// </summary>
public static class ServiceOrderResolver
{
    private static readonly ConcurrentDictionary<Type, ServiceOrderInfo> AttributeCache = new();

    /// <summary>
    /// Orders services by their ordering attributes.
    /// </summary>
    public static T[] OrderByDependencies<T>(List<T> services)
    {
        if (services.Count == 0)
        {
            return [];
        }

        if (services.Count == 1)
        {
            ValidateSingleService(services[0]!);
            return [services[0]];
        }

        var (firstGroup, middleGroup, lastGroup) = PartitionIntoGroups(services);
        ValidateCrossGroupDependencies(firstGroup, middleGroup, lastGroup);

        var orderedFirst = TopologicalSort(firstGroup);
        var orderedMiddle = TopologicalSort(middleGroup);
        var orderedLast = TopologicalSort(lastGroup);

        return ConcatenateResults(orderedFirst, orderedMiddle, orderedLast, services.Count);
    }

    private static void ValidateSingleService<T>(T service)
    {
        var info = GetOrderInfo(service!.GetType());
        if (info is { RunsFirst: true, RunsLast: true })
        {
            throw new InvalidOperationException(
                $"Service {service.GetType().Name} cannot have both [RunsFirst] and [RunsLast]");
        }
    }

    private static (List<T> first, List<T> middle, List<T> last) PartitionIntoGroups<T>(List<T> services)
    {
        var firstGroup = new List<T>();
        var middleGroup = new List<T>();
        var lastGroup = new List<T>();

        foreach (var service in services)
        {
            var info = GetOrderInfo(service!.GetType());
            if (info is { RunsFirst: true, RunsLast: true })
            {
                throw new InvalidOperationException(
                    $"Service {service.GetType().Name} cannot have both [RunsFirst] and [RunsLast]");
            }

            if (info.RunsFirst)
            {
                firstGroup.Add(service);
            }
            else if (info.RunsLast)
            {
                lastGroup.Add(service);
            }
            else
            {
                middleGroup.Add(service);
            }
        }

        return (firstGroup, middleGroup, lastGroup);
    }

    private static T[] ConcatenateResults<T>(T[] first, T[] middle, T[] last, int totalCount)
    {
        var result = new T[totalCount];
        first.CopyTo(result, 0);
        middle.CopyTo(result, first.Length);
        last.CopyTo(result, first.Length + middle.Length);
        return result;
    }

    private static T[] TopologicalSort<T>(List<T> services)
    {
        if (services.Count == 0)
        {
            return [];
        }

        if (services.Count == 1)
        {
            return [services[0]];
        }

        var (typeToIndex, indexToService) = BuildTypeIndexMapping(services);
        var (inDegree, adjacency) = BuildAdjacencyGraph(indexToService, typeToIndex);
        return ExecuteKahnsAlgorithm(indexToService, inDegree, adjacency);
    }

    private static (Dictionary<Type, int> typeToIndex, T[] indexToService) BuildTypeIndexMapping<T>(List<T> services)
    {
        var count = services.Count;
        var typeToIndex = new Dictionary<Type, int>(count);
        var indexToService = new T[count];

        for (var i = 0; i < count; i++)
        {
            var service = services[i];
            indexToService[i] = service;
            typeToIndex[service!.GetType()] = i;
        }

        return (typeToIndex, indexToService);
    }

    private static (int[] inDegree, List<int>?[] adjacency) BuildAdjacencyGraph<T>(
        T[] indexToService, Dictionary<Type, int> typeToIndex)
    {
        var count = indexToService.Length;
        var inDegree = new int[count];
        var adjacency = new List<int>?[count];

        for (var i = 0; i < count; i++)
        {
            var info = GetOrderInfo(indexToService[i]!.GetType());
            AddRunsBeforeEdges(i, info.RunsBefore, typeToIndex, adjacency, inDegree);
            AddRunsAfterEdges(i, info.RunsAfter, typeToIndex, adjacency, inDegree);
        }

        return (inDegree, adjacency);
    }

    private static void AddRunsBeforeEdges(
        int sourceIndex, Type[] beforeTypes, Dictionary<Type, int> typeToIndex,
        List<int>?[] adjacency, int[] inDegree)
    {
        foreach (var beforeType in beforeTypes)
        {
            if (typeToIndex.TryGetValue(beforeType, out var targetIndex))
            {
                adjacency[sourceIndex] ??= [];
                adjacency[sourceIndex]!.Add(targetIndex);
                inDegree[targetIndex]++;
            }
        }
    }

    private static void AddRunsAfterEdges(
        int targetIndex, Type[] afterTypes, Dictionary<Type, int> typeToIndex,
        List<int>?[] adjacency, int[] inDegree)
    {
        foreach (var afterType in afterTypes)
        {
            if (typeToIndex.TryGetValue(afterType, out var sourceIndex))
            {
                adjacency[sourceIndex] ??= [];
                adjacency[sourceIndex]!.Add(targetIndex);
                inDegree[targetIndex]++;
            }
        }
    }

    private static T[] ExecuteKahnsAlgorithm<T>(T[] indexToService, int[] inDegree, List<int>?[] adjacency)
    {
        var count = indexToService.Length;
        var queue = InitializeQueue(inDegree, count);
        var (result, processedCount) = ProcessQueue(queue, indexToService, adjacency, inDegree);

        if (processedCount != count)
        {
            ThrowCycleDetectedError(indexToService, inDegree);
        }

        return result;
    }

    private static Queue<int> InitializeQueue(int[] inDegree, int count)
    {
        var queue = new Queue<int>();
        for (var i = 0; i < count; i++)
        {
            if (inDegree[i] == 0)
            {
                queue.Enqueue(i);
            }
        }
        return queue;
    }

    private static (T[] result, int processedCount) ProcessQueue<T>(Queue<int> queue, T[] indexToService, List<int>?[] adjacency, int[] inDegree)
    {
        var result = new T[indexToService.Length];
        var resultIndex = 0;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            result[resultIndex++] = indexToService[current];

            if (adjacency[current] is { } neighbors)
            {
                foreach (var neighbor in neighbors)
                {
                    if (--inDegree[neighbor] == 0)
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }
        }

        return (result, resultIndex);
    }

    private static void ThrowCycleDetectedError<T>(T[] indexToService, int[] inDegree)
    {
        var cycleTypes = new List<string>();
        for (var i = 0; i < indexToService.Length; i++)
        {
            if (inDegree[i] > 0)
            {
                cycleTypes.Add(indexToService[i]!.GetType().Name);
            }
        }

        throw new InvalidOperationException(
            $"Circular dependency detected in service ordering: {string.Join(" -> ", cycleTypes)}");
    }

    private static void ValidateCrossGroupDependencies<T>(
        List<T> firstGroup, List<T> middleGroup, List<T> lastGroup)
    {
        var firstTypes = ToTypeSet(firstGroup);
        var middleTypes = ToTypeSet(middleGroup);
        var lastTypes = ToTypeSet(lastGroup);

        ValidateFirstGroupDependencies(firstGroup, middleTypes, lastTypes);
        ValidateLastGroupDependencies(lastGroup, middleTypes, firstTypes);

        static HashSet<Type> ToTypeSet<TService>(List<TService> services)
        {
            var set = new HashSet<Type>();
            foreach (var service in services)
            {
                set.Add(service!.GetType());
            }
            return set;
        }
    }

    private static void ValidateFirstGroupDependencies<T>(
        List<T> firstGroup, HashSet<Type> middleTypes, HashSet<Type> lastTypes)
    {
        foreach (var service in firstGroup)
        {
            var info = GetOrderInfo(service!.GetType());
            foreach (var afterType in info.RunsAfter)
            {
                if (middleTypes.Contains(afterType) || lastTypes.Contains(afterType))
                {
                    throw new InvalidOperationException(
                        $"[RunsFirst] service {service.GetType().Name} cannot have [RunsAfter({afterType.Name})] " +
                        $"where {afterType.Name} is not also [RunsFirst]");
                }
            }
        }
    }

    private static void ValidateLastGroupDependencies<T>(
        List<T> lastGroup, HashSet<Type> middleTypes, HashSet<Type> firstTypes)
    {
        foreach (var service in lastGroup)
        {
            var info = GetOrderInfo(service!.GetType());
            foreach (var beforeType in info.RunsBefore)
            {
                if (middleTypes.Contains(beforeType) || firstTypes.Contains(beforeType))
                {
                    throw new InvalidOperationException(
                        $"[RunsLast] service {service.GetType().Name} cannot have [RunsBefore({beforeType.Name})] " +
                        $"where {beforeType.Name} is not also [RunsLast]");
                }
            }
        }
    }

    private static ServiceOrderInfo GetOrderInfo(Type type)
    {
        return AttributeCache.GetOrAdd(type, static t =>
        {
            var runsBefore = t.GetCustomAttributes<RunsBeforeAttribute>()
                .SelectMany(a => a.Types)
                .ToArray();

            var runsAfter = t.GetCustomAttributes<RunsAfterAttribute>()
                .SelectMany(a => a.Types)
                .ToArray();

            var runsFirst = t.GetCustomAttribute<RunsFirstAttribute>() is not null;
            var runsLast = t.GetCustomAttribute<RunsLastAttribute>() is not null;

            return new ServiceOrderInfo(runsBefore, runsAfter, runsFirst, runsLast);
        });
    }
}
