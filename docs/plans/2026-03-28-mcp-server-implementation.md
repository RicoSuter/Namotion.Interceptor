# MCP Server Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement the core `Namotion.Interceptor.Mcp` package and HomeBlaze MCP extensions, enabling AI agents to browse, query, and interact with the subject registry via MCP.

**Architecture:** A core package provides 4 tools (`query`, `get_property`, `set_property`, `list_types`) with extension points (`IMcpSubjectEnricher`, `IMcpTypeProvider`, `IMcpToolProvider`). HomeBlaze provides implementations: subject enrichment (`$title`, `$icon`, `$type`), type discovery from `SubjectTypeRegistry`, and method tools (`list_methods`, `invoke_method`). Tools are transport-agnostic `McpToolDescriptor` instances (metadata + plain function), wrapped as MCP tools or `AIFunction` by consumers.

**Tech Stack:** .NET 9.0, ModelContextProtocol SDK, System.Text.Json, xUnit

**Design docs:**
- Core: `docs/plans/mcp-server.md`
- HomeBlaze extensions: `src/HomeBlaze/HomeBlaze/Data/Docs/plans/mcp-extensions.md`

---

### Task 1: Create Namotion.Interceptor.Mcp project scaffold

**Files:**
- Create: `src/Namotion.Interceptor.Mcp/Namotion.Interceptor.Mcp.csproj`
- Modify: `src/Namotion.Interceptor.slnx`

**Step 1: Create project directory and csproj**

```xml
<!-- src/Namotion.Interceptor.Mcp/Namotion.Interceptor.Mcp.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net9.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
    </PropertyGroup>

    <ItemGroup>
        <InternalsVisibleTo Include="Namotion.Interceptor.Mcp.Tests" />
    </ItemGroup>

    <ItemGroup>
        <PackageReference Include="ModelContextProtocol" Version="0.1.0-preview.12" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\Namotion.Interceptor.Registry\Namotion.Interceptor.Registry.csproj" />
    </ItemGroup>

</Project>
```

Note: Check latest `ModelContextProtocol` NuGet version at implementation time. The version above is a placeholder.

**Step 2: Add project to solution**

Add to `src/Namotion.Interceptor.slnx` in a new section after the Extensions folder:

```xml
<Folder Name="/AI/">
    <Project Path="Namotion.Interceptor.Mcp/Namotion.Interceptor.Mcp.csproj" />
</Folder>
```

**Step 3: Verify build**

Run: `dotnet build src/Namotion.Interceptor.Mcp/Namotion.Interceptor.Mcp.csproj`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Mcp/ src/Namotion.Interceptor.slnx
git commit -m "feat: scaffold Namotion.Interceptor.Mcp project"
```

---

### Task 2: Create test project scaffold

**Files:**
- Create: `src/Namotion.Interceptor.Mcp.Tests/Namotion.Interceptor.Mcp.Tests.csproj`
- Modify: `src/Namotion.Interceptor.slnx`

**Step 1: Create test project**

```xml
<!-- src/Namotion.Interceptor.Mcp.Tests/Namotion.Interceptor.Mcp.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net9.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <IsPackable>false</IsPackable>
        <IsTestProject>true</IsTestProject>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.0" />
        <PackageReference Include="xunit" Version="2.9.3" />
        <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
            <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
            <PrivateAssets>all</PrivateAssets>
        </PackageReference>
        <PackageReference Include="coverlet.collector" Version="6.0.4">
            <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
            <PrivateAssets>all</PrivateAssets>
        </PackageReference>
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\Namotion.Interceptor.Mcp\Namotion.Interceptor.Mcp.csproj" />
        <ProjectReference Include="..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
        <ProjectReference Include="..\Namotion.Interceptor.Testing\Namotion.Interceptor.Testing.csproj" />
    </ItemGroup>

</Project>
```

**Step 2: Add to solution**

Add to `src/Namotion.Interceptor.slnx` in the Tests folder:

```xml
<Project Path="Namotion.Interceptor.Mcp.Tests/Namotion.Interceptor.Mcp.Tests.csproj" />
```

**Step 3: Verify build**

Run: `dotnet build src/Namotion.Interceptor.Mcp.Tests/Namotion.Interceptor.Mcp.Tests.csproj`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Mcp.Tests/ src/Namotion.Interceptor.slnx
git commit -m "feat: scaffold Namotion.Interceptor.Mcp.Tests project"
```

---

### Task 3: Implement abstraction interfaces

**Files:**
- Create: `src/Namotion.Interceptor.Mcp/Abstractions/IMcpSubjectEnricher.cs`
- Create: `src/Namotion.Interceptor.Mcp/Abstractions/IMcpTypeProvider.cs`
- Create: `src/Namotion.Interceptor.Mcp/Abstractions/IMcpToolProvider.cs`
- Create: `src/Namotion.Interceptor.Mcp/Abstractions/McpTypeInfo.cs`
- Create: `src/Namotion.Interceptor.Mcp/McpToolDescriptor.cs`

**Step 1: Create IMcpSubjectEnricher**

```csharp
// src/Namotion.Interceptor.Mcp/Abstractions/IMcpSubjectEnricher.cs
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Mcp.Abstractions;

/// <summary>
/// Adds subject-level metadata fields (prefixed with $) to query responses.
/// </summary>
public interface IMcpSubjectEnricher
{
    void EnrichSubject(RegisteredSubject subject, IDictionary<string, object?> metadata);
}
```

**Step 2: Create IMcpTypeProvider and McpTypeInfo**

```csharp
// src/Namotion.Interceptor.Mcp/Abstractions/McpTypeInfo.cs
namespace Namotion.Interceptor.Mcp.Abstractions;

/// <summary>
/// Describes a type available in the subject registry.
/// </summary>
/// <param name="Name">Full type name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="IsInterface">True for abstraction interfaces, false for concrete types.</param>
public record McpTypeInfo(string Name, string? Description, bool IsInterface);
```

```csharp
// src/Namotion.Interceptor.Mcp/Abstractions/IMcpTypeProvider.cs
namespace Namotion.Interceptor.Mcp.Abstractions;

/// <summary>
/// Provides type information for the list_types tool.
/// </summary>
public interface IMcpTypeProvider
{
    IEnumerable<McpTypeInfo> GetTypes();
}
```

**Step 3: Create McpToolDescriptor and IMcpToolProvider**

```csharp
// src/Namotion.Interceptor.Mcp/McpToolDescriptor.cs
using System.Text.Json;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// Transport-agnostic tool descriptor. Consumers wrap as MCP tools or AIFunction.
/// </summary>
public class McpToolDescriptor
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement InputSchema { get; init; }
    public required Func<JsonElement, CancellationToken, Task<object?>> Handler { get; init; }
}
```

