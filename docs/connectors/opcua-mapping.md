# OPC UA Mapping Guide

This guide explains how to map C# object models to OPC UA address spaces using Namotion.Interceptor's OPC UA integration.

## Introduction

### Server vs Client

The same mapping attributes work for both scenarios:

- **Server**: Creates OPC UA nodes in the address space based on your C# model
- **Client**: Discovers OPC UA nodes and maps them to your C# properties

### Configuration Resolution

Configuration is resolved by merging multiple sources. Higher priority sources override lower ones:

| Priority | Source | Description |
|----------|--------|-------------|
| 1 (highest) | Fluent configuration | Runtime overrides via `FluentOpcUaNodeMapper` |
| 2 | Property-level `[OpcUaNode]` | Instance-specific settings |
| 3 | Class-level `[OpcUaNode]` | Type defaults (TypeDefinition, NodeClass) |
| 4 (fallback) | Property name | BrowseName defaults to C# property name |

**Example:**

```
Class attribute:    { TypeDefinition: "MotorType" }
Property attribute: { BrowseName: "MainMotor", SamplingInterval: 100 }
Fluent config:      { SamplingInterval: 50 }

Result: { TypeDefinition: "MotorType", BrowseName: "MainMotor", SamplingInterval: 50 }
```

Each field is resolved independently - a source only overrides fields it explicitly sets.

### Key Concept: BrowseName Lives on the Node

In OPC UA, the **BrowseName is an attribute of the Node**, not the Reference. This differs from typical object-oriented thinking:

| Model | Name Location |
|-------|---------------|
| Typical OO | Property name (on the reference) |
| OPC UA | BrowseName (on the node itself) |

