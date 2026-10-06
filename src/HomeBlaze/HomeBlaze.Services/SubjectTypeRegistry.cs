using System.Collections.Concurrent;
using HomeBlaze.Storage.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services;

/// <summary>
/// Registry for resolving subject types from JSON "Type" discriminator values
/// and mapping file extensions to subject types.
/// </summary>
public class SubjectTypeRegistry
{
    private readonly TypeProvider _typeProvider;
    private Snapshot? _snapshot;

    public SubjectTypeRegistry(TypeProvider typeProvider)
    {
        _typeProvider = typeProvider;
    }

    /// <summary>
    /// Gets all registered subject types.
    /// </summary>
    public IReadOnlyCollection<Type> RegisteredTypes => GetSnapshot().TypesByName.Values.Distinct().ToList();

    /// <summary>
    /// Gets all registered file extension mappings.
    /// </summary>
    public IReadOnlyDictionary<string, Type> ExtensionMappings => GetSnapshot().TypesByExtension;

    /// <summary>
    /// Resolves a type from a "Type" discriminator value.
    /// </summary>
    public Type? ResolveType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        var snapshot = GetSnapshot();

        if (snapshot.TypesByName.TryGetValue(typeName, out var type))
            return type;

        // Try Type.GetType for fully qualified names
        type = Type.GetType(typeName);
        if (type != null && typeof(IInterceptorSubject).IsAssignableFrom(type))
        {
            snapshot.TypesByName[typeName] = type;
            if (type.FullName != null)
                snapshot.TypesByName[type.FullName] = type;
            return type;
        }

        return null;
    }

    /// <summary>
    /// Resolves a type from a file extension.
    /// </summary>
    public Type? ResolveTypeForExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return null;

        if (!extension.StartsWith('.'))
            extension = "." + extension;

        var snapshot = GetSnapshot();
        snapshot.TypesByExtension.TryGetValue(extension.ToLowerInvariant(), out var type);
        return type;
    }

    /// <summary>
    /// Checks if a type is registered.
    /// </summary>
    public bool IsRegistered(Type type)
    {
        var snapshot = GetSnapshot();
        return type.FullName != null && snapshot.TypesByName.ContainsKey(type.FullName);
    }

    /// <summary>
    /// Checks if a file extension has a mapping.
    /// </summary>
    public bool HasExtensionMapping(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return false;

        if (!extension.StartsWith('.'))
            extension = "." + extension;

        var snapshot = GetSnapshot();
        return snapshot.TypesByExtension.ContainsKey(extension.ToLowerInvariant());
    }

    private Snapshot GetSnapshot()
    {
        var types = _typeProvider.Types;
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null || !ReferenceEquals(snapshot.Source, types))
        {
            snapshot = new Snapshot(types, ScanTypes(types), ScanExtensions(types));
            Volatile.Write(ref _snapshot, snapshot);
        }

        return snapshot;
    }

    private static ConcurrentDictionary<string, Type> ScanTypes(IReadOnlyCollection<Type> types)
    {
        var dictionary = new ConcurrentDictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in types)
        {
            if (type.GetCustomAttributes(typeof(InterceptorSubjectAttribute), false).Length != 0 &&
                typeof(IInterceptorSubject).IsAssignableFrom(type))
            {
                if (type.FullName != null)
                    dictionary[type.FullName] = type;
                dictionary[type.Name] = type;
            }
        }

        return dictionary;
    }

    private static ConcurrentDictionary<string, Type> ScanExtensions(IReadOnlyCollection<Type> types)
    {
        var dictionary = new ConcurrentDictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in types)
        {
            foreach (var attribute in type.GetCustomAttributes(typeof(FileExtensionAttribute), false).Cast<FileExtensionAttribute>())
            {
                dictionary[attribute.Extension.ToLowerInvariant()] = type;
            }
        }

        return dictionary;
    }

    private sealed class Snapshot(
        IReadOnlyCollection<Type> source,
        ConcurrentDictionary<string, Type> typesByName,
        ConcurrentDictionary<string, Type> typesByExtension)
    {
        public IReadOnlyCollection<Type> Source => source;

        public ConcurrentDictionary<string, Type> TypesByName => typesByName;

        public ConcurrentDictionary<string, Type> TypesByExtension => typesByExtension;
    }
}