```csharp
// src/Namotion.Interceptor.Mcp/Abstractions/IMcpToolProvider.cs
namespace Namotion.Interceptor.Mcp.Abstractions;

/// <summary>
/// Provides additional tools beyond the 4 core tools.
/// </summary>
public interface IMcpToolProvider
{
    IEnumerable<McpToolDescriptor> GetTools();
}
```

**Step 4: Verify build**

Run: `dotnet build src/Namotion.Interceptor.Mcp/Namotion.Interceptor.Mcp.csproj`
Expected: Build succeeds

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Mcp/
git commit -m "feat: add MCP abstraction interfaces and McpToolDescriptor"
```

---

### Task 4: Implement McpServerConfiguration

**Files:**
- Create: `src/Namotion.Interceptor.Mcp/McpServerConfiguration.cs`

**Step 1: Create configuration class**

```csharp
// src/Namotion.Interceptor.Mcp/McpServerConfiguration.cs
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// Configuration for the MCP subject server.
/// </summary>
public class McpServerConfiguration
{
    /// <summary>
    /// Property filtering and path resolution. Reuses existing IPathProvider from Registry.
    /// </summary>
    public required IPathProvider PathProvider { get; init; }

    /// <summary>
    /// Subject-level JSON enrichment for query responses (e.g., $title, $icon, $type).
    /// </summary>
    public IList<IMcpSubjectEnricher> SubjectEnrichers { get; init; } = [];

    /// <summary>
    /// Type discovery for the list_types tool.
    /// </summary>
    public IList<IMcpTypeProvider> TypeProviders { get; init; } = [];

    /// <summary>
    /// Additional tools beyond the 4 core tools (e.g., list_methods, invoke_method).
    /// </summary>
    public IList<IMcpToolProvider> ToolProviders { get; init; } = [];

    /// <summary>
    /// Maximum subject tree traversal depth (default: 10).
    /// </summary>
    public int MaxDepth { get; init; } = 10;

    /// <summary>
    /// Maximum subjects in a single query response (default: 100).
    /// </summary>
    public int MaxSubjectsPerResponse { get; init; } = 100;

    /// <summary>
    /// When true, set_property is blocked and invoke_method only allows Query methods.
    /// </summary>
    public bool IsReadOnly { get; init; } = true;
}
```

**Step 2: Verify build**

Run: `dotnet build src/Namotion.Interceptor.Mcp/Namotion.Interceptor.Mcp.csproj`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Mcp/McpServerConfiguration.cs
git commit -m "feat: add McpServerConfiguration"
```

---

### Task 5: Add SubjectAbstractionsAssemblyAttribute to core

This attribute is needed by `SubjectAbstractionsAssemblyTypeProvider` and is also referenced by the dynamic subject proxying plan. It marks assemblies containing subject abstraction interfaces.

**Files:**
- Create: `src/Namotion.Interceptor/Attributes/SubjectAbstractionsAssemblyAttribute.cs`

**Step 1: Write the failing test**

```csharp
// src/Namotion.Interceptor.Mcp.Tests/SubjectAbstractionsAssemblyTypeProviderTests.cs
using Namotion.Interceptor.Mcp.Abstractions;

namespace Namotion.Interceptor.Mcp.Tests;

// Test assembly marker — will be used after the type provider is implemented
[assembly: Namotion.Interceptor.SubjectAbstractionsAssembly]

public class SubjectAbstractionsAssemblyAttributeTests
{
    [Fact]
    public void Attribute_can_be_applied_to_assembly()
    {
        var attribute = typeof(SubjectAbstractionsAssemblyAttributeTests).Assembly
            .GetCustomAttributes(typeof(SubjectAbstractionsAssemblyAttribute), false);

        Assert.Single(attribute);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests --filter "FullyQualifiedName~SubjectAbstractionsAssemblyAttributeTests" -v n`
Expected: FAIL — `SubjectAbstractionsAssemblyAttribute` does not exist

**Step 3: Implement**

```csharp
// src/Namotion.Interceptor/Attributes/SubjectAbstractionsAssemblyAttribute.cs
namespace Namotion.Interceptor;

/// <summary>
/// Marks an assembly as containing subject abstraction interfaces eligible for
/// MCP type discovery and dynamic subject proxy interface resolution.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public class SubjectAbstractionsAssemblyAttribute : Attribute;
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests --filter "FullyQualifiedName~SubjectAbstractionsAssemblyAttributeTests" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor/Attributes/SubjectAbstractionsAssemblyAttribute.cs src/Namotion.Interceptor.Mcp.Tests/
git commit -m "feat: add SubjectAbstractionsAssemblyAttribute to core"
```

---

### Task 6: Implement SubjectAbstractionsAssemblyTypeProvider

**Files:**
- Create: `src/Namotion.Interceptor.Mcp/Implementations/SubjectAbstractionsAssemblyTypeProvider.cs`
- Modify: `src/Namotion.Interceptor.Mcp.Tests/SubjectAbstractionsAssemblyTypeProviderTests.cs`

**Step 1: Write the failing test**

Add to the existing test file:

```csharp
// src/Namotion.Interceptor.Mcp.Tests/SubjectAbstractionsAssemblyTypeProviderTests.cs
using Namotion.Interceptor.Mcp.Implementations;

public class SubjectAbstractionsAssemblyTypeProviderTests
{
    // ... existing attribute test ...

    [Fact]
    public void GetTypes_returns_interfaces_from_marked_assemblies()
    {
        var provider = new SubjectAbstractionsAssemblyTypeProvider();
        var types = provider.GetTypes().ToList();

        // The test assembly is marked with [SubjectAbstractionsAssembly]
        // and defines ITestSensor below — it should appear in results
        Assert.Contains(types, t => t.Name == typeof(ITestSensor).FullName);
        Assert.All(types, t => Assert.True(t.IsInterface));
    }

    [Fact]
    public void GetTypes_excludes_non_interface_types()
    {
        var provider = new SubjectAbstractionsAssemblyTypeProvider();
        var types = provider.GetTypes().ToList();

        // Concrete classes should not appear
        Assert.DoesNotContain(types, t => t.Name == typeof(SubjectAbstractionsAssemblyTypeProviderTests).FullName);
    }
}

// Test interface in the marked assembly
public interface ITestSensor
{
    decimal Temperature { get; }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests --filter "FullyQualifiedName~SubjectAbstractionsAssemblyTypeProviderTests" -v n`
Expected: FAIL — `SubjectAbstractionsAssemblyTypeProvider` does not exist

**Step 3: Implement**

