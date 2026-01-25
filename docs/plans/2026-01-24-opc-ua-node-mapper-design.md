# OPC UA Node Mapper Design

## Overview

Replace the current `PathProvider`-based OPC UA mapping with a dedicated `IOpcUaNodeMapper` interface that provides flexible, composable configuration for mapping C# properties to OPC UA nodes.

## Goals

1. **Flexible mapping** - Support attribute-based, path provider-based, fluent, and custom mapping strategies
2. **Composable** - Mappers can be combined via `CompositeNodeMapper` with merge semantics
3. **Unified interface** - Single interface and config record for both client and server (with XML docs indicating client/server-only fields)
4. **Instance-based fluent configuration** - Configure properties by path, not just by type (e.g., `Motor1.Speed` vs `Motor2.Speed`)
5. **Validation** - Warn when C# properties with OPC UA attributes have no matching server node
6. **Semantic attribute model** - Two core attributes matching OPC UA concepts: `OpcUaNodeAttribute` (node metadata) and `OpcUaReferenceAttribute` (edge/reference)

---

## Attribute Consolidation

### Core Concept: Nodes and References

In OPC UA, a C# property represents two concepts:
1. **The Node** - The target node with its attributes (BrowseName, TypeDefinition, etc.)
2. **The Reference** - The edge connecting parent to this node (ReferenceType)

```
Parent ObjectNode
       │
       │ ← Reference (HasComponent, HasProperty, HasAddIn, etc.)
       ▼
   Target Node (Object or Variable)
```

### Three Attributes

#### 1. `OpcUaNodeAttribute` (on class or property)

Describes the node itself. Can be applied to:
- **Class**: Defines defaults for all instances of this type
- **Property**: Overrides/extends class-level settings for this specific instance

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false)]
public class OpcUaNodeAttribute : Attribute
{
    // Node identification
    public string? BrowseName { get; init; }
    public string? BrowseNamespaceUri { get; init; }
    public string? NodeIdentifier { get; init; }  // Property-level only (instance-specific)
    public string? NodeNamespaceUri { get; init; }

    // Display
    public string? DisplayName { get; init; }
    public string? Description { get; init; }

    // Type definition
    public string? TypeDefinition { get; init; }
    public string? TypeDefinitionNamespace { get; init; }

    // NodeClass override (default: Auto - infer from C# type)
    public OpcUaNodeClass NodeClass { get; init; } = OpcUaNodeClass.Auto;

    // DataType override (default: null - infer from C# type)
    // Use when C# type is object or doesn't map directly to OPC UA type
    // Examples: "Double", "Int32", "NodeId", "LocalizedText", "QualifiedName"
    public string? DataType { get; init; }

    // Client monitoring (property-level only)
    public int SamplingInterval { get; init; } = int.MinValue;  // Sentinel: not set
    public uint QueueSize { get; init; } = uint.MaxValue;  // Sentinel: not set
    public DiscardOldestMode DiscardOldest { get; init; } = DiscardOldestMode.Unset;
    public DataChangeTrigger DataChangeTrigger { get; init; } = (DataChangeTrigger)(-1);
    public DeadbandType DeadbandType { get; init; } = (DeadbandType)(-1);
    public double DeadbandValue { get; init; } = double.NaN;

    // Server (class or property level)
    public ModellingRule ModellingRule { get; init; } = ModellingRule.Unset;
    public byte EventNotifier { get; init; } = 0;
}

public enum OpcUaNodeClass
{
    Auto,      // Infer: class → Object, primitive property → Variable
    Object,    // Force ObjectNode
    Variable   // Force VariableNode (complex variable with children)
}

