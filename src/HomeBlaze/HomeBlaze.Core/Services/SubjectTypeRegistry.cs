using System.Collections.Concurrent;
using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Core.Services;

/// <summary>
/// Registry for resolving subject types from JSON "Type" discriminator values
/// and mapping file extensions to subject types.
/// </summary>
public class SubjectTypeRegistry
{
    private readonly ConcurrentDictionary<string, Type> _typesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Type> _typesByExtension = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets all registered subject types.
    /// </summary>
    public IReadOnlyCollection<Type> RegisteredTypes => _typesByName.Values.Distinct().ToList();

    /// <summary>
    /// Gets all registered file extension mappings.
    /// </summary>
    public IReadOnlyDictionary<string, Type> ExtensionMappings => _typesByExtension;

    /// <summary>
    /// Registers a subject type by its full name and optional aliases.
    /// </summary>
    public SubjectTypeRegistry Register<T>(params string[] aliases) where T : IInterceptorSubject
    {
        return Register(typeof(T), aliases);
    }

    /// <summary>
    /// Registers a subject type by its full name and optional aliases.
    /// </summary>
    public SubjectTypeRegistry Register(Type type, params string[] aliases)
    {
        if (!typeof(IInterceptorSubject).IsAssignableFrom(type))
            throw new ArgumentException($"Type {type.FullName} must implement IInterceptorSubject", nameof(type));

        // Register by full name
        if (type.FullName != null)
            _typesByName[type.FullName] = type;

        // Register by simple name
        _typesByName[type.Name] = type;

        // Register aliases
        foreach (var alias in aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias))
                _typesByName[alias] = type;
        }

        // Check for FileExtension attributes
        foreach (var attr in type.GetCustomAttributes<FileExtensionAttribute>())
        {
            _typesByExtension[attr.Extension] = type;
        }

        return this;
    }

    /// <summary>
    /// Registers a file extension mapping.
    /// </summary>
    public SubjectTypeRegistry RegisterExtension(string extension, Type type)
    {
        if (string.IsNullOrEmpty(extension))
            throw new ArgumentException("Extension cannot be null or empty", nameof(extension));

        if (!extension.StartsWith('.'))
            extension = "." + extension;

        _typesByExtension[extension.ToLowerInvariant()] = type;
        return this;
    }

    /// <summary>
    /// Registers a file extension mapping.
    /// </summary>
    public SubjectTypeRegistry RegisterExtension<T>(string extension) where T : IInterceptorSubject
    {
        return RegisterExtension(extension, typeof(T));
    }

    /// <summary>
    /// Scans assemblies for types with [InterceptorSubject] attribute and registers them.
    /// Also registers file extension mappings from [FileExtension] attributes.
    /// </summary>
    public SubjectTypeRegistry ScanAssemblies(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    // Use GetCustomAttributes (plural) to avoid exception if multiple attributes exist
                    if (type.GetCustomAttributes<InterceptorSubjectAttribute>().Any() &&
                        typeof(IInterceptorSubject).IsAssignableFrom(type))
                    {
                        Register(type);
                    }
                }
            }
            catch (ReflectionTypeLoadException)
            {
                // Skip assemblies that can't be loaded
            }
        }

        return this;
    }

    /// <summary>
    /// Resolves a type from a "Type" discriminator value.
    /// Tries full name, then simple name, then aliases.
    /// </summary>
    public Type? ResolveType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        // Try direct lookup
        if (_typesByName.TryGetValue(typeName, out var type))
            return type;

        // Try Type.GetType for fully qualified names
        type = Type.GetType(typeName);
        if (type != null && typeof(IInterceptorSubject).IsAssignableFrom(type))
        {
            Register(type);
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

        _typesByExtension.TryGetValue(extension.ToLowerInvariant(), out var type);
        return type;
    }

    /// <summary>
    /// Checks if a type is registered.
    /// </summary>
    public bool IsRegistered(Type type)
    {
        return type.FullName != null && _typesByName.ContainsKey(type.FullName);
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

        return _typesByExtension.ContainsKey(extension.ToLowerInvariant());
    }
}
