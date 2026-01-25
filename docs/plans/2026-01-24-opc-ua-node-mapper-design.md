# OPC UA Node Mapper Design

## Overview

Replace the current `PathProvider`-based OPC UA mapping with a dedicated `IOpcUaNodeMapper` interface that provides flexible, composable configuration for mapping C# properties to OPC UA nodes.

## Goals

1. **Flexible mapping** - Support attribute-based, path provider-based, fluent, and custom mapping strategies
2. **Composable** - Mappers can be combined via `CompositeNodeMapper` with merge semantics
3. **Unified interface** - Single interface and config record for both client and server (with XML docs indicating client/server-only fields)
4. **Instance-based fluent configuration** - Configure properties by path, not just by type (e.g., `Motor1.Speed` vs `Motor2.Speed`)
5. **Validation** - Warn when C# properties with OPC UA attributes have no matching server node

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Shared Layer                         │
│  IPathProvider (inclusion, path segments, generic)      │
│  OpcUaNodeAttribute (shared attribute, all properties)  │
│  IOpcUaNodeMapper (unified interface)                   │
│  OpcUaNodeConfiguration (unified record)                │
└─────────────────────────────────────────────────────────┘
                          │
          ┌───────────────┼───────────────┐
          ▼               ▼               ▼
┌─────────────────┐ ┌───────────────┐ ┌─────────────────┐
│ Attribute...    │ │ PathProvider..│ │ Fluent...       │
│ NodeMapper      │ │ NodeMapper    │ │ NodeMapper<T>   │
└─────────────────┘ └───────────────┘ └─────────────────┘
          │               │               │
          └───────────────┼───────────────┘
                          ▼
              ┌─────────────────────┐
              │ CompositeNodeMapper │
              │ (merge semantics)   │
              └─────────────────────┘
```

---

## Unified Interface

```csharp
namespace Namotion.Interceptor.OpcUa;

/// <summary>
/// Maps C# properties to OPC UA node configuration.
/// Used by both client and server.
/// </summary>
public interface IOpcUaNodeMapper
{
    /// <summary>
    /// Gets OPC UA configuration for a property, or null if not mapped.
    /// Returns partial configuration; use CompositeNodeMapper to merge multiple mappers.
    /// </summary>
    OpcUaNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property);

    /// <summary>
    /// Client only: Finds property matching an OPC UA node (reverse lookup for discovery).
    /// Server implementations should return null.
    /// </summary>
    Task<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject subject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken);
}
```

---

## Unified Configuration Record

```csharp
namespace Namotion.Interceptor.OpcUa;

/// <summary>
/// OPC UA node configuration. All fields are nullable to support partial configuration
/// and merge semantics. Fields are documented as client-only or server-only where applicable.
/// </summary>
public record OpcUaNodeConfiguration
{
    // Naming / identification (shared)
    public string? BrowseName { get; init; }
    public string? BrowseNamespaceUri { get; init; }
    public string? NodeIdentifier { get; init; }
    public string? NodeNamespaceUri { get; init; }

    /// <summary>Localized display name (if different from BrowseName).</summary>
    public string? DisplayName { get; init; }

    /// <summary>Human-readable description.</summary>
    public string? Description { get; init; }

    // Monitoring configuration (client only)
    /// <summary>Client only: Sampling interval in milliseconds.</summary>
    public int? SamplingInterval { get; init; }

    /// <summary>Client only: Queue size for monitored items.</summary>
    public uint? QueueSize { get; init; }

    /// <summary>Client only: Whether to discard oldest values when queue is full.</summary>
    public bool? DiscardOldest { get; init; }

    /// <summary>Client only: Trigger for data change notifications.</summary>
    public DataChangeTrigger? DataChangeTrigger { get; init; }

    /// <summary>Client only: Deadband type for filtering value changes.</summary>
    public DeadbandType? DeadbandType { get; init; }

    /// <summary>Client only: Deadband value for filtering.</summary>
    public double? DeadbandValue { get; init; }

    // Node structure (server only)
    /// <summary>Server only: Reference type (e.g., "HasComponent", "HasProperty").</summary>
    public string? ReferenceType { get; init; }

    /// <summary>Server only: Reference type for collection items.</summary>
    public string? ItemReferenceType { get; init; }

    /// <summary>Server only: Type definition (e.g., "FolderType").</summary>
    public string? TypeDefinition { get; init; }

    /// <summary>Server only: Namespace for type definition.</summary>
    public string? TypeDefinitionNamespace { get; init; }

    // Companion spec support (server only)
    /// <summary>Server only: Modelling rule (Mandatory, Optional, etc.).</summary>
    public ModellingRule? ModellingRule { get; init; }

    /// <summary>Server only: Event notifier flags for objects that emit events.</summary>
    public byte? EventNotifier { get; init; }

    /// <summary>Server only: Additional non-hierarchical references (HasInterface, etc.).</summary>
    public IReadOnlyList<OpcUaReference>? AdditionalReferences { get; init; }