When two references point to the same node, they both show the same BrowseName. This matters for [Shared References](#shared-references).

---

## Attributes Quick Reference

| Attribute | Target | Purpose |
|-----------|--------|---------|
| `[OpcUaNode]` | Class or Property | BrowseName, TypeDefinition, NodeClass, monitoring settings |
| `[OpcUaReference]` | Property | Reference type (HasComponent, HasProperty), collection structure |
| `[OpcUaValue]` | Property | Marks the primary value for Variable nodes |
| `[PropertyAttribute]` | Property | Attaches property as child of another variable |

---

## Mapping Scenarios

### Simple Properties

Primitive C# properties map to OPC UA Variable nodes.

```csharp
[InterceptorSubject]
public partial class Machine
{
    public partial double Speed { get; set; }
    public partial string? Status { get; set; }
    public partial bool IsRunning { get; set; }
}
```

```
Machine
├── Speed (HasProperty) = 100.0
├── Status (HasProperty) = "Running"
└── IsRunning (HasProperty) = true
```

The BrowseName defaults to the C# property name. Override it with `[OpcUaNode]`:

```csharp
[OpcUaNode("CurrentSpeed")]
public partial double Speed { get; set; }
```

**Primitive Arrays**

Arrays of primitives (e.g., `double[]`, `string[]`) map to a single Variable node with an array value - they do NOT create multiple child nodes:

```csharp
[InterceptorSubject]
public partial class Machine
{
    public partial double[] Temperatures { get; set; }
}
```

```
Machine
└── Temperatures (HasProperty) = [25.5, 26.0, 24.8]  // Single node with array value
```

This is different from [Collections](#collections---container-structure) of objects, which create multiple child nodes.

**Monitoring Settings (Client)**

Configure how the OPC UA client monitors value changes:

```csharp
[OpcUaNode(SamplingInterval = 100, DeadbandType = DeadbandType.Absolute, DeadbandValue = 0.5)]
public partial double Temperature { get; set; }
```

| Setting | Description |
|---------|-------------|
| `SamplingInterval` | Milliseconds between samples; 0 = exception-based (immediate) |
| `QueueSize` | Buffer size for monitored items |
| `DeadbandType` | None, Absolute, or Percent |
| `DeadbandValue` | Threshold for deadband filtering |
| `DataChangeTrigger` | Status, StatusValue, or StatusValueTimestamp |

**Additional Settings**

| Setting | Description |
|---------|-------------|
| `TypeDefinition` | OPC UA type (e.g., "AnalogItemType") |
| `DisplayName` | Localized display name |
| `Description` | Human-readable description |
| `DataType` | Override C# type mapping (e.g., "Double", "NodeId") |
| `ModellingRule` | Server: Mandatory, Optional, etc. |

---

### Property Attributes

Attach metadata properties as children of another Variable node using `[PropertyAttribute]`.

Use this when you need OPC UA standard properties (like `EURange`, `EngineeringUnits`) on a simple property without creating a separate class.

```csharp
[InterceptorSubject]
public partial class Machine
{
    [OpcUaNode("Temperature", TypeDefinition = "AnalogItemType")]
    public partial double Temperature { get; set; }

    [PropertyAttribute(nameof(Temperature), "EngineeringUnits")]
    public partial string? Temperature_Unit { get; set; }

    [PropertyAttribute(nameof(Temperature), "EURange")]
    public partial Range? Temperature_Range { get; set; }
}
```

```
Machine
└── Temperature (HasProperty, AnalogItemType) = 25.5
    ├── EngineeringUnits (HasProperty) = "°C"
    └── EURange (HasProperty) = { Low: -40, High: 85 }
```

The C# property name (`Temperature_Unit`) is just a convention - the OPC UA BrowseName comes from the `[PropertyAttribute]` parameter.

**When to use PropertyAttribute vs Value Classes:**

- **PropertyAttribute**: Adding a few metadata properties to a simple value
- **Value Classes**: Reusable pattern with multiple properties (see [Value Classes](#value-classes))

---

### Object References

Object properties create OPC UA Object nodes with a reference from the parent.

```csharp
[InterceptorSubject]
public partial class Machine
{
    [OpcUaReference("HasComponent")]
    public partial Motor Motor { get; set; }
}

[InterceptorSubject]
public partial class Motor
{
    public partial double Speed { get; set; }
    public partial double Temperature { get; set; }
}
```

```
Machine
└── Motor (HasComponent) ──► Object "Motor"
    ├── Speed (HasProperty)
    └── Temperature (HasProperty)
```

**Custom BrowseName**

Override the BrowseName with `[OpcUaNode]` on the property:

```csharp
[OpcUaReference("HasComponent")]
[OpcUaNode("MainMotor")]
public partial Motor Motor1 { get; set; }

[OpcUaReference("HasComponent")]
[OpcUaNode("AuxiliaryMotor")]
public partial Motor Motor2 { get; set; }
```

```
Machine
├── MainMotor (HasComponent)
└── AuxiliaryMotor (HasComponent)
```

**Nullable References**

Nullable properties (`Motor?`) work the same way - if the value is `null`, no OPC UA node is created for that property.

```csharp
[OpcUaReference("HasComponent")]
public partial Motor? OptionalMotor { get; set; }
```

**Reference Types**

The `[OpcUaReference]` attribute specifies how the parent references the child node:

| Reference Type | Use Case |
|----------------|----------|
| `HasProperty` | Simple characteristics, metadata (default for primitive properties) |
| `HasComponent` | Structural containment, child objects |
| `HasAddIn` | Optional add-in components (Machinery spec) |
| `Organizes` | Folder-style organization |

```csharp
[OpcUaReference("HasComponent")]
public partial Motor Motor { get; set; }

[OpcUaReference("HasAddIn")]
public partial Identification? Identification { get; set; }

[OpcUaReference("Organizes")]
public partial Folder Components { get; set; }
```

**Custom Reference Types**

Use custom or companion-spec reference types by specifying the namespace:

```csharp
[OpcUaReference("MyCustomReference", ReferenceTypeNamespace = "http://example.com/")]
public partial Device Device { get; set; }
```

---

### Collections - Container Structure

Arrays and lists of objects create multiple child nodes under a container folder (default behavior).

```csharp
[InterceptorSubject]
public partial class Machine
{
    [OpcUaReference("HasComponent")]
    public partial Motor[] Motors { get; set; }
}
```

```csharp
machine.Motors = [new Motor(), new Motor(), new Motor()];
```

```
Machine
└── Motors (HasComponent, FolderType)          // Container node
    ├── Motors[0] (HasComponent) ──► Object
    ├── Motors[1] (HasComponent) ──► Object
    └── Motors[2] (HasComponent) ──► Object
```

**BrowseName for Items**: `PropertyName[index]` (e.g., `Motors[0]`, `Motors[1]`)

**Customize Item Reference Type**

Use `ItemReferenceType` to specify a different reference type for the items:

```csharp
[OpcUaReference("Organizes", ItemReferenceType = "HasComponent")]
[OpcUaNode("Motors", TypeDefinition = "FolderType")]
public partial Motor[] Motors { get; set; }
```

```
Machine
└── Motors (Organizes, FolderType)
    ├── Motors[0] (HasComponent)
    ├── Motors[1] (HasComponent)
    └── Motors[2] (HasComponent)
```

**Limitation**: The same instance cannot appear at multiple indices - it would create conflicting BrowseNames.

---

### Collections - Flat Structure

Place collection items directly under the parent node (no container folder).

```csharp
[InterceptorSubject]
public partial class Machine
{
    [OpcUaReference("HasComponent", CollectionStructure = CollectionNodeStructure.Flat)]
    public partial Motor[] Motors { get; set; }
}
```

```
Machine
├── Motors[0] (HasComponent) ──► Object
├── Motors[1] (HasComponent) ──► Object
└── Motors[2] (HasComponent) ──► Object
```

Use flat structure when:
- The container folder adds unnecessary hierarchy
- You want items as direct siblings of other properties

---

### Dictionaries

Dictionaries create child nodes using the dictionary key as the BrowseName.

```csharp
[InterceptorSubject]
public partial class Machine
{
    [OpcUaReference("Organizes", ItemReferenceType = "HasComponent")]
    [OpcUaNode("Motors", TypeDefinition = "FolderType")]
    public partial Dictionary<string, Motor> Motors { get; set; }
}
```

```csharp
machine.Motors["Pump1"] = new Motor();
machine.Motors["Pump2"] = new Motor();
machine.Motors["MainDrive"] = new Motor();
```

```
Machine
└── Motors (Organizes, FolderType)
    ├── Pump1 (HasComponent) ──► Object
    ├── Pump2 (HasComponent) ──► Object
    └── MainDrive (HasComponent) ──► Object
```

**Key Advantage**: Dictionary keys become meaningful BrowseNames instead of numeric indices.

**Limitation**: The same instance cannot appear under multiple keys - it would create conflicting BrowseNames.

---

### Value Classes

Classes that map to OPC UA **Variable nodes** with both a value and child properties.

Use this pattern when modeling OPC UA VariableTypes like `AnalogSignalVariableType` where the node has a primary value AND metadata properties.

```csharp
[InterceptorSubject]
[OpcUaNode(NodeClass = OpcUaNodeClass.Variable)]
public partial class AnalogSignal
{
    [OpcUaValue]
    public partial double Value { get; set; }

    public partial string? EngineeringUnits { get; set; }
    public partial double? MinValue { get; set; }
    public partial double? MaxValue { get; set; }
}

[InterceptorSubject]
public partial class Machine
{
    [OpcUaReference("HasComponent")]
    public partial AnalogSignal Temperature { get; set; }
}
```

```
Machine
└── Temperature (HasComponent) ──► VariableNode "Temperature"
    ├── Value = 25.5                    // The Variable's value attribute
    ├── EngineeringUnits (HasProperty) = "°C"
    ├── MinValue (HasProperty) = -40.0
    └── MaxValue (HasProperty) = 85.0
```

**Key points:**

- `[OpcUaValue]` marks which property provides the Variable node's Value attribute
- `NodeClass = OpcUaNodeClass.Variable` on the class makes it a VariableNode instead of ObjectNode
- Other properties become `HasProperty` children of the VariableNode

**When to use Value Classes vs PropertyAttribute:**

- **Value Classes**: Reusable pattern, multiple instances with same structure
- **PropertyAttribute**: One-off metadata on a single property

---

### Shared References

When the same C# object instance is referenced from multiple properties, only **one OPC UA node** is created with multiple references pointing to it.

**BrowseName Resolution for Shared Instances**

The BrowseName is determined by the **first property** that references the instance during loading. You have two options:

| Approach | Behavior | Predictability |
|----------|----------|----------------|
| Class-level `[OpcUaNode]` | All references use the class-defined name | Predictable (recommended) |
| Property-level `[OpcUaNode]` | First property encountered wins | Depends on load order |

**Recommended: Class-level BrowseName**

```csharp
[InterceptorSubject]
[OpcUaNode("Identification")]  // BrowseName defined on class - predictable
public partial class MachineIdentification
{
    public partial string? Manufacturer { get; set; }
    public partial string? SerialNumber { get; set; }
}

[InterceptorSubject]
public partial class Machine
{
    [OpcUaReference("HasAddIn")]
    public partial MachineIdentification Identification { get; set; }

    [OpcUaReference("HasComponent")]
    public partial MachineryBuildingBlocks MachineryBuildingBlocks { get; set; }
}

[InterceptorSubject]
public partial class MachineryBuildingBlocks
{
    [OpcUaReference("HasAddIn")]
    public partial MachineIdentification? Identification { get; set; }
}
```

**Alternative: Property-level BrowseName (first wins)**

```csharp
[InterceptorSubject]
public partial class MachineIdentification
{
    public partial string? Manufacturer { get; set; }
}

[InterceptorSubject]
public partial class Machine
{
    [OpcUaReference("HasAddIn")]
    [OpcUaNode("Identification")]  // If loaded first, this name wins
    public partial MachineIdentification Identification { get; set; }

    [OpcUaReference("HasComponent")]
    public partial MachineryBuildingBlocks MachineryBuildingBlocks { get; set; }
}

[InterceptorSubject]
public partial class MachineryBuildingBlocks
{
    [OpcUaReference("HasAddIn")]
    [OpcUaNode("Identification")]  // Ignored if Machine.Identification loaded first (if redefined, ensure it uses same BrowseName)
    public partial MachineIdentification? Identification { get; set; }
}
```

**Usage:**

```csharp
var identification = new MachineIdentification { Manufacturer = "Acme" };
machine.Identification = identification;
machine.MachineryBuildingBlocks.Identification = identification;  // Same instance!
```

```
Machine
├── Identification (HasAddIn) ──────────────────┐
└── MachineryBuildingBlocks (HasComponent)      │
    └── Identification (HasAddIn) ──────────────┴──► Single Node "Identification"
                                                     └── Manufacturer (HasProperty)
```

**Why class-level is recommended**: It ensures consistent BrowseName regardless of which property is processed first. With property-level only, the name depends on load order, which may vary.

**Not supported for collections/dictionaries**: The same instance cannot appear at multiple indices or keys because each position requires a unique BrowseName.

---

### Additional References

Add non-hierarchical references (e.g., `HasInterface`, `HasTypeDefinition`) that don't affect the containment hierarchy.

Use fluent configuration to add additional references:

```csharp
var mapper = new FluentOpcUaNodeMapper<Machine>()
    .Map(m => m.Motor, motor => motor
        .ReferenceType("HasComponent")
        .AdditionalReference(
            referenceType: "HasInterface",
            targetNodeId: "IVendorNameplateType",
            targetNamespaceUri: "http://opcfoundation.org/UA/DI/",
            isForward: true));
```

```
Machine
└── Motor (HasComponent) ──► Object "Motor"
         (HasInterface) ──► IVendorNameplateType
```

**Common use cases:**

- `HasInterface` - Declare that a node implements an interface type
- Custom references from companion specifications

---

## Fluent Configuration

Configure mapping at runtime without attributes using `FluentOpcUaNodeMapper`.

```csharp
var mapper = new FluentOpcUaNodeMapper<Machine>()
    .Map(m => m.Motor1, motor => motor
        .BrowseName("MainDriveMotor")
        .ReferenceType("HasComponent")
        .Map(m => m.Speed, speed => speed
            .SamplingInterval(50)))
    .Map(m => m.Motor2, motor => motor
        .BrowseName("AuxMotor")
        .ReferenceType("HasComponent"));
```

**Nested Configuration**

Configure child properties using nested `.Map()` calls:

```csharp
.Map(m => m.Motor, motor => motor
    .BrowseName("Motor")
    .Map(m => m.Speed, speed => speed
        .SamplingInterval(100)
        .DeadbandType(DeadbandType.Absolute)
        .DeadbandValue(0.5)))
```

**Reusable Configuration**

Create extension methods for common patterns:

```csharp
public static class MotorMappingExtensions
{
    public static IPropertyBuilder<Motor> ConfigureMotor(this IPropertyBuilder<Motor> builder)
    {
        return builder
            .ReferenceType("HasComponent")
            .Map(m => m.Speed, s => s.SamplingInterval(50))
            .Map(m => m.Temperature, t => t.SamplingInterval(500));
    }
}

// Usage
var mapper = new FluentOpcUaNodeMapper<Machine>()
    .Map(m => m.Motor1, motor => motor.BrowseName("MainMotor").ConfigureMotor())
    .Map(m => m.Motor2, motor => motor.BrowseName("AuxMotor").ConfigureMotor());
```

**CompositeNodeMapper**

Combine multiple mappers with "last wins" semantics:

```csharp
var config = new OpcUaClientConfiguration
{
    ServerUrl = "opc.tcp://localhost:4840",
    NodeMapper = new CompositeNodeMapper(
        new PathProviderOpcUaNodeMapper(DefaultPathProvider.Instance),  // Fallback
        new AttributeOpcUaNodeMapper(),                                  // Attributes
        fluentMapper)                                                    // Highest priority
};
```

The last mapper in the list has highest priority for any field it sets.

---

## Troubleshooting

### BrowseName Not What Expected

**Check the resolution order** (highest priority first):

1. Fluent configuration
2. Property-level `[OpcUaNode]`
3. Class-level `[OpcUaNode]`
4. Property name

**Common causes:**

- Property-level attribute overrides class-level
- Fluent config overrides all attributes
- Forgot to add attribute entirely

### Collection Items Have Wrong Names

Collection items use the pattern `PropertyName[index]` (e.g., `Motors[0]`).

**Class-level BrowseName is ignored** for collection items by design. The class-level `[OpcUaNode]` sets type defaults but not the item BrowseName.

For custom item names, use a Dictionary instead of an array.

### Node Not Appearing

**Check if property is included:**

- PathProvider might be filtering it out
- Property type might not be recognized as a subject

**For object references:**

- Ensure the property type has `[InterceptorSubject]`
- Add `[OpcUaReference]` to specify reference type

### Shared Instance Creates Multiple Nodes

For predictable shared references, add **class-level** `[OpcUaNode]`:

```csharp
[InterceptorSubject]
[OpcUaNode("SharedName")]  // Recommended for predictable sharing
public partial class SharedClass { }
```

Without this, the BrowseName depends on which property is loaded first. If using property-level `[OpcUaNode]`, ensure all properties referencing the same instance use the same BrowseName.

---

## Complete Example

A complete example implementing OPC UA Machinery (OPC 40001) and PADIM (OPC 30385) companion specifications.

```csharp
// Machinery-compliant Machine with Identification pattern
[InterceptorSubject]
[OpcUaNode("Machine",
    TypeDefinition = "MachineType",
    TypeDefinitionNamespace = "http://opcfoundation.org/UA/Machinery/")]
public partial class Machine
{
    [OpcUaReference("HasAddIn")]
    public partial MachineIdentification Identification { get; set; }

    [OpcUaReference("HasComponent")]
    public partial MachineryBuildingBlocks MachineryBuildingBlocks { get; set; }

    [OpcUaReference("HasComponent")]
    public partial Dictionary<string, AnalogSignal> ProcessValues { get; set; }
}

// Shared Identification - same instance appears in multiple places
[InterceptorSubject]
[OpcUaNode("Identification",
    TypeDefinition = "MachineIdentificationType",
    TypeDefinitionNamespace = "http://opcfoundation.org/UA/Machinery/")]
public partial class MachineIdentification
{
    public partial string? Manufacturer { get; set; }
    public partial string? SerialNumber { get; set; }
    public partial string? ProductInstanceUri { get; set; }
}

[InterceptorSubject]
[OpcUaNode("MachineryBuildingBlocks")]
public partial class MachineryBuildingBlocks
{
    [OpcUaReference("HasAddIn")]
    public partial MachineIdentification? Identification { get; set; }
}

// PADIM-compliant AnalogSignal as a VariableNode
[InterceptorSubject]
[OpcUaNode(
    TypeDefinition = "AnalogSignalVariableType",
    TypeDefinitionNamespace = "http://opcfoundation.org/UA/PADIM/",
    NodeClass = OpcUaNodeClass.Variable)]
public partial class AnalogSignal
{
    [OpcUaValue]
    public partial double ActualValue { get; set; }

    public partial Range? EURange { get; set; }
    public partial EUInformation? EngineeringUnits { get; set; }
}
```

**Usage:**

```csharp
var identification = new MachineIdentification(context)
{
    Manufacturer = "Acme Corp",
    SerialNumber = "SN-12345"
};

var machine = new Machine(context)
{
    Identification = identification,
    MachineryBuildingBlocks = new MachineryBuildingBlocks(context)
    {
        Identification = identification  // Same instance - shared reference
    },
    ProcessValues = new Dictionary<string, AnalogSignal>
    {
        ["Temperature"] = new AnalogSignal(context) { ActualValue = 25.5 },
        ["Pressure"] = new AnalogSignal(context) { ActualValue = 101.3 }
    }
};
```

**Resulting OPC UA Structure:**

```
Machine (MachineType)
├── Identification (HasAddIn) ─────────────────┐
├── MachineryBuildingBlocks (HasComponent)     │
│   └── Identification (HasAddIn) ─────────────┴──► MachineIdentificationType
│                                                   ├── Manufacturer = "Acme Corp"
│                                                   ├── SerialNumber = "SN-12345"
│                                                   └── ProductInstanceUri
└── ProcessValues (HasComponent, FolderType)
    ├── Temperature (HasComponent) ──► AnalogSignalVariableType
    │   ├── ActualValue = 25.5
    │   ├── EURange
    │   └── EngineeringUnits
    └── Pressure (HasComponent) ──► AnalogSignalVariableType
        ├── ActualValue = 101.3
        ├── EURange
        └── EngineeringUnits
```

---

## Scope and Limitations

This mapping system is designed for **instance mapping** - creating Object and Variable nodes from C# object models.

### Supported

- Object Nodes from `[InterceptorSubject]` classes
- Variable Nodes from properties or `[OpcUaValue]` classes
- References: HasProperty, HasComponent, HasAddIn, Organizes, custom types
- Collections: Arrays/Lists with Container or Flat structure
- Dictionaries: String-keyed with container structure
- Sharing: Same instance referenced from multiple properties

### Not Supported

| Feature | Description |
|---------|-------------|
| Type Definitions | Creating new ObjectTypes, VariableTypes. Use built-in types or load from NodeSet XML. |
| Methods | OPC UA callable operations |
| Events | Full event type definitions |
| AccessLevel Override | Auto-detected from C# property (read-only vs read-write) |
| History | Historizing attribute and historical data access |
| Collection/Dictionary Sharing | Same instance at multiple indices/keys |
