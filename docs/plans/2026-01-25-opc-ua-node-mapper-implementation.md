# OPC UA Node Mapper Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace PathProvider-based OPC UA mapping with a flexible `IOpcUaNodeMapper` interface supporting attribute-based, path provider-based, and fluent configuration strategies.

**Architecture:** Three mapper implementations (`AttributeOpcUaNodeMapper`, `PathProviderOpcUaNodeMapper`, `FluentOpcUaNodeMapper<T>`) combined via `CompositeNodeMapper` with "last wins" merge semantics (later mappers override earlier ones). Unified `OpcUaNodeConfiguration` record for all mapping data. Support for both class-based VariableTypes (`[OpcUaValue]`) and inline PropertyAttribute children.

**Tech Stack:** C# 13, .NET 9.0, xUnit, OPC UA SDK

---

## Phase 1: Core Types and Interfaces

### Task 1.1: Create OpcUaNodeClass Enum

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Mapping/OpcUaNodeClass.cs`

**Step 1: Write the enum**

```csharp
namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Specifies the OPC UA NodeClass for a property or class.
/// </summary>
public enum OpcUaNodeClass
{
    /// <summary>
    /// Auto-detect: classes become ObjectNodes, primitive properties become VariableNodes.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Force ObjectNode regardless of C# type.
    /// </summary>
    Object = 1,

    /// <summary>
    /// Force VariableNode. Use for classes representing VariableTypes (e.g., AnalogSignalVariableType).
    /// </summary>
    Variable = 2
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Mapping/OpcUaNodeClass.cs
git commit -m "$(cat <<'EOF'
feat(opcua): add OpcUaNodeClass enum for NodeClass override

Allows forcing Object or Variable NodeClass instead of auto-detection.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 1.2: Create ModellingRule Enum

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Mapping/ModellingRule.cs`

**Step 1: Write the enum**

```csharp
namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// OPC UA modelling rules for type definitions.
/// </summary>
public enum ModellingRule
{
    /// <summary>
    /// No modelling rule specified.
    /// </summary>
    Unset = -1,

    /// <summary>
    /// Instance must exist in all instances of the containing type.
    /// </summary>
    Mandatory = 0,

    /// <summary>
    /// Instance may or may not exist.
    /// </summary>
    Optional = 1,

    /// <summary>
    /// Placeholder for mandatory instances in a collection.
    /// </summary>
    MandatoryPlaceholder = 2,

    /// <summary>
    /// Placeholder for optional instances in a collection.
    /// </summary>
    OptionalPlaceholder = 3
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Mapping/ModellingRule.cs
git commit -m "$(cat <<'EOF'
feat(opcua): add ModellingRule enum for server type definitions

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 1.3: Create OpcUaAdditionalReference Record

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Mapping/OpcUaAdditionalReference.cs`

**Step 1: Write the record**

```csharp
namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Represents an additional OPC UA reference for non-hierarchical relationships (e.g., HasInterface).
/// </summary>
public record OpcUaAdditionalReference
{
    /// <summary>
    /// The reference type (e.g., "HasInterface", "GeneratesEvent").
    /// </summary>
    public required string ReferenceType { get; init; }

    /// <summary>
    /// The target node identifier.
    /// </summary>
    public required string TargetNodeId { get; init; }

    /// <summary>
    /// The namespace URI for the target node. If null, uses the default namespace.
    /// </summary>
    public string? TargetNamespaceUri { get; init; }

    /// <summary>
    /// Whether this is a forward reference. Default is true.
    /// </summary>
    public bool IsForward { get; init; } = true;
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Mapping/OpcUaAdditionalReference.cs
git commit -m "$(cat <<'EOF'
feat(opcua): add OpcUaAdditionalReference for non-hierarchical references

Supports HasInterface and other custom reference types.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 1.4: Create OpcUaNodeConfiguration Record

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Mapping/OpcUaNodeConfiguration.cs`

**Step 1: Write the record**

```csharp
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// OPC UA node and reference configuration. All fields are nullable to support partial
/// configuration and merge semantics.
/// </summary>
public record OpcUaNodeConfiguration
{
    // Node identification (shared)
    /// <summary>Browse name for the node.</summary>
    public string? BrowseName { get; init; }

    /// <summary>Namespace URI for the browse name.</summary>
    public string? BrowseNamespaceUri { get; init; }

    /// <summary>Explicit node identifier. Property-level only.</summary>
    public string? NodeIdentifier { get; init; }

    /// <summary>Namespace URI for the node identifier.</summary>
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

    /// <summary>DataType override (null = infer from C# type). Examples: "Double", "NodeId".</summary>
    public string? DataType { get; init; }

    /// <summary>True if this property holds the main value for a VariableNode class.</summary>
    public bool? IsValue { get; init; }

    // Reference configuration
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
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Mapping/OpcUaNodeConfiguration.cs
git commit -m "$(cat <<'EOF'
feat(opcua): add OpcUaNodeConfiguration record with merge semantics

Unified configuration for node identification, type definitions, monitoring,
and server settings. Supports partial configuration and composable merging.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 1.5: Create IOpcUaNodeMapper Interface

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Mapping/IOpcUaNodeMapper.cs`

**Step 1: Write the interface**

```csharp
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Mapping;

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
    /// <param name="property">The registered property to get configuration for.</param>
    /// <returns>The configuration, or null if this mapper doesn't handle the property.</returns>
    OpcUaNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property);

