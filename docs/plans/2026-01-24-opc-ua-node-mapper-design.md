# OPC UA Node Mapper Design

## Overview

Replace the current `PathProvider`-based OPC UA mapping with a dedicated `IOpcUaClientNodeMapper` interface that provides flexible, composable configuration for mapping C# properties to OPC UA nodes.

## Goals

1. **Flexible mapping** - Support attribute-based, path provider-based, fluent, and custom mapping strategies
2. **Composable** - All implementations support fallback chaining
3. **Clear separation** - Client and server have separate mappers with technology-specific configuration
4. **Validation** - Warn when C# properties with OPC UA attributes have no matching server node

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Shared Layer                         │
│  IPathProvider (inclusion, path segments, generic)      │
│  OpcUaNodeAttribute (shared attribute, all properties)  │
└─────────────────────────────────────────────────────────┘
                          │
          ┌───────────────┴───────────────┐
          ▼                               ▼
┌─────────────────────────┐   ┌─────────────────────────┐
│      OPC UA Client      │   │      OPC UA Server      │
├─────────────────────────┤   ├─────────────────────────┤
│ IOpcUaClientNodeMapper  │   │ IOpcUaServerNodeMapper  │
│ OpcUaClientNodeConfig   │   │ OpcUaServerNodeConfig   │
│ - Attribute...          │   │ - Attribute...          │
│ - PathProvider...       │   │ - PathProvider...       │
│ - Fluent...             │   │ - Fluent...             │
└─────────────────────────┘   └─────────────────────────┘
```

---

## Client-Side Design

### Interface

```csharp
namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Maps C# properties to OPC UA client node configuration.
/// </summary>
public interface IOpcUaClientNodeMapper
{
    /// <summary>
    /// Gets OPC UA client configuration for a property, or null if not mapped.
    /// </summary>
    OpcUaClientNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property);

    /// <summary>
    /// Finds property matching an OPC UA node (reverse lookup for client-side discovery).
    /// Async with full session access for maximum flexibility.
    /// </summary>
    Task<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject subject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken);
}
```

### Configuration Record

```csharp
namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// OPC UA client-specific node configuration.
/// </summary>
public record OpcUaClientNodeConfiguration
{
    // Naming / identification
    public string? BrowseName { get; init; }
    public string? BrowseNamespaceUri { get; init; }
    public string? NodeIdentifier { get; init; }
    public string? NodeNamespaceUri { get; init; }

    // Monitoring configuration
    public int? SamplingInterval { get; init; }
    public uint? QueueSize { get; init; }
    public bool? DiscardOldest { get; init; }
    public DataChangeTrigger? DataChangeTrigger { get; init; }
    public DeadbandType? DeadbandType { get; init; }
    public double? DeadbandValue { get; init; }
}
```

### Implementation 1: AttributeOpcUaClientNodeMapper

Maps properties using `[OpcUaNode]` attributes.

```csharp
namespace Namotion.Interceptor.OpcUa.Client;

public class AttributeOpcUaClientNodeMapper : IOpcUaClientNodeMapper
{
    private readonly IOpcUaClientNodeMapper? _fallback;

    public AttributeOpcUaClientNodeMapper(IOpcUaClientNodeMapper? fallback = null)
    {
        _fallback = fallback;
    }