    /// <summary>
    /// Merges this configuration with another, where this configuration takes priority.
    /// Null fields in this are filled from other.
    /// </summary>
    public OpcUaNodeConfiguration MergeWith(OpcUaNodeConfiguration? other)
    {
        if (other is null) return this;

        return this with
        {
            BrowseName = BrowseName ?? other.BrowseName,
            BrowseNamespaceUri = BrowseNamespaceUri ?? other.BrowseNamespaceUri,
            NodeIdentifier = NodeIdentifier ?? other.NodeIdentifier,
            NodeNamespaceUri = NodeNamespaceUri ?? other.NodeNamespaceUri,
            DisplayName = DisplayName ?? other.DisplayName,
            Description = Description ?? other.Description,
            SamplingInterval = SamplingInterval ?? other.SamplingInterval,
            QueueSize = QueueSize ?? other.QueueSize,
            DiscardOldest = DiscardOldest ?? other.DiscardOldest,
            DataChangeTrigger = DataChangeTrigger ?? other.DataChangeTrigger,
            DeadbandType = DeadbandType ?? other.DeadbandType,
            DeadbandValue = DeadbandValue ?? other.DeadbandValue,
            ReferenceType = ReferenceType ?? other.ReferenceType,
            ItemReferenceType = ItemReferenceType ?? other.ItemReferenceType,
            TypeDefinition = TypeDefinition ?? other.TypeDefinition,
            TypeDefinitionNamespace = TypeDefinitionNamespace ?? other.TypeDefinitionNamespace,
            ModellingRule = ModellingRule ?? other.ModellingRule,
            EventNotifier = EventNotifier ?? other.EventNotifier,
            AdditionalReferences = AdditionalReferences ?? other.AdditionalReferences,
        };
    }
}

/// <summary>
/// Represents an OPC UA reference for additional non-hierarchical relationships.
/// </summary>
public record OpcUaReference
{
    public required string ReferenceType { get; init; }
    public required string TargetNodeId { get; init; }
    public string? TargetNamespaceUri { get; init; }
    public bool IsForward { get; init; } = true;
}

/// <summary>
/// OPC UA modelling rule for type definitions.
/// </summary>
public enum ModellingRule
{
    Mandatory,
    Optional,
    MandatoryPlaceholder,
    OptionalPlaceholder,
    ExposesItsArray
}
```

**Note:** The following are intentionally excluded (derived at runtime):
- `AccessLevel` - Derived from `property.HasSetter`
- `ValueRank`, `ArrayDimensions` - Derived from property type/value
- `Historizing` - Requires full history server implementation

**Future enhancement:** See "Property Attributes as VariableNode Children" section below for EURange, EngineeringUnits, and other companion spec metadata.

---

## CompositeNodeMapper

Combines multiple mappers with merge semantics. Earlier mappers in the list take priority for conflicting values.

```csharp
namespace Namotion.Interceptor.OpcUa;

public class CompositeNodeMapper : IOpcUaNodeMapper
{
    private readonly IOpcUaNodeMapper[] _mappers;

    public CompositeNodeMapper(params IOpcUaNodeMapper[] mappers)
    {
        _mappers = mappers;
    }

    public OpcUaNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property)
    {
        OpcUaNodeConfiguration? result = null;

        foreach (var mapper in _mappers)
        {
            var config = mapper.TryGetConfiguration(property);
            if (config is not null)
            {
                result = result?.MergeWith(config) ?? config;
            }
        }

        return result;
    }

    public async Task<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject subject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken)
    {
        foreach (var mapper in _mappers)
        {
            var property = await mapper.TryGetPropertyAsync(
                subject, nodeReference, session, cancellationToken);
            if (property is not null)
                return property;
        }

        return null;
    }
}
```

**Merge example:**
```
Fluent:       { SamplingInterval: 50 }
Attribute:    { BrowseName: "Speed", SamplingInterval: 100 }
PathProvider: { BrowseName: "speed" }

Result: { BrowseName: "Speed", SamplingInterval: 50 }
         ↑ Attribute wins      ↑ Fluent wins
```

---

## Implementation 1: AttributeOpcUaNodeMapper

Maps properties using `[OpcUaNode]` attributes. Converts sentinel values (used in attributes because C# doesn't support nullable attribute parameters) to nullable configuration values.

```csharp
namespace Namotion.Interceptor.OpcUa;

public class AttributeOpcUaNodeMapper : IOpcUaNodeMapper
{
    private readonly string? _defaultNamespaceUri;

    public AttributeOpcUaNodeMapper(string? defaultNamespaceUri = null)
    {
        _defaultNamespaceUri = defaultNamespaceUri;
    }