public enum ModellingRule
{
    Unset = -1,
    Mandatory,
    Optional,
    MandatoryPlaceholder,
    OptionalPlaceholder
}
```

#### 2. `OpcUaReferenceAttribute` (on property only)

Describes the reference (edge) from parent to this node.

```csharp
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class OpcUaReferenceAttribute : Attribute
{
    public OpcUaReferenceAttribute(string referenceType = "HasProperty")
    {
        ReferenceType = referenceType;
    }

    /// <summary>
    /// Reference type for this property (e.g., "HasComponent", "HasProperty", "HasAddIn", "Organizes").
    /// Default is "HasProperty".
    /// </summary>
    public string ReferenceType { get; }

    /// <summary>
    /// Reference type for collection/dictionary items.
    /// Default is same as ReferenceType.
    /// </summary>
    public string? ItemReferenceType { get; init; }
}
```

#### 3. `OpcUaValueAttribute` (on property only)

Marks the property that holds the main value for a VariableNode class.

```csharp
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class OpcUaValueAttribute : Attribute
{
}
```

### Resolution Order

For node configuration:
1. **Class-level** `[OpcUaNode]` → base node metadata for the type
2. **Property-level** `[OpcUaNode]` → overrides/extends for this instance
3. **Property name** → fallback for BrowseName if not specified

### Removed Attributes

The following separate attributes are consolidated into `OpcUaNodeAttribute`:
- `OpcUaTypeDefinitionAttribute` → `TypeDefinition` + `TypeDefinitionNamespace` properties
- `OpcUaNodeReferenceTypeAttribute` → replaced by `OpcUaReferenceAttribute`
- `OpcUaNodeItemReferenceTypeAttribute` → `ItemReferenceType` on `OpcUaReferenceAttribute`

---

## Usage Examples

### Basic Object with Properties

```csharp
[InterceptorSubject]
[OpcUaNode(TypeDefinition = "MachineIdentificationType",
           TypeDefinitionNamespace = "http://opcfoundation.org/UA/Machinery/")]
public partial class Identification
{
    // HasProperty is default, no attributes needed
    public partial string? Manufacturer { get; set; }
    public partial string? SerialNumber { get; set; }
}
```

### Object with Different Reference Types

```csharp
[InterceptorSubject]
[OpcUaNode(TypeDefinition = "MachineType")]
public partial class Machine
{
    // HasAddIn reference (override default HasProperty)
    [OpcUaReference("HasAddIn")]
    public partial Identification Identification { get; set; }

    // HasComponent reference
    [OpcUaReference("HasComponent")]
    public partial MachineryBuildingBlocks MachineryBuildingBlocks { get; set; }

    // Organizes for folder-style collections
    [OpcUaReference("Organizes", ItemReferenceType = "HasComponent")]
    public partial IReadOnlyDictionary<string, ProcessValueType> Monitoring { get; set; }
}
```

### Same Instance, Multiple References

```csharp
var identification = new Identification();
var machine = new Machine
{
    Identification = identification,
    MachineryBuildingBlocks = new MachineryBuildingBlocks
    {
        Identification = identification  // Same instance
    }
};
```

```csharp
public partial class Machine
{
    // Primary reference - defines NodeId
    [OpcUaReference("HasAddIn")]
    [OpcUaNode(NodeIdentifier = "Identification")]
    public partial Identification Identification { get; set; }
}

public partial class MachineryBuildingBlocks
{
    // Secondary reference - just an edge, node already exists
    [OpcUaReference("HasAddIn")]
    public partial Identification Identification { get; set; }
}
```

### Different Instances with Different NodeIds

```csharp
[InterceptorSubject]
[OpcUaNode(TypeDefinition = "MotorType")]
public partial class Motor
{
    public partial double Speed { get; set; }
}

[InterceptorSubject]
public partial class Machine
{
    [OpcUaReference("HasComponent")]
    [OpcUaNode(NodeIdentifier = "MainMotor", BrowseName = "MainMotor")]
    public partial Motor Motor1 { get; set; }

    [OpcUaReference("HasComponent")]
    [OpcUaNode(NodeIdentifier = "AuxMotor", BrowseName = "AuxMotor")]
    public partial Motor Motor2 { get; set; }
}
```

### Complex VariableNode (e.g., AnalogSignalVariableType)

When a class represents a VariableType (not ObjectType), use `NodeClass = Variable` and mark the value property:

```csharp
[InterceptorSubject]
[OpcUaNode(
    TypeDefinition = "AnalogSignalVariableType",
    TypeDefinitionNamespace = "http://opcfoundation.org/UA/PADIM/",
    NodeClass = OpcUaNodeClass.Variable)]