    public OpcUaClientNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property)
    {
        var attr = property.TryGetOpcUaNodeAttribute();
        if (attr is not null)
            return FromAttribute(attr);

        return _fallback?.TryGetConfiguration(property);
    }

    public async Task<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject subject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken)
    {
        var nodeIdString = nodeReference.NodeId.Identifier.ToString();
        var nodeNamespaceUri = nodeReference.NodeId.NamespaceUri
            ?? session.NamespaceUris.GetString(nodeReference.NodeId.NamespaceIndex);

        // Priority 1: Explicit NodeIdentifier match
        foreach (var property in subject.Properties)
        {
            var attr = property.TryGetOpcUaNodeAttribute();
            if (attr?.NodeIdentifier == nodeIdString &&
                (attr.NodeNamespaceUri is null || attr.NodeNamespaceUri == nodeNamespaceUri))
            {
                return property;
            }
        }

        // Priority 2: BrowseName match
        var browseName = nodeReference.BrowseName.Name;
        foreach (var property in subject.Properties)
        {
            var attr = property.TryGetOpcUaNodeAttribute();
            if (attr?.BrowseName == browseName)
            {
                return property;
            }
        }

        // Fallback
        if (_fallback is not null)
            return await _fallback.TryGetPropertyAsync(subject, nodeReference, session, cancellationToken);

        return null;
    }

    private static OpcUaClientNodeConfiguration FromAttribute(OpcUaNodeAttribute attr) => new()
    {
        BrowseName = attr.BrowseName,
        BrowseNamespaceUri = attr.BrowseNamespaceUri,
        NodeIdentifier = attr.NodeIdentifier,
        NodeNamespaceUri = attr.NodeNamespaceUri,
        SamplingInterval = attr.SamplingInterval,
        QueueSize = attr.QueueSize,
        DiscardOldest = attr.DiscardOldest,
        DataChangeTrigger = attr.DataChangeTrigger,
        DeadbandType = attr.DeadbandType,
        DeadbandValue = attr.DeadbandValue,
    };
}
```

### Implementation 2: PathProviderOpcUaClientNodeMapper

Maps properties using an `IPathProvider` for inclusion and browse names.

```csharp
namespace Namotion.Interceptor.OpcUa.Client;

public class PathProviderOpcUaClientNodeMapper : IOpcUaClientNodeMapper
{
    private readonly IPathProvider _pathProvider;
    private readonly IOpcUaClientNodeMapper? _fallback;

    public PathProviderOpcUaClientNodeMapper(
        IPathProvider pathProvider,
        IOpcUaClientNodeMapper? fallback = null)
    {
        _pathProvider = pathProvider;
        _fallback = fallback;
    }

    public OpcUaClientNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property)
    {
        if (!_pathProvider.IsPropertyIncluded(property))
            return _fallback?.TryGetConfiguration(property);

        var segment = _pathProvider.TryGetPropertySegment(property);
        if (segment is not null)
            return new OpcUaClientNodeConfiguration { BrowseName = segment };

        return _fallback?.TryGetConfiguration(property);
    }

    public async Task<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject subject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken)
    {
        var browseName = nodeReference.BrowseName.Name;

        // Find property where PathProvider segment matches browse name
        foreach (var property in subject.Properties)
        {
            if (!_pathProvider.IsPropertyIncluded(property))
                continue;

            var segment = _pathProvider.TryGetPropertySegment(property);
            if (segment == browseName)
                return property;
        }

        // Fallback
        if (_fallback is not null)
            return await _fallback.TryGetPropertyAsync(subject, nodeReference, session, cancellationToken);

        return null;
    }
}
```

### Implementation 3: FluentOpcUaClientNodeMapper

Maps properties using code-based fluent configuration.

```csharp
namespace Namotion.Interceptor.OpcUa.Client;

public class FluentOpcUaClientNodeMapper : IOpcUaClientNodeMapper
{
    private readonly Dictionary<(Type SubjectType, string PropertyName), OpcUaClientNodeConfiguration> _mappings = new();
    private readonly IOpcUaClientNodeMapper? _fallback;

    public FluentOpcUaClientNodeMapper(IOpcUaClientNodeMapper? fallback = null)
    {
        _fallback = fallback;
    }

    public FluentOpcUaClientNodeMapper Map<TSubject>(
        Expression<Func<TSubject, object?>> propertySelector,
        string browseName,
        string? browseNamespaceUri = null,
        string? nodeIdentifier = null,
        string? nodeNamespaceUri = null,
        int? samplingInterval = null,
        uint? queueSize = null,
        bool? discardOldest = null,
        DataChangeTrigger? dataChangeTrigger = null,
        DeadbandType? deadbandType = null,
        double? deadbandValue = null)
    {
        var propertyName = GetPropertyName(propertySelector);
        _mappings[(typeof(TSubject), propertyName)] = new OpcUaClientNodeConfiguration
        {
            BrowseName = browseName,
            BrowseNamespaceUri = browseNamespaceUri,
            NodeIdentifier = nodeIdentifier,
            NodeNamespaceUri = nodeNamespaceUri,
            SamplingInterval = samplingInterval,
            QueueSize = queueSize,
            DiscardOldest = discardOldest,
            DataChangeTrigger = dataChangeTrigger,
            DeadbandType = deadbandType,
            DeadbandValue = deadbandValue,
        };
        return this;
    }