    public OpcUaNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property)
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

        if (opcUaAttr is null && refTypeAttr is null && typeDefAttr is null)
            return null;

        return new OpcUaNodeConfiguration
        {
            BrowseName = opcUaAttr?.BrowseName,
            BrowseNamespaceUri = opcUaAttr?.BrowseNamespaceUri,
            NodeIdentifier = opcUaAttr?.NodeIdentifier,
            NodeNamespaceUri = opcUaAttr?.NodeNamespaceUri,
            // Convert sentinel values to nullable using existing extension methods
            SamplingInterval = opcUaAttr.GetSamplingIntervalOrNull(),
            QueueSize = opcUaAttr.GetQueueSizeOrNull(),
            DiscardOldest = opcUaAttr.GetDiscardOldestOrNull(),
            DataChangeTrigger = opcUaAttr.GetDataChangeTriggerOrNull(),
            DeadbandType = opcUaAttr.GetDeadbandTypeOrNull(),
            DeadbandValue = opcUaAttr.GetDeadbandValueOrNull(),
            ReferenceType = refTypeAttr?.Type,
            ItemReferenceType = itemRefTypeAttr?.Type,
            TypeDefinition = typeDefAttr?.Type,
            TypeDefinitionNamespace = typeDefAttr?.Namespace,
        };
    }

    public Task<RegisteredSubjectProperty?> TryGetPropertyAsync(
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
            if (attr?.NodeIdentifier == nodeIdString)
            {
                // Use attribute namespace, fall back to default, then compare
                var propertyNamespaceUri = attr.NodeNamespaceUri ?? _defaultNamespaceUri;
                if (propertyNamespaceUri is null || propertyNamespaceUri == nodeNamespaceUri)
                {
                    return Task.FromResult<RegisteredSubjectProperty?>(property);
                }
            }
        }

        // Priority 2: BrowseName match
        var browseName = nodeReference.BrowseName.Name;
        foreach (var property in subject.Properties)
        {
            var attr = property.TryGetOpcUaNodeAttribute();
            if (attr?.BrowseName == browseName)
            {
                return Task.FromResult<RegisteredSubjectProperty?>(property);
            }
        }

        return Task.FromResult<RegisteredSubjectProperty?>(null);
    }
}
```

---

## Implementation 2: PathProviderOpcUaNodeMapper

Maps properties using an `IPathProvider` for inclusion and browse names.

```csharp
namespace Namotion.Interceptor.OpcUa;

public class PathProviderOpcUaNodeMapper : IOpcUaNodeMapper
{
    private readonly IPathProvider _pathProvider;

    public PathProviderOpcUaNodeMapper(IPathProvider pathProvider)
    {
        _pathProvider = pathProvider;
    }

    public OpcUaNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property)
    {
        if (!_pathProvider.IsPropertyIncluded(property))
            return null;

        // Use PathProvider segment, or fall back to property.BrowseName
        // (which is AttributeName for attributes, Name for regular properties)
        var browseName = _pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;

        // Default ReferenceType to HasProperty for attributes
        var referenceType = property.IsAttribute ? "HasProperty" : null;

        return new OpcUaNodeConfiguration
        {
            BrowseName = browseName,
            ReferenceType = referenceType
        };
    }

    public Task<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject subject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken)
    {
        var browseName = nodeReference.BrowseName.Name;

        foreach (var property in subject.Properties)
        {
            if (!_pathProvider.IsPropertyIncluded(property))
                continue;

            var segment = _pathProvider.TryGetPropertySegment(property);
            if (segment == browseName)
                return Task.FromResult<RegisteredSubjectProperty?>(property);
        }

        return Task.FromResult<RegisteredSubjectProperty?>(null);
    }
}
```

---

## Implementation 3: FluentOpcUaNodeMapper&lt;T&gt;

Maps properties using code-based fluent configuration with nested builders. Supports instance-based configuration (different config for `Motor1.Speed` vs `Motor2.Speed`).

### Usage Examples

```csharp
// Basic usage with nested builders
var mapper = new FluentOpcUaNodeMapper<Machine>()
    .Map(m => m.Motor1, motor => motor
        .BrowseName("MainDriveMotor")
        .ReferenceType("HasComponent")
        .Map(m => m.Speed, speed => speed
            .BrowseName("Speed")
            .SamplingInterval(50))
        .Map(m => m.Temperature, temp => temp
            .BrowseName("Temp")
            .SamplingInterval(500)))
    .Map(m => m.Motor2, motor => motor
        .BrowseName("AuxMotor")
        .Map(m => m.Speed, speed => speed
            .SamplingInterval(100)));

// Reusable configuration via extension methods
public static TBuilder ConfigureMotor<TBuilder>(this TBuilder builder)
    where TBuilder : IPropertyBuilder<Motor>
{
    return builder
        .Map(m => m.Speed, speed => speed
            .BrowseName("Speed")
            .SamplingInterval(50))
        .Map(m => m.Temperature, temp => temp
            .BrowseName("Temp"));
}

