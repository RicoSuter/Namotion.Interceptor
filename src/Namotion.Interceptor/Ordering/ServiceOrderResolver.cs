using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Ordering;

/// <summary>
/// Resolves service execution order based on ordering attributes.
/// Uses Kahn's algorithm for topological sorting with three-group partitioning (First, Middle, Last).
/// </summary>
internal static class ServiceOrderResolver
{
    private static readonly ConcurrentDictionary<Type, (Type[] RunsBefore, Type[] RunsAfter, bool RunsFirst, bool RunsLast)> Cache = new();

    /// <summary>
    /// Orders services by their ordering attributes.
    /// </summary>
    public static T[] OrderByDependencies<T>(T[] services)
    {
        if (services.Length <= 1)
        {
            if (services.Length == 1)
                ValidateService(services[0]!);
            return services.Length == 0 ? [] : [services[0]];
        }

        // Fast path: check if partitioning is needed
        var hasFirstOrLast = false;
        for (var i = 0; i < services.Length; i++)
        {
            var info = GetOrderInfo(services[i]!.GetType());
            if (info.RunsFirst || info.RunsLast)
            {
                hasFirstOrLast = true;
                break;
            }
        }

        return hasFirstOrLast ? OrderWithPartitioning(services) : TopologicalSort(services);
    }