    public FluentOpcUaClientNodeMapper Exclude<TSubject>(
        Expression<Func<TSubject, object?>> propertySelector)
    {
        var propertyName = GetPropertyName(propertySelector);
        _mappings[(typeof(TSubject), propertyName)] = null!; // Marker for exclusion
        return this;
    }

    public OpcUaClientNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property)
    {
        var key = (property.Subject.Subject.GetType(), property.Name);
        if (_mappings.TryGetValue(key, out var config))
            return config; // Returns null if excluded

        return _fallback?.TryGetConfiguration(property);
    }

    public async Task<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject subject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken)
    {
        var browseName = nodeReference.BrowseName.Name;
        var subjectType = subject.Subject.GetType();

        // Find property with matching browse name in mappings
        foreach (var property in subject.Properties)
        {
            var key = (subjectType, property.Name);
            if (_mappings.TryGetValue(key, out var config) && config?.BrowseName == browseName)
                return property;
        }

        // Fallback
        if (_fallback is not null)
            return await _fallback.TryGetPropertyAsync(subject, nodeReference, session, cancellationToken);

        return null;
    }

    private static string GetPropertyName<TSubject>(Expression<Func<TSubject, object?>> propertySelector)
    {
        var expression = propertySelector.Body;

        // Handle boxing (Convert node wrapping value types)
        if (expression is UnaryExpression { NodeType: ExpressionType.Convert } unary)
            expression = unary.Operand;

        if (expression is MemberExpression member)
            return member.Member.Name;

        throw new ArgumentException("Expression must be a property access", nameof(propertySelector));
    }
}
```

---

## Integration

### OpcUaClientConfiguration Changes

```csharp
public class OpcUaClientConfiguration
{
    // REMOVED: public required IPathProvider PathProvider { get; init; }

    /// <summary>
    /// Maps C# properties to OPC UA nodes.
    /// Defaults to attribute-based mapping with property name fallback.
    /// </summary>
    public IOpcUaClientNodeMapper NodeMapper { get; init; }
        = new AttributeOpcUaClientNodeMapper(
            fallback: new PathProviderOpcUaClientNodeMapper(new AttributeBasedPathProvider("opc")));

    // ... rest unchanged
}
```

### Usage Examples

```csharp
// Default: Attributes with property name fallback
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://localhost:4840"
    // NodeMapper uses default
};

// Attributes only (no fallback)
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://localhost:4840",
    NodeMapper = new AttributeOpcUaClientNodeMapper()
};

// PathProvider-based filtering
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://localhost:4840",
    NodeMapper = new PathProviderOpcUaClientNodeMapper(
        new AttributeBasedPathProvider("opc"))
};

// Fluent with attribute fallback
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://localhost:4840",
    NodeMapper = new FluentOpcUaClientNodeMapper(
        fallback: new AttributeOpcUaClientNodeMapper())
        .Map<Machine>(m => m.Temperature, "Temp", samplingInterval: 50)
        .Map<Machine>(m => m.Pressure, "Press")
        .Exclude<Machine>(m => m.InternalState)
};

// Full chain: Fluent -> Attributes -> All properties (overrides default to include everything)
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://localhost:4840",
    NodeMapper = new FluentOpcUaClientNodeMapper(
        fallback: new AttributeOpcUaClientNodeMapper(
            fallback: new PathProviderOpcUaClientNodeMapper(DefaultPathProvider.Instance))) // All properties
        .Map<Machine>(m => m.Temperature, "Temp", samplingInterval: 50)
};
```

---

## Validation Feature

After loading subjects, warn about unmatched properties that have `[OpcUaNode]` attributes:

```csharp
// In OpcUaSubjectLoader, after loading:
private void ValidatePropertyMappings(RegisteredSubject subject, HashSet<RegisteredSubjectProperty> matchedProperties)
{
    foreach (var property in subject.Properties)
    {
        var attr = property.TryGetOpcUaNodeAttribute();
        if (attr is not null && !matchedProperties.Contains(property))
        {
            _logger.LogWarning(
                "Property {Subject}.{Property} has [OpcUaNode] attribute but no matching OPC UA node was found on server.",
                subject.Subject.GetType().Name,
                property.Name);
        }
    }
}
```

---

## Server-Side Design

The server creates OPC UA nodes from C# properties (opposite direction from client). No reverse lookup needed.

### Interface

```csharp
namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// Maps C# properties to OPC UA server node configuration.
/// </summary>
public interface IOpcUaServerNodeMapper
{
    /// <summary>
    /// Gets OPC UA server configuration for a property, or null if not mapped.
    /// </summary>
    OpcUaServerNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property);
}
```

### Configuration Record

```csharp
namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// OPC UA server-specific node configuration.
/// </summary>
public record OpcUaServerNodeConfiguration
{
    // Naming / identification
    public string? BrowseName { get; init; }
    public string? BrowseNamespaceUri { get; init; }
    public string? NodeIdentifier { get; init; }
    public string? NodeNamespaceUri { get; init; }