// Usage with extension method
var mapper = new FluentOpcUaNodeMapper<Machine>()
    .Map(m => m.Motor1, motor => motor
        .BrowseName("MainDriveMotor")
        .ConfigureMotor())
    .Map(m => m.Motor2, motor => motor
        .BrowseName("AuxMotor")
        .ConfigureMotor());
```

### Builder Interface

```csharp
namespace Namotion.Interceptor.OpcUa;

public interface IPropertyBuilder<T>
{
    // Shared - naming/identification
    IPropertyBuilder<T> BrowseName(string value);
    IPropertyBuilder<T> BrowseNamespaceUri(string value);
    IPropertyBuilder<T> NodeIdentifier(string value);
    IPropertyBuilder<T> NodeNamespaceUri(string value);
    IPropertyBuilder<T> DisplayName(string value);
    IPropertyBuilder<T> Description(string value);

    // Client only - monitoring
    IPropertyBuilder<T> SamplingInterval(int value);
    IPropertyBuilder<T> QueueSize(uint value);
    IPropertyBuilder<T> DiscardOldest(bool value);
    IPropertyBuilder<T> DataChangeTrigger(DataChangeTrigger value);
    IPropertyBuilder<T> DeadbandType(DeadbandType value);
    IPropertyBuilder<T> DeadbandValue(double value);

    // Server only - node structure
    IPropertyBuilder<T> ReferenceType(string value);
    IPropertyBuilder<T> ItemReferenceType(string value);
    IPropertyBuilder<T> TypeDefinition(string value);
    IPropertyBuilder<T> TypeDefinitionNamespace(string value);

    // Server only - companion spec support
    IPropertyBuilder<T> ModellingRule(ModellingRule value);
    IPropertyBuilder<T> EventNotifier(byte value);
    IPropertyBuilder<T> AddReference(string referenceType, string targetNodeId, string? targetNamespaceUri = null, bool isForward = true);

    // Nested property mapping
    IPropertyBuilder<T> Map<TProperty>(
        Expression<Func<T, TProperty>> propertySelector,
        Action<IPropertyBuilder<TProperty>> configure);
}
```

### Implementation

```csharp
namespace Namotion.Interceptor.OpcUa;

public class FluentOpcUaNodeMapper<T> : IOpcUaNodeMapper
{
    // Maps property paths to configurations
    // Key is the full path like "Motor1.Speed" or "Motor1.Temperature"
    private readonly Dictionary<string, OpcUaNodeConfiguration> _mappings = new();

    public FluentOpcUaNodeMapper<T> Map<TProperty>(
        Expression<Func<T, TProperty>> propertySelector,
        Action<IPropertyBuilder<TProperty>> configure)
    {
        var path = GetPropertyPath(propertySelector);
        var builder = new PropertyBuilder<TProperty>(path, _mappings);
        configure(builder);
        return this;
    }

    public OpcUaNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property)
    {
        var path = GetPropertyPath(property);
        return _mappings.TryGetValue(path, out var config) ? config : null;
    }

    public Task<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject subject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken)
    {
        var browseName = nodeReference.BrowseName.Name;

        foreach (var property in subject.Properties)
        {
            var path = GetPropertyPath(property);
            if (_mappings.TryGetValue(path, out var config) && config.BrowseName == browseName)
                return Task.FromResult<RegisteredSubjectProperty?>(property);
        }

        return Task.FromResult<RegisteredSubjectProperty?>(null);
    }

    private static string GetPropertyPath<TProperty>(Expression<Func<T, TProperty>> expression)
    {
        // Extract path from expression like m => m.Motor1.Speed -> "Motor1.Speed"
        var parts = new List<string>();
        var current = expression.Body;

        while (current is MemberExpression member)
        {
            parts.Insert(0, member.Member.Name);
            current = member.Expression;
        }

        return string.Join(".", parts);
    }

    private static string GetPropertyPath(RegisteredSubjectProperty property)
    {
        // Build path by walking up the object graph via Parents
        var parts = new List<string> { property.Name };
        var currentSubject = property.Parent;

        while (currentSubject.Parents.Length > 0)
        {
            var parent = currentSubject.Parents[0]; // First parent
            parts.Insert(0, parent.Property.Name);
            currentSubject = parent.Property.Parent;
        }

        return string.Join(".", parts);
    }

    private class PropertyBuilder<TProp> : IPropertyBuilder<TProp>
    {
        private readonly string _basePath;
        private readonly Dictionary<string, OpcUaNodeConfiguration> _mappings;
        private OpcUaNodeConfiguration _config = new();

        public PropertyBuilder(string basePath, Dictionary<string, OpcUaNodeConfiguration> mappings)
        {
            _basePath = basePath;
            _mappings = mappings;
            _mappings[basePath] = _config;
        }

        public IPropertyBuilder<TProp> BrowseName(string value)
        {
            _config = _config with { BrowseName = value };
            _mappings[_basePath] = _config;
            return this;
        }

        public IPropertyBuilder<TProp> SamplingInterval(int value)
        {
            _config = _config with { SamplingInterval = value };
            _mappings[_basePath] = _config;
            return this;
        }

        // ... other property setters follow same pattern

        public IPropertyBuilder<TProp> Map<TProperty>(
            Expression<Func<TProp, TProperty>> propertySelector,
            Action<IPropertyBuilder<TProperty>> configure)
        {
            var relativePath = GetPropertyPath(propertySelector);
            var fullPath = $"{_basePath}.{relativePath}";
            var builder = new PropertyBuilder<TProperty>(fullPath, _mappings);
            configure(builder);
            return this;
        }

        private static string GetPropertyPath<TProperty>(Expression<Func<TProp, TProperty>> expression)
        {
            // Same logic as parent class
            var parts = new List<string>();
            var current = expression.Body;

            while (current is MemberExpression member)
            {
                parts.Insert(0, member.Member.Name);
                current = member.Expression;
            }

            return string.Join(".", parts);
        }
    }
}
```

---

## Integration

### OpcUaClientConfiguration Changes

```csharp
public class OpcUaClientConfiguration
{
    // PathProvider property is REMOVED - use NodeMapper instead

