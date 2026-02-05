using Opc.Ua;

namespace Namotion.Interceptor.OpcUa;

/// <summary>
/// Registry for mapping OPC UA TypeDefinition NodeIds to C# types.
/// Used to resolve which C# type to instantiate when external clients call AddNodes.
/// </summary>
public class OpcUaTypeRegistry
{
    private readonly Dictionary<NodeId, Type> _typeDefinitionToType = new();
    private readonly Dictionary<Type, NodeId> _typeToTypeDefinition = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Registers a mapping from an OPC UA TypeDefinition to a C# type.
    /// </summary>
    /// <typeparam name="T">The C# type that implements <see cref="IInterceptorSubject"/>.</typeparam>
    /// <param name="typeDefinitionId">The OPC UA TypeDefinition NodeId.</param>
    public void RegisterType<T>(NodeId typeDefinitionId) where T : IInterceptorSubject
    {
        RegisterType(typeof(T), typeDefinitionId);
    }

    /// <summary>
    /// Registers a mapping from an OPC UA TypeDefinition to a C# type.
    /// </summary>
    /// <param name="type">The C# type that implements <see cref="IInterceptorSubject"/>.</param>
    /// <param name="typeDefinitionId">The OPC UA TypeDefinition NodeId.</param>
    public void RegisterType(Type type, NodeId typeDefinitionId)
    {
        if (!typeof(IInterceptorSubject).IsAssignableFrom(type))
        {
            throw new ArgumentException(
                $"Type '{type.FullName}' must implement {nameof(IInterceptorSubject)}.",
                nameof(type));
        }

        lock (_lock)
        {
            _typeDefinitionToType[typeDefinitionId] = type;
            _typeToTypeDefinition[type] = typeDefinitionId;
        }
    }

    /// <summary>
    /// Resolves a C# type from an OPC UA TypeDefinition NodeId.
    /// </summary>
    /// <param name="typeDefinitionId">The OPC UA TypeDefinition NodeId.</param>
    /// <returns>The C# type if registered, null otherwise.</returns>
    public Type? ResolveType(NodeId typeDefinitionId)
    {
        lock (_lock)
        {
            return _typeDefinitionToType.GetValueOrDefault(typeDefinitionId);
        }
    }

    /// <summary>
    /// Gets the OPC UA TypeDefinition NodeId for a C# type.
    /// </summary>
    /// <param name="type">The C# type.</param>
    /// <returns>The TypeDefinition NodeId if registered, null otherwise.</returns>
    public NodeId? GetTypeDefinition(Type type)
    {
        lock (_lock)
        {
            return _typeToTypeDefinition.GetValueOrDefault(type);
        }
    }

    /// <summary>
    /// Gets the OPC UA TypeDefinition NodeId for a C# type.
    /// </summary>
    /// <typeparam name="T">The C# type.</typeparam>
    /// <returns>The TypeDefinition NodeId if registered, null otherwise.</returns>
    public NodeId? GetTypeDefinition<T>() where T : IInterceptorSubject
    {
        return GetTypeDefinition(typeof(T));
    }

    /// <summary>
    /// Checks if a TypeDefinition is registered.
    /// </summary>
    /// <param name="typeDefinitionId">The OPC UA TypeDefinition NodeId.</param>
    /// <returns>True if registered, false otherwise.</returns>
    public bool IsTypeRegistered(NodeId typeDefinitionId)
    {
        lock (_lock)
        {
            return _typeDefinitionToType.ContainsKey(typeDefinitionId);
        }
    }

    /// <summary>
    /// Gets all registered type mappings.
    /// </summary>
    /// <returns>A dictionary of TypeDefinition NodeIds to C# types.</returns>
    public IReadOnlyDictionary<NodeId, Type> GetAllRegistrations()
    {
        lock (_lock)
        {
            return new Dictionary<NodeId, Type>(_typeDefinitionToType);
        }
    }

    /// <summary>
    /// Clears all registered type mappings.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _typeDefinitionToType.Clear();
            _typeToTypeDefinition.Clear();
        }
    }
}