```csharp
// src/Namotion.Interceptor.Mcp/Implementations/SubjectAbstractionsAssemblyTypeProvider.cs
using System.Reflection;
using Namotion.Interceptor.Mcp.Abstractions;

namespace Namotion.Interceptor.Mcp.Implementations;

/// <summary>
/// Returns interfaces from assemblies marked with [SubjectAbstractionsAssembly].
/// </summary>
public class SubjectAbstractionsAssemblyTypeProvider : IMcpTypeProvider
{
    public IEnumerable<McpTypeInfo> GetTypes()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetCustomAttribute<SubjectAbstractionsAssemblyAttribute>() is null)
            {
                continue;
            }

            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsInterface)
                {
                    yield return new McpTypeInfo(type.FullName!, null, IsInterface: true);
                }
            }
        }
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests --filter "FullyQualifiedName~SubjectAbstractionsAssemblyTypeProviderTests" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Mcp/ src/Namotion.Interceptor.Mcp.Tests/
git commit -m "feat: implement SubjectAbstractionsAssemblyTypeProvider"
```

---

### Task 7: Implement core tool logic — QueryTool

This is the most complex tool. It traverses the subject graph, applies path provider filtering, includes properties/attributes, runs subject enrichers, and respects depth/truncation limits.

**Files:**
- Create: `src/Namotion.Interceptor.Mcp/Tools/McpToolFactory.cs`
- Create: `src/Namotion.Interceptor.Mcp.Tests/Tools/QueryToolTests.cs`

**Step 1: Create a test subject for all tool tests**

```csharp
// src/Namotion.Interceptor.Mcp.Tests/TestSubjects.cs
using Namotion.Interceptor;

namespace Namotion.Interceptor.Mcp.Tests;

[InterceptorSubject]
public partial class TestRoom
{
    public partial string Name { get; set; }
    public partial decimal Temperature { get; set; }
    public partial TestDevice? Device { get; set; }
}

[InterceptorSubject]
public partial class TestDevice
{
    public partial string DeviceName { get; set; }
    public partial bool IsOn { get; set; }
}
```

**Step 2: Write the failing test for query tool**

```csharp
// src/Namotion.Interceptor.Mcp.Tests/Tools/QueryToolTests.cs
using System.Text.Json;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class QueryToolTests
{
    [Fact]
    public async Task Query_returns_subject_tree_with_children()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration
        {
            PathProvider = new DefaultPathProvider()
        };
        var factory = new McpToolFactory(room, config);
        var tools = factory.CreateTools();
        var queryTool = tools.First(t => t.Name == "query");

        // Act
        var input = JsonSerializer.SerializeToElement(new { depth = 1, includeProperties = true });
        var result = await queryTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.NotNull(json.GetProperty("subjects"));
        Assert.True(json.GetProperty("subjectCount").GetInt32() > 0);
    }

    [Fact]
    public async Task Query_depth_zero_returns_no_children()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var room = new TestRoom(context) { Name = "Living Room", Temperature = 21.5m };
        room.Device = new TestDevice(context) { DeviceName = "Light", IsOn = true };

        var config = new McpServerConfiguration
        {
            PathProvider = new DefaultPathProvider()
        };
        var factory = new McpToolFactory(room, config);
        var tools = factory.CreateTools();
        var queryTool = tools.First(t => t.Name == "query");

        // Act
        var input = JsonSerializer.SerializeToElement(new { depth = 0, includeProperties = true });
        var result = await queryTool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — depth=0 means subject properties only, no child subjects expanded
        Assert.Equal(0, json.GetProperty("subjectCount").GetInt32());
    }
}
```

**Step 3: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests --filter "FullyQualifiedName~QueryToolTests" -v n`
Expected: FAIL — `McpToolFactory` does not exist

**Step 4: Implement McpToolFactory with query tool**

The `McpToolFactory` creates all core tools and merges with tool providers. This is the central class. Start with the query tool only:

```csharp
// src/Namotion.Interceptor.Mcp/Tools/McpToolFactory.cs
using System.Text.Json;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Creates McpToolDescriptor instances for all core and extension tools.
/// </summary>
public class McpToolFactory
{
    private readonly IInterceptorSubject _rootSubject;
    private readonly McpServerConfiguration _configuration;

    public McpToolFactory(IInterceptorSubject rootSubject, McpServerConfiguration configuration)
    {
        _rootSubject = rootSubject;
        _configuration = configuration;
    }

    public IReadOnlyList<McpToolDescriptor> CreateTools()
    {
        var tools = new List<McpToolDescriptor>
        {
            CreateQueryTool(),
            CreateGetPropertyTool(),
            CreateSetPropertyTool(),
            CreateListTypesTool()
        };

        foreach (var provider in _configuration.ToolProviders)
        {
            tools.AddRange(provider.GetTools());
        }

        return tools;
    }