    /// <summary>
    /// Maps C# properties to OPC UA nodes.
    /// Note: Default is null. If not set, a default composite mapper is created
    /// using DefaultNamespaceUri (for AttributeOpcUaNodeMapper).
    /// </summary>
    public IOpcUaNodeMapper? NodeMapper { get; init; }

    // Lazily create default mapper with access to DefaultNamespaceUri
    private IOpcUaNodeMapper? _resolvedNodeMapper;
    internal IOpcUaNodeMapper ActualNodeMapper => NodeMapper ?? LazyInitializer.EnsureInitialized(
        ref _resolvedNodeMapper,
        () => new CompositeNodeMapper(
            new AttributeOpcUaNodeMapper(DefaultNamespaceUri),
            new PathProviderOpcUaNodeMapper(new AttributeBasedPathProvider("opc"))))!;

    // Existing Default* properties remain for configuration-level defaults
    // (DefaultSamplingInterval, DefaultQueueSize, etc.)
    // These are applied AFTER mapper resolution - see CreateMonitoredItem below
}
```

### OpcUaServerConfiguration Changes

```csharp
public class OpcUaServerConfiguration
{
    // PathProvider property is REMOVED - use NodeMapper instead

    /// <summary>
    /// Maps C# properties to OPC UA nodes.
    /// Defaults to composite of attribute-based mapping with path provider fallback.
    /// </summary>
    public IOpcUaNodeMapper NodeMapper { get; init; }
        = new CompositeNodeMapper(
            new AttributeOpcUaNodeMapper(),
            new PathProviderOpcUaNodeMapper(new AttributeBasedPathProvider("opc")));
}
```

### CreateMonitoredItem Migration

The existing `CreateMonitoredItem` method changes to use `NodeMapper` instead of direct attribute access:

```csharp
// In OpcUaClientConfiguration
internal MonitoredItem CreateMonitoredItem(NodeId nodeId, RegisteredSubjectProperty property)
{
    // Get mapper configuration (replaces TryGetOpcUaNodeAttribute)
    var config = ActualNodeMapper.TryGetConfiguration(property);

    var item = new MonitoredItem(TelemetryContext)
    {
        StartNodeId = nodeId,
        AttributeId = Opc.Ua.Attributes.Value,
        MonitoringMode = MonitoringMode.Reporting,
        Handle = property
    };

    // Apply mapper config, then configuration-level defaults
    var samplingInterval = config?.SamplingInterval ?? DefaultSamplingInterval;
    if (samplingInterval.HasValue)
    {
        item.SamplingInterval = samplingInterval.Value;
    }

    var queueSize = config?.QueueSize ?? DefaultQueueSize;
    if (queueSize.HasValue)
    {
        item.QueueSize = queueSize.Value;
    }

    var discardOldest = config?.DiscardOldest ?? DefaultDiscardOldest;
    if (discardOldest.HasValue)
    {
        item.DiscardOldest = discardOldest.Value;
    }

    var filter = CreateDataChangeFilter(config);
    if (filter != null)
    {
        item.Filter = filter;
    }

    return item;
}

private DataChangeFilter? CreateDataChangeFilter(OpcUaNodeConfiguration? config)
{
    var trigger = config?.DataChangeTrigger ?? DefaultDataChangeTrigger;
    var deadbandType = config?.DeadbandType ?? DefaultDeadbandType;
    var deadbandValue = config?.DeadbandValue ?? DefaultDeadbandValue;

    if (!trigger.HasValue && !deadbandType.HasValue && !deadbandValue.HasValue)
    {
        return null;
    }

    return new DataChangeFilter
    {
        Trigger = trigger ?? DataChangeTrigger.StatusValue,
        DeadbandType = (uint)(deadbandType ?? DeadbandType.None),
        DeadbandValue = deadbandValue ?? 0.0
    };
}
```

### Usage Examples

```csharp
// Default: Attributes merged with property name fallback
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://localhost:4840"
    // NodeMapper uses default CompositeNodeMapper
};

