# OPC UA Mapping Guide

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
    // Constructor - BrowseName is required
    public OpcUaNodeAttribute(string browseName, string? browseNamespaceUri);

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
[OpcUaNode(TypeDefinition = "MachineIdentificationType",
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
[OpcUaNode(TypeDefinition = "MachineType")]
public partial class Machine
{
    // Child object - use HasComponent for structural containment
    [OpcUaReference("HasComponent")]
    public partial Motor Motor1 { get; set; }

    // Simple property - HasProperty is default
    public partial string? SerialNumber { get; set; }
}

[InterceptorSubject]
[OpcUaNode(TypeDefinition = "MotorType")]
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
    [OpcUaNode(BrowseName = "Identification",
               BrowseNamespaceUri = "http://opcfoundation.org/UA/DI/")]
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
    [OpcUaNode(TypeDefinition = "FolderType")]
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
[OpcUaNode(
    TypeDefinition = "AnalogSignalVariableType",
    TypeDefinitionNamespace = "http://opcfoundation.org/UA/PADIM/",
    NodeClass = OpcUaNodeClass.Variable)]  // Force VariableNode
public partial class AnalogSignalVariable
{
    // The primary value of this VariableNode
    [OpcUaValue]
    public partial double ActualValue { get; set; }

    // Child properties (standard OPC UA metadata)
    public partial Range? EURange { get; set; }
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

For adding standard OPC UA metadata (EURange, EngineeringUnits) to simple properties:

```csharp
[InterceptorSubject]
public partial class TemperatureSensor
{
    [OpcUaNode(TypeDefinition = "AnalogItemType")]
    public partial double Value { get; set; }

    // Property attributes become HasProperty children of Value
    [PropertyAttribute(nameof(Value), "EURange")]
    public partial Range? Value_EURange { get; set; }

    [PropertyAttribute(nameof(Value), "EngineeringUnits")]
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
    [OpcUaNode(NodeIdentifier = "Identification")]  // Explicit NodeId
    public partial Identification Identification { get; set; }
}

public partial class MachineryBuildingBlocks
{
    [OpcUaReference("HasAddIn")]
    // No NodeIdentifier - references existing node
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
    [OpcUaNode(NodeIdentifier = "MainMotor", BrowseName = "MainMotor")]
    public partial Motor Motor1 { get; set; }

    [OpcUaReference("HasComponent")]
    [OpcUaNode(NodeIdentifier = "AuxMotor", BrowseName = "AuxMotor")]
    public partial Motor Motor2 { get; set; }
}
```

## Fluent Configuration

For runtime configuration without attributes.

**Important:** Fluent configuration is **property-level only**. It configures specific property instances (e.g., `Motor1.Speed` vs `Motor2.Speed`), not type-level defaults.

For **class-level configuration** (TypeDefinition, NodeClass that applies to all instances of a type), use:
- `[OpcUaNode]` attribute on the class, OR
- Composite mapper with `AttributeOpcUaNodeMapper` to pick up class attributes

```csharp
// Class-level: Use attributes (applies to ALL Motor instances)
[OpcUaNode(TypeDefinition = "MotorType")]
public partial class Motor { ... }

// Instance-level: Use fluent (different config per instance)
var mapper = new FluentOpcUaNodeMapper<Machine>()
    .Map(m => m.Motor1, m => m.BrowseName("MainMotor").SamplingInterval(50))
    .Map(m => m.Motor2, m => m.BrowseName("AuxMotor").SamplingInterval(100));

// Combine both: Fluent overrides, attributes provide defaults
var config = new OpcUaClientConfiguration
{
    NodeMapper = new CompositeNodeMapper(
        mapper,                          // Instance overrides
        new AttributeOpcUaNodeMapper())  // Class-level defaults
};
```

### Basic Fluent Example

```csharp
var mapper = new FluentOpcUaNodeMapper<Machine>()
    .Map(m => m.Motor1, motor => motor
        .BrowseName("MainDriveMotor")
        .ReferenceType("HasComponent")
        .Map(m => m.Speed, speed => speed
            .BrowseName("Speed")
            .SamplingInterval(50))
        .Map(m => m.Temperature, temp => temp
            .SamplingInterval(500)))
    .Map(m => m.Motor2, motor => motor
        .BrowseName("AuxMotor")
        .ReferenceType("HasComponent")
        .Map(m => m.Speed, speed => speed
            .SamplingInterval(100)));

var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://localhost:4840",
    NodeMapper = new CompositeNodeMapper(
        mapper,                              // Fluent config takes priority
        new AttributeOpcUaNodeMapper(),      // Then attributes
        new PathProviderOpcUaNodeMapper(DefaultPathProvider.Instance))  // Fallback
};
```

### Reusable Configuration

```csharp
public static class MotorMappingExtensions
{
    public static IPropertyBuilder<Motor> ConfigureMotor<TBuilder>(
        this IPropertyBuilder<Motor> builder)
    {
        return builder
            .ReferenceType("HasComponent")
            .Map(m => m.Speed, s => s.SamplingInterval(50))
            .Map(m => m.Temperature, t => t.SamplingInterval(500));
    }
}

// Usage
var mapper = new FluentOpcUaNodeMapper<Machine>()
    .Map(m => m.Motor1, motor => motor
        .BrowseName("MainMotor")
        .ConfigureMotor())
    .Map(m => m.Motor2, motor => motor
        .BrowseName("AuxMotor")
        .ConfigureMotor());
```

## Composite Mappers

Multiple mappers can be combined with "last wins" merge semantics. Later mappers override earlier ones:

```csharp
var mapper = new CompositeNodeMapper(
    new PathProviderOpcUaNodeMapper(pathProvider),  // Base - fallback names
    new AttributeOpcUaNodeMapper(),                  // Override - attributes
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
[OpcUaNode(TypeDefinition = "MachineType",
           TypeDefinitionNamespace = "http://opcfoundation.org/UA/Machinery/")]
public partial class Machine
{
    [OpcUaReference("HasAddIn")]
    [OpcUaNode(BrowseName = "Identification",
               BrowseNamespaceUri = "http://opcfoundation.org/UA/DI/")]
    public partial MachineIdentification Identification { get; set; }

    [OpcUaReference("HasComponent")]
    [OpcUaNode(BrowseName = "MachineryBuildingBlocks")]
    public partial MachineryBuildingBlocks MachineryBuildingBlocks { get; set; }
}
```

### PADIM (OPC 30385)

```csharp
[InterceptorSubject]
[OpcUaNode(
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

### Security Attributes

The following security-related attributes are not supported:
- `WriteMask` / `UserWriteMask` - Attribute-level write permissions
- `RolePermissions` / `UserRolePermissions` - Role-based access control
- `AccessRestrictions` - Security restrictions

### History

`Historizing` attribute and historical data access require a full history server implementation and are out of scope.

### Views

OPC UA Views (filtered address space subsets) are not supported.

### Fluent Class-Level Configuration

The fluent mapper (`FluentOpcUaNodeMapper<T>`) only supports property-level configuration. For class-level defaults (TypeDefinition, NodeClass), use `[OpcUaNode]` attributes on classes combined with `CompositeNodeMapper`.

## Future Extensibility

The following features can be added in future versions **without breaking changes** to the current mapping model:

### Type Definition Creation

Creating new OPC UA ObjectTypes/VariableTypes in the address space (not just referencing existing ones). Could add:
- `IOpcUaTypeMapper` interface for type-level mapping
- `[OpcUaObjectType]` / `[OpcUaVariableType]` attributes for explicit type definitions
- HasSubtype hierarchy generation from C# class inheritance

### Structural Synchronization

Dynamic address space sync when C# objects attach/detach at runtime (see PR #121):
- Uses `IOpcUaNodeMapper.TryGetConfiguration()` to get node configuration
- Creates/removes nodes dynamically via `CustomNodeManager`
- Fires ModelChangeEvents for client notification

### Interface Support

Explicit HasInterface references:
```csharp
// Potential future syntax
[OpcUaInterface("IVendorNameplateType", "http://opcfoundation.org/UA/DI/")]
public partial class MachineIdentification { ... }
```