    private McpToolDescriptor CreateQueryTool()
    {
        return new McpToolDescriptor
        {
            Name = "query",
            Description = "Browse the subject tree. Paths use dot notation (e.g., root.livingRoom.temperature). " +
                          "Collections use brackets (e.g., sensors[0], devices[myDevice]).",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Starting path (default: root)" },
                    depth = new { type = "integer", description = "Max depth (default: 1)" },
                    includeProperties = new { type = "boolean", description = "Include property values (default: false)" },
                    includeAttributes = new { type = "boolean", description = "Include registry attributes on properties (default: false)" },
                    types = new { type = "array", items = new { type = "string" }, description = "Filter subjects by type/interface full names" }
                }
            }),
            Handler = HandleQueryAsync
        };
    }

    private async Task<object?> HandleQueryAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var pathProvider = _configuration.PathProvider as PathProviderBase
            ?? throw new InvalidOperationException("PathProvider must extend PathProviderBase for path resolution.");

        var rootRegistered = _rootSubject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Root subject is not registered.");

        var path = input.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
        var depth = input.TryGetProperty("depth", out var depthElement) ? depthElement.GetInt32() : 1;
        var includeProperties = input.TryGetProperty("includeProperties", out var propsElement) && propsElement.GetBoolean();
        var includeAttributes = input.TryGetProperty("includeAttributes", out var attrsElement) && attrsElement.GetBoolean();

        string[]? typeFilter = null;
        if (input.TryGetProperty("types", out var typesElement))
        {
            typeFilter = typesElement.EnumerateArray().Select(e => e.GetString()!).ToArray();
        }

        depth = Math.Min(depth, _configuration.MaxDepth);

        // Resolve starting subject
        RegisteredSubject startSubject;
        if (string.IsNullOrEmpty(path))
        {
            startSubject = rootRegistered;
        }
        else
        {
            var property = pathProvider.TryGetPropertyFromPath(rootRegistered, path);
            if (property is null)
            {
                return new { error = $"Path not found: {path}" };
            }

            var childSubject = property.GetValue() as IInterceptorSubject;
            startSubject = childSubject?.TryGetRegisteredSubject()
                ?? throw new InvalidOperationException($"Path '{path}' does not resolve to a subject.");
        }

        var subjectCount = 0;
        var truncated = false;
        var subjects = BuildSubjectTree(startSubject, pathProvider, depth, includeProperties,
            includeAttributes, typeFilter, ref subjectCount, ref truncated);

        return new
        {
            path = path ?? "",
            subjects,
            truncated,
            subjectCount
        };
    }

    private Dictionary<string, object?> BuildSubjectTree(
        RegisteredSubject subject,
        PathProviderBase pathProvider,
        int remainingDepth,
        bool includeProperties,
        bool includeAttributes,
        string[]? typeFilter,
        ref int subjectCount,
        ref bool truncated)
    {
        var result = new Dictionary<string, object?>();

        foreach (var property in subject.Properties)
        {
            if (property.IsAttribute || !pathProvider.IsPropertyIncluded(property))
            {
                continue;
            }

            var segment = pathProvider.TryGetPropertySegment(property) ?? property.BrowseName;

            if (property.CanContainSubjects && remainingDepth > 0)
            {
                if (property.IsSubjectReference)
                {
                    var childSubject = (property.GetValue() as IInterceptorSubject)?.TryGetRegisteredSubject();
                    if (childSubject is not null && !ShouldFilterOut(childSubject, typeFilter))
                    {
                        if (subjectCount >= _configuration.MaxSubjectsPerResponse)
                        {
                            truncated = true;
                            continue;
                        }

                        subjectCount++;
                        result[segment] = BuildSubjectNode(childSubject, pathProvider,
                            remainingDepth - 1, includeProperties, includeAttributes, typeFilter,
                            ref subjectCount, ref truncated);
                    }
                }
                else if (property.IsSubjectDictionary || property.IsSubjectCollection)
                {
                    var children = new Dictionary<string, object?>();
                    foreach (var child in property.Children)
                    {
                        var childRegistered = child.Subject.TryGetRegisteredSubject();
                        if (childRegistered is null || ShouldFilterOut(childRegistered, typeFilter))
                        {
                            continue;
                        }

                        if (subjectCount >= _configuration.MaxSubjectsPerResponse)
                        {
                            truncated = true;
                            break;
                        }

                        subjectCount++;
                        var key = child.Index?.ToString() ?? child.Subject.GetHashCode().ToString();
                        children[key] = BuildSubjectNode(childRegistered, pathProvider,
                            remainingDepth - 1, includeProperties, includeAttributes, typeFilter,
                            ref subjectCount, ref truncated);
                    }

                    if (children.Count > 0)
                    {
                        result[segment] = children;
                    }
                }
            }
            else if (includeProperties && !property.CanContainSubjects)
            {
                result[segment] = BuildPropertyValue(property, includeAttributes);
            }
        }

        return result;
    }

    private Dictionary<string, object?> BuildSubjectNode(
        RegisteredSubject subject,
        PathProviderBase pathProvider,
        int remainingDepth,
        bool includeProperties,
        bool includeAttributes,
        string[]? typeFilter,
        ref int subjectCount,
        ref bool truncated)
    {
        var node = new Dictionary<string, object?>();

        // Subject-level metadata from enrichers
        foreach (var enricher in _configuration.SubjectEnrichers)
        {
            enricher.EnrichSubject(subject, node);
        }

        // $hasChildren
        var hasChildren = subject.Properties.Any(p =>
            !p.IsAttribute && pathProvider.IsPropertyIncluded(p) && p.CanContainSubjects);
        node["$hasChildren"] = hasChildren;

        // Properties and child subjects
        var tree = BuildSubjectTree(subject, pathProvider, remainingDepth,
            includeProperties, includeAttributes, typeFilter, ref subjectCount, ref truncated);

        foreach (var kvp in tree)
        {
            node[kvp.Key] = kvp.Value;
        }

        return node;
    }

    private static object? BuildPropertyValue(RegisteredSubjectProperty property, bool includeAttributes)
    {
        if (!includeAttributes)
        {
            return new { value = property.GetValue() };
        }

        var attributes = new Dictionary<string, object?>();
        foreach (var attribute in property.Attributes)
        {
            attributes[attribute.BrowseName] = attribute.GetValue();
        }

        var result = new Dictionary<string, object?>
        {
            ["value"] = property.GetValue()
        };

        if (attributes.Count > 0)
        {
            result["attributes"] = attributes;
        }

        return result;
    }

    private static bool ShouldFilterOut(RegisteredSubject subject, string[]? typeFilter)
    {
        if (typeFilter is null || typeFilter.Length == 0)
        {
            return false;
        }

        var subjectType = subject.Subject.GetType();
        foreach (var filter in typeFilter)
        {
            if (subjectType.FullName == filter || subjectType.Name == filter)
            {
                return false;
            }

            if (subjectType.GetInterfaces().Any(i => i.FullName == filter || i.Name == filter))
            {
                return false;
            }
        }

        return true;
    }

    // Stub implementations for remaining tools — implemented in subsequent tasks
    private McpToolDescriptor CreateGetPropertyTool() => new()
    {
        Name = "get_property",
        Description = "Read a property value by path.",
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } }),
        Handler = HandleGetPropertyAsync
    };

    private McpToolDescriptor CreateSetPropertyTool() => new()
    {
        Name = "set_property",
        Description = "Write a property value by path.",
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { path = new { type = "string" }, value = new { } }, required = new[] { "path", "value" } }),
        Handler = HandleSetPropertyAsync
    };

    private McpToolDescriptor CreateListTypesTool() => new()
    {
        Name = "list_types",
        Description = "List available types (interfaces and concrete types).",
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
        Handler = HandleListTypesAsync
    };

    private Task<object?> HandleGetPropertyAsync(JsonElement input, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    private Task<object?> HandleSetPropertyAsync(JsonElement input, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    private Task<object?> HandleListTypesAsync(JsonElement input, CancellationToken cancellationToken)
        => throw new NotImplementedException();
}
```

**Step 5: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests --filter "FullyQualifiedName~QueryToolTests" -v n`
Expected: PASS

**Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Mcp/ src/Namotion.Interceptor.Mcp.Tests/
git commit -m "feat: implement query tool with subject tree traversal"
```

---

### Task 8: Implement get_property and set_property tools

**Files:**
- Modify: `src/Namotion.Interceptor.Mcp/Tools/McpToolFactory.cs`
- Create: `src/Namotion.Interceptor.Mcp.Tests/Tools/GetPropertyToolTests.cs`
- Create: `src/Namotion.Interceptor.Mcp.Tests/Tools/SetPropertyToolTests.cs`

**Step 1: Write failing tests**

```csharp
// src/Namotion.Interceptor.Mcp.Tests/Tools/GetPropertyToolTests.cs
using System.Text.Json;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class GetPropertyToolTests
{
    [Fact]
    public async Task GetProperty_returns_value_and_type()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        var factory = new McpToolFactory(room, new McpServerConfiguration { PathProvider = new DefaultPathProvider() });
        var tool = factory.CreateTools().First(t => t.Name == "get_property");

        var input = JsonSerializer.SerializeToElement(new { path = "Temperature" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.Equal(21.5m, json.GetProperty("value").GetDecimal());
        Assert.Equal("Decimal", json.GetProperty("type").GetString());
    }

    [Fact]
    public async Task GetProperty_returns_error_for_invalid_path()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        var factory = new McpToolFactory(room, new McpServerConfiguration { PathProvider = new DefaultPathProvider() });
        var tool = factory.CreateTools().First(t => t.Name == "get_property");

        var input = JsonSerializer.SerializeToElement(new { path = "NonExistent" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.True(json.TryGetProperty("error", out _));
    }
}
```

```csharp
// src/Namotion.Interceptor.Mcp.Tests/Tools/SetPropertyToolTests.cs
using System.Text.Json;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class SetPropertyToolTests
{
    [Fact]
    public async Task SetProperty_updates_value()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        var factory = new McpToolFactory(room, new McpServerConfiguration { PathProvider = new DefaultPathProvider(), IsReadOnly = false });
        var tool = factory.CreateTools().First(t => t.Name == "set_property");

        var input = JsonSerializer.SerializeToElement(new { path = "Temperature", value = 25.0 });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal(25.0m, room.Temperature);
    }

    [Fact]
    public async Task SetProperty_blocked_when_read_only()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var room = new TestRoom(context) { Name = "Test", Temperature = 21.5m };
        var factory = new McpToolFactory(room, new McpServerConfiguration { PathProvider = new DefaultPathProvider(), IsReadOnly = true });
        var tool = factory.CreateTools().First(t => t.Name == "set_property");

        var input = JsonSerializer.SerializeToElement(new { path = "Temperature", value = 25.0 });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.True(json.TryGetProperty("error", out _));
        Assert.Equal(21.5m, room.Temperature);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests --filter "FullyQualifiedName~GetPropertyToolTests|FullyQualifiedName~SetPropertyToolTests" -v n`
Expected: FAIL — `NotImplementedException`

**Step 3: Implement get_property and set_property handlers**

Replace the stub implementations in `McpToolFactory.cs`:

```csharp
private Task<object?> HandleGetPropertyAsync(JsonElement input, CancellationToken cancellationToken)
{
    var pathProvider = _configuration.PathProvider as PathProviderBase
        ?? throw new InvalidOperationException("PathProvider must extend PathProviderBase.");

    var rootRegistered = _rootSubject.TryGetRegisteredSubject()
        ?? throw new InvalidOperationException("Root subject is not registered.");

    var path = input.GetProperty("path").GetString()!;
    var property = pathProvider.TryGetPropertyFromPath(rootRegistered, path);

    if (property is null)
    {
        return Task.FromResult<object?>(new { error = $"Path not found: {path}" });
    }

    var attributes = new Dictionary<string, object?>();
    foreach (var attribute in property.Attributes)
    {
        attributes[attribute.BrowseName] = attribute.GetValue();
    }

    var result = new Dictionary<string, object?>
    {
        ["value"] = property.GetValue(),
        ["type"] = property.Type.Name,
        ["isWritable"] = property.HasSetter
    };

    if (attributes.Count > 0)
    {
        result["attributes"] = attributes;
    }

    return Task.FromResult<object?>(result);
}

private Task<object?> HandleSetPropertyAsync(JsonElement input, CancellationToken cancellationToken)
{
    if (_configuration.IsReadOnly)
    {
        return Task.FromResult<object?>(new { error = "Server is in read-only mode." });
    }

    var pathProvider = _configuration.PathProvider as PathProviderBase
        ?? throw new InvalidOperationException("PathProvider must extend PathProviderBase.");

    var rootRegistered = _rootSubject.TryGetRegisteredSubject()
        ?? throw new InvalidOperationException("Root subject is not registered.");

    var path = input.GetProperty("path").GetString()!;
    var property = pathProvider.TryGetPropertyFromPath(rootRegistered, path);

    if (property is null)
    {
        return Task.FromResult<object?>(new { error = $"Path not found: {path}" });
    }

    if (!property.HasSetter)
    {
        return Task.FromResult<object?>(new { error = $"Property is not writable: {path}" });
    }

    var previousValue = property.GetValue();
    var newValue = JsonSerializer.Deserialize(input.GetProperty("value").GetRawText(), property.Type);
    property.SetValue(newValue);

    return Task.FromResult<object?>(new { success = true, previousValue });
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests --filter "FullyQualifiedName~GetPropertyToolTests|FullyQualifiedName~SetPropertyToolTests" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Mcp/ src/Namotion.Interceptor.Mcp.Tests/
git commit -m "feat: implement get_property and set_property tools"
```

---

### Task 9: Implement list_types tool

**Files:**
- Modify: `src/Namotion.Interceptor.Mcp/Tools/McpToolFactory.cs`
- Create: `src/Namotion.Interceptor.Mcp.Tests/Tools/ListTypesToolTests.cs`

**Step 1: Write failing test**

```csharp
// src/Namotion.Interceptor.Mcp.Tests/Tools/ListTypesToolTests.cs
using System.Text.Json;
using Namotion.Interceptor.Mcp.Implementations;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tests.Tools;

public class ListTypesToolTests
{
    [Fact]
    public async Task ListTypes_returns_types_from_all_providers()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var room = new TestRoom(context);
        var config = new McpServerConfiguration
        {
            PathProvider = new DefaultPathProvider(),
            TypeProviders = { new SubjectAbstractionsAssemblyTypeProvider() }
        };
        var factory = new McpToolFactory(room, config);
        var tool = factory.CreateTools().First(t => t.Name == "list_types");

        var input = JsonSerializer.SerializeToElement(new { });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        var types = json.GetProperty("types");
        Assert.True(types.GetArrayLength() > 0);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests --filter "FullyQualifiedName~ListTypesToolTests" -v n`
Expected: FAIL — `NotImplementedException`

**Step 3: Implement**

Replace the stub in `McpToolFactory.cs`:

```csharp
private Task<object?> HandleListTypesAsync(JsonElement input, CancellationToken cancellationToken)
{
    var types = _configuration.TypeProviders
        .SelectMany(p => p.GetTypes())
        .ToList();

    return Task.FromResult<object?>(new { types });
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests --filter "FullyQualifiedName~ListTypesToolTests" -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Mcp/ src/Namotion.Interceptor.Mcp.Tests/
git commit -m "feat: implement list_types tool"
```

---

### Task 10: Implement McpSubjectServer and DI extension

Wire tools to the ModelContextProtocol SDK and provide `AddMcpSubjectServer` extension method.

**Files:**
- Create: `src/Namotion.Interceptor.Mcp/McpSubjectServer.cs`
- Create: `src/Namotion.Interceptor.Mcp/Extensions/ServiceCollectionExtensions.cs`

**Step 1: Implement McpSubjectServer**

The exact implementation depends on the ModelContextProtocol SDK version. The pattern should be:

```csharp
// src/Namotion.Interceptor.Mcp/McpSubjectServer.cs
using Namotion.Interceptor.Mcp.Tools;

namespace Namotion.Interceptor.Mcp;

/// <summary>
/// MCP server that exposes the subject registry via MCP protocol.
/// </summary>
public class McpSubjectServer
{
    private readonly IInterceptorSubject _rootSubject;
    private readonly McpServerConfiguration _configuration;
    private readonly McpToolFactory _toolFactory;

    public McpSubjectServer(IInterceptorSubject rootSubject, McpServerConfiguration configuration)
    {
        _rootSubject = rootSubject;
        _configuration = configuration;
        _toolFactory = new McpToolFactory(rootSubject, configuration);
    }

    public IReadOnlyList<McpToolDescriptor> GetTools() => _toolFactory.CreateTools();

    // MCP SDK integration: register tools on the MCP server builder.
    // Exact API depends on ModelContextProtocol SDK version.
    // Implementation should create MCP Tool objects from McpToolDescriptor
    // and register them with the SDK's server builder.
}
```

**Step 2: Implement DI extension**

```csharp
// src/Namotion.Interceptor.Mcp/Extensions/ServiceCollectionExtensions.cs
using Microsoft.Extensions.DependencyInjection;

namespace Namotion.Interceptor.Mcp.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMcpSubjectServer(
        this IServiceCollection services,
        IInterceptorSubject rootSubject,
        McpServerConfiguration configuration)
    {
        var server = new McpSubjectServer(rootSubject, configuration);
        services.AddSingleton(server);

        // Register with ModelContextProtocol SDK
        // Exact registration depends on SDK version and hosting model (stdio, SSE, etc.)

        return services;
    }
}
```

Note: The exact MCP SDK integration will depend on the version available. Check `ModelContextProtocol` NuGet docs for the hosting/builder API at implementation time.

**Step 3: Verify build**

Run: `dotnet build src/Namotion.Interceptor.Mcp/Namotion.Interceptor.Mcp.csproj`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Mcp/
git commit -m "feat: add McpSubjectServer and DI extension"
```

---

### Task 11: Run all core tests

**Step 1: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.Mcp.Tests -v n`
Expected: All PASS

**Step 2: Run full solution tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration" -v n`
Expected: All PASS

---

### Task 12: Implement HomeBlaze StateAttributePathProvider

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Services/Mcp/StateAttributePathProvider.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services.Tests/Mcp/StateAttributePathProviderTests.cs`

Note: MCP extensions go in `HomeBlaze.Services` (not `HomeBlaze.AI`) since they don't depend on LLM packages.

**Step 1: Add Mcp project reference to HomeBlaze.Services.csproj**

Add to the `<ItemGroup>` with project references:

```xml
<ProjectReference Include="..\..\Namotion.Interceptor.Mcp\Namotion.Interceptor.Mcp.csproj" />
```

**Step 2: Write failing test**

```csharp
// src/HomeBlaze/HomeBlaze.Services.Tests/Mcp/StateAttributePathProviderTests.cs
using HomeBlaze.Services.Mcp;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services.Tests.Mcp;

public class StateAttributePathProviderTests
{
    [Fact]
    public void IsPropertyIncluded_returns_true_for_state_properties()
    {
        // Arrange — create a subject with [State] property and check the path provider
        // This test depends on having a test subject with [State] attribute
        // which requires the MethodPropertyInitializer and PropertyAttributeInitializer
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        // Use existing test infrastructure or create minimal test subject
        var provider = new StateAttributePathProvider();

        Assert.NotNull(provider);
        // Full integration test with [State] properties
    }
}
```

Note: The exact test implementation depends on having a test subject with `[State]` attribute and `PropertyAttributeInitializer` registered. Use existing HomeBlaze test infrastructure.

**Step 3: Implement**

```csharp
// src/HomeBlaze/HomeBlaze.Services/Mcp/StateAttributePathProvider.cs
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Services.Mcp;

/// <summary>
/// Exposes [State] properties via MCP. Uses state metadata name as path segment.
/// </summary>
public class StateAttributePathProvider : PathProviderBase
{
    public override bool IsPropertyIncluded(RegisteredSubjectProperty property)
        => property.TryGetAttribute(KnownAttributes.State) is not null;

    public override string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        var metadata = property.TryGetAttribute(KnownAttributes.State)?.GetValue() as StateMetadata;
        return metadata?.Name ?? property.Name;
    }
}
```

**Step 4: Verify build and tests**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Services/HomeBlaze.Services.csproj`
Expected: Build succeeds

**Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/ src/HomeBlaze/HomeBlaze.Services.Tests/
git commit -m "feat: add StateAttributePathProvider for HomeBlaze MCP"
```

---

### Task 13: Implement HomeBlaze MCP enricher and type provider

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Services/Mcp/HomeBlazeMcpSubjectEnricher.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services/Mcp/SubjectTypeRegistryTypeProvider.cs`

**Step 1: Implement subject enricher**

```csharp
// src/HomeBlaze/HomeBlaze.Services/Mcp/HomeBlazeMcpSubjectEnricher.cs
using HomeBlaze.Abstractions;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services.Mcp;

/// <summary>
/// Enriches MCP query responses with $title, $icon, and $type from HomeBlaze interfaces.
/// </summary>
public class HomeBlazeMcpSubjectEnricher : IMcpSubjectEnricher
{
    public void EnrichSubject(RegisteredSubject subject, IDictionary<string, object?> metadata)
    {
        if (subject.Subject is ITitleProvider titleProvider)
        {
            metadata["$title"] = titleProvider.Title;
        }

        if (subject.Subject is IIconProvider iconProvider)
        {
            metadata["$icon"] = iconProvider.IconName;
        }

        metadata["$type"] = subject.Subject.GetType().Name;
    }
}
```

**Step 2: Implement type registry provider**

```csharp
// src/HomeBlaze/HomeBlaze.Services/Mcp/SubjectTypeRegistryTypeProvider.cs
using Namotion.Interceptor.Mcp.Abstractions;

namespace HomeBlaze.Services.Mcp;

/// <summary>
/// Returns concrete subject types from HomeBlaze's SubjectTypeRegistry.
/// </summary>
public class SubjectTypeRegistryTypeProvider : IMcpTypeProvider
{
    private readonly SubjectTypeRegistry _typeRegistry;

    public SubjectTypeRegistryTypeProvider(SubjectTypeRegistry typeRegistry)
    {
        _typeRegistry = typeRegistry;
    }

    public IEnumerable<McpTypeInfo> GetTypes()
    {
        foreach (var type in _typeRegistry.RegisteredTypes)
        {
            yield return new McpTypeInfo(type.FullName!, null, IsInterface: false);
        }
    }
}
```

**Step 3: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Services/HomeBlaze.Services.csproj`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/Mcp/
git commit -m "feat: add HomeBlaze MCP subject enricher and type provider"
```

---

### Task 14: Implement HomeBlaze MCP tool provider (list_methods, invoke_method)

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Services/Mcp/HomeBlazeMcpToolProvider.cs`

**Step 1: Implement**

```csharp
// src/HomeBlaze/HomeBlaze.Services/Mcp/HomeBlazeMcpToolProvider.cs
using System.Text.Json;
using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Services.Mcp;

/// <summary>
/// Provides list_methods and invoke_method tools for HomeBlaze subjects.
/// </summary>
public class HomeBlazeMcpToolProvider : IMcpToolProvider
{
    private readonly IInterceptorSubject _rootSubject;
    private readonly PathProviderBase _pathProvider;
    private readonly bool _isReadOnly;

    public HomeBlazeMcpToolProvider(
        IInterceptorSubject rootSubject,
        PathProviderBase pathProvider,
        bool isReadOnly)
    {
        _rootSubject = rootSubject;
        _pathProvider = pathProvider;
        _isReadOnly = isReadOnly;
    }

    public IEnumerable<McpToolDescriptor> GetTools()
    {
        yield return new McpToolDescriptor
        {
            Name = "list_methods",
            Description = "List operations and queries available on a subject at the given path.",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { path = new { type = "string", description = "Subject path" } },
                required = new[] { "path" }
            }),
            Handler = HandleListMethodsAsync
        };

        yield return new McpToolDescriptor
        {
            Name = "invoke_method",
            Description = "Execute a method on a subject. When server is read-only, only query methods are allowed.",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Subject path" },
                    method = new { type = "string", description = "Method name" },
                    arguments = new { type = "object", description = "Method arguments (optional)" }
                },
                required = new[] { "path", "method" }
            }),
            Handler = HandleInvokeMethodAsync
        };
    }

    private Task<object?> HandleListMethodsAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var subject = ResolveSubject(input.GetProperty("path").GetString()!);
        if (subject is null)
        {
            return Task.FromResult<object?>(new { error = "Subject not found." });
        }

        var methods = subject.GetAllMethods().Select(m => new
        {
            name = m.PropertyName,
            kind = m.Kind.ToString().ToLowerInvariant(),
            parameters = m.Parameters
                .Where(p => p.RequiresInput)
                .Select(p => new { name = p.Name, type = p.Type.Name })
                .ToArray()
        });

        return Task.FromResult<object?>(new { methods });
    }

    private async Task<object?> HandleInvokeMethodAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var subject = ResolveSubject(input.GetProperty("path").GetString()!);
        if (subject is null)
        {
            return new { error = "Subject not found." };
        }

        var methodName = input.GetProperty("method").GetString()!;
        var methodProperty = subject.TryGetProperty(methodName);
        if (methodProperty?.GetValue() is not MethodMetadata method)
        {
            return new { error = $"Method not found: {methodName}" };
        }

        if (_isReadOnly && method.Kind != MethodKind.Query)
        {
            return new { error = "Operations are not allowed in read-only mode." };
        }

        try
        {
            // Parse arguments from JSON into parameter array
            object?[]? parameters = null;
            if (input.TryGetProperty("arguments", out var argumentsElement))
            {
                var inputParams = method.Parameters.Where(p => p.RequiresInput).ToArray();
                parameters = new object?[method.Parameters.Length];

                for (var i = 0; i < method.Parameters.Length; i++)
                {
                    var param = method.Parameters[i];
                    if (param.RequiresInput && argumentsElement.TryGetProperty(param.Name, out var argValue))
                    {
                        parameters[i] = JsonSerializer.Deserialize(argValue.GetRawText(), param.Type);
                    }
                }
            }

            var result = await method.InvokeAsync(parameters, null, cancellationToken);
            return result is not null ? new { success = true, result } : new { success = true };
        }
        catch (Exception exception)
        {
            return new { error = exception.Message };
        }
    }

    private RegisteredSubject? ResolveSubject(string path)
    {
        var rootRegistered = _rootSubject.TryGetRegisteredSubject();
        if (rootRegistered is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(path))
        {
            return rootRegistered;
        }

        var property = _pathProvider.TryGetPropertyFromPath(rootRegistered, path);
        var childSubject = property?.GetValue() as IInterceptorSubject;
        return childSubject?.TryGetRegisteredSubject();
    }
}
```

**Step 2: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Services/HomeBlaze.Services.csproj`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/Mcp/
git commit -m "feat: add HomeBlaze MCP tool provider (list_methods, invoke_method)"
```

---

### Task 15: Register MCP server in HomeBlaze host (opt-in via config)

The MCP server is opt-in via `appsettings.json`. It defaults to enabled in Development environment only.

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Services/ServiceCollectionExtensions.cs`
- Modify: `src/HomeBlaze/HomeBlaze/Program.cs`
- Modify: `src/HomeBlaze/HomeBlaze/appsettings.json`
- Modify: `src/HomeBlaze/HomeBlaze/appsettings.Development.json`