// Attributes only
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://localhost:4840",
    NodeMapper = new AttributeOpcUaNodeMapper()
};

// Fluent with attribute and path provider fallback (merge semantics)
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://localhost:4840",
    NodeMapper = new CompositeNodeMapper(
        new FluentOpcUaNodeMapper<Machine>()
            .Map(m => m.Motor1.Speed, s => s.SamplingInterval(50))
            .Map(m => m.Motor2.Speed, s => s.SamplingInterval(100)),
        new AttributeOpcUaNodeMapper(),
        new PathProviderOpcUaNodeMapper(DefaultPathProvider.Instance))
};

// Instance-based configuration with shared defaults
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://localhost:4840",
    NodeMapper = new CompositeNodeMapper(
        new FluentOpcUaNodeMapper<Machine>()
            .Map(m => m.Motor1, motor => motor
                .BrowseName("MainDriveMotor")
                .Map(m => m.Speed, s => s.SamplingInterval(50)))
            .Map(m => m.Motor2, motor => motor
                .BrowseName("AuxMotor")
                .Map(m => m.Speed, s => s.SamplingInterval(200))),
        new AttributeOpcUaNodeMapper())
};
```

---

## Validation Feature

After loading subjects, warn about unmatched properties that have OPC UA configuration. Collects all unmatched properties into a single log entry to avoid spam:

```csharp
// In OpcUaSubjectLoader, after loading:
private void ValidatePropertyMappings(RegisteredSubject subject, HashSet<RegisteredSubjectProperty> matchedProperties)
{
    var unmatchedProperties = new List<string>();

    foreach (var property in subject.Properties)
    {
        var config = _configuration.NodeMapper.TryGetConfiguration(property);
        if (config is not null && !matchedProperties.Contains(property))
        {
            unmatchedProperties.Add(property.Name);
        }
    }

    if (unmatchedProperties.Count > 0)
    {
        _logger.LogWarning(
            "Subject {Subject} has {Count} mapped properties with no matching OPC UA nodes: {Properties}",
            subject.Subject.GetType().Name,
            unmatchedProperties.Count,
            unmatchedProperties);
    }
}
```

**Output:**
```
warn: Subject Machine has 3 mapped properties with no matching OPC UA nodes: ["Temperature", "Pressure", "Speed"]
```

Single log entry with structured parameters for easy filtering/alerting.

---

## Property Attributes as VariableNode Children

Registry property attributes enable child nodes (HasProperty) on VariableNodes. This is how OPC UA models metadata like EURange, EngineeringUnits, InstrumentRange, etc.

### Conceptual Mapping

```
C# Model (Registry)                 OPC UA Model
───────────────────                 ────────────────────
Property                       →    VariableNode
  └── PropertyAttribute        →      └── VariableNode (HasProperty)
```

### Example: Temperature with EURange

```csharp
[InterceptorSubject]
public partial class TemperatureSensor
{
    [OpcUaNode]
    public partial double Value { get; set; }

    // Property attribute = child node on Value
    [PropertyAttribute(nameof(Value), "EURange")]
    [OpcUaNode(ReferenceType = "HasProperty")]
    public partial Range Value_EURange { get; set; }

    [PropertyAttribute(nameof(Value), "EngineeringUnits")]
    [OpcUaNode(ReferenceType = "HasProperty")]
    public partial EUInformation Value_EngineeringUnits { get; set; }
}
```

**Produces OPC UA structure:**
```
TemperatureSensor (ObjectNode)
└── Value (VariableNode)
    ├── EURange (VariableNode, HasProperty)
    │   └── Value: { Low: 0, High: 100 }
    └── EngineeringUnits (VariableNode, HasProperty)
        └── Value: { DisplayName: "°C", UnitId: 4408256 }
```

### Defaults for Property Attributes

The registry already provides smart defaults via `RegisteredSubjectProperty.BrowseName`:

```csharp
// In RegisteredSubjectProperty.cs
public string BrowseName => IsAttribute ? AttributeMetadata.AttributeName : Name;
```

So for:
```csharp
[PropertyAttribute(nameof(Value), "EURange")]  // AttributeName = "EURange"
public partial Range Value_EURange { get; set; }  // Name = "Value_EURange"
```

- `property.BrowseName` returns `"EURange"` (not `"Value_EURange"`)
- Default `ReferenceType` for attributes should be `"HasProperty"`

The `PathProviderOpcUaNodeMapper` should use `property.BrowseName` as the default OPC UA BrowseName.

### Configuration Options

**Option A: Via [OpcUaNode] attribute (explicit config):**
```csharp
[PropertyAttribute(nameof(Value), "EURange")]
[OpcUaNode(ReferenceType = "HasProperty")]  // Optional - HasProperty is the default for attributes
public partial Range Value_EURange { get; set; }
```

**Option B: No config needed (uses defaults):**
```csharp
[PropertyAttribute(nameof(Value), "EURange")]
public partial Range Value_EURange { get; set; }
// BrowseName defaults to "EURange" (from AttributeName)
// ReferenceType defaults to "HasProperty" (for attributes)
```

**Option C: Via fluent mapper (override defaults):**
```csharp
var mapper = new FluentOpcUaNodeMapper<TemperatureSensor>()
    .Map(m => m.Value_EURange, eurange => eurange
        .BrowseName("CustomName"));  // Override default
