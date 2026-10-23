using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>The simple and full names of a runtime type, its base types and its interfaces, cached per type.</summary>
internal sealed class TypeNameCache
{
    private readonly ConcurrentDictionary<Type, FrozenSet<string>> _namesByType = new();

    public bool IsAssignableToAny(Type type, string[] typeNames)
    {
        var names = _namesByType.GetOrAdd(type, static type => CollectNames(type));
        foreach (var typeName in typeNames)
        {
            if (names.Contains(typeName))
            {
                return true;
            }
        }

        return false;
    }

    private static FrozenSet<string> CollectNames(Type type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.BaseType)
        {
            AddNames(names, current);
        }

        foreach (var interfaceType in type.GetInterfaces())
        {
            AddNames(names, interfaceType);
        }

        return names.ToFrozenSet(StringComparer.Ordinal);
    }

    private static void AddNames(HashSet<string> names, Type type)
    {
        names.Add(type.Name);
        if (type.FullName is not null)
        {
            names.Add(type.FullName);
        }
    }
}