**Step 1: Add config to appsettings**

```json
// src/HomeBlaze/HomeBlaze/appsettings.json
{
  "UseMcpServer": false
}
```

```json
// src/HomeBlaze/HomeBlaze/appsettings.Development.json
{
  "UseMcpServer": true
}
```

**Step 2: Add MCP registration extension method**

Add to `ServiceCollectionExtensions.cs` in `HomeBlaze.Services`:

```csharp
public static IServiceCollection AddHomeBlazeMcpServer(
    this IServiceCollection services,
    IInterceptorSubject rootSubject,
    SubjectTypeRegistry typeRegistry)
{
    var pathProvider = new StateAttributePathProvider();
    var configuration = new McpServerConfiguration
    {
        PathProvider = pathProvider,
        SubjectEnrichers = { new HomeBlazeMcpSubjectEnricher() },
        TypeProviders =
        {
            new SubjectAbstractionsAssemblyTypeProvider(),
            new SubjectTypeRegistryTypeProvider(typeRegistry)
        },
        ToolProviders = { new HomeBlazeMcpToolProvider(rootSubject, pathProvider, isReadOnly: false) },
        IsReadOnly = false
    };

    services.AddMcpSubjectServer(rootSubject, configuration);
    return services;
}
```

**Step 3: Wire in Program.cs conditionally**