```

All approaches work - attribute properties are regular properties that the mapper can configure.

### Server Implementation

**Current gap:** `CustomNodeManager.CreateVariableNode` creates variable nodes but doesn't enumerate `property.Attributes`.

**Required changes to `CustomNodeManager.cs`:**

```csharp
private void CreateVariableNode(string propertyName, RegisteredSubjectProperty property, NodeId parentNodeId, string parentPath)
{
    // ... existing variable node creation ...

    property.Reference.SetPropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey, variableNode);

    // NEW: Create child nodes for property attributes
    CreateAttributeNodes(property, variableNode.NodeId, parentPath + propertyName);
}

private void CreateAttributeNodes(RegisteredSubjectProperty property, NodeId parentNodeId, string parentPath)
{
    foreach (var attribute in property.Attributes)
    {
        var attrConfig = _configuration.NodeMapper.TryGetConfiguration(attribute);
        if (attrConfig is null)
            continue;

        // Use BrowseName from config, or default to attribute name
        var browseName = attrConfig.BrowseName ?? attribute.BrowseName;

        // Use configured reference type, default to HasProperty for attributes
        var referenceType = attrConfig.ReferenceType ?? "HasProperty";
        var referenceTypeId = GetReferenceTypeIdFromString(referenceType);

        // Create child variable node
        var value = _configuration.ValueConverter.ConvertToNodeValue(attribute.GetValue(), attribute);
        var typeInfo = _configuration.ValueConverter.GetNodeTypeInfo(attribute.Type);
        var nodeId = GetNodeId(attribute, parentPath + PathDelimiter + browseName);

        var childNode = CreateVariableNode(parentNodeId, nodeId,
            new QualifiedName(browseName, NamespaceIndex), typeInfo, referenceTypeId);

        childNode.Handle = attribute.Reference;
        childNode.Value = value;

        // ... setup change handlers similar to regular variables ...

        attribute.Reference.SetPropertyData(OpcUaSubjectServerBackgroundService.OpcVariableKey, childNode);
    }
}
```

### Client Implementation

**Current gap:** `OpcUaSubjectLoader.LoadSubjectAsync` loads properties but doesn't load attribute child nodes.

**Required changes to `OpcUaSubjectLoader.cs`:**

```csharp
private async Task LoadSubjectAsync(...)
{
    // ... existing property loading ...

    // After loading a variable property:
    if (!property.IsSubjectReference && !property.IsSubjectCollection && !property.IsSubjectDictionary)
    {
        MonitorValueNode(childNodeId, property, monitoredItems);

        // NEW: Load attribute child nodes
        await LoadPropertyAttributesAsync(property, childNodeId, session, monitoredItems, cancellationToken);
    }
}

private async Task LoadPropertyAttributesAsync(
    RegisteredSubjectProperty property,
    NodeId variableNodeId,
    ISession session,
    List<MonitoredItem> monitoredItems,
    CancellationToken cancellationToken)
{
    // Browse child nodes of the variable (HasProperty references)
    var childRefs = await BrowsePropertyChildrenAsync(variableNodeId, session, cancellationToken);

    foreach (var childRef in childRefs)
    {
        // Find matching attribute by browse name
        var attribute = property.Attributes
            .FirstOrDefault(a => a.BrowseName == childRef.BrowseName.Name);

        if (attribute is not null)
        {
            var childNodeId = ExpandedNodeId.ToNodeId(childRef.NodeId, session.NamespaceUris);
            MonitorValueNode(childNodeId, attribute, monitoredItems);
        }
    }
}

private async Task<ReferenceDescriptionCollection> BrowsePropertyChildrenAsync(
    NodeId nodeId, ISession session, CancellationToken cancellationToken)
{
    // Browse with HasProperty reference type
    var browseDescriptions = new BrowseDescriptionCollection
    {
        new BrowseDescription
        {
            NodeId = nodeId,
            BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = ReferenceTypeIds.HasProperty,
            IncludeSubtypes = true,
            NodeClassMask = (uint)NodeClass.Variable,
            ResultMask = (uint)BrowseResultMask.All
        }
    };

    // ... browse and return results ...
}
```
```

### Benefits