    // Node structure
    public string? ReferenceType { get; init; }           // e.g., "HasComponent", "HasProperty"
    public string? ItemReferenceType { get; init; }       // For collection items
    public string? TypeDefinition { get; init; }          // e.g., "FolderType"
    public string? TypeDefinitionNamespace { get; init; }
}
```

**Note:** The following are intentionally excluded:
- `AccessLevel` - Derived from `property.HasSetter`
- `ValueRank`, `ArrayDimensions` - Derived from property type/value
- `Historizing` - Requires full history server implementation
- `EURange`, `EngineeringUnits` - Deferred for simplicity
- `Description` - Deferred, could map from `[Description]` attribute later

### Implementation 1: AttributeOpcUaServerNodeMapper

```csharp
namespace Namotion.Interceptor.OpcUa.Server;

public class AttributeOpcUaServerNodeMapper : IOpcUaServerNodeMapper
{
    private readonly IOpcUaServerNodeMapper? _fallback;

    public AttributeOpcUaServerNodeMapper(IOpcUaServerNodeMapper? fallback = null)
    {
        _fallback = fallback;
    }

    public OpcUaServerNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property)
    {
        var opcUaAttr = property.TryGetOpcUaNodeAttribute();
        var refTypeAttr = property.ReflectionAttributes
            .OfType<OpcUaNodeReferenceTypeAttribute>()
            .FirstOrDefault();
        var itemRefTypeAttr = property.ReflectionAttributes
            .OfType<OpcUaNodeItemReferenceTypeAttribute>()
            .FirstOrDefault();
        var typeDefAttr = property.ReflectionAttributes
            .OfType<OpcUaTypeDefinitionAttribute>()
            .FirstOrDefault();

        if (opcUaAttr is not null || refTypeAttr is not null || typeDefAttr is not null)
        {
            return new OpcUaServerNodeConfiguration
            {
                BrowseName = opcUaAttr?.BrowseName,
                BrowseNamespaceUri = opcUaAttr?.BrowseNamespaceUri,
                NodeIdentifier = opcUaAttr?.NodeIdentifier,
                NodeNamespaceUri = opcUaAttr?.NodeNamespaceUri,
                ReferenceType = refTypeAttr?.Type,
                ItemReferenceType = itemRefTypeAttr?.Type,
                TypeDefinition = typeDefAttr?.Type,
                TypeDefinitionNamespace = typeDefAttr?.Namespace,
            };
        }

        return _fallback?.TryGetConfiguration(property);
    }
}
```

### Implementation 2: PathProviderOpcUaServerNodeMapper

```csharp
namespace Namotion.Interceptor.OpcUa.Server;

public class PathProviderOpcUaServerNodeMapper : IOpcUaServerNodeMapper
{
    private readonly IPathProvider _pathProvider;
    private readonly IOpcUaServerNodeMapper? _fallback;

    public PathProviderOpcUaServerNodeMapper(
        IPathProvider pathProvider,
        IOpcUaServerNodeMapper? fallback = null)
    {
        _pathProvider = pathProvider;
        _fallback = fallback;
    }

    public OpcUaServerNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property)
    {
        if (!_pathProvider.IsPropertyIncluded(property))
            return _fallback?.TryGetConfiguration(property);

        var segment = _pathProvider.TryGetPropertySegment(property);
        if (segment is not null)
            return new OpcUaServerNodeConfiguration { BrowseName = segment };

        return _fallback?.TryGetConfiguration(property);
    }
}
```

### Implementation 3: FluentOpcUaServerNodeMapper

```csharp
namespace Namotion.Interceptor.OpcUa.Server;

