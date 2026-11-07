using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>
/// The simple and full names of a runtime type, its base types and its interfaces, cached per type. The cache does not
/// keep a type alive, so a collectible load context can still unload.
/// </summary>
internal sealed class TypeNameCache
{
    private readonly ConditionalWeakTable<Type, FrozenSet<string>> _namesByType = new();

    public bool IsAssignableToAny(Type type, string[] typeNames)
    {
        var names = _namesByType.GetValue(type, static type => CollectNames(type));
        foreach (var typeName in typeNames)
        {
            if (names.Contains(typeName))
            {
                return true;
            }
        }

        return false;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Trimming can remove an interface implementation nothing uses, so a Types selector naming such an interface may not match; documented on PermissionRule.Types.")]
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