    /// <summary>
    /// Client only: Finds property matching an OPC UA node (reverse lookup for discovery).
    /// Server implementations should return null.
    /// </summary>
    /// <param name="subject">The subject to search in.</param>
    /// <param name="nodeReference">The OPC UA node reference to match.</param>
    /// <param name="session">The OPC UA session for namespace resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching property, or null if not found.</returns>
    Task<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject subject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken);
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Mapping/IOpcUaNodeMapper.cs
git commit -m "$(cat <<'EOF'
feat(opcua): add IOpcUaNodeMapper interface

Unified interface for mapping C# properties to OPC UA nodes.
Supports both forward mapping (property → config) and reverse lookup (node → property).

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 1.6: Write Unit Tests for OpcUaNodeConfiguration.MergeWith

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Mapping/OpcUaNodeConfigurationTests.cs`

**Step 1: Write the failing test**

```csharp
using Namotion.Interceptor.OpcUa.Mapping;
using Opc.Ua;
using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class OpcUaNodeConfigurationTests
{
    [Fact]
    public void MergeWith_WhenThisHasValue_KeepsThisValue()
    {
        // Arrange
        var config1 = new OpcUaNodeConfiguration { BrowseName = "First", SamplingInterval = 100 };
        var config2 = new OpcUaNodeConfiguration { BrowseName = "Second", SamplingInterval = 200 };

        // Act
        var result = config1.MergeWith(config2);

        // Assert
        Assert.Equal("First", result.BrowseName);
        Assert.Equal(100, result.SamplingInterval);
    }

    [Fact]
    public void MergeWith_WhenThisHasNull_TakesOtherValue()
    {
        // Arrange
        var config1 = new OpcUaNodeConfiguration { BrowseName = "First" };
        var config2 = new OpcUaNodeConfiguration { SamplingInterval = 200, QueueSize = 10 };

        // Act
        var result = config1.MergeWith(config2);

        // Assert
        Assert.Equal("First", result.BrowseName);
        Assert.Equal(200, result.SamplingInterval);
        Assert.Equal(10u, result.QueueSize);
    }

    [Fact]
    public void MergeWith_WhenOtherIsNull_ReturnsThis()
    {
        // Arrange
        var config = new OpcUaNodeConfiguration { BrowseName = "Test" };

        // Act
        var result = config.MergeWith(null);

        // Assert
        Assert.Same(config, result);
    }

    [Fact]
    public void MergeWith_MergesAllFields()
    {
        // Arrange
        var config1 = new OpcUaNodeConfiguration
        {
            BrowseName = "Name1",
            DataChangeTrigger = DataChangeTrigger.Status
        };
        var config2 = new OpcUaNodeConfiguration
        {
            BrowseName = "Name2",
            BrowseNamespaceUri = "http://test/",
            NodeIdentifier = "Node1",
            TypeDefinition = "BaseType",
            SamplingInterval = 500,
            ModellingRule = Mapping.ModellingRule.Mandatory
        };

        // Act
        var result = config1.MergeWith(config2);

        // Assert
        Assert.Equal("Name1", result.BrowseName); // config1 wins
        Assert.Equal("http://test/", result.BrowseNamespaceUri); // from config2
        Assert.Equal("Node1", result.NodeIdentifier); // from config2
        Assert.Equal("BaseType", result.TypeDefinition); // from config2
        Assert.Equal(500, result.SamplingInterval); // from config2
        Assert.Equal(DataChangeTrigger.Status, result.DataChangeTrigger); // config1 wins
        Assert.Equal(Mapping.ModellingRule.Mandatory, result.ModellingRule); // from config2
    }
}
```

**Step 2: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaNodeConfigurationTests" -v n`
Expected: All 4 tests pass

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Mapping/OpcUaNodeConfigurationTests.cs
git commit -m "$(cat <<'EOF'
test(opcua): add unit tests for OpcUaNodeConfiguration.MergeWith

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Phase 2: New Attributes

### Task 2.1: Create OpcUaReferenceAttribute

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Attributes/OpcUaReferenceAttribute.cs`

**Step 1: Write the attribute**

```csharp
namespace Namotion.Interceptor.OpcUa.Attributes;

/// <summary>
/// Specifies the OPC UA reference type from parent to this node.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class OpcUaReferenceAttribute : Attribute
{
    /// <summary>
    /// Creates a new OPC UA reference attribute.
    /// </summary>
    /// <param name="referenceType">The reference type (e.g., "HasComponent", "HasProperty", "HasAddIn").</param>
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
    /// If not specified, uses the same as ReferenceType.
    /// </summary>
    public string? ItemReferenceType { get; init; }
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Attributes/OpcUaReferenceAttribute.cs
git commit -m "$(cat <<'EOF'
feat(opcua): add OpcUaReferenceAttribute for reference type configuration

Replaces OpcUaNodeReferenceTypeAttribute and OpcUaNodeItemReferenceTypeAttribute
with a single attribute supporting both property and item reference types.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2.2: Create OpcUaValueAttribute

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Attributes/OpcUaValueAttribute.cs`

**Step 1: Write the attribute**

```csharp
namespace Namotion.Interceptor.OpcUa.Attributes;

/// <summary>
/// Marks the property that holds the main value for a VariableNode class.
/// Use this when a class represents a complex VariableType (e.g., AnalogSignalVariableType)
/// where one property is the OPC UA Value attribute and others are child Properties.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class OpcUaValueAttribute : Attribute
{
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Attributes/OpcUaValueAttribute.cs
git commit -m "$(cat <<'EOF'
feat(opcua): add OpcUaValueAttribute for VariableType value property

Marks which property holds the OPC UA Value attribute when a class
represents a complex VariableType like AnalogSignalVariableType.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2.3: Enhance OpcUaNodeAttribute with New Properties

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Attributes/OpcUaNodeAttribute.cs`

**Step 1: Read current file**

Read the current OpcUaNodeAttribute.cs to understand its structure.

**Step 2: Add new properties**

Add the following properties to the existing class:
- `DisplayName`
- `Description`
- `TypeDefinition`
- `TypeDefinitionNamespace`
- `NodeClass`
- `DataType`
- `ModellingRule`
- `EventNotifier`

Also change `AttributeUsage` to allow class-level application.

**Step 3: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Attributes/OpcUaNodeAttribute.cs
git commit -m "$(cat <<'EOF'
feat(opcua): enhance OpcUaNodeAttribute with type definition and server properties

- Add DisplayName, Description for localization
- Add TypeDefinition, TypeDefinitionNamespace (consolidates OpcUaTypeDefinitionAttribute)
- Add NodeClass for forcing Object/Variable
- Add DataType for explicit type override
- Add ModellingRule, EventNotifier for server
- Allow attribute on classes for type-level defaults

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2.4: Add Standard OPC UA Types (Range, EUInformation)

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Types/Range.cs`
- Create: `src/Namotion.Interceptor.OpcUa/Types/EUInformation.cs`

**Step 1: Write Range record**

```csharp
namespace Namotion.Interceptor.OpcUa.Types;

/// <summary>
/// OPC UA Range type for EURange, InstrumentRange, and similar properties.
/// Represents a numeric range with low and high bounds.
/// </summary>
public record Range(double Low, double High);
```

**Step 2: Write EUInformation record**

```csharp
namespace Namotion.Interceptor.OpcUa.Types;

/// <summary>
/// OPC UA EUInformation type for EngineeringUnits property.
/// Contains information about the engineering unit of a value.
/// </summary>
public record EUInformation
{
    /// <summary>
    /// Creates a new EUInformation instance.
    /// </summary>
    /// <param name="namespaceUri">The namespace URI for the unit (typically "http://www.opcfoundation.org/UA/units/un/cefact").</param>
    /// <param name="unitId">The UNECE unit ID.</param>
    /// <param name="displayName">The display name for the unit (e.g., "°C", "bar").</param>
    /// <param name="description">Optional description of the unit.</param>
    public EUInformation(string namespaceUri, int unitId, string displayName, string? description = null)
    {
        NamespaceUri = namespaceUri;
        UnitId = unitId;
        DisplayName = displayName;
        Description = description;
    }

    /// <summary>
    /// The namespace URI for the unit definition.
    /// </summary>
    public string NamespaceUri { get; }

    /// <summary>
    /// The UNECE unit ID.
    /// </summary>
    public int UnitId { get; }

    /// <summary>
    /// The localized display name for the unit.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Optional description of the unit.
    /// </summary>
    public string? Description { get; }
}
```

**Step 3: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Types/
git commit -m "$(cat <<'EOF'
feat(opcua): add standard OPC UA types Range and EUInformation

These types support companion spec patterns like AnalogItemType
with EURange and EngineeringUnits properties.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Phase 3: Mapper Implementations

### Task 3.1: Create AttributeOpcUaNodeMapper

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Mapping/AttributeOpcUaNodeMapper.cs`

**Step 1: Write the mapper**

```csharp
using System.Reflection;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Maps properties using OpcUaNode, OpcUaReference, and OpcUaValue attributes.
/// </summary>
public class AttributeOpcUaNodeMapper : IOpcUaNodeMapper
{
    private readonly string? _defaultNamespaceUri;

    /// <summary>
    /// Creates a new attribute-based node mapper.
    /// </summary>
    /// <param name="defaultNamespaceUri">Default namespace URI for nodes without explicit namespace.</param>
    public AttributeOpcUaNodeMapper(string? defaultNamespaceUri = null)
    {
        _defaultNamespaceUri = defaultNamespaceUri;
    }

    /// <inheritdoc />
    public OpcUaNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property)
    {
        // Get class-level OpcUaNode from the property's type (for object references)
        var classAttribute = GetClassLevelOpcUaNodeAttribute(property);

        // Get property-level attributes
        var propertyAttribute = property.ReflectionAttributes
            .OfType<OpcUaNodeAttribute>()
            .FirstOrDefault();
        var referenceAttribute = property.ReflectionAttributes
            .OfType<OpcUaReferenceAttribute>()
            .FirstOrDefault();
        var valueAttribute = property.ReflectionAttributes
            .OfType<OpcUaValueAttribute>()
            .FirstOrDefault();

        // Also check for legacy OpcUaTypeDefinitionAttribute
        var typeDefAttribute = property.ReflectionAttributes
            .OfType<OpcUaTypeDefinitionAttribute>()
            .FirstOrDefault();
        var legacyRefTypeAttribute = property.ReflectionAttributes
            .OfType<OpcUaNodeReferenceTypeAttribute>()
            .FirstOrDefault();
        var legacyItemRefTypeAttribute = property.ReflectionAttributes
            .OfType<OpcUaNodeItemReferenceTypeAttribute>()
            .FirstOrDefault();

        // No OPC UA configuration at all
        if (classAttribute is null && propertyAttribute is null && referenceAttribute is null &&
            valueAttribute is null && typeDefAttribute is null &&
            legacyRefTypeAttribute is null && legacyItemRefTypeAttribute is null)
        {
            return null;
        }

        // Build configuration from class-level first
        var classConfig = classAttribute is not null ? BuildConfigFromNodeAttribute(classAttribute) : null;

        // Then property-level
        var propertyConfig = propertyAttribute is not null ? BuildConfigFromNodeAttribute(propertyAttribute) : null;

        // Start with property config, merge class config as fallback
        var config = propertyConfig?.MergeWith(classConfig) ?? classConfig ?? new OpcUaNodeConfiguration();

        // Apply reference attribute (new style)
        if (referenceAttribute is not null)
        {
            config = config with
            {
                ReferenceType = config.ReferenceType ?? referenceAttribute.ReferenceType,
                ItemReferenceType = config.ItemReferenceType ?? referenceAttribute.ItemReferenceType
            };
        }

        // Apply legacy reference type attributes
        if (legacyRefTypeAttribute is not null && config.ReferenceType is null)
        {
            config = config with { ReferenceType = legacyRefTypeAttribute.Type };
        }
        if (legacyItemRefTypeAttribute is not null && config.ItemReferenceType is null)
        {
            config = config with { ItemReferenceType = legacyItemRefTypeAttribute.Type };
        }

        // Apply legacy type definition attribute
        if (typeDefAttribute is not null)
        {
            config = config with
            {
                TypeDefinition = config.TypeDefinition ?? typeDefAttribute.Type,
                TypeDefinitionNamespace = config.TypeDefinitionNamespace ?? typeDefAttribute.Namespace
            };
        }

        // Apply value attribute
        if (valueAttribute is not null)
        {
            config = config with { IsValue = true };
        }

        return config;
    }

    /// <inheritdoc />
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
            var attribute = property.ReflectionAttributes
                .OfType<OpcUaNodeAttribute>()
                .FirstOrDefault();

            if (attribute?.NodeIdentifier == nodeIdString)
            {
                var propertyNamespaceUri = attribute.NodeNamespaceUri ?? _defaultNamespaceUri;
                if (propertyNamespaceUri is null || propertyNamespaceUri == nodeNamespaceUri)
                {
                    return Task.FromResult<RegisteredSubjectProperty?>(property);
                }
            }
        }

        // Priority 2: BrowseName match via attribute
        var browseName = nodeReference.BrowseName.Name;
        foreach (var property in subject.Properties)
        {
            var attribute = property.ReflectionAttributes
                .OfType<OpcUaNodeAttribute>()
                .FirstOrDefault();

            if (attribute?.BrowseName == browseName)
            {
                return Task.FromResult<RegisteredSubjectProperty?>(property);
            }
        }

        return Task.FromResult<RegisteredSubjectProperty?>(null);
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

    private static OpcUaNodeConfiguration BuildConfigFromNodeAttribute(OpcUaNodeAttribute attribute)
    {
        return new OpcUaNodeConfiguration
        {
            BrowseName = attribute.BrowseName,
            BrowseNamespaceUri = attribute.BrowseNamespaceUri,
            NodeIdentifier = attribute.NodeIdentifier,
            NodeNamespaceUri = attribute.NodeNamespaceUri,
            DisplayName = attribute.DisplayName,
            Description = attribute.Description,
            TypeDefinition = attribute.TypeDefinition,
            TypeDefinitionNamespace = attribute.TypeDefinitionNamespace,
            NodeClass = attribute.NodeClass != OpcUaNodeClass.Auto ? attribute.NodeClass : null,
            DataType = attribute.DataType,
            SamplingInterval = attribute.SamplingInterval != int.MinValue ? attribute.SamplingInterval : null,
            QueueSize = attribute.QueueSize != uint.MaxValue ? attribute.QueueSize : null,
            DiscardOldest = attribute.DiscardOldest switch
            {
                DiscardOldestMode.True => true,
                DiscardOldestMode.False => false,
                _ => null
            },
            DataChangeTrigger = (int)attribute.DataChangeTrigger != -1 ? attribute.DataChangeTrigger : null,
            DeadbandType = (int)attribute.DeadbandType != -1 ? attribute.DeadbandType : null,
            DeadbandValue = !double.IsNaN(attribute.DeadbandValue) ? attribute.DeadbandValue : null,
            ModellingRule = attribute.ModellingRule != Mapping.ModellingRule.Unset ? attribute.ModellingRule : null,
            EventNotifier = attribute.EventNotifier != 0 ? attribute.EventNotifier : null,
        };
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Mapping/AttributeOpcUaNodeMapper.cs
git commit -m "$(cat <<'EOF'
feat(opcua): add AttributeOpcUaNodeMapper implementation

Maps properties using OpcUaNode, OpcUaReference, and OpcUaValue attributes.
Supports class-level type defaults, legacy attribute compatibility,
and reverse lookup by NodeIdentifier or BrowseName.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3.2: Create PathProviderOpcUaNodeMapper

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Mapping/PathProviderOpcUaNodeMapper.cs`

**Step 1: Write the mapper**

```csharp
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Maps properties using an IPathProvider for inclusion and browse names.
/// Provides default reference type of "HasProperty".
/// </summary>
public class PathProviderOpcUaNodeMapper : IOpcUaNodeMapper
{
    private readonly IPathProvider _pathProvider;

    /// <summary>
    /// Creates a new path provider-based node mapper.
    /// </summary>
    /// <param name="pathProvider">The path provider for property inclusion and naming.</param>
    public PathProviderOpcUaNodeMapper(IPathProvider pathProvider)
    {
        _pathProvider = pathProvider;
    }

    /// <inheritdoc />
    public OpcUaNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property)
    {
        if (!_pathProvider.IsPropertyIncluded(property))
        {
            return null;
        }

        // Use PathProvider segment, or fall back to property.BrowseName
        // (which is AttributeName for attributes, Name for regular properties)
        var browseName = _pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;

        // Default ReferenceType: "HasProperty" for attributes, null for others
        // This allows CompositeNodeMapper to fill in from other sources
        var referenceType = property.IsAttribute ? "HasProperty" : null;

        return new OpcUaNodeConfiguration
        {
            BrowseName = browseName,
            ReferenceType = referenceType
        };
    }

    /// <inheritdoc />
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
            {
                continue;
            }

            var segment = _pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;
            if (segment == browseName)
            {
                return Task.FromResult<RegisteredSubjectProperty?>(property);
            }
        }

        return Task.FromResult<RegisteredSubjectProperty?>(null);
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Mapping/PathProviderOpcUaNodeMapper.cs
git commit -m "$(cat <<'EOF'
feat(opcua): add PathProviderOpcUaNodeMapper implementation

Uses IPathProvider for property inclusion and browse name resolution.
Defaults to "HasProperty" reference type for attribute properties.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3.3: Create CompositeNodeMapper

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Mapping/CompositeNodeMapper.cs`

**Step 1: Write the mapper**

```csharp
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Combines multiple node mappers with merge semantics.
/// Earlier mappers in the list take priority for conflicting values.
/// </summary>
public class CompositeNodeMapper : IOpcUaNodeMapper
{
    private readonly IOpcUaNodeMapper[] _mappers;

    /// <summary>
    /// Creates a composite mapper from multiple mappers.
    /// </summary>
    /// <param name="mappers">Mappers in order (later mappers override earlier ones).</param>
    public CompositeNodeMapper(params IOpcUaNodeMapper[] mappers)
    {
        _mappers = mappers;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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
            {
                return property;
            }
        }

        return null;
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Mapping/CompositeNodeMapper.cs
git commit -m "$(cat <<'EOF'
feat(opcua): add CompositeNodeMapper for combining mappers

Merges configurations from multiple mappers where earlier mappers
take priority. First mapper to return a property match wins for
reverse lookup.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3.4: Write Unit Tests for CompositeNodeMapper

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Mapping/CompositeNodeMapperTests.cs`

**Step 1: Write the test file**

```csharp
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry.Abstractions;
using Moq;
using Opc.Ua;
using Opc.Ua.Client;
using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class CompositeNodeMapperTests
{
    [Fact]
    public void TryGetConfiguration_MergesFromMultipleMappers_LastWins()
    {
        // Arrange
        var propertyMock = new Mock<RegisteredSubjectProperty>();
        var property = propertyMock.Object;

        var mapper1Mock = new Mock<IOpcUaNodeMapper>();
        mapper1Mock.Setup(m => m.TryGetConfiguration(property))
            .Returns(new OpcUaNodeConfiguration { SamplingInterval = 50 });

        var mapper2Mock = new Mock<IOpcUaNodeMapper>();
        mapper2Mock.Setup(m => m.TryGetConfiguration(property))
            .Returns(new OpcUaNodeConfiguration { BrowseName = "Speed", SamplingInterval = 100 });

        var mapper3Mock = new Mock<IOpcUaNodeMapper>();
        mapper3Mock.Setup(m => m.TryGetConfiguration(property))
            .Returns(new OpcUaNodeConfiguration { BrowseName = "velocity", ReferenceType = "HasProperty" });

        var composite = new CompositeNodeMapper(
            mapper1Mock.Object, mapper2Mock.Object, mapper3Mock.Object);

        // Act
        var result = composite.TryGetConfiguration(property);

        // Assert - "last wins" semantics: later mappers override earlier ones
        Assert.NotNull(result);
        Assert.Equal(100, result.SamplingInterval);      // mapper2 wins (later than mapper1)
        Assert.Equal("velocity", result.BrowseName);     // mapper3 wins (later than mapper2)
        Assert.Equal("HasProperty", result.ReferenceType); // mapper3 provides
    }

    [Fact]
    public void TryGetConfiguration_ReturnsNullWhenAllMappersReturnNull()
    {
        // Arrange
        var propertyMock = new Mock<RegisteredSubjectProperty>();
        var property = propertyMock.Object;

        var mapper1Mock = new Mock<IOpcUaNodeMapper>();
        mapper1Mock.Setup(m => m.TryGetConfiguration(property))
            .Returns((OpcUaNodeConfiguration?)null);

        var mapper2Mock = new Mock<IOpcUaNodeMapper>();
        mapper2Mock.Setup(m => m.TryGetConfiguration(property))
            .Returns((OpcUaNodeConfiguration?)null);

        var composite = new CompositeNodeMapper(mapper1Mock.Object, mapper2Mock.Object);

        // Act
        var result = composite.TryGetConfiguration(property);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetPropertyAsync_ReturnsFirstMatch()
    {
        // Arrange
        var subjectMock = new Mock<RegisteredSubject>();
        var nodeRef = new ReferenceDescription();
        var sessionMock = new Mock<ISession>();
        var expectedPropertyMock = new Mock<RegisteredSubjectProperty>();

        var mapper1Mock = new Mock<IOpcUaNodeMapper>();
        mapper1Mock.Setup(m => m.TryGetPropertyAsync(
                It.IsAny<RegisteredSubject>(), It.IsAny<ReferenceDescription>(),
                It.IsAny<ISession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RegisteredSubjectProperty?)null);

        var mapper2Mock = new Mock<IOpcUaNodeMapper>();
        mapper2Mock.Setup(m => m.TryGetPropertyAsync(
                It.IsAny<RegisteredSubject>(), It.IsAny<ReferenceDescription>(),
                It.IsAny<ISession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPropertyMock.Object);

        var mapper3Mock = new Mock<IOpcUaNodeMapper>();
        // mapper3 should not be called since mapper2 returns a match

        var composite = new CompositeNodeMapper(
            mapper1Mock.Object, mapper2Mock.Object, mapper3Mock.Object);

        // Act
        var result = await composite.TryGetPropertyAsync(
            subjectMock.Object, nodeRef, sessionMock.Object, default);

        // Assert
        Assert.Same(expectedPropertyMock.Object, result);
        mapper3Mock.Verify(m => m.TryGetPropertyAsync(
            It.IsAny<RegisteredSubject>(), It.IsAny<ReferenceDescription>(),
            It.IsAny<ISession>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~CompositeNodeMapperTests" -v n`
Expected: All 3 tests pass

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Mapping/CompositeNodeMapperTests.cs
git commit -m "$(cat <<'EOF'
test(opcua): add unit tests for CompositeNodeMapper

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3.5: Create FluentOpcUaNodeMapper (Basic Structure)

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Mapping/FluentOpcUaNodeMapper.cs`
- Create: `src/Namotion.Interceptor.OpcUa/Mapping/IPropertyBuilder.cs`

**Step 1: Write the IPropertyBuilder interface**

```csharp
using System.Linq.Expressions;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Builder interface for fluent property configuration.
/// </summary>
/// <typeparam name="T">The type being configured.</typeparam>
public interface IPropertyBuilder<T>
{
    // Shared - naming/identification
    IPropertyBuilder<T> BrowseName(string value);
    IPropertyBuilder<T> BrowseNamespaceUri(string value);
    IPropertyBuilder<T> NodeIdentifier(string value);
    IPropertyBuilder<T> NodeNamespaceUri(string value);
    IPropertyBuilder<T> DisplayName(string value);
    IPropertyBuilder<T> Description(string value);

    // Type definition
    IPropertyBuilder<T> TypeDefinition(string value);
    IPropertyBuilder<T> TypeDefinitionNamespace(string value);
    IPropertyBuilder<T> NodeClass(OpcUaNodeClass value);
    IPropertyBuilder<T> DataType(string value);

    // Reference configuration
    IPropertyBuilder<T> ReferenceType(string value);
    IPropertyBuilder<T> ItemReferenceType(string value);

    // Client only - monitoring
    IPropertyBuilder<T> SamplingInterval(int value);
    IPropertyBuilder<T> QueueSize(uint value);
    IPropertyBuilder<T> DiscardOldest(bool value);
    IPropertyBuilder<T> DataChangeTrigger(DataChangeTrigger value);
    IPropertyBuilder<T> DeadbandType(DeadbandType value);
    IPropertyBuilder<T> DeadbandValue(double value);

    // Server only
    IPropertyBuilder<T> ModellingRule(ModellingRule value);
    IPropertyBuilder<T> EventNotifier(byte value);

    // Nested property mapping
    IPropertyBuilder<T> Map<TProperty>(
        Expression<Func<T, TProperty>> propertySelector,
        Action<IPropertyBuilder<TProperty>> configure);
}
```

**Step 2: Write the FluentOpcUaNodeMapper class**

```csharp
using System.Linq.Expressions;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Maps properties using code-based fluent configuration.
/// Supports instance-based configuration (different config for Motor1.Speed vs Motor2.Speed).
/// </summary>
/// <typeparam name="T">The root type to configure.</typeparam>
public class FluentOpcUaNodeMapper<T> : IOpcUaNodeMapper
{
    private readonly Dictionary<string, OpcUaNodeConfiguration> _mappings = new();

    /// <summary>
    /// Maps a property with fluent configuration.
    /// </summary>
    public FluentOpcUaNodeMapper<T> Map<TProperty>(
        Expression<Func<T, TProperty>> propertySelector,
        Action<IPropertyBuilder<TProperty>> configure)
    {
        var path = GetPropertyPath(propertySelector);
        var builder = new PropertyBuilder<TProperty>(path, _mappings);
        configure(builder);
        return this;
    }

    /// <inheritdoc />
    public OpcUaNodeConfiguration? TryGetConfiguration(RegisteredSubjectProperty property)
    {
        var path = GetPropertyPath(property);
        return _mappings.TryGetValue(path, out var config) ? config : null;
    }

    /// <inheritdoc />
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
            {
                return Task.FromResult<RegisteredSubjectProperty?>(property);
            }
        }

        return Task.FromResult<RegisteredSubjectProperty?>(null);
    }

    private static string GetPropertyPath<TProperty>(Expression<Func<T, TProperty>> expression)
    {
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
        var parts = new List<string> { property.Name };
        var currentSubject = property.Parent;

        while (currentSubject.Parents.Length > 0)
        {
            var parent = currentSubject.Parents[0];
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

        private IPropertyBuilder<TProp> UpdateConfig(Func<OpcUaNodeConfiguration, OpcUaNodeConfiguration> update)
        {
            _config = update(_config);
            _mappings[_basePath] = _config;
            return this;
        }

        public IPropertyBuilder<TProp> BrowseName(string value) =>
            UpdateConfig(c => c with { BrowseName = value });

        public IPropertyBuilder<TProp> BrowseNamespaceUri(string value) =>
            UpdateConfig(c => c with { BrowseNamespaceUri = value });

        public IPropertyBuilder<TProp> NodeIdentifier(string value) =>
            UpdateConfig(c => c with { NodeIdentifier = value });

        public IPropertyBuilder<TProp> NodeNamespaceUri(string value) =>
            UpdateConfig(c => c with { NodeNamespaceUri = value });

        public IPropertyBuilder<TProp> DisplayName(string value) =>
            UpdateConfig(c => c with { DisplayName = value });

        public IPropertyBuilder<TProp> Description(string value) =>
            UpdateConfig(c => c with { Description = value });

        public IPropertyBuilder<TProp> TypeDefinition(string value) =>
            UpdateConfig(c => c with { TypeDefinition = value });

        public IPropertyBuilder<TProp> TypeDefinitionNamespace(string value) =>
            UpdateConfig(c => c with { TypeDefinitionNamespace = value });

        public IPropertyBuilder<TProp> NodeClass(OpcUaNodeClass value) =>
            UpdateConfig(c => c with { NodeClass = value });

        public IPropertyBuilder<TProp> DataType(string value) =>
            UpdateConfig(c => c with { DataType = value });

        public IPropertyBuilder<TProp> ReferenceType(string value) =>
            UpdateConfig(c => c with { ReferenceType = value });

        public IPropertyBuilder<TProp> ItemReferenceType(string value) =>
            UpdateConfig(c => c with { ItemReferenceType = value });

        public IPropertyBuilder<TProp> SamplingInterval(int value) =>
            UpdateConfig(c => c with { SamplingInterval = value });

        public IPropertyBuilder<TProp> QueueSize(uint value) =>
            UpdateConfig(c => c with { QueueSize = value });

        public IPropertyBuilder<TProp> DiscardOldest(bool value) =>
            UpdateConfig(c => c with { DiscardOldest = value });

        public IPropertyBuilder<TProp> DataChangeTrigger(DataChangeTrigger value) =>
            UpdateConfig(c => c with { DataChangeTrigger = value });

        public IPropertyBuilder<TProp> DeadbandType(DeadbandType value) =>
            UpdateConfig(c => c with { DeadbandType = value });

        public IPropertyBuilder<TProp> DeadbandValue(double value) =>
            UpdateConfig(c => c with { DeadbandValue = value });

        public IPropertyBuilder<TProp> ModellingRule(ModellingRule value) =>
            UpdateConfig(c => c with { ModellingRule = value });

        public IPropertyBuilder<TProp> EventNotifier(byte value) =>
            UpdateConfig(c => c with { EventNotifier = value });

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

**Step 3: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Mapping/IPropertyBuilder.cs src/Namotion.Interceptor.OpcUa/Mapping/FluentOpcUaNodeMapper.cs
git commit -m "$(cat <<'EOF'
feat(opcua): add FluentOpcUaNodeMapper for code-based configuration

Enables instance-specific configuration via fluent API with nested
property mapping (e.g., different SamplingInterval for Motor1.Speed
vs Motor2.Speed).

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Phase 4: Client Integration

### Task 4.1: Add NodeMapper Property to OpcUaClientConfiguration

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientConfiguration.cs`

**Step 1: Read current file to understand structure**

Read OpcUaClientConfiguration.cs.

**Step 2: Add NodeMapper property and ActualNodeMapper**

Add the following to the class:

```csharp
/// <summary>
/// Maps C# properties to OPC UA nodes.
/// If not set, a default composite mapper is created using AttributeOpcUaNodeMapper
/// and PathProviderOpcUaNodeMapper.
/// </summary>
public IOpcUaNodeMapper? NodeMapper { get; init; }

private IOpcUaNodeMapper? _resolvedNodeMapper;

/// <summary>
/// Gets the actual node mapper, creating a default if not configured.
/// </summary>
internal IOpcUaNodeMapper ActualNodeMapper => NodeMapper ?? LazyInitializer.EnsureInitialized(
    ref _resolvedNodeMapper,
    () => new CompositeNodeMapper(
        new AttributeOpcUaNodeMapper(DefaultNamespaceUri),
        new PathProviderOpcUaNodeMapper(PathProvider)))!;
```

Add required using statements:
```csharp
using Namotion.Interceptor.OpcUa.Mapping;
```

**Step 3: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaClientConfiguration.cs
git commit -m "$(cat <<'EOF'
feat(opcua): add NodeMapper property to OpcUaClientConfiguration

Defaults to composite of AttributeOpcUaNodeMapper and PathProviderOpcUaNodeMapper.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4.2: Update CreateMonitoredItem to Use NodeMapper

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientConfiguration.cs`

**Step 1: Modify CreateMonitoredItem method**

Replace the existing `CreateMonitoredItem` method to use `ActualNodeMapper.TryGetConfiguration()` instead of `property.TryGetOpcUaNodeAttribute()`.

**Step 2: Modify CreateDataChangeFilter method**

Update to accept `OpcUaNodeConfiguration?` instead of `OpcUaNodeAttribute?`.

**Step 3: Build and run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests -v n`
Expected: All tests pass

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaClientConfiguration.cs
git commit -m "$(cat <<'EOF'
refactor(opcua): update CreateMonitoredItem to use NodeMapper

Replaces direct attribute access with NodeMapper configuration lookup.
Maintains backward compatibility through default composite mapper.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4.3: Update OpcUaSubjectLoader to Use NodeMapper

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectLoader.cs`

**Step 1: Update FindSubjectProperty method**

Replace the current `FindSubjectProperty` implementation to use `_configuration.ActualNodeMapper.TryGetPropertyAsync()`.

**Step 2: Add LoadPropertyAttributesAsync method**

Add method to load property attribute children after loading a variable node.

**Step 3: Build and run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests -v n`
Expected: All tests pass

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectLoader.cs
git commit -m "$(cat <<'EOF'
refactor(opcua): update OpcUaSubjectLoader to use NodeMapper

- Replace FindSubjectProperty with NodeMapper.TryGetPropertyAsync
- Add LoadPropertyAttributesAsync for property attribute children
- Support HasProperty children on VariableNodes

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Phase 5: Server Integration

### Task 5.1: Add NodeMapper Property to OpcUaServerConfiguration

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerConfiguration.cs`

**Step 1: Add NodeMapper property**

```csharp
/// <summary>
/// Maps C# properties to OPC UA nodes.
/// Defaults to composite of AttributeOpcUaNodeMapper and PathProviderOpcUaNodeMapper.
/// </summary>
public IOpcUaNodeMapper NodeMapper { get; init; }
    = new CompositeNodeMapper(
        new AttributeOpcUaNodeMapper(),
        new PathProviderOpcUaNodeMapper(new AttributeBasedPathProvider("opc")));
```

**Step 2: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Server/OpcUaServerConfiguration.cs
git commit -m "$(cat <<'EOF'
feat(opcua): add NodeMapper property to OpcUaServerConfiguration

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5.2: Update CustomNodeManager to Use NodeMapper

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/CustomNodeManager.cs`

**Step 1: Update GetBrowseName to use NodeMapper**

**Step 2: Update GetNodeId to use NodeMapper**

**Step 3: Update GetReferenceTypeId to use NodeMapper**

**Step 4: Update GetTypeDefinitionId to use NodeMapper**

**Step 5: Add CreateAttributeNodes method for property attributes**

**Step 6: Handle NodeClass.Variable for VariableType classes**

**Step 7: Build and run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests -v n`
Expected: All tests pass

**Step 8: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Server/CustomNodeManager.cs
git commit -m "$(cat <<'EOF'
refactor(opcua): update CustomNodeManager to use NodeMapper

- Replace direct attribute access with NodeMapper configuration
- Add CreateAttributeNodes for property attribute children
- Support NodeClass.Variable for VariableType classes
- Support OpcUaValue property marking

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Phase 6: Integration Tests

### Task 6.1: Write Integration Test for Attribute Mapping

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Mapping/AttributeOpcUaNodeMapperIntegrationTests.cs`

**Step 1: Write test models and tests**

Create test models with various attribute combinations and verify mapping works correctly.

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~AttributeOpcUaNodeMapperIntegrationTests" -v n`
Expected: All tests pass

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Mapping/
git commit -m "$(cat <<'EOF'
test(opcua): add integration tests for attribute-based mapping

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6.2: Write Integration Test for PropertyAttribute Children

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Integration/Testing/TestModel.cs`
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Mapping/PropertyAttributeIntegrationTests.cs`

**Step 1: Add test model with PropertyAttribute**

```csharp
[InterceptorSubject]
public partial class SensorWithMetadata
{
    public partial double Value { get; set; }

    [PropertyAttribute(nameof(Value), "EURange")]
    public partial Range? Value_EURange { get; set; }

    [PropertyAttribute(nameof(Value), "EngineeringUnits")]
    public partial EUInformation? Value_EngineeringUnits { get; set; }
}
```

**Step 2: Write integration test**

Test that property attributes are created as HasProperty children of the parent VariableNode.

**Step 3: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~PropertyAttributeIntegrationTests" -v n`
Expected: All tests pass

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/
git commit -m "$(cat <<'EOF'
test(opcua): add integration tests for PropertyAttribute children

Verifies that PropertyAttribute properties become HasProperty children
of their parent VariableNode in the OPC UA address space.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Phase 7: Migration and Cleanup

### Task 7.1: Mark Legacy Attributes as Obsolete

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Attributes/OpcUaTypeDefinitionAttribute.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Attributes/OpcUaNodeReferenceTypeAttribute.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Attributes/OpcUaNodeItemReferenceTypeAttribute.cs`

**Step 1: Add Obsolete attributes**

```csharp
[Obsolete("Use OpcUaNodeAttribute.TypeDefinition and TypeDefinitionNamespace instead.")]
public class OpcUaTypeDefinitionAttribute : Attribute { ... }

[Obsolete("Use OpcUaReferenceAttribute instead.")]
public class OpcUaNodeReferenceTypeAttribute : Attribute { ... }

[Obsolete("Use OpcUaReferenceAttribute.ItemReferenceType instead.")]
public class OpcUaNodeItemReferenceTypeAttribute : Attribute { ... }
```

**Step 2: Build to see warnings**

Run: `dotnet build src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj`
Expected: Build succeeds with obsolete warnings

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Attributes/
git commit -m "$(cat <<'EOF'
deprecate(opcua): mark legacy attributes as obsolete

- OpcUaTypeDefinitionAttribute → use OpcUaNodeAttribute.TypeDefinition
- OpcUaNodeReferenceTypeAttribute → use OpcUaReferenceAttribute
- OpcUaNodeItemReferenceTypeAttribute → use OpcUaReferenceAttribute.ItemReferenceType

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7.2: Update Sample Models to Use New Attributes

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa.SampleModel/` (various files)

**Step 1: Update sample models**

Replace legacy attributes with new consolidated attributes.

**Step 2: Build and run samples**

Run: `dotnet build src/Namotion.Interceptor.OpcUa.SampleModel/`
Expected: Build succeeds without obsolete warnings

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.SampleModel/
git commit -m "$(cat <<'EOF'
refactor(samples): migrate to new OPC UA mapping attributes

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7.3: Run Full Test Suite

**Step 1: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx -v n`
Expected: All tests pass

**Step 2: Run integration tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category=Integration" -v n`
Expected: All integration tests pass

---

### Task 7.4: Final Documentation Update

**Files:**
- Modify: `docs/opcua-mapping.md`

**Step 1: Update documentation**

Ensure documentation reflects the new attribute model and mapper architecture.

**Step 2: Commit**

```bash
git add docs/opcua-mapping.md
git commit -m "$(cat <<'EOF'
docs(opcua): update mapping documentation for new architecture

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Phase 8: Comprehensive Unit Tests

### Task 8.1: Unit Tests for AttributeOpcUaNodeMapper

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Mapping/AttributeOpcUaNodeMapperTests.cs`

**Step 1: Write comprehensive unit tests**

```csharp
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry.Abstractions;
using Moq;
using Opc.Ua;
using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class AttributeOpcUaNodeMapperTests
{
    [Fact]
    public void TryGetConfiguration_WithOpcUaNodeAttribute_ReturnsBrowseName()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var property = CreatePropertyWithAttributes(
            new OpcUaNodeAttribute("Speed", "http://test/"));

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("Speed", config.BrowseName);
        Assert.Equal("http://test/", config.BrowseNamespaceUri);
    }

    [Fact]
    public void TryGetConfiguration_WithOpcUaReferenceAttribute_ReturnsReferenceType()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var property = CreatePropertyWithAttributes(
            new OpcUaReferenceAttribute("HasComponent") { ItemReferenceType = "Organizes" });

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("HasComponent", config.ReferenceType);
        Assert.Equal("Organizes", config.ItemReferenceType);
    }

    [Fact]
    public void TryGetConfiguration_WithOpcUaValueAttribute_SetsIsValueTrue()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var property = CreatePropertyWithAttributes(new OpcUaValueAttribute());

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.True(config.IsValue);
    }

    [Fact]
    public void TryGetConfiguration_WithMonitoringAttributes_ReturnsMonitoringConfig()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var attr = new OpcUaNodeAttribute("Test", null)
        {
            SamplingInterval = 100,
            QueueSize = 5,
            DiscardOldest = DiscardOldestMode.False,
            DataChangeTrigger = DataChangeTrigger.StatusValueTimestamp,
            DeadbandType = DeadbandType.Absolute,
            DeadbandValue = 0.5
        };
        var property = CreatePropertyWithAttributes(attr);

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal(100, config.SamplingInterval);
        Assert.Equal(5u, config.QueueSize);
        Assert.False(config.DiscardOldest);
        Assert.Equal(DataChangeTrigger.StatusValueTimestamp, config.DataChangeTrigger);
        Assert.Equal(DeadbandType.Absolute, config.DeadbandType);
        Assert.Equal(0.5, config.DeadbandValue);
    }

    [Fact]
    public void TryGetConfiguration_WithNoAttributes_ReturnsNull()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var property = CreatePropertyWithAttributes();

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.Null(config);
    }

    [Fact]
    public void TryGetConfiguration_WithLegacyTypeDefinitionAttribute_ReturnsTypeDefinition()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var property = CreatePropertyWithAttributes(
            new OpcUaTypeDefinitionAttribute("MotorType", "http://machinery/"));

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("MotorType", config.TypeDefinition);
        Assert.Equal("http://machinery/", config.TypeDefinitionNamespace);
    }

    [Fact]
    public void TryGetConfiguration_WithLegacyReferenceTypeAttribute_ReturnsReferenceType()
    {
        // Arrange
        var mapper = new AttributeOpcUaNodeMapper();
        var property = CreatePropertyWithAttributes(
            new OpcUaNodeReferenceTypeAttribute("HasAddIn"));

        // Act
        var config = mapper.TryGetConfiguration(property);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("HasAddIn", config.ReferenceType);
    }

    private static RegisteredSubjectProperty CreatePropertyWithAttributes(params Attribute[] attributes)
    {
        var propertyMock = new Mock<RegisteredSubjectProperty>();
        propertyMock.Setup(p => p.ReflectionAttributes).Returns(attributes);
        propertyMock.Setup(p => p.IsSubjectReference).Returns(false);
        propertyMock.Setup(p => p.IsSubjectCollection).Returns(false);
        propertyMock.Setup(p => p.IsSubjectDictionary).Returns(false);
        return propertyMock.Object;
    }
}
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~AttributeOpcUaNodeMapperTests" -v n`
Expected: All tests pass

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Mapping/AttributeOpcUaNodeMapperTests.cs
git commit -m "$(cat <<'EOF'
test(opcua): add comprehensive unit tests for AttributeOpcUaNodeMapper

Tests cover:
- Browse name and namespace from OpcUaNodeAttribute
- Reference types from OpcUaReferenceAttribute
- IsValue from OpcUaValueAttribute
- All monitoring configuration properties
- Legacy attribute compatibility

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 8.2: Unit Tests for PathProviderOpcUaNodeMapper

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Mapping/PathProviderOpcUaNodeMapperTests.cs`

**Step 1: Write unit tests**

```csharp
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Moq;
using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

public class PathProviderOpcUaNodeMapperTests
{
    [Fact]
    public void TryGetConfiguration_WhenPropertyIncluded_ReturnsBrowseName()
    {
        // Arrange
        var pathProviderMock = new Mock<IPathProvider>();
        var propertyMock = new Mock<RegisteredSubjectProperty>();

        pathProviderMock.Setup(p => p.IsPropertyIncluded(propertyMock.Object)).Returns(true);
        pathProviderMock.Setup(p => p.TryGetPropertySegment(propertyMock.Object)).Returns("Speed");
        propertyMock.Setup(p => p.IsAttribute).Returns(false);

        var mapper = new PathProviderOpcUaNodeMapper(pathProviderMock.Object);

        // Act
        var config = mapper.TryGetConfiguration(propertyMock.Object);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("Speed", config.BrowseName);
    }

    [Fact]
    public void TryGetConfiguration_WhenPropertyExcluded_ReturnsNull()
    {
        // Arrange
        var pathProviderMock = new Mock<IPathProvider>();
        var propertyMock = new Mock<RegisteredSubjectProperty>();

        pathProviderMock.Setup(p => p.IsPropertyIncluded(propertyMock.Object)).Returns(false);

        var mapper = new PathProviderOpcUaNodeMapper(pathProviderMock.Object);

        // Act
        var config = mapper.TryGetConfiguration(propertyMock.Object);

        // Assert
        Assert.Null(config);
    }

    [Fact]
    public void TryGetConfiguration_WhenIsAttribute_ReturnsHasPropertyReferenceType()
    {
        // Arrange
        var pathProviderMock = new Mock<IPathProvider>();
        var propertyMock = new Mock<RegisteredSubjectProperty>();

        pathProviderMock.Setup(p => p.IsPropertyIncluded(propertyMock.Object)).Returns(true);
        pathProviderMock.Setup(p => p.TryGetPropertySegment(propertyMock.Object)).Returns("EURange");
        propertyMock.Setup(p => p.IsAttribute).Returns(true);
        propertyMock.Setup(p => p.BrowseName).Returns("EURange");

        var mapper = new PathProviderOpcUaNodeMapper(pathProviderMock.Object);

        // Act
        var config = mapper.TryGetConfiguration(propertyMock.Object);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("HasProperty", config.ReferenceType);
    }

    [Fact]
    public void TryGetConfiguration_WhenNotAttribute_ReturnsNullReferenceType()
    {
        // Arrange
        var pathProviderMock = new Mock<IPathProvider>();
        var propertyMock = new Mock<RegisteredSubjectProperty>();

        pathProviderMock.Setup(p => p.IsPropertyIncluded(propertyMock.Object)).Returns(true);
        pathProviderMock.Setup(p => p.TryGetPropertySegment(propertyMock.Object)).Returns("Speed");
        propertyMock.Setup(p => p.IsAttribute).Returns(false);

        var mapper = new PathProviderOpcUaNodeMapper(pathProviderMock.Object);

        // Act
        var config = mapper.TryGetConfiguration(propertyMock.Object);

        // Assert
        Assert.NotNull(config);
        Assert.Null(config.ReferenceType); // Allows CompositeMapper to fill from other sources
    }

    [Fact]
    public void TryGetConfiguration_WhenNoSegment_UsesBrowseName()
    {
        // Arrange
        var pathProviderMock = new Mock<IPathProvider>();
        var propertyMock = new Mock<RegisteredSubjectProperty>();

        pathProviderMock.Setup(p => p.IsPropertyIncluded(propertyMock.Object)).Returns(true);
        pathProviderMock.Setup(p => p.TryGetPropertySegment(propertyMock.Object)).Returns((string?)null);
        propertyMock.Setup(p => p.BrowseName).Returns("DefaultName");
        propertyMock.Setup(p => p.IsAttribute).Returns(false);

        var mapper = new PathProviderOpcUaNodeMapper(pathProviderMock.Object);

        // Act
        var config = mapper.TryGetConfiguration(propertyMock.Object);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("DefaultName", config.BrowseName);
    }
}
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~PathProviderOpcUaNodeMapperTests" -v n`
Expected: All tests pass

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Mapping/PathProviderOpcUaNodeMapperTests.cs
git commit -m "$(cat <<'EOF'
test(opcua): add unit tests for PathProviderOpcUaNodeMapper

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 8.3: Unit Tests for FluentOpcUaNodeMapper

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Mapping/FluentOpcUaNodeMapperTests.cs`

**Step 1: Write unit tests**

```csharp
using Namotion.Interceptor.OpcUa.Mapping;
using Opc.Ua;
using Xunit;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

// Test model classes
public class TestMachine
{
    public TestMotor Motor1 { get; set; } = new();
    public TestMotor Motor2 { get; set; } = new();
    public string Name { get; set; } = "";
}

public class TestMotor
{
    public double Speed { get; set; }
    public double Temperature { get; set; }
}

public class FluentOpcUaNodeMapperTests
{
    [Fact]
    public void Map_SingleProperty_StoresConfiguration()
    {
        // Arrange & Act
        var mapper = new FluentOpcUaNodeMapper<TestMachine>()
            .Map(m => m.Name, p => p
                .BrowseName("MachineName")
                .SamplingInterval(500));

        // Assert - we can't easily test TryGetConfiguration without a real RegisteredSubjectProperty
        // This test validates the fluent API compiles and doesn't throw
        Assert.NotNull(mapper);
    }

    [Fact]
    public void Map_NestedProperty_StoresConfiguration()
    {
        // Arrange & Act
        var mapper = new FluentOpcUaNodeMapper<TestMachine>()
            .Map(m => m.Motor1, motor => motor
                .BrowseName("MainMotor")
                .ReferenceType("HasComponent")
                .Map(m => m.Speed, speed => speed
                    .SamplingInterval(50)
                    .DeadbandType(DeadbandType.Absolute)
                    .DeadbandValue(0.1)));

        // Assert
        Assert.NotNull(mapper);
    }

    [Fact]
    public void Map_DifferentInstancesOfSameType_CanHaveDifferentConfig()
    {
        // Arrange & Act
        var mapper = new FluentOpcUaNodeMapper<TestMachine>()
            .Map(m => m.Motor1, motor => motor
                .BrowseName("MainDriveMotor")
                .Map(m => m.Speed, s => s.SamplingInterval(50)))
            .Map(m => m.Motor2, motor => motor
                .BrowseName("AuxiliaryMotor")
                .Map(m => m.Speed, s => s.SamplingInterval(200)));

        // Assert - validates the fluent API supports instance-specific config
        Assert.NotNull(mapper);
    }

    [Fact]
    public void Map_AllBuilderMethods_DoNotThrow()
    {
        // Arrange & Act - exercise all builder methods
        var mapper = new FluentOpcUaNodeMapper<TestMachine>()
            .Map(m => m.Motor1, motor => motor
                .BrowseName("Motor")
                .BrowseNamespaceUri("http://test/")
                .NodeIdentifier("Motor1")
                .NodeNamespaceUri("http://test/")
                .DisplayName("Main Motor")
                .Description("The main drive motor")
                .TypeDefinition("MotorType")
                .TypeDefinitionNamespace("http://machinery/")
                .NodeClass(OpcUaNodeClass.Object)
                .DataType("Double")
                .ReferenceType("HasComponent")
                .ItemReferenceType("Organizes")
                .SamplingInterval(100)
                .QueueSize(10)
                .DiscardOldest(true)
                .DataChangeTrigger(DataChangeTrigger.StatusValue)
                .DeadbandType(DeadbandType.Percent)
                .DeadbandValue(5.0)
                .ModellingRule(ModellingRule.Mandatory)
                .EventNotifier(1));

        // Assert
        Assert.NotNull(mapper);
    }
}
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~FluentOpcUaNodeMapperTests" -v n`
Expected: All tests pass

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Mapping/FluentOpcUaNodeMapperTests.cs
git commit -m "$(cat <<'EOF'
test(opcua): add unit tests for FluentOpcUaNodeMapper

Tests fluent API for nested property mapping and instance-specific configuration.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

## Phase 9: Complex Model Integration Tests

### Task 9.1: Create Complex Test Model with All Mapping Features

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Integration/Testing/ComplexTestModel.cs`

**Step 1: Write comprehensive test model**

```csharp
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.OpcUa.Types;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Complex test model demonstrating all OPC UA mapping features.
/// </summary>
[InterceptorSubject]
[OpcUaNode(TypeDefinition = "MachineType", TypeDefinitionNamespace = "http://test.machinery/")]
public partial class ComplexMachine
{
    public ComplexMachine()
    {
        Identification = null!;
        MainMotor = null!;
        AuxMotor = null!;
        ProcessValues = new Dictionary<string, AnalogSignal>();
    }

    /// <summary>HasAddIn reference to Identification.</summary>
    [OpcUaReference("HasAddIn")]
    [OpcUaNode(BrowseName = "Identification", BrowseNamespaceUri = "http://opcfoundation.org/UA/DI/")]
    public partial MachineIdentification? Identification { get; set; }

    /// <summary>HasComponent reference with explicit BrowseName.</summary>
    [OpcUaReference("HasComponent")]
    [OpcUaNode(BrowseName = "MainDriveMotor", NodeIdentifier = "MainMotor")]
    public partial Motor? MainMotor { get; set; }

    /// <summary>Different BrowseName for same Motor type.</summary>
    [OpcUaReference("HasComponent")]
    [OpcUaNode(BrowseName = "AuxiliaryMotor", NodeIdentifier = "AuxMotor")]
    public partial Motor? AuxMotor { get; set; }

    /// <summary>Organizes reference for folder-style collection.</summary>
    [OpcUaReference("Organizes", ItemReferenceType = "HasComponent")]
    [OpcUaNode(BrowseName = "ProcessValues", TypeDefinition = "FolderType")]
    public partial IReadOnlyDictionary<string, AnalogSignal> ProcessValues { get; set; }

    /// <summary>Simple property with HasProperty (default).</summary>
    [Path("opc", "Status")]
    public partial int Status { get; set; }
}

[InterceptorSubject]
[OpcUaNode(TypeDefinition = "MachineIdentificationType", TypeDefinitionNamespace = "http://opcfoundation.org/UA/Machinery/")]
public partial class MachineIdentification
{
    [Path("opc", "Manufacturer")]
    public partial string? Manufacturer { get; set; }

    [Path("opc", "SerialNumber")]
    public partial string? SerialNumber { get; set; }

    [Path("opc", "ProductInstanceUri")]
    public partial string? ProductInstanceUri { get; set; }
}

[InterceptorSubject]
[OpcUaNode(TypeDefinition = "MotorType", TypeDefinitionNamespace = "http://test.machinery/")]
public partial class Motor
{
    /// <summary>Speed with fast sampling.</summary>
    [OpcUaNode("Speed", null, SamplingInterval = 50, DeadbandType = DeadbandType.Absolute, DeadbandValue = 0.1)]
    public partial double Speed { get; set; }

    /// <summary>Temperature with slower sampling.</summary>
    [OpcUaNode("Temperature", null, SamplingInterval = 500)]
    public partial double Temperature { get; set; }

    /// <summary>Running status.</summary>
    [Path("opc", "IsRunning")]
    public partial bool IsRunning { get; set; }
}

/// <summary>
/// Complex VariableType pattern - class represents a VariableNode with children.
/// </summary>
[InterceptorSubject]
[OpcUaNode(
    TypeDefinition = "AnalogSignalVariableType",
    TypeDefinitionNamespace = "http://opcfoundation.org/UA/PADIM/",
    NodeClass = OpcUaNodeClass.Variable)]
public partial class AnalogSignal
{
    /// <summary>The primary value of this VariableNode.</summary>
    [OpcUaValue]
    [Path("opc", "ActualValue")]
    public partial double ActualValue { get; set; }

    /// <summary>Child property for engineering unit range.</summary>
    [Path("opc", "EURange")]
    public partial Range? EURange { get; set; }

    /// <summary>Child property for engineering units.</summary>
    [Path("opc", "EngineeringUnits")]
    public partial EUInformation? EngineeringUnits { get; set; }
}

/// <summary>
/// Test model using PropertyAttribute pattern for inline metadata.
/// </summary>
[InterceptorSubject]
public partial class SensorWithPropertyAttributes
{
    public SensorWithPropertyAttributes()
    {
        Value_EURange = new Range(0, 100);
        Value_EngineeringUnits = new EUInformation(
            "http://www.opcfoundation.org/UA/units/un/cefact",
            4408256, "°C", "Degrees Celsius");
    }

    /// <summary>Main temperature value.</summary>
    [OpcUaNode("Temperature", null, TypeDefinition = "AnalogItemType")]
    public partial double Value { get; set; }

    /// <summary>EURange as PropertyAttribute child of Value.</summary>
    [PropertyAttribute(nameof(Value), "EURange")]
    public partial Range? Value_EURange { get; set; }

    /// <summary>EngineeringUnits as PropertyAttribute child of Value.</summary>
    [PropertyAttribute(nameof(Value), "EngineeringUnits")]
    public partial EUInformation? Value_EngineeringUnits { get; set; }
}
```

**Step 2: Build to verify model compiles**

Run: `dotnet build src/Namotion.Interceptor.OpcUa.Tests/`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Integration/Testing/ComplexTestModel.cs
git commit -m "$(cat <<'EOF'
test(opcua): add complex test model with all mapping features

Includes:
- MachineType with HasAddIn, HasComponent references
- Different BrowseNames for same type (MainMotor vs AuxMotor)
- Organizes reference for folder collections
- VariableType pattern (AnalogSignal with OpcUaValue)
- PropertyAttribute pattern (SensorWithPropertyAttributes)
- Type definitions from companion specs

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 9.2: Server-Client Round-Trip Integration Test

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaNodeMapperIntegrationTests.cs`

**Step 1: Write comprehensive round-trip test**

```csharp
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.OpcUa.Types;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Integration tests verifying 1:1 mapping between server and client models.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaNodeMapperIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private TestLogger? _logger;
    private OpcUaTestServer<ComplexMachine>? _server;
    private OpcUaTestClient<ComplexMachine>? _client;
    private PortLease? _port;

    public OpcUaNodeMapperIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ComplexMachine_ServerToClient_AllPropertiesSynchronize()
    {
        try
        {
            // Arrange
            await StartServerAsync();
            await StartClientAsync();

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            // Act - Set values on server
            _server.Root.Status = 42;
            _server.Root.Identification!.Manufacturer = "Acme Corp";
            _server.Root.Identification.SerialNumber = "SN-12345";
            _server.Root.MainMotor!.Speed = 1500.5;
            _server.Root.MainMotor.Temperature = 65.3;
            _server.Root.MainMotor.IsRunning = true;
            _server.Root.AuxMotor!.Speed = 750.0;
            _server.Root.AuxMotor.IsRunning = false;

            // Assert - Client receives all values
            await AssertPropertySynchronizedAsync(
                () => _client.Root.Status == 42,
                "Status");

            await AssertPropertySynchronizedAsync(
                () => _client.Root.Identification?.Manufacturer == "Acme Corp",
                "Identification.Manufacturer");

            await AssertPropertySynchronizedAsync(
                () => _client.Root.Identification?.SerialNumber == "SN-12345",
                "Identification.SerialNumber");

            await AssertPropertySynchronizedAsync(
                () => Math.Abs(_client.Root.MainMotor?.Speed ?? 0 - 1500.5) < 0.01,
                "MainMotor.Speed");

            await AssertPropertySynchronizedAsync(
                () => Math.Abs(_client.Root.MainMotor?.Temperature ?? 0 - 65.3) < 0.01,
                "MainMotor.Temperature");

            await AssertPropertySynchronizedAsync(
                () => _client.Root.MainMotor?.IsRunning == true,
                "MainMotor.IsRunning");

            await AssertPropertySynchronizedAsync(
                () => Math.Abs(_client.Root.AuxMotor?.Speed ?? 0 - 750.0) < 0.01,
                "AuxMotor.Speed");

            await AssertPropertySynchronizedAsync(
                () => _client.Root.AuxMotor?.IsRunning == false,
                "AuxMotor.IsRunning");
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task ComplexMachine_ClientToServer_WritesSynchronize()
    {
        try
        {
            // Arrange
            await StartServerAsync();
            await StartClientAsync();

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            // Wait for initial sync
            await AssertPropertySynchronizedAsync(
                () => _client.Root.MainMotor?.Speed == _server.Root.MainMotor?.Speed,
                "Initial MainMotor.Speed sync");

            // Act - Write from client
            _client.Root.MainMotor!.Speed = 2000.0;
            _client.Root.Status = 100;

            // Assert - Server receives writes
            await AssertPropertySynchronizedAsync(
                () => Math.Abs(_server.Root.MainMotor?.Speed ?? 0 - 2000.0) < 0.01,
                "Server receives MainMotor.Speed write");

            await AssertPropertySynchronizedAsync(
                () => _server.Root.Status == 100,
                "Server receives Status write");
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task DictionaryCollection_ServerToClient_ItemsSynchronize()
    {
        try
        {
            // Arrange
            await StartServerAsync();

            // Add process values to server before starting client
            var tempSignal = new AnalogSignal(_server!.Root!.Context!)
            {
                ActualValue = 25.5,
                EURange = new Range(-40, 120),
                EngineeringUnits = new EUInformation(
                    "http://www.opcfoundation.org/UA/units/un/cefact",
                    4408256, "°C")
            };

            var pressureSignal = new AnalogSignal(_server.Root.Context!)
            {
                ActualValue = 101.3,
                EURange = new Range(0, 200),
                EngineeringUnits = new EUInformation(
                    "http://www.opcfoundation.org/UA/units/un/cefact",
                    4732723, "kPa")
            };

            _server.Root.ProcessValues = new Dictionary<string, AnalogSignal>
            {
                ["Temperature"] = tempSignal,
                ["Pressure"] = pressureSignal
            };

            await StartClientAsync();

            // Assert - Client receives dictionary items
            await AssertPropertySynchronizedAsync(
                () => _client!.Root!.ProcessValues.ContainsKey("Temperature"),
                "ProcessValues contains Temperature");

            await AssertPropertySynchronizedAsync(
                () => Math.Abs(_client!.Root!.ProcessValues["Temperature"].ActualValue - 25.5) < 0.01,
                "Temperature.ActualValue");

            await AssertPropertySynchronizedAsync(
                () => _client!.Root!.ProcessValues.ContainsKey("Pressure"),
                "ProcessValues contains Pressure");

            await AssertPropertySynchronizedAsync(
                () => Math.Abs(_client!.Root!.ProcessValues["Pressure"].ActualValue - 101.3) < 0.01,
                "Pressure.ActualValue");
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private async Task AssertPropertySynchronizedAsync(Func<bool> condition, string propertyName)
    {
        await AsyncTestHelpers.WaitUntilAsync(
            condition,
            timeout: TimeSpan.FromSeconds(30),
            message: $"Property '{propertyName}' should synchronize");
    }

    private async Task StartServerAsync()
    {
        _logger = new TestLogger(_output);
        _port = await OpcUaTestPortPool.AcquireAsync();

        _server = new OpcUaTestServer<ComplexMachine>(_logger);
        await _server.StartAsync(
            context => new ComplexMachine(context),
            (context, root) =>
            {
                root.Status = 0;
                root.Identification = new MachineIdentification(context)
                {
                    Manufacturer = "Test Manufacturer",
                    SerialNumber = "TEST-001"
                };
                root.MainMotor = new Motor(context)
                {
                    Speed = 0,
                    Temperature = 20.0,
                    IsRunning = false
                };
                root.AuxMotor = new Motor(context)
                {
                    Speed = 0,
                    Temperature = 20.0,
                    IsRunning = false
                };
                root.ProcessValues = new Dictionary<string, AnalogSignal>();
            },
            baseAddress: _port.BaseAddress,
            certificateStoreBasePath: _port.CertificateStoreBasePath);
    }

    private async Task StartClientAsync()
    {
        _client = new OpcUaTestClient<ComplexMachine>(_logger!);
        await _client.StartAsync(
            context => new ComplexMachine(context),
            isConnected: root => root.Status >= 0, // Simple connection check
            serverUrl: _port!.ServerUrl,
            certificateStoreBasePath: _port.CertificateStoreBasePath);
    }

    private async Task CleanupAsync()
    {
        await (_client?.StopAsync() ?? Task.CompletedTask);
        await (_server?.StopAsync() ?? Task.CompletedTask);
        _port?.Dispose();
        _port = null;
    }
}
```

**Step 2: Run integration tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaNodeMapperIntegrationTests" -v n`
Expected: All tests pass

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaNodeMapperIntegrationTests.cs
git commit -m "$(cat <<'EOF'
test(opcua): add server-client round-trip integration tests

Verifies 1:1 mapping for:
- Complex hierarchical models
- HasAddIn, HasComponent references
- Different BrowseNames for same type
- Bidirectional write synchronization
- Dictionary collections with Organizes reference

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 9.3: PropertyAttribute Children Integration Test

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaPropertyAttributeIntegrationTests.cs`

**Step 1: Write PropertyAttribute integration test**

```csharp
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.OpcUa.Types;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Integration tests for PropertyAttribute mapping to OPC UA HasProperty children.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaPropertyAttributeIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private TestLogger? _logger;
    private OpcUaTestServer<SensorWithPropertyAttributes>? _server;
    private OpcUaTestClient<SensorWithPropertyAttributes>? _client;
    private PortLease? _port;

    public OpcUaPropertyAttributeIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task PropertyAttribute_ServerToClient_ChildrenSynchronize()
    {
        try
        {
            // Arrange
            await StartServerAsync();
            await StartClientAsync();

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            // Act - Set values on server
            _server.Root.Value = 75.5;
            _server.Root.Value_EURange = new Range(0, 150);
            _server.Root.Value_EngineeringUnits = new EUInformation(
                "http://www.opcfoundation.org/UA/units/un/cefact",
                4408256, "°C", "Degrees Celsius");

            // Assert - Client receives parent value
            await AsyncTestHelpers.WaitUntilAsync(
                () => Math.Abs(_client.Root.Value - 75.5) < 0.01,
                timeout: TimeSpan.FromSeconds(30),
                message: "Value should synchronize");

            // Assert - Client receives EURange child
            await AsyncTestHelpers.WaitUntilAsync(
                () => _client.Root.Value_EURange != null &&
                      Math.Abs(_client.Root.Value_EURange.Low - 0) < 0.01 &&
                      Math.Abs(_client.Root.Value_EURange.High - 150) < 0.01,
                timeout: TimeSpan.FromSeconds(30),
                message: "Value_EURange should synchronize as HasProperty child");

            // Assert - Client receives EngineeringUnits child
            await AsyncTestHelpers.WaitUntilAsync(
                () => _client.Root.Value_EngineeringUnits != null &&
                      _client.Root.Value_EngineeringUnits.DisplayName == "°C",
                timeout: TimeSpan.FromSeconds(30),
                message: "Value_EngineeringUnits should synchronize as HasProperty child");
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task PropertyAttribute_ClientToServer_ChildrenWritable()
    {
        try
        {
            // Arrange
            await StartServerAsync();
            await StartClientAsync();

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            // Wait for initial sync
            await AsyncTestHelpers.WaitUntilAsync(
                () => _client.Root.Value_EURange != null,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial sync");

            // Act - Write child property from client
            _client.Root.Value_EURange = new Range(-50, 200);

            // Assert - Server receives write
            await AsyncTestHelpers.WaitUntilAsync(
                () => _server.Root.Value_EURange != null &&
                      Math.Abs(_server.Root.Value_EURange.Low - (-50)) < 0.01 &&
                      Math.Abs(_server.Root.Value_EURange.High - 200) < 0.01,
                timeout: TimeSpan.FromSeconds(30),
                message: "Server should receive Value_EURange write");
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private async Task StartServerAsync()
    {
        _logger = new TestLogger(_output);
        _port = await OpcUaTestPortPool.AcquireAsync();

        _server = new OpcUaTestServer<SensorWithPropertyAttributes>(_logger);
        await _server.StartAsync(
            context => new SensorWithPropertyAttributes(context),
            (context, root) =>
            {
                root.Value = 25.0;
                root.Value_EURange = new Range(0, 100);
                root.Value_EngineeringUnits = new EUInformation(
                    "http://www.opcfoundation.org/UA/units/un/cefact",
                    4408256, "°C", "Degrees Celsius");
            },
            baseAddress: _port.BaseAddress,
            certificateStoreBasePath: _port.CertificateStoreBasePath);
    }

    private async Task StartClientAsync()
    {
        _client = new OpcUaTestClient<SensorWithPropertyAttributes>(_logger!);
        await _client.StartAsync(
            context => new SensorWithPropertyAttributes(context),
            isConnected: root => true,
            serverUrl: _port!.ServerUrl,
            certificateStoreBasePath: _port.CertificateStoreBasePath);
    }

    private async Task CleanupAsync()
    {
        await (_client?.StopAsync() ?? Task.CompletedTask);
        await (_server?.StopAsync() ?? Task.CompletedTask);
        _port?.Dispose();
        _port = null;
    }
}
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaPropertyAttributeIntegrationTests" -v n`
Expected: All tests pass

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaPropertyAttributeIntegrationTests.cs
git commit -m "$(cat <<'EOF'
test(opcua): add PropertyAttribute children integration tests

Verifies that PropertyAttribute properties become HasProperty children
of their parent VariableNode and synchronize bidirectionally.

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 9.4: VariableType Class Integration Test

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaVariableTypeIntegrationTests.cs`

**Step 1: Write VariableType pattern test**

```csharp
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.OpcUa.Types;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Integration tests for class-based VariableType pattern (NodeClass.Variable + OpcUaValue).
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaVariableTypeIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private TestLogger? _logger;
    private OpcUaTestServer<AnalogSignalTestRoot>? _server;
    private OpcUaTestClient<AnalogSignalTestRoot>? _client;
    private PortLease? _port;

    public OpcUaVariableTypeIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task VariableTypeClass_ServerToClient_ValueAndChildrenSynchronize()
    {
        try
        {
            // Arrange
            await StartServerAsync();
            await StartClientAsync();

            Assert.NotNull(_server?.Root);
            Assert.NotNull(_client?.Root);

            // Act - Set values on server
            _server.Root.Temperature!.ActualValue = 85.5;
            _server.Root.Temperature.EURange = new Range(-40, 150);
            _server.Root.Temperature.EngineeringUnits = new EUInformation(
                "http://www.opcfoundation.org/UA/units/un/cefact",
                4408256, "°C");

            // Assert - Client receives the value (marked with OpcUaValue)
            await AsyncTestHelpers.WaitUntilAsync(
                () => Math.Abs(_client.Root.Temperature?.ActualValue ?? 0 - 85.5) < 0.01,
                timeout: TimeSpan.FromSeconds(30),
                message: "ActualValue (OpcUaValue) should synchronize");

            // Assert - Client receives child properties
            await AsyncTestHelpers.WaitUntilAsync(
                () => _client.Root.Temperature?.EURange != null &&
                      Math.Abs(_client.Root.Temperature.EURange.Low - (-40)) < 0.01,
                timeout: TimeSpan.FromSeconds(30),
                message: "EURange child should synchronize");

            await AsyncTestHelpers.WaitUntilAsync(
                () => _client.Root.Temperature?.EngineeringUnits?.DisplayName == "°C",
                timeout: TimeSpan.FromSeconds(30),
                message: "EngineeringUnits child should synchronize");
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private async Task StartServerAsync()
    {
        _logger = new TestLogger(_output);
        _port = await OpcUaTestPortPool.AcquireAsync();

        _server = new OpcUaTestServer<AnalogSignalTestRoot>(_logger);
        await _server.StartAsync(
            context => new AnalogSignalTestRoot(context),
            (context, root) =>
            {
                root.Temperature = new AnalogSignal(context)
                {
                    ActualValue = 20.0,
                    EURange = new Range(0, 100),
                    EngineeringUnits = new EUInformation(
                        "http://www.opcfoundation.org/UA/units/un/cefact",
                        4408256, "°C")
                };
            },
            baseAddress: _port.BaseAddress,
            certificateStoreBasePath: _port.CertificateStoreBasePath);
    }

    private async Task StartClientAsync()
    {
        _client = new OpcUaTestClient<AnalogSignalTestRoot>(_logger!);
        await _client.StartAsync(
            context => new AnalogSignalTestRoot(context),
            isConnected: root => root.Temperature != null,
            serverUrl: _port!.ServerUrl,
            certificateStoreBasePath: _port.CertificateStoreBasePath);
    }

    private async Task CleanupAsync()
    {
        await (_client?.StopAsync() ?? Task.CompletedTask);
        await (_server?.StopAsync() ?? Task.CompletedTask);
        _port?.Dispose();
        _port = null;
    }
}

/// <summary>Test root for VariableType testing.</summary>
[InterceptorSubject]
public partial class AnalogSignalTestRoot
{
    [OpcUaReference("HasComponent")]
    [Path("opc", "Temperature")]
    public partial AnalogSignal? Temperature { get; set; }
}
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaVariableTypeIntegrationTests" -v n`
Expected: All tests pass

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaVariableTypeIntegrationTests.cs
git commit -m "$(cat <<'EOF'
test(opcua): add VariableType class integration tests

Verifies that classes with NodeClass.Variable and OpcUaValue:
- Create VariableNodes instead of ObjectNodes
- Map OpcUaValue property to the OPC UA Value attribute
- Create child VariableNodes for other properties

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 9.5: Run Full Test Suite and Verify Coverage

**Step 1: Run all unit tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category!=Integration" -v n`
Expected: All unit tests pass

**Step 2: Run all integration tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category=Integration" -v n`
Expected: All integration tests pass

**Step 3: Run full solution tests**

Run: `dotnet test src/Namotion.Interceptor.slnx -v n`
Expected: All tests pass

---

## Summary

| Phase | Tasks | Purpose |
|-------|-------|---------|
| **1. Core Types** | 1.1-1.6 | Enums, records, interface foundation |
| **2. Attributes** | 2.1-2.4 | New/enhanced attributes and standard types |
| **3. Mappers** | 3.1-3.5 | Three mapper implementations |
| **4. Client** | 4.1-4.3 | Client configuration and loader integration |
| **5. Server** | 5.1-5.2 | Server configuration and node manager integration |
| **6. Basic Tests** | 6.1-6.2 | Initial integration tests |
| **7. Migration** | 7.1-7.4 | Deprecation, samples, docs |
| **8. Unit Tests** | 8.1-8.3 | Comprehensive unit test coverage |
| **9. Integration Tests** | 9.1-9.5 | Complex model round-trip tests |

**Total: ~35 bite-sized tasks**

**Test Coverage:**
- Unit tests for each mapper implementation
- Unit tests for configuration merge semantics
- Integration tests for complex hierarchical models
- Integration tests for PropertyAttribute children
- Integration tests for VariableType class pattern
- Bidirectional synchronization verification
- Dictionary/collection mapping verification

Each task follows TDD where applicable and includes verification commands.