public class FluentOpcUaServerNodeMapper : IOpcUaServerNodeMapper
{
    private readonly Dictionary<(Type SubjectType, string PropertyName), OpcUaServerNodeConfiguration?> _mappings = new();
    private readonly IOpcUaServerNodeMapper? _fallback;

    public FluentOpcUaServerNodeMapper(IOpcUaServerNodeMapper? fallback = null)
    {
        _fallback = fallback;
    }

    public FluentOpcUaServerNodeMapper Map<TSubject>(
        Expression<Func<TSubject, object?>> propertySelector,
        string browseName,
        string? browseNamespaceUri = null,
        string? nodeIdentifier = null,
        string? nodeNamespaceUri = null,
        string? referenceType = null,
        string? itemReferenceType = null,
        string? typeDefinition = null,
        string? typeDefinitionNamespace = null)
    {
        var propertyName = GetPropertyName(propertySelector);
        _mappings[(typeof(TSubject), propertyName)] = new OpcUaServerNodeConfiguration
        {
            BrowseName = browseName,
            BrowseNamespaceUri = browseNamespaceUri,
            NodeIdentifier = nodeIdentifier,
            NodeNamespaceUri = nodeNamespaceUri,
            ReferenceType = referenceType,
            ItemReferenceType = itemReferenceType,
            TypeDefinition = typeDefinition,
            TypeDefinitionNamespace = typeDefinitionNamespace,
        };
        return this;
    }

    public FluentOpcUaServerNodeMapper Exclude<TSubject>(
        Expression<Func<TSubject, object?>> propertySelector)
    {
        var propertyName = GetPropertyName(propertySelector);
        _mappings[(typeof(TSubject), propertyName)] = null;
        return this;
    }

    public OpcUaServerNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property)
    {
        var key = (property.Subject.Subject.GetType(), property.Name);
        if (_mappings.TryGetValue(key, out var config))
            return config;

        return _fallback?.TryGetConfiguration(property);
    }

    private static string GetPropertyName<TSubject>(Expression<Func<TSubject, object?>> propertySelector)
    {
        var expression = propertySelector.Body;
        if (expression is UnaryExpression { NodeType: ExpressionType.Convert } unary)
            expression = unary.Operand;

        if (expression is MemberExpression member)
            return member.Member.Name;

        throw new ArgumentException("Expression must be a property access", nameof(propertySelector));
    }
}
```

### Integration

```csharp
public class OpcUaServerConfiguration
{
    // REMOVED: public required IPathProvider PathProvider { get; init; }

    /// <summary>
    /// Maps C# properties to OPC UA nodes.
    /// Defaults to attribute-based mapping with property name fallback.
    /// </summary>
    public IOpcUaServerNodeMapper NodeMapper { get; init; }
        = new AttributeOpcUaServerNodeMapper(
            fallback: new PathProviderOpcUaServerNodeMapper(new AttributeBasedPathProvider("opc")));

    // ... rest unchanged
}
```

### Server Usage Examples

```csharp
// Default: Attributes with property name fallback
var config = new OpcUaServerConfiguration
{
    // NodeMapper uses default
};

// Attributes only
var config = new OpcUaServerConfiguration
{
    NodeMapper = new AttributeOpcUaServerNodeMapper()
};