1. **Consistent model** - No special-casing for EURange, etc.
2. **Dynamic updates** - Attribute values are tracked properties, changes propagate to OPC UA
3. **Companion spec support** - Generated code models everything as properties/attributes
4. **Reusable** - Same pattern for custom metadata

### Standard OPC UA Property Types

For companion spec compliance, provide standard types:

```csharp
// Standard OPC UA types for property values
public record Range(double Low, double High);

public record EUInformation(
    string NamespaceUri,
    int UnitId,
    string DisplayName,
    string? Description = null);
```

---

## Summary

| Component | Name |
|-----------|------|
| **Interface** | `IOpcUaNodeMapper` |
| **Config record** | `OpcUaNodeConfiguration` |
| **Composite** | `CompositeNodeMapper` (merge semantics) |
| **Implementations** | `AttributeOpcUaNodeMapper`, `PathProviderOpcUaNodeMapper`, `FluentOpcUaNodeMapper<T>` |
| **Default** | `CompositeNodeMapper(AttributeOpcUaNodeMapper, PathProviderOpcUaNodeMapper)` |
| **Attribute** | `OpcUaNodeAttribute` (contains all properties) |
| **Standard types** | `Range`, `EUInformation` (for companion spec metadata) |

### Key Design Decisions

1. **Unified interface** - Single `IOpcUaNodeMapper` for client and server; XML docs indicate client/server-only fields
2. **Composite with merge** - `CompositeNodeMapper` merges configurations; earlier mappers take priority
3. **No fallback in mappers** - Individual mappers are simple; composition happens via `CompositeNodeMapper`
4. **Instance-based fluent config** - `FluentOpcUaNodeMapper<T>` uses paths, enabling different config for `Motor1.Speed` vs `Motor2.Speed`
5. **Nested builder API** - `.Map()` method with builder callbacks; method-per-property without `With` prefix
6. **Reusable via extension methods** - `IPropertyBuilder<T>` enables shared configuration across instances
7. **PathProvider for filtering** - Shared inclusion logic via `IPathProvider`
8. **Derived values excluded** - AccessLevel, ValueRank, ArrayDimensions derived from C# property metadata
9. **Property attributes as VariableNode children** - Registry property attributes create HasProperty child nodes (EURange, EngineeringUnits, etc.)
10. **Inclusion via null return** - A property is excluded if all mappers return `null` for it; `PathProviderOpcUaNodeMapper` uses `IsPropertyIncluded()` to return null for excluded properties

---

## Migration Guide

### Breaking Changes

1. **`PathProvider` property removed** from both `OpcUaClientConfiguration` and `OpcUaServerConfiguration`
   - Replace with `NodeMapper` property
   - If you only used PathProvider for filtering/naming, wrap it: `new PathProviderOpcUaNodeMapper(yourPathProvider)`

2. **`AttributeOpcUaNodeMapper` constructor changed**
   - Now accepts optional `defaultNamespaceUri` parameter
   - The default composite mapper passes `DefaultNamespaceUri` automatically

### Migration Examples

**Before:**
```csharp
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://localhost:4840",
    PathProvider = new AttributeBasedPathProvider("opc"),
    // ...
};
```

**After (using defaults):**
```csharp
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://localhost:4840",
    // NodeMapper uses default if not specified
    // ...
};
```

**After (custom mapper):**
```csharp
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://localhost:4840",
    DefaultNamespaceUri = "http://example.com/",
    NodeMapper = new CompositeNodeMapper(
        new FluentOpcUaNodeMapper<Machine>()
            .Map(m => m.Motor1.Speed, s => s.SamplingInterval(50)),
        new AttributeOpcUaNodeMapper("http://example.com/"),
        new PathProviderOpcUaNodeMapper(new AttributeBasedPathProvider("opc"))),
    // ...
};
```

### Internal Code Changes

1. **`OpcUaSubjectLoader`**: Replace `_configuration.PathProvider` with `_configuration.ActualNodeMapper`
2. **`CustomNodeManager`**: Replace `_configuration.PathProvider` with `_configuration.NodeMapper`
3. **Both**: Use `TryGetConfiguration()` for config, `TryGetPropertyAsync()` for reverse lookup

---

## Documentation Updates

### docs/connectors.md

Add a section describing the **Property Mapper** pattern for complex connectors:

1. **When to use property mappers** - OPC UA, Modbus, BACnet need mappers; MQTT, Kafka use PathProvider
2. **Mapper interface pattern** - `TryGetConfiguration()` + `TryGetPropertyAsync()` (client only)
3. **Composite pattern** - Combine mappers with merge semantics
4. **Standard implementations** - Attribute, PathProvider, Fluent
5. **Link to this design** - Reference as canonical example

### docs/connectors/opcua.md

Update with:

1. **Node Mapper Configuration** - What `IOpcUaNodeMapper` does, default behavior
2. **Built-in Mappers** - Document each implementation
3. **Customization Examples** - Show common configuration patterns
4. **Configuration Record** - Document all properties with client/server annotations