```csharp
// In Program.cs, after AddHomeBlazeHost()
if (builder.Configuration.GetValue<bool>("UseMcpServer"))
{
    // Root subject and type registry are resolved from DI
    // Exact timing depends on when root is available — may need post-build hook
    builder.Services.AddHomeBlazeMcpServer(rootSubject, typeRegistry);
}
```

Note: The exact integration point depends on when the root subject is available. The root subject is loaded by `RootManager` at startup. The MCP server registration may need to be deferred or the root subject resolved lazily. Check the `RootManager` initialization flow during implementation.

**Step 4: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`
Expected: Build succeeds

**Step 5: Commit**

```bash
git add src/HomeBlaze/
git commit -m "feat: register MCP server in HomeBlaze host (opt-in via UseMcpServer config)"
```

---

### Task 16: Add [SubjectAbstractionsAssembly] to HomeBlaze.Abstractions

**Files:**
- Create or modify: `src/HomeBlaze/HomeBlaze.Abstractions/AssemblyInfo.cs`

**Step 1: Mark assembly**

```csharp
// src/HomeBlaze/HomeBlaze.Abstractions/AssemblyInfo.cs
using Namotion.Interceptor;

[assembly: SubjectAbstractionsAssembly]
```

**Step 2: Verify build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Abstractions/HomeBlaze.Abstractions.csproj`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Abstractions/
git commit -m "feat: mark HomeBlaze.Abstractions with [SubjectAbstractionsAssembly]"
```

---

### Task 17: Create docs/mcp.md documentation

**Files:**
- Create: `docs/mcp.md`
- Modify: `README.md`

**Step 1: Create docs/mcp.md**

```markdown
# MCP Server