// Fluent with attribute fallback
var config = new OpcUaServerConfiguration
{
    NodeMapper = new FluentOpcUaServerNodeMapper(
        fallback: new AttributeOpcUaServerNodeMapper())
        .Map<Machine>(m => m.Temperature, "Temp", referenceType: "HasComponent")
        .Exclude<Machine>(m => m.InternalState)
};
```

---

## Summary

### Client

| Component | Name |
|-----------|------|
| **Interface** | `IOpcUaClientNodeMapper` |
| **Config record** | `OpcUaClientNodeConfiguration` |
| **Implementations** | `AttributeOpcUaClientNodeMapper`, `PathProviderOpcUaClientNodeMapper`, `FluentOpcUaClientNodeMapper` |
| **Default** | `AttributeOpcUaClientNodeMapper` → `PathProviderOpcUaClientNodeMapper(new AttributeBasedPathProvider("opc"))` |

### Server

| Component | Name |
|-----------|------|
| **Interface** | `IOpcUaServerNodeMapper` |
| **Config record** | `OpcUaServerNodeConfiguration` |
| **Implementations** | `AttributeOpcUaServerNodeMapper`, `PathProviderOpcUaServerNodeMapper`, `FluentOpcUaServerNodeMapper` |
| **Default** | `AttributeOpcUaServerNodeMapper` → `PathProviderOpcUaServerNodeMapper(new AttributeBasedPathProvider("opc"))` |

### Shared

| Component | Name |
|-----------|------|
| **Attribute** | `OpcUaNodeAttribute` (contains both client + server properties) |
| **Path provider** | `IPathProvider` (used by `PathProvider*` implementations) |

### Key Design Decisions

1. **Separate interfaces** - Client and server have different concerns (monitoring vs structure)
2. **Fallback chaining** - All implementations support composable fallback
3. **PathProvider for filtering** - Shared inclusion logic via `IPathProvider`
4. **Single attribute** - `OpcUaNodeAttribute` contains all properties, each mapper reads what it needs
5. **Derived values excluded** - AccessLevel, ValueRank, ArrayDimensions derived from C# property metadata

---

## Documentation Update

Add a new section to `docs/connectors.md` describing the **Property Mapper** pattern for complex connectors.

### Content to Add

At the bottom of `connectors.md`, add a section covering:

1. **When to use property mappers** - Table comparing protocols:
   - OPC UA, Modbus, BACnet → Need mappers (complex per-property config)
   - MQTT, Kafka → PathProvider suffices (topics = paths)

2. **Mapper interface pattern** - Template for client/server interfaces:
   - Client: `TryGetConfiguration()` + `TryGetPropertyAsync()` (reverse lookup)
   - Server: `TryGetConfiguration()` only

3. **Configuration records** - Separate client/server records for different concerns

4. **Standard implementations** - Three implementations with fallback:
   - Attribute-based
   - PathProvider-based
   - Fluent

5. **Composing mappers** - Examples of fallback chaining

6. **Shared attribute** - Single attribute for both client/server properties

7. **Relationship with PathProvider** - How mappers integrate with existing path infrastructure

8. **Link to OPC UA design** - Reference this design document as the canonical example

---

## OPC UA Documentation Update

Update `docs/connectors/opcua.md` with the new mapper configuration.

### Sections to Add/Update

1. **Node Mapper Configuration** - New section explaining:
   - What `IOpcUaClientNodeMapper` / `IOpcUaServerNodeMapper` do
   - Default behavior (attributes → PathProvider fallback)
   - When to customize

2. **Built-in Mappers** - Document each implementation:
   - `AttributeOpcUaClientNodeMapper` - Uses `[OpcUaNode]` attributes
   - `PathProviderOpcUaClientNodeMapper` - Uses `IPathProvider` for inclusion/naming
   - `FluentOpcUaClientNodeMapper` - Code-based configuration

3. **Customization Examples**:

   ```csharp
   // Default: Attributes with property name fallback
   var config = new OpcUaClientConfiguration
   {
       ServerUrl = "opc.tcp://localhost:4840"
       // NodeMapper uses default
   };

   // Attributes only (no fallback to property names)
   var config = new OpcUaClientConfiguration
   {
       ServerUrl = "opc.tcp://localhost:4840",
       NodeMapper = new AttributeOpcUaClientNodeMapper()
   };

   // Fluent with attribute fallback
   var config = new OpcUaClientConfiguration
   {
       ServerUrl = "opc.tcp://localhost:4840",
       NodeMapper = new FluentOpcUaClientNodeMapper(
           fallback: new AttributeOpcUaClientNodeMapper())
           .Map<Machine>(m => m.Temperature, "Temp", samplingInterval: 50)
           .Exclude<Machine>(m => m.InternalState)
   };
   ```

4. **Configuration Records** - Document all properties:
   - `OpcUaClientNodeConfiguration` - BrowseName, NodeIdentifier, SamplingInterval, etc.
   - `OpcUaServerNodeConfiguration` - BrowseName, NodeIdentifier, ReferenceType, etc.

5. **Custom Mapper Implementation** - Example of implementing `IOpcUaClientNodeMapper` for advanced scenarios