public partial class AnalogSignalVariable
{
    // The main value of this VariableNode
    [OpcUaValue]
    public partial object? ActualValue { get; set; }

    // Child Properties (HasProperty is default)
    public partial object? EURange { get; set; }
    public partial object? EngineeringUnits { get; set; }
}
```

**Produces OPC UA structure:**
```
AnalogSignalVariable (VariableNode, type: AnalogSignalVariableType)
├── Value = ActualValue (the Variable's value attribute)
├── EURange (VariableNode via HasProperty)
└── EngineeringUnits (VariableNode via HasProperty)
```

### Property Attributes as VariableNode Children

For standard AnalogItemType pattern (metadata on a specific property):

```csharp
[InterceptorSubject]
public partial class TemperatureSensor
{
    public partial double Value { get; set; }

    // Property attributes become HasProperty children of Value
    [PropertyAttribute(nameof(Value), "EURange")]
    public partial Range Value_EURange { get; set; }

    [PropertyAttribute(nameof(Value), "EngineeringUnits")]
    public partial EUInformation Value_EngineeringUnits { get; set; }
}
```

**Produces:**
```
TemperatureSensor (ObjectNode)
└── Value (VariableNode)
    ├── EURange (VariableNode via HasProperty)
    └── EngineeringUnits (VariableNode via HasProperty)
```

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
/// OPC UA node and reference configuration. All fields are nullable to support partial
/// configuration and merge semantics. Fields are documented as client-only or server-only
/// where applicable.
/// </summary>
public record OpcUaNodeConfiguration
{
    // Node identification (shared)
    public string? BrowseName { get; init; }
    public string? BrowseNamespaceUri { get; init; }
    public string? NodeIdentifier { get; init; }
    public string? NodeNamespaceUri { get; init; }

    /// <summary>Localized display name (if different from BrowseName).</summary>
    public string? DisplayName { get; init; }

    /// <summary>Human-readable description.</summary>
    public string? Description { get; init; }

    // Type definition (shared)
    /// <summary>Type definition (e.g., "FolderType", "AnalogItemType").</summary>
    public string? TypeDefinition { get; init; }

    /// <summary>Namespace for type definition.</summary>
    public string? TypeDefinitionNamespace { get; init; }

    /// <summary>NodeClass override (null = auto-detect from C# type).</summary>
    public OpcUaNodeClass? NodeClass { get; init; }

    /// <summary>DataType override (null = infer from C# type). Examples: "Double", "NodeId", "LocalizedText".</summary>
    public string? DataType { get; init; }

    /// <summary>True if this property holds the main value for a VariableNode class.</summary>
    public bool? IsValue { get; init; }

    // Reference configuration (from OpcUaReferenceAttribute)
    /// <summary>Reference type from parent (e.g., "HasComponent", "HasProperty"). Default: "HasProperty".</summary>
    public string? ReferenceType { get; init; }

    /// <summary>Reference type for collection/dictionary items.</summary>
    public string? ItemReferenceType { get; init; }

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

    // Server configuration
    /// <summary>Server only: Modelling rule (Mandatory, Optional, etc.).</summary>
    public ModellingRule? ModellingRule { get; init; }

    /// <summary>Server only: Event notifier flags for objects that emit events.</summary>
    public byte? EventNotifier { get; init; }

    /// <summary>Server only: Additional non-hierarchical references (HasInterface, etc.).</summary>
    public IReadOnlyList<OpcUaAdditionalReference>? AdditionalReferences { get; init; }

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
            TypeDefinition = TypeDefinition ?? other.TypeDefinition,
            TypeDefinitionNamespace = TypeDefinitionNamespace ?? other.TypeDefinitionNamespace,
            NodeClass = NodeClass ?? other.NodeClass,
            DataType = DataType ?? other.DataType,
            IsValue = IsValue ?? other.IsValue,
            ReferenceType = ReferenceType ?? other.ReferenceType,
            ItemReferenceType = ItemReferenceType ?? other.ItemReferenceType,
            SamplingInterval = SamplingInterval ?? other.SamplingInterval,
            QueueSize = QueueSize ?? other.QueueSize,
            DiscardOldest = DiscardOldest ?? other.DiscardOldest,
            DataChangeTrigger = DataChangeTrigger ?? other.DataChangeTrigger,
            DeadbandType = DeadbandType ?? other.DeadbandType,
            DeadbandValue = DeadbandValue ?? other.DeadbandValue,
            ModellingRule = ModellingRule ?? other.ModellingRule,
            EventNotifier = EventNotifier ?? other.EventNotifier,
            AdditionalReferences = AdditionalReferences ?? other.AdditionalReferences,
        };
    }
}

/// <summary>
/// Represents an additional OPC UA reference for non-hierarchical relationships.
/// </summary>
public record OpcUaAdditionalReference
{
    public required string ReferenceType { get; init; }
    public required string TargetNodeId { get; init; }
    public string? TargetNamespaceUri { get; init; }
    public bool IsForward { get; init; } = true;
}

/// <summary>
/// OPC UA NodeClass for explicit override.
/// </summary>
public enum OpcUaNodeClass
{
    Auto = 0,   // Infer: class → Object, primitive → Variable
    Object,     // Force ObjectNode
    Variable    // Force VariableNode (complex variable with children)
}
```

**Note:** The following are intentionally excluded (derived at runtime):
- `AccessLevel` - Derived from `property.HasSetter`
- `ValueRank`, `ArrayDimensions` - Derived from property type/value
- `Historizing` - Requires full history server implementation

---

## CompositeNodeMapper

Combines multiple mappers with "last wins" merge semantics. Later mappers in the list override earlier mappers for conflicting values. This matches typical configuration layering patterns (base config → overrides).

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
                // Later mappers override earlier ones ("last wins")
                result = config.MergeWith(result);
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

**Merge example (last wins):**
```
PathProvider: { BrowseName: "speed" }          // Base
Attribute:    { BrowseName: "Speed", SamplingInterval: 100 }  // Override
Fluent:       { SamplingInterval: 50 }          // Final override

Result: { BrowseName: "Speed", SamplingInterval: 50 }
         ↑ Attribute wins (later)  ↑ Fluent wins (latest)
```

---

## Implementation 1: AttributeOpcUaNodeMapper

Maps properties using the consolidated `[OpcUaNode]`, `[OpcUaReference]`, and `[OpcUaValue]` attributes.

**Resolution order:**
1. Class-level `[OpcUaNode]` on the property's type (for object references)
2. Property-level `[OpcUaNode]` (overrides class-level)
3. Property-level `[OpcUaReference]` (reference configuration)
4. Property-level `[OpcUaValue]` (marks value property)

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
        // Get class-level OpcUaNode from the property's type (for object references)
        var classAttr = GetClassLevelOpcUaNodeAttribute(property);

        // Get property-level attributes
        var propertyAttr = property.TryGetOpcUaNodeAttribute();
        var referenceAttr = property.ReflectionAttributes
            .OfType<OpcUaReferenceAttribute>()
            .FirstOrDefault();
        var valueAttr = property.ReflectionAttributes
            .OfType<OpcUaValueAttribute>()
            .FirstOrDefault();

        // No OPC UA configuration at all
        if (classAttr is null && propertyAttr is null && referenceAttr is null && valueAttr is null)
            return null;

        // Build configuration from class-level first, then override with property-level
        var classConfig = classAttr is not null ? BuildConfigFromAttribute(classAttr) : null;
        var propertyConfig = propertyAttr is not null ? BuildConfigFromAttribute(propertyAttr) : null;

        // Start with class config, merge property config on top
        var config = propertyConfig?.MergeWith(classConfig) ?? classConfig ?? new OpcUaNodeConfiguration();

        // Apply reference attribute
        if (referenceAttr is not null)
        {
            config = config with
            {
                ReferenceType = config.ReferenceType ?? referenceAttr.ReferenceType,
                ItemReferenceType = config.ItemReferenceType ?? referenceAttr.ItemReferenceType
            };
        }

        // Apply value attribute
        if (valueAttr is not null)
        {
            config = config with { IsValue = true };
        }

        return config;
    }

    private static OpcUaNodeAttribute? GetClassLevelOpcUaNodeAttribute(RegisteredSubjectProperty property)
    {
        // For object references, get the OpcUaNode attribute from the referenced type
        if (property.IsSubjectReference || property.IsSubjectCollection || property.IsSubjectDictionary)
        {
            var elementType = GetElementType(property.Type);
            return elementType?.GetCustomAttribute<OpcUaNodeAttribute>();
        }
        return null;
    }

    private static Type? GetElementType(Type type)
    {
        if (type.IsGenericType)
        {
            var args = type.GetGenericArguments();
            // For Dictionary<K,V>, return V
            if (args.Length == 2) return args[1];
            // For IEnumerable<T>, return T
            if (args.Length == 1) return args[0];
        }
        return type;
    }

    private static OpcUaNodeConfiguration BuildConfigFromAttribute(OpcUaNodeAttribute attr)
    {
        return new OpcUaNodeConfiguration
        {
            BrowseName = attr.BrowseName,
            BrowseNamespaceUri = attr.BrowseNamespaceUri,
            NodeIdentifier = attr.NodeIdentifier,
            NodeNamespaceUri = attr.NodeNamespaceUri,
            DisplayName = attr.DisplayName,
            Description = attr.Description,
            TypeDefinition = attr.TypeDefinition,
            TypeDefinitionNamespace = attr.TypeDefinitionNamespace,
            NodeClass = attr.NodeClass != OpcUaNodeClass.Auto ? attr.NodeClass : null,
            DataType = attr.DataType,
            // Convert sentinel values to nullable
            SamplingInterval = attr.GetSamplingIntervalOrNull(),
            QueueSize = attr.GetQueueSizeOrNull(),
            DiscardOldest = attr.GetDiscardOldestOrNull(),
            DataChangeTrigger = attr.GetDataChangeTriggerOrNull(),
            DeadbandType = attr.GetDeadbandTypeOrNull(),
            DeadbandValue = attr.GetDeadbandValueOrNull(),
            ModellingRule = attr.ModellingRule != ModellingRule.Unset ? attr.ModellingRule : null,
            EventNotifier = attr.EventNotifier != 0 ? attr.EventNotifier : null,
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

Maps properties using an `IPathProvider` for inclusion and browse names. Provides default reference type of "HasProperty".

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

        // Default ReferenceType is always "HasProperty" (simple, predictable)
        // Users override with [OpcUaReference] when needed
        return new OpcUaNodeConfiguration
        {
            BrowseName = browseName,
            ReferenceType = "HasProperty"
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
        var config = _configuration.ActualNodeMapper.TryGetConfiguration(property);
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
    [OpcUaNodeReferenceType("HasProperty")]  // Optional - HasProperty is default for attributes
    public partial Range Value_EURange { get; set; }

    [PropertyAttribute(nameof(Value), "EngineeringUnits")]
    [OpcUaNodeReferenceType("HasProperty")]  // Optional - HasProperty is default for attributes
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

**Option A: Via [OpcUaNodeReferenceType] attribute (explicit config):**
```csharp
[PropertyAttribute(nameof(Value), "EURange")]
[OpcUaNodeReferenceType("HasProperty")]  // Optional - HasProperty is the default for attributes
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
| **Attributes** | `OpcUaNodeAttribute`, `OpcUaReferenceAttribute`, `OpcUaValueAttribute` |
| **Standard types** | `Range`, `EUInformation` (for companion spec metadata) |

### Key Design Decisions

1. **Semantic attribute model** - Two core attributes matching OPC UA concepts:
   - `OpcUaNodeAttribute` (class or property) - node metadata, type definition
   - `OpcUaReferenceAttribute` (property only) - reference/edge type
   - `OpcUaValueAttribute` (property only) - marks value property for VariableNode classes
2. **Class → property resolution** - Class-level `[OpcUaNode]` provides defaults; property-level overrides
3. **Default reference type** - `"HasProperty"` (simple, predictable); override with `[OpcUaReference]`
4. **NodeClass override** - `OpcUaNodeClass.Variable` for classes representing VariableTypes (e.g., AnalogSignalVariableType)
5. **Unified interface** - Single `IOpcUaNodeMapper` for client and server; XML docs indicate client/server-only fields
6. **Composite with merge** - `CompositeNodeMapper` merges configurations; earlier mappers take priority
7. **No fallback in mappers** - Individual mappers are simple; composition happens via `CompositeNodeMapper`
8. **Instance-based fluent config** - `FluentOpcUaNodeMapper<T>` uses paths, enabling different config for `Motor1.Speed` vs `Motor2.Speed`
9. **Derived values excluded** - AccessLevel, ValueRank, ArrayDimensions derived from C# property metadata
10. **Property attributes as VariableNode children** - Registry property attributes create HasProperty child nodes
11. **Inclusion via null return** - A property is excluded if all mappers return `null` for it

---

## OPC UA Concepts Coverage

This design covers the following OPC UA Part 3 concepts:

### NodeClasses

| NodeClass | Coverage | C# Mapping |
|-----------|----------|------------|
| **Object** | Full | C# classes → ObjectNodes |
| **Variable** | Full | C# properties → VariableNodes (Property or DataVariable) |
| **ObjectType** | Full | `TypeDefinition` attribute |
| **VariableType** | Full | `TypeDefinition` + `NodeClass = Variable` |
| **Method** | Future | C# methods (not in this plan) |
| **ReferenceType** | N/A | Use by name, don't create |
| **DataType** | Derived | From C# types |
| **View** | N/A | Out of scope |

### Node Attributes

| Attribute | Coverage | Source |
|-----------|----------|--------|
| NodeId | Full | `NodeIdentifier` + `NodeNamespaceUri` |
| NodeClass | Full | `OpcUaNodeClass` enum |
| BrowseName | Full | `BrowseName` + `BrowseNamespaceUri` |
| DisplayName | Full | `DisplayName` property |
| Description | Full | `Description` property |
| EventNotifier | Full | `EventNotifier` property |
| Value | Full | C# property value |
| DataType | Derived | From C# type |
| ValueRank | Derived | From C# type (scalar, array) |
| ArrayDimensions | Derived | From C# array |
| AccessLevel | Derived | From `property.HasSetter` |
| WriteMask | N/A | Out of scope |
| Historizing | N/A | Requires history server |

### Reference Types

| Reference | Coverage | Configuration |
|-----------|----------|---------------|
| HasProperty | Default | `[OpcUaReference]` or default |
| HasComponent | Full | `[OpcUaReference("HasComponent")]` |
| HasAddIn | Full | `[OpcUaReference("HasAddIn")]` |
| Organizes | Full | `[OpcUaReference("Organizes")]` |
| HasTypeDefinition | Automatic | Via `TypeDefinition` property |
| HasModellingRule | Automatic | Via `ModellingRule` property |
| HasInterface | Partial | Via `AdditionalReferences` |
| GeneratesEvent | N/A | Out of scope |

### Property vs DataVariable Distinction

OPC UA distinguishes:
- **Properties**: Leaf nodes, cannot have children, use `HasProperty`
- **DataVariables**: Can have children via `HasComponent`, own Properties

Our model:
- Default `"HasProperty"` for simple values (correct for Properties)
- Use `[OpcUaReference("HasComponent")]` for:
  - Object references
  - Complex VariableTypes with children (`NodeClass.Variable`)

### Companion Spec Support

The model supports standard companion specs:
- **Machinery (OPC 40001)**: HasAddIn, MachineType, MachineIdentificationType
- **PADIM (OPC 30385)**: AnalogSignalVariableType with children
- **DI (OPC 10000-100)**: Identification patterns

---

## Documentation

User documentation: [docs/opcua-mapping.md](../opcua-mapping.md)
