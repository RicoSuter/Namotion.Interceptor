# OPC UA Mapping Guide

> Part of the [OPC UA integration](connectors-opcua.md). See also: [Client](connectors-opcua-client.md) | [Server](connectors-opcua-server.md)

This document describes how to map C# object models to OPC UA address spaces using Namotion.Interceptor's OPC UA integration.

## Overview

The OPC UA mapping system translates between:
- **C# classes** → OPC UA Object/Variable Nodes
- **C# properties** → OPC UA Variable Nodes (Properties or DataVariables)

## Server vs Client

The same mapping attributes work for both server and client:

- **Server**: Creates OPC UA nodes in the address space based on C# model
- **Client**: Discovers OPC UA nodes and maps them to C# properties

Some configuration applies only to one side:
- `SamplingInterval`, `QueueSize`, `DataChangeTrigger` - Client monitoring
- `EventNotifier`, `ModellingRule` - Server node structure

## Mapper Configuration

The mapping is driven by the `IPropertyMapper<OpcUaPropertyMapping>` interface (or `IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>` for the client, which adds reverse lookup), configured via the `Mapper` property on `OpcUaClientConfiguration` and `OpcUaServerConfiguration`. This extends the general [property mapper](connectors.md#property-mappers) concept with OPC UA-specific node metadata. The default is an `OpcUaCompositeMapper` combining:
- `OpcUaPathProviderMapper`: maps `[Path("opc", "...")]` attributes (see [Path Providers](connectors.md#path-providers))
- `OpcUaAttributeMapper`: maps `[OpcUaNode]` and `[OpcUaReference]` attributes

For custom mapping, set `Mapper` explicitly:

```csharp
var config = new OpcUaClientConfiguration
{
    Mapper = new OpcUaCompositeMapper(
        new OpcUaPathProviderMapper(
            new AttributeBasedPathProvider("opc")),  // Base: path fallback
        new OpcUaAttributeMapper(),                  // Attribute defaults
        fluentMapper),                               // Fluent wins (see Code-Based Mapping)
    // ... other settings
};
```

See [Composite Mappers](#composite-mappers) for merge semantics and [Code-Based (Fluent) Mapping](#code-based-fluent-mapping) for code-based mapping.

## Scope and Limitations

This mapping system is designed for **instance mapping** - creating Object and Variable nodes in an OPC UA address space from C# object models. It supports:

- **Object Nodes**: From C# classes decorated with `[InterceptorSubject]`
- **Variable Nodes**: From C# properties (primitives, arrays, or `[OpcUaValue]` classes)

It does **not** support creating new type definitions (ObjectTypes, VariableTypes, DataTypes). Type definitions are referenced via the `TypeDefinition` configuration property but must either:
- Exist as built-in OPC UA types (e.g., `FolderType`, `BaseDataVariableType`)
- Be loaded from NodeSet XML files via `OpcUaServerConfiguration.LoadPredefinedNodes()`

For scenarios requiring custom type definitions, create them in companion specification NodeSet files and load them at server startup.

## Core Concepts

### OPC UA Address Space Basics

An OPC UA address space consists of:
- **Nodes** - Objects, Variables, Methods, Types, etc.
- **References** - Directed edges connecting nodes (HasComponent, HasProperty, etc.)
- **Attributes** - Metadata on nodes (BrowseName, DisplayName, Value, etc.)

### Mapping Model

```
C# Model                          OPC UA Address Space
─────────────────────────         ────────────────────────────────
class Machine                 →   ObjectNode (type: MachineType)
├── property Motor1           →   ├── ObjectNode via HasComponent
│   ├── property Speed        →   │   └── VariableNode via HasProperty
│   └── property Temp         →   │   └── VariableNode via HasProperty
└── property SerialNumber     →   └── VariableNode via HasProperty
```

## Attributes

### OpcUaNodeAttribute

Defines node metadata. Can be applied to classes (type-level defaults) or properties (instance-specific overrides).

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public class OpcUaNodeAttribute : Attribute
{
    // Constructor - BrowseName is required, namespace defaults to null
    public OpcUaNodeAttribute(string browseName, string? browseNamespaceUri = null, string? connectorName = null);

    // Node identification (from constructor)
    public string BrowseName { get; }
    public string? BrowseNamespaceUri { get; }

    // Node identification (optional overrides)
    public string? NodeIdentifier { get; init; }      // Property-level only
    public string? NodeNamespaceUri { get; init; }

    // Display
    public string? DisplayName { get; init; }
    public string? Description { get; init; }

    // Type definition
    public string? TypeDefinition { get; init; }
    public string? TypeDefinitionNamespace { get; init; }

    // NodeClass override
    public OpcUaNodeClass NodeClass { get; init; } = OpcUaNodeClass.Auto;

    // DataType override (when C# type is object or doesn't map directly)
    // Examples: "Double", "Int32", "NodeId", "LocalizedText"
    public string? DataType { get; init; }

    // Client monitoring
    public int SamplingInterval { get; init; }        // int.MinValue = not set; 0 = exception-based
    public uint QueueSize { get; init; }              // uint.MaxValue = not set; set explicitly with SamplingInterval=0
    public DiscardOldestMode DiscardOldest { get; init; }
    public DataChangeTrigger DataChangeTrigger { get; init; }
    public DeadbandType DeadbandType { get; init; }
    public double DeadbandValue { get; init; }        // NaN = not set

    // Server
    public ModellingRule ModellingRule { get; init; }
    public byte EventNotifier { get; init; }
}
```

> For details on sampling vs exception-based monitoring, see [Monitoring & Subscriptions](connectors-opcua-client.md#monitoring--subscriptions).

**Resolution order:**
1. Class-level `[OpcUaNode]` - defaults for the type
2. Property-level `[OpcUaNode]` - overrides for specific instance
3. Property name - fallback for BrowseName

### OpcUaReferenceAttribute

Defines the reference (edge) from parent to this node.

```csharp
[AttributeUsage(AttributeTargets.Property)]
public class OpcUaReferenceAttribute : Attribute
{
    public OpcUaReferenceAttribute(string referenceType = "HasProperty");

    public string ReferenceType { get; }           // e.g., "HasComponent", "HasProperty"
    public string? ItemReferenceType { get; init; } // For collection/dictionary items
}
```

**Default:** `"HasProperty"` - override explicitly when needed.

### OpcUaValueAttribute

Marks the property that holds the main value for a VariableNode class.

```csharp
[AttributeUsage(AttributeTargets.Property)]
public class OpcUaValueAttribute : Attribute { }
```

Used when a class represents a complex VariableType (like AnalogSignalVariableType) where the class has child properties but also a primary value.

## Common Patterns

### Basic Object with Properties

```csharp
[InterceptorSubject]
[OpcUaNode("Identification",
    TypeDefinition = "MachineIdentificationType",
    TypeDefinitionNamespace = "http://opcfoundation.org/UA/Machinery/")]
public partial class Identification
{
    // Default reference is HasProperty - no attribute needed
    public partial string? Manufacturer { get; set; }
    public partial string? SerialNumber { get; set; }
    public partial string? ProductInstanceUri { get; set; }
}
```

**Produces:**
```
Identification (ObjectNode, type: MachineIdentificationType)
├── Manufacturer (VariableNode via HasProperty)
├── SerialNumber (VariableNode via HasProperty)
└── ProductInstanceUri (VariableNode via HasProperty)
```

### Object Composition with HasComponent

For structural composition (parent "contains" child), use `HasComponent`:

```csharp
[InterceptorSubject]
[OpcUaNode("Machine", TypeDefinition = "MachineType")]
public partial class Machine
{
    // Child object - use HasComponent for structural containment
    [OpcUaReference("HasComponent")]
    public partial Motor Motor1 { get; set; }

    // Simple property - HasProperty is default
    public partial string? SerialNumber { get; set; }
}

[InterceptorSubject]
[OpcUaNode("Motor", TypeDefinition = "MotorType")]
public partial class Motor
{
    public partial double Speed { get; set; }
    public partial double Temperature { get; set; }
}
```

**Produces:**
```
Machine (ObjectNode, type: MachineType)
├── Motor1 (ObjectNode via HasComponent, type: MotorType)
│   ├── Speed (VariableNode via HasProperty)
│   └── Temperature (VariableNode via HasProperty)
└── SerialNumber (VariableNode via HasProperty)
```

### HasAddIn Reference (Companion Specs)

Machinery and other companion specs use `HasAddIn` for optional add-in components:

```csharp
[InterceptorSubject]
public partial class Machine
{
    [OpcUaReference("HasAddIn")]
    [OpcUaNode("Identification", "http://opcfoundation.org/UA/DI/")]
    public partial Identification Identification { get; set; }

    [OpcUaReference("HasComponent")]
    public partial MachineryBuildingBlocks MachineryBuildingBlocks { get; set; }
}
```

### Collections with Organizes

For folder-style organization:

```csharp
[InterceptorSubject]
public partial class Machine
{
    // Folder organizing process values
    [OpcUaReference("Organizes", ItemReferenceType = "HasComponent")]
    [OpcUaNode("Monitoring", TypeDefinition = "FolderType")]
    public partial IReadOnlyDictionary<string, ProcessValueType> Monitoring { get; set; }
}
```

**Produces:**
```
Machine (ObjectNode)
└── Monitoring (FolderType via Organizes)
    ├── Temperature (ObjectNode via HasComponent)
    ├── Pressure (ObjectNode via HasComponent)
    └── Flow (ObjectNode via HasComponent)
```

### Complex VariableNode (VariableType with Children)

When a class represents a VariableType (not ObjectType), the node is a Variable that has a value AND child properties:

```csharp
[InterceptorSubject]
[OpcUaNode("AnalogSignalVariable",
    TypeDefinition = "AnalogSignalVariableType",
    TypeDefinitionNamespace = "http://opcfoundation.org/UA/PADIM/",
    NodeClass = OpcUaNodeClass.Variable)]  // Force VariableNode
public partial class AnalogSignalVariable
{
    // The primary value of this VariableNode
    [OpcUaNode("ActualValue")]
    [OpcUaValue]
    public partial double ActualValue { get; set; }

    // Child properties (standard OPC UA metadata)
    [OpcUaNode("EURange")]
    public partial Range? EURange { get; set; }

    [OpcUaNode("EngineeringUnits")]
    public partial EUInformation? EngineeringUnits { get; set; }
}

[InterceptorSubject]
public partial class Sensor
{
    // Reference to VariableNode - use HasComponent (not HasProperty)
    // because this is a DataVariable, not a simple Property
    [OpcUaReference("HasComponent")]
    public partial AnalogSignalVariable Temperature { get; set; }
}
```

**Produces:**
```
Sensor (ObjectNode)
└── Temperature (VariableNode via HasComponent, type: AnalogSignalVariableType)
    ├── Value = ActualValue (the Variable's value attribute)
    ├── EURange (VariableNode via HasProperty)
    └── EngineeringUnits (VariableNode via HasProperty)
```

### Property Attributes (Metadata on Variables)

For adding standard OPC UA metadata (EURange, EngineeringUnits) to simple properties, use `[PropertyAttribute]`. Properties marked with this attribute are created as HasProperty subnodes of their parent variable node, matching OPC UA semantics where attributes belong to the variable they describe.

```csharp
[InterceptorSubject]
public partial class TemperatureSensor
{
    [OpcUaNode("Value", TypeDefinition = "AnalogItemType")]
    public partial double Value { get; set; }

    // Property attributes become HasProperty children of Value
    [PropertyAttribute(nameof(Value), "EURange")]
    [OpcUaNode("EURange")]
    public partial Range? Value_EURange { get; set; }

    [PropertyAttribute(nameof(Value), "EngineeringUnits")]
    [OpcUaNode("EngineeringUnits")]
    public partial EUInformation? Value_EngineeringUnits { get; set; }
}
```

**Produces:**
```
TemperatureSensor (ObjectNode)
└── Value (VariableNode, type: AnalogItemType)
    ├── EURange (VariableNode via HasProperty)
    └── EngineeringUnits (VariableNode via HasProperty)
```

Note: The C# property names (e.g., `Value_EURange`) are just convention - the OPC UA browse names come from the `[Path]` or `[OpcUaNode]` attributes. Attributes can be nested (attributes on attributes) for complex metadata hierarchies.

`[PropertyAttribute]` is one way to define property attributes. Attributes can also be added dynamically at runtime via `AddAttribute()`. The OPC UA mapping handles all registry attributes the same way. For more on the property attributes concept, see [Registry](registry.md#define-attributes-using-properties).

### Same Instance, Multiple References

When the same C# object is referenced from multiple places, only one OPC UA node is created:

```csharp
var identification = new Identification { Manufacturer = "Acme" };

var machine = new Machine
{
    // Primary reference - defines NodeId
    Identification = identification,

    MachineryBuildingBlocks = new MachineryBuildingBlocks
    {
        // Secondary reference - just an edge to existing node
        Identification = identification
    }
};
```

```csharp
public partial class Machine
{
    [OpcUaReference("HasAddIn")]
    [OpcUaNode("Identification", NodeIdentifier = "Identification")]  // Explicit NodeId
    public partial Identification Identification { get; set; }
}

public partial class MachineryBuildingBlocks
{
    [OpcUaReference("HasAddIn")]
    [OpcUaNode("Identification")]  // No NodeIdentifier - references existing node
    public partial Identification Identification { get; set; }
}
```

> **Note:** Processing order is non-deterministic when multiple properties reference the same object. Duplicate node metadata on all properties to ensure consistent behavior.

### Different NodeIds for Same Type

Different instances of the same type get different NodeIds:

```csharp
[InterceptorSubject]
public partial class Machine
{
    [OpcUaReference("HasComponent")]
    [OpcUaNode("MainMotor", NodeIdentifier = "MainMotor")]
    public partial Motor Motor1 { get; set; }

    [OpcUaReference("HasComponent")]
    [OpcUaNode("AuxMotor", NodeIdentifier = "AuxMotor")]
    public partial Motor Motor2 { get; set; }
}
```

## Code-Based (Fluent) Mapping

`OpcUaFluentMapperBuilder<TRoot>` is a complete, code-based alternative to attribute mapping. Configure a type once with `ForType<T>().Map(...)` and `ForType<T>().Configure(...)`, then call `Build(...)` to produce an `OpcUaFluentMapper`. The mapping resolves everywhere that type appears in the object graph, including collection and dictionary elements (which get bracket indices such as `motors[1]/speed`).

Fluent mapping is layered after attributes in the composite, so fluent wins on conflicts. Omit fluent for attribute-only setups. The two sources compose freely.

### Entry Points

- `ForType<T>().Map(member, configure)`: registers a property. The member selector must be a single member of `T` (not a chain). Applies everywhere `T` appears, including derived types (see Inheritance below).
- `ForType<T>().Configure(configure)`: registers class-level (type-self) node metadata for `T`, equivalent to `[OpcUaNode]` on the class. Merges into every subject member of that type.

The `BrowseName(...)` setter serves a dual role: it sets both the path segment used for node browse-name and reverse navigation, and the node BrowseName metadata. Omit it and both default to the member name, so most leaves need no explicit `BrowseName` call.

### Example

```csharp
var fluentMapper = new OpcUaFluentMapperBuilder<Plant>()
    .ForType<Motor>()
        .Configure(b => b.TypeDefinition("MotorType", "http://my/uri").NodeClass(OpcUaNodeClass.Object))
        .Map(m => m.Speed,  b => b.BrowseName("Speed").SamplingInterval(500))
        .Map(m => m.Torque, b => b.BrowseName("Torque").DataType("Double"))
    .ForType<Pump>()
        .Map(p => p.Motor,  b => b.BrowseName("Motor").ReferenceType("HasComponent"))
    .ForType<Plant>()
        .Map(p => p.Motors,  b => b.BrowseName("Motors").ItemReferenceType("HasComponent"))
        .Map(p => p.Sensors, b => b.BrowseName("Sensors").ItemReferenceType("HasComponent"))
    .ForType<Sensor>()
        .Configure(b => b.NodeClass(OpcUaNodeClass.Variable))
        .Map(s => s.Value, b => b.IsValue())
    .Build('.');
```

Because `Motor` is configured once at the type level, all motors in the graph (direct properties, collection elements, nested references) resolve the same browse names and sampling intervals without repeating configuration.

### DI Registration

The simple typed overloads (`AddOpcUaSubjectServer<T>("opc")`) build the attribute-only composite. To register a fluent mapper, use the configuration overload and set the `Mapper` property. The connector fills the remaining defaults (telemetry, type resolver) from DI when you leave them unset, so you only configure what you need:

```csharp
services.AddOpcUaSubjectServer<Plant>(
    sp => sp.GetRequiredService<Plant>(),
    _ => new OpcUaServerConfiguration
    {
        RootName = "Devices",
        Mapper = new OpcUaCompositeMapper(
            new OpcUaPathProviderMapper(new AttributeBasedPathProvider("opc")),
            new OpcUaAttributeMapper("opc"),
            new OpcUaFluentMapperBuilder<Plant>()
                .ForType<Motor>()
                    .Configure(b => b.TypeDefinition("MotorType"))
                    .Map(m => m.Speed, b => b.SamplingInterval(500))
                .Build('.'))
    });
```

### Combining Fluent and Attributes

Fluent and attribute sources are layered in composite order: attributes first, fluent last (fluent wins on field conflicts). This allows incremental migration: start with attributes and override specific properties or types in code without changing the annotated types.

```csharp
var config = new OpcUaClientConfiguration
{
    Mapper = new OpcUaCompositeMapper(
        new OpcUaPathProviderMapper(new AttributeBasedPathProvider("opc")),
        new OpcUaAttributeMapper(),
        fluentMapper)
};
```

### Inheritance

A `ForType<T>()` registration applies to all types derived from `T` and all types implementing `T` (when `T` is an interface). The most specific registration wins. For example, `ForType<Motor>()` covers `ServoMotor` instances, and `ForType<IMotor>()` covers all concrete implementers, with a concrete-type registration taking precedence over an interface registration.

### Property Builder Setters

All the following setters are available on the property builder passed to `Map(...)` and `Configure(...)`:

`BrowseName`, `DisplayName`, `Description`, `TypeDefinition`, `NodeClass`, `DataType`, `IsValue`, `ReferenceType`, `ItemReferenceType`, `SamplingInterval`, `QueueSize`, `DiscardOldest`, `DataChangeTrigger`, `DeadbandType`, `DeadbandValue`, `ModellingRule`, `EventNotifier`, `AdditionalReference`, `NodeIdentifier`, `NodeNamespaceUri`, `BrowseNamespaceUri`.

`AdditionalReference` adds a non-hierarchical reference (e.g., `HasInterface`) to the node. Parameters:
- `referenceType`: Reference type name (e.g., `"HasInterface"`, `"Organizes"`)
- `referenceTypeNamespace`: Optional namespace URI for the reference type (uses default namespace if null)
- `targetNodeId`: Identifier of the target node
- `targetNamespaceUri`: Optional namespace URI for the target node (uses default namespace if null)
- `isForward`: Direction of the reference (default: `true`)

## Composite Mappers

Multiple mappers can be combined with "last wins" merge semantics using `OpcUaCompositeMapper`. Later mappers override earlier ones:

```csharp
var mapper = new OpcUaCompositeMapper(
    new OpcUaPathProviderMapper(pathProvider),  // Base - fallback names
    new OpcUaAttributeMapper(),                  // Override - attributes
    fluentMapper);                                   // Final - runtime overrides
```

**Merge example (last wins):**
```
PathProvider: { BrowseName: "speed" }
Attribute:    { BrowseName: "Speed", SamplingInterval: 100 }
Fluent:       { SamplingInterval: 50 }

Result: { BrowseName: "Speed", SamplingInterval: 50 }
         ↑ Attribute wins (later)  ↑ Fluent wins (latest)
```

**Note on ReferenceType defaults:** `OpcUaPathProviderMapper` returns `null` for `ReferenceType` on non-attribute properties, allowing later mappers to specify it. `OpcUaAttributeMapper` uses `"HasProperty"` as the default when `[OpcUaReference]` is not specified. This design allows the composite chain to resolve defaults correctly.

> For custom value converters and type resolvers, see [Extensibility](connectors-opcua-client.md#extensibility).

## Standard Reference Types

| Reference Type | Use Case |
|----------------|----------|
| `HasProperty` | Simple characteristics, metadata (default) |
| `HasComponent` | Structural containment, child objects |
| `HasAddIn` | Optional add-in components (Machinery) |
| `Organizes` | Folder-style organization |
| `HasInterface` | Type implements interface |
| `HasEventSource` | Event subscription chains |

## OPC UA Companion Spec Support

### Machinery (OPC 40001)

```csharp
[InterceptorSubject]
[OpcUaNode("Machine",
    TypeDefinition = "MachineType",
    TypeDefinitionNamespace = "http://opcfoundation.org/UA/Machinery/")]
public partial class Machine
{
    [OpcUaReference("HasAddIn")]
    [OpcUaNode("Identification", "http://opcfoundation.org/UA/DI/")]
    public partial MachineIdentification Identification { get; set; }

    [OpcUaReference("HasComponent")]
    [OpcUaNode("MachineryBuildingBlocks")]
    public partial MachineryBuildingBlocks MachineryBuildingBlocks { get; set; }
}
```

### PADIM (OPC 30385)

```csharp
[InterceptorSubject]
[OpcUaNode("AnalogSignalVariable",
    TypeDefinition = "AnalogSignalVariableType",
    TypeDefinitionNamespace = "http://opcfoundation.org/UA/PADIM/",
    NodeClass = OpcUaNodeClass.Variable)]
public partial class AnalogSignalVariable
{
    [OpcUaValue]
    public partial double ActualValue { get; set; }

    public partial Range? EURange { get; set; }
    public partial EUInformation? EngineeringUnits { get; set; }
}
```

## Limitations

The following OPC UA features are not yet supported but can be added in future versions without structural changes to the mapping model:

### Methods

OPC UA Methods (callable operations) are not currently supported. Future implementation could use:

```csharp
// Potential future syntax
[OpcUaMethod]
public partial Task<int> Start(int speed, CancellationToken ct);
```

Methods would map to:
- Method NodeClass with Executable/UserExecutable attributes
- InputArguments and OutputArguments as HasProperty children

### Events

OPC UA Events (notifications) are partially supported:
- `EventNotifier` attribute exists for marking event-producing nodes
- Full event type definitions and `GeneratesEvent` references not yet supported

Future implementation could use:

```csharp
// Potential future syntax
[OpcUaEventType(TypeDefinition = "AlarmEventType")]
public partial class MachineAlarm
{
    public partial string Message { get; set; }
    public partial int Severity { get; set; }
}

[OpcUaNode(EventNotifier = 1)]  // SubscribeToEvents
[OpcUaGeneratesEvent(typeof(MachineAlarm))]
public partial class Machine { ... }
```

### AccessLevel Configuration

`AccessLevel` and `UserAccessLevel` are auto-detected from C# property definitions:
- Read-only properties (`get` only) → `CurrentRead`
- Read-write properties (`get`/`set`) → `CurrentReadOrWrite`

Explicit AccessLevel configuration (e.g., making a writable C# property read-only in OPC UA, or adding `HistoryRead` flag) is not yet supported.

### Security Attributes

The following security-related attributes are not supported:
- `WriteMask` / `UserWriteMask` - Attribute-level write permissions
- `RolePermissions` / `UserRolePermissions` - Role-based access control
- `AccessRestrictions` - Security restrictions

### History

`Historizing` attribute and historical data access require a full history server implementation and are out of scope.

### Views

OPC UA Views (filtered address space subsets) are not supported.

### Fluent InlinePaths Declaration

The `[InlinePaths]` declaration marker is not yet supported in the fluent API. Use the `[InlinePaths]` attribute directly on dictionary properties when transparent path resolution is needed. All other OPC UA node and monitoring settings are available via `OpcUaFluentMapperBuilder<TRoot>`.

## Future Extensibility

The following features can be added in future versions **without breaking changes** to the current mapping model:

### Type Definition Creation

Creating new OPC UA ObjectTypes/VariableTypes in the address space (not just referencing existing ones). Could add:
- `IOpcUaTypeMapper` interface for type-level mapping
- `[OpcUaObjectType]` / `[OpcUaVariableType]` attributes for explicit type definitions
- HasSubtype hierarchy generation from C# class inheritance

### Structural Synchronization

Dynamic address space sync when C# objects attach/detach at runtime (see PR #121):
- Uses `IPropertyMapper<OpcUaPropertyMapping>.TryGetMapping()` to get node configuration
- Creates/removes nodes dynamically via `CustomNodeManager`
- Fires ModelChangeEvents for client notification

### Interface Support

Explicit HasInterface references:
```csharp
// Potential future syntax
[OpcUaInterface("IVendorNameplateType", "http://opcfoundation.org/UA/DI/")]
public partial class MachineIdentification { ... }
```