    private static T[] OrderWithPartitioning<T>(T[] services)
    {
        var (firstCount, lastCount) = CountGroups(services);
        var middleCount = services.Length - firstCount - lastCount;

        var firstGroup = firstCount > 0 ? new T[firstCount] : null;
        var middleGroup = middleCount > 0 ? new T[middleCount] : null;
        var lastGroup = lastCount > 0 ? new T[lastCount] : null;
        PartitionGroups(services, firstGroup, middleGroup, lastGroup);

        ValidateCrossGroupDependencies(firstGroup, middleGroup, lastGroup);

        // Sort each group and write to result
        var result = new T[services.Length];
        var offset = 0;

        if (firstGroup != null)
        {
            TopologicalSortInto(firstGroup, result, offset);
            offset += firstCount;
        }
        if (middleGroup != null)
        {
            TopologicalSortInto(middleGroup, result, offset);
            offset += middleCount;
        }
        if (lastGroup != null)
        {
            TopologicalSortInto(lastGroup, result, offset);
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int FirstCount, int LastCount) CountGroups<T>(T[] services)
    {
        var firstCount = 0;
        var lastCount = 0;
        for (var i = 0; i < services.Length; i++)
        {
            var info = GetOrderInfo(services[i]!.GetType());
            if (info is { RunsFirst: true, RunsLast: true })
                throw new InvalidOperationException($"Service {services[i]!.GetType().Name} cannot have both [RunsFirst] and [RunsLast]");
            if (info.RunsFirst) firstCount++;
            else if (info.RunsLast) lastCount++;
        }

        return (firstCount, lastCount);
    }

    private static void PartitionGroups<T>(T[] services, T[]? firstGroup, T[]? middleGroup, T[]? lastGroup)
    {
        int firstIndex = 0, middleIndex = 0, lastIndex = 0;

        for (var i = 0; i < services.Length; i++)
        {
            var service = services[i];
            var info = GetOrderInfo(service!.GetType());
            if (info.RunsFirst) firstGroup![firstIndex++] = service;
            else if (info.RunsLast) lastGroup![lastIndex++] = service;
            else middleGroup![middleIndex++] = service;
        }
    }

    private static T[] TopologicalSort<T>(T[] services)
    {
        var result = new T[services.Length];
        TopologicalSortInto(services, result, 0);
        return result;
    }

    private static void TopologicalSortInto<T>(T[] services, T[] result, int resultOffset)
    {
        var count = services.Length;
        if (count == 0) return;
        if (count == 1)
        {
            result[resultOffset] = services[0];
            return;
        }

        // Build type-to-indices mapping; a type can have multiple instances when a
        // context aggregates fallback contexts that each register the same service type
        var typeToIndices = new Dictionary<Type, List<int>>(count);
        for (var i = 0; i < count; i++)
        {
            var type = services[i]!.GetType();
            if (!typeToIndices.TryGetValue(type, out var indices))
                typeToIndices[type] = indices = [];
            indices.Add(i);
        }

        // Build adjacency list and in-degree counts; edges bind to every instance of the referenced type
        var adjacency = new List<int>[count];
        var inDegree = new int[count];
        BuildDependencyGraph(services, typeToIndices, adjacency, inDegree);

        // Kahn's algorithm with sorted ready set (preserves registration order)
        var ready = new SortedSet<int>();
        for (var i = 0; i < count; i++)
        {
            if (inDegree[i] == 0)
                ready.Add(i);
        }

        var resultIndex = 0;
        while (ready.Count > 0)
        {
            var current = ready.Min;
            ready.Remove(current);
            result[resultOffset + resultIndex++] = services[current];

            var neighbors = adjacency[current];
            if (neighbors != null)
                ReleaseDependents(neighbors, inDegree, ready);
        }

        if (resultIndex != count)
        {
            ThrowCircularDependency(services, inDegree);
        }
    }

    private static void ThrowCircularDependency<T>(T[] services, int[] inDegree)
    {
        var cycleTypes = new List<string>();
        for (var i = 0; i < services.Length; i++)
        {
            if (inDegree[i] > 0)
                cycleTypes.Add(services[i]!.GetType().Name);
        }
        throw new InvalidOperationException($"Circular dependency detected in service ordering: {string.Join(" -> ", cycleTypes)}");
    }

    private static void BuildDependencyGraph<T>(T[] services, Dictionary<Type, List<int>> typeToIndices, List<int>[] adjacency, int[] inDegree)
    {
        for (var i = 0; i < services.Length; i++)
        {
            var info = GetOrderInfo(services[i]!.GetType());
            if (info.RunsBefore.Length > 0)
                AddRunsBeforeEdges(i, info.RunsBefore, typeToIndices, adjacency, inDegree);
            if (info.RunsAfter.Length > 0)
                AddRunsAfterEdges(i, info.RunsAfter, typeToIndices, adjacency, inDegree);
        }
    }

    private static void AddRunsBeforeEdges(int index, Type[] beforeTypes, Dictionary<Type, List<int>> typeToIndices, List<int>[] adjacency, int[] inDegree)
    {
        foreach (var beforeType in beforeTypes)
        {
            if (typeToIndices.TryGetValue(beforeType, out var targets))
            {
                foreach (var target in targets)
                {
                    (adjacency[index] ??= []).Add(target);
                    inDegree[target]++;
                }
            }
        }
    }

    private static void AddRunsAfterEdges(int index, Type[] afterTypes, Dictionary<Type, List<int>> typeToIndices, List<int>[] adjacency, int[] inDegree)
    {
        foreach (var afterType in afterTypes)
        {
            if (typeToIndices.TryGetValue(afterType, out var sources))
            {
                foreach (var source in sources)
                {
                    (adjacency[source] ??= []).Add(index);
                    inDegree[index]++;
                }
            }
        }
    }

    private static void ReleaseDependents(List<int> neighbors, int[] inDegree, SortedSet<int> ready)
    {
        foreach (var neighbor in neighbors)
        {
            if (--inDegree[neighbor] == 0)
                ready.Add(neighbor);
        }
    }

    private static void ValidateService<T>(T service)
    {
        var info = GetOrderInfo(service!.GetType());
        if (info is { RunsFirst: true, RunsLast: true })
            throw new InvalidOperationException($"Service {service.GetType().Name} cannot have both [RunsFirst] and [RunsLast]");
    }

    private static void ValidateCrossGroupDependencies<T>(T[]? firstGroup, T[]? middleGroup, T[]? lastGroup)
    {
        HashSet<Type>? middleTypes = null;
        if (firstGroup != null)
            middleTypes = ValidateFirstGroupDependencies(firstGroup, middleGroup, lastGroup);
        if (lastGroup != null)
            ValidateLastGroupDependencies(firstGroup, middleGroup, lastGroup, middleTypes);
    }

    private static HashSet<Type>? ValidateFirstGroupDependencies<T>(T[] firstGroup, T[]? middleGroup, T[]? lastGroup)
    {
        HashSet<Type>? middleTypes = null;
        HashSet<Type>? lastTypes = null;
        foreach (var service in firstGroup)
        {
            var info = GetOrderInfo(service!.GetType());
            foreach (var afterType in info.RunsAfter)
            {
                middleTypes ??= GetTypes(middleGroup);
                lastTypes ??= GetTypes(lastGroup);
                if (middleTypes.Contains(afterType) || lastTypes.Contains(afterType))
                    throw new InvalidOperationException(
                        $"[RunsFirst] service {service.GetType().Name} cannot have [RunsAfter({afterType.Name})] " +
                        $"where {afterType.Name} is not also [RunsFirst]");
            }
        }

        return middleTypes;
    }

    private static void ValidateLastGroupDependencies<T>(T[]? firstGroup, T[]? middleGroup, T[] lastGroup, HashSet<Type>? middleTypes)
    {
        HashSet<Type>? firstTypes = null;
        foreach (var service in lastGroup)
        {
            var info = GetOrderInfo(service!.GetType());
            foreach (var beforeType in info.RunsBefore)
            {
                firstTypes ??= GetTypes(firstGroup);
                middleTypes ??= GetTypes(middleGroup);
                if (firstTypes.Contains(beforeType) || middleTypes.Contains(beforeType))
                    throw new InvalidOperationException(
                        $"[RunsLast] service {service.GetType().Name} cannot have [RunsBefore({beforeType.Name})] " +
                        $"where {beforeType.Name} is not also [RunsLast]");
            }
        }
    }

    private static HashSet<Type> GetTypes<T>(T[]? group)
    {
        var set = new HashSet<Type>();
        if (group != null)
            foreach (var service in group)
                set.Add(service!.GetType());
        return set;
    }

    private static (Type[] RunsBefore, Type[] RunsAfter, bool RunsFirst, bool RunsLast) GetOrderInfo(Type type)
    {
        return Cache.GetOrAdd(type, static t => (
            t.GetCustomAttributes<RunsBeforeAttribute>().SelectMany(a => a.Types).ToArray(),
            t.GetCustomAttributes<RunsAfterAttribute>().SelectMany(a => a.Types).ToArray(),
            t.GetCustomAttribute<RunsFirstAttribute>() is not null,
            t.GetCustomAttribute<RunsLastAttribute>() is not null
        ));
    }
}
