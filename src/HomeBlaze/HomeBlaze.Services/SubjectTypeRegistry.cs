using System.Collections.Concurrent;
using HomeBlaze.Abstractions.Attributes;
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
    private readonly Lazy<ConcurrentDictionary<string, Type>> _typesByName;
    private readonly Lazy<ConcurrentDictionary<string, Type>> _typesByExtension;

    public SubjectTypeRegistry(TypeProvider typeProvider)
    {
        _typeProvider = typeProvider;
        _typesByName = new Lazy<ConcurrentDictionary<string, Type>>(ScanTypes);
        _typesByExtension = new Lazy<ConcurrentDictionary<string, Type>>(ScanExtensions);
    }

    /// <summary>
    /// Gets all registered subject types.
    /// </summary>
    public IReadOnlyCollection<Type> RegisteredTypes => _typesByName.Value.Values.Distinct().ToList();

    /// <summary>
    /// Gets all registered file extension mappings.
    /// </summary>
    public IReadOnlyDictionary<string, Type> ExtensionMappings => _typesByExtension.Value;

    /// <summary>
    /// Resolves a type from a "Type" discriminator value.
    /// </summary>
    public Type? ResolveType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        if (_typesByName.Value.TryGetValue(typeName, out var type))
            return type;

        // Try Type.GetType for fully qualified names
        type = Type.GetType(typeName);
        if (type != null && typeof(IInterceptorSubject).IsAssignableFrom(type))
        {
            _typesByName.Value[typeName] = type;
            if (type.FullName != null)
                _typesByName.Value[type.FullName] = type;
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

        _typesByExtension.Value.TryGetValue(extension.ToLowerInvariant(), out var type);
        return type;
    }

    /// <summary>
    /// Checks if a type is registered.
    /// </summary>
    public bool IsRegistered(Type type)
    {
        return type.FullName != null && _typesByName.Value.ContainsKey(type.FullName);
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

        return _typesByExtension.Value.ContainsKey(extension.ToLowerInvariant());
    }

    private ConcurrentDictionary<string, Type> ScanTypes()
    {
        var dictionary = new ConcurrentDictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in _typeProvider.Types)
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

    private ConcurrentDictionary<string, Type> ScanExtensions()
    {
        var dictionary = new ConcurrentDictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in _typeProvider.Types)
        {
            foreach (var attribute in type.GetCustomAttributes(typeof(FileExtensionAttribute), false).Cast<FileExtensionAttribute>())
            {
                dictionary[attribute.Extension.ToLowerInvariant()] = type;
            }
        }

        return dictionary;
    }
}