Namotion.Interceptor.Mcp exposes the subject registry via [MCP (Model Context Protocol)](https://modelcontextprotocol.io), enabling AI agents to browse, query, and interact with the object graph.

## Installation

```xml
<PackageReference Include="Namotion.Interceptor.Mcp" Version="0.1.0" />
```

## Quick Start

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry();

var root = new MyRootSubject(context);

// Configure MCP server
services.AddMcpSubjectServer(root, new McpServerConfiguration
{
    PathProvider = new DefaultPathProvider()
});
```

## Tools

The MCP server provides 4 core tools:

| Tool | Description |
|------|-------------|
| `query` | Browse the subject tree with depth control, property inclusion, and type filtering |
| `get_property` | Read a property value with type and registry attributes |
| `set_property` | Write a property value (blocked when `IsReadOnly`) |
| `list_types` | List available types from registered type providers |

### Path Format

Paths use dot notation with bracket indexing:

```
root.livingRoom.temperature
root.sensors[0].value
root.devices[myDevice].status
```

### Query Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `path` | root | Starting path |
| `depth` | 1 | Max traversal depth (0 = properties only, no children) |
| `includeProperties` | false | Include property values |
| `includeAttributes` | false | Include registry attributes on properties |
| `types` | all | Filter subjects by type/interface full names |

## Configuration

```csharp
var config = new McpServerConfiguration
{
    // Required: property filtering and path resolution
    PathProvider = new DefaultPathProvider(),

    // Optional: subject-level metadata enrichment
    SubjectEnrichers = { new MySubjectEnricher() },

    // Optional: type discovery for list_types
    TypeProviders = { new SubjectAbstractionsAssemblyTypeProvider() },

    // Optional: additional tools
    ToolProviders = { new MyToolProvider() },

    // Safety limits
    MaxDepth = 10,
    MaxSubjectsPerResponse = 100,
    IsReadOnly = true
};
```

### Access Control

| `IsReadOnly` | `set_property` | `invoke_method` (Query) | `invoke_method` (Operation) |
|--------------|---------------|------------------------|-----------------------------|
| `true` | Blocked | Allowed | Blocked |
| `false` | Allowed | Allowed | Allowed |

## Extension Points

### IMcpSubjectEnricher

Add subject-level metadata (prefixed with `$`) to query responses:

```csharp
public class MyEnricher : IMcpSubjectEnricher
{
    public void EnrichSubject(RegisteredSubject subject, IDictionary<string, object?> metadata)
    {
        metadata["$customField"] = "value";
    }
}
```

### IMcpTypeProvider

Provide types for the `list_types` tool:

```csharp
public class MyTypeProvider : IMcpTypeProvider
{
    public IEnumerable<McpTypeInfo> GetTypes()
    {
        yield return new McpTypeInfo("MyNamespace.IMyInterface", "Description", IsInterface: true);
    }
}
```

The built-in `SubjectAbstractionsAssemblyTypeProvider` returns all interfaces from assemblies marked with `[SubjectAbstractionsAssembly]`.

### IMcpToolProvider

Add custom tools:

```csharp
public class MyToolProvider : IMcpToolProvider
{
    public IEnumerable<McpToolDescriptor> GetTools()
    {
        yield return new McpToolDescriptor
        {
            Name = "my_tool",
            Description = "My custom tool",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
            Handler = async (input, ct) => new { result = "hello" }
        };
    }
}
```

Tools are transport-agnostic `McpToolDescriptor` instances. They can be wrapped as MCP tools for external agents or `AIFunction` objects for in-process use.
```

**Step 2: Add MCP row to README.md packages table**

Add to the "Integrations" section in README.md:

```markdown
| **Namotion.Interceptor.Mcp** | MCP server for AI agent access to the subject registry | [MCP](docs/mcp.md) |
```

**Step 3: Commit**

```bash
git add docs/mcp.md README.md
git commit -m "docs: add MCP server documentation"
```

---

### Task 18: Update architecture design docs

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/Data/Docs/architecture/design/ai.md`
- Modify: `src/HomeBlaze/HomeBlaze/Data/Docs/architecture/state.md`

**Step 1: Update ai.md status from Planned to Implemented**

Update the MCP-related sections to reflect implementation status. Change `[Planned]` labels to `[Implemented]` for the MCP tool layering section.

**Step 2: Update state.md**

Change the MCP server rows from `Planned` to `Implemented`:

```markdown
| MCP server (core tools) | Implemented | `Namotion.Interceptor.Mcp` — `query`, `get_property`, `set_property`, `list_types` |
| MCP server (HomeBlaze extensions) | Implemented | Subject enrichment, type discovery, `list_methods`, `invoke_method` via `McpServerConfiguration` |
```

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze/Data/Docs/
git commit -m "docs: update architecture docs to reflect MCP implementation"
```

---

### Task 19: Final verification

**Step 1: Build entire solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeds with no errors

**Step 2: Run all unit tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration" -v n`
Expected: All PASS

**Step 3: Verify no warnings as errors**

The solution has `TreatWarningsAsErrors` enabled. Ensure no new warnings.
