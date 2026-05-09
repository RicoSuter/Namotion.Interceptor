# ConnectorTester Snapshot Comparer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract `ConnectorTester` snapshot-comparison and failure-diagnostics improvements onto master so PR #197 (`feature/websocket-structural-mutations`) and `feature/opcua-bidirectional-structural-sync` can rebase and drop their local copies.

**Architecture:** New `SnapshotComparer` static class encapsulates `Capture(TestNode)` and `SnapshotsMatch(string, string)`. `Capture` builds a deterministic, normalized JSON via `SubjectUpdate.CreateCompleteUpdate`. `SnapshotsMatch` does fast-path string equality with a JSON-walking fallback that respects the `NullTimestampTicks` architectural contract. New `Namotion.Interceptor.ConnectorTester.Tests` xUnit project pins the contract. `VerificationEngine` switches to the new comparer and gains three failure diagnostics: per-cycle JSON dump, per-property write-timestamp diff, and a re-sync classifier.

**Tech Stack:** .NET 9.0, C# 13 preview, xUnit 2.9, `System.Text.Json`, `System.Text.Json.Nodes`, `Microsoft.Extensions.Hosting`, source-generated `[InterceptorSubject]` partial properties. Spec at `docs/superpowers/specs/2026-05-10-connector-tester-snapshot-comparer-design.md`.

---

## File Map

| Status | Path | Responsibility |
|---|---|---|
| New | `src/Namotion.Interceptor.ConnectorTester/Engine/SnapshotComparer.cs` | `Capture(TestNode root) → string` and `SnapshotsMatch(string a, string b) → bool`. The contract. |
| New | `src/Namotion.Interceptor.ConnectorTester.Tests/Namotion.Interceptor.ConnectorTester.Tests.csproj` | xUnit test project. References `Namotion.Interceptor.ConnectorTester` and `Namotion.Interceptor.Generator`. |
| New | `src/Namotion.Interceptor.ConnectorTester.Tests/Engine/SnapshotComparerTests.cs` | 15 unit tests pinning each comparison-contract rule. |
| Modified | `src/Namotion.Interceptor.slnx` | Register the new test project under `/Tests/`. |
| Modified | `src/Namotion.Interceptor.ConnectorTester/Engine/VerificationEngine.cs` | Replace inline `CreateSnapshot` + string equality with `SnapshotComparer`. Add `LogFailureSnapshots`, `LogPropertyDiffsWithTimestamps`, `LogReSyncCheck`. Reuse them in the failure path. |
| Modified | `docs/connector-tester.md` | Rewrite verification semantics, failure output, and "What the Tester Detects" sections. |

No changes to `MutationEngine`, `TestNode`, configuration types, `Program.cs`, or any code outside the `ConnectorTester` project plus the new test project.

---

### Task 1: Create `SnapshotComparer` skeleton with deterministic `Capture`

**Files:**
- Create: `src/Namotion.Interceptor.ConnectorTester/Engine/SnapshotComparer.cs`

This task introduces the file with `Capture` only (returning a normalized JSON string). `SnapshotsMatch` lands in Task 4 once the JSON shape is stable. Tests land in Task 3 once the test project exists.

- [ ] **Step 1: Create the `SnapshotComparer` file**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Captures and compares participant snapshots for the ConnectorTester convergence check.
/// Produces a deterministic JSON representation per participant so equal source state
/// (modulo legitimate per-participant differences such as root subject IDs and
/// structural-property timestamps) yields equal strings.
/// </summary>
public static class SnapshotComparer
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Builds a normalized snapshot JSON for the given root.
    /// </summary>
    public static string Capture(TestNode root)
    {
        var update = SubjectUpdate.CreateCompleteUpdate(root, []);
        var rawJson = JsonSerializer.Serialize(update, CompactJsonOptions);
        var node = JsonNode.Parse(rawJson)!.AsObject();
        Normalize(node);
        return node.ToJsonString(CompactJsonOptions);
    }

    private static void Normalize(JsonObject root)
    {
        var rootId = root["root"]?.GetValue<string>();

        if (root["subjects"] is JsonObject subjects)
        {
            foreach (var (_, subjectNode) in subjects)
            {
                if (subjectNode is JsonObject properties)
                {
                    NormalizeProperties(properties, rootId);
                }
            }

            ReplaceSubjects(root, subjects, rootId);
        }

        if (rootId is not null)
        {
            root["root"] = "ROOT";
        }
    }

    private static void NormalizeProperties(JsonObject properties, string? rootId)
    {
        foreach (var (_, propertyNode) in properties)
        {
            if (propertyNode is not JsonObject property)
            {
                continue;
            }

            var kind = property["kind"]?.GetValue<string>();

            // Strip timestamps from non-Value properties: structural-property timestamps
            // are local creation moments and never propagate across the wire.
            if (kind != "Value")
            {
                property.Remove("timestamp");
            }

            // Replace per-participant root ID references with "ROOT".
            if (kind == "Object" && property["id"]?.GetValue<string>() == rootId)
            {
                property["id"] = "ROOT";
            }

            if ((kind == "Collection" || kind == "Dictionary") &&
                property["items"] is JsonArray items)
            {
                foreach (var itemNode in items)
                {
                    if (itemNode is JsonObject itemObject &&
                        itemObject["id"]?.GetValue<string>() == rootId)
                    {
                        itemObject["id"] = "ROOT";
                    }
                }

                // Dictionary items have no defined order; sort by their "index" field
                // (the dictionary key, serialized as a string). Collection items keep
                // their source order (order is part of equality).
                if (kind == "Dictionary")
                {
                    var sortedItems = items
                        .Select(item => item!.AsObject())
                        .OrderBy(item => item["index"]?.GetValue<string>(), StringComparer.Ordinal)
                        .Select(item => item.DeepClone())
                        .ToArray();

                    items.Clear();
                    foreach (var item in sortedItems)
                    {
                        items.Add(item);
                    }
                }
            }
        }
    }

    private static void ReplaceSubjects(JsonObject root, JsonObject subjects, string? rootId)
    {
        // Sort subject IDs (with root renamed) and property keys for deterministic output.
        var entries = subjects
            .Select(kvp => (
                Key: kvp.Key == rootId ? "ROOT" : kvp.Key,
                Properties: kvp.Value!.AsObject()))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToList();

        var sorted = new JsonObject();
        foreach (var (key, properties) in entries)
        {
            var sortedProperties = new JsonObject();
            foreach (var propertyKey in properties
                .Select(kvp => kvp.Key)
                .OrderBy(name => name, StringComparer.Ordinal))
            {
                sortedProperties[propertyKey] = properties[propertyKey]!.DeepClone();
            }
            sorted[key] = sortedProperties;
        }

        root["subjects"] = sorted;
    }
}
```

- [ ] **Step 2: Verify the project compiles**

Run: `dotnet build src/Namotion.Interceptor.ConnectorTester/Namotion.Interceptor.ConnectorTester.csproj`
Expected: build succeeds. `SnapshotComparer` is unused so far; that's fine.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.ConnectorTester/Engine/SnapshotComparer.cs
git commit -m "feat(connector-tester): add SnapshotComparer.Capture for deterministic snapshots"
```

---

### Task 2: Create the test project skeleton

**Files:**
- Create: `src/Namotion.Interceptor.ConnectorTester.Tests/Namotion.Interceptor.ConnectorTester.Tests.csproj`
- Create: `src/Namotion.Interceptor.ConnectorTester.Tests/Engine/SnapshotComparerTests.cs` (placeholder with one passing test so the project actually runs)
- Modify: `src/Namotion.Interceptor.slnx`

The csproj mirrors `Namotion.Interceptor.Connectors.Tests.csproj` (xUnit, Moq, Microsoft.NET.Test.Sdk) plus a project reference to `Namotion.Interceptor.ConnectorTester` and the source generator. The placeholder test exists so `dotnet test` succeeds before the contract tests are written in Task 3.

- [ ] **Step 1: Create the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net9.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <IsPackable>false</IsPackable>
        <IsTestProject>true</IsTestProject>
        <LangVersion>preview</LangVersion>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.10" />
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
        <ProjectReference Include="..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
        <ProjectReference Include="..\Namotion.Interceptor.ConnectorTester\Namotion.Interceptor.ConnectorTester.csproj" />
    </ItemGroup>

</Project>
```

- [ ] **Step 2: Add the placeholder test file**

Create `src/Namotion.Interceptor.ConnectorTester.Tests/Engine/SnapshotComparerTests.cs`:

```csharp
namespace Namotion.Interceptor.ConnectorTester.Tests.Engine;

public class SnapshotComparerTests
{
    [Fact]
    public void WhenProjectIsCreated_ThenPlaceholderTestPasses()
    {
        // Arrange / Act / Assert
        Assert.True(true);
    }
}
```

This placeholder exists so `dotnet test` runs without "no tests found" warnings and is replaced by real cases in Task 3.

- [ ] **Step 3: Register the project in the solution**

Edit `src/Namotion.Interceptor.slnx`. Inside the `<Folder Name="/Tests/">` element (around line 101), add this line alongside the existing entries:

```xml
    <Project Path="Namotion.Interceptor.ConnectorTester.Tests/Namotion.Interceptor.ConnectorTester.Tests.csproj" />
```

The folder block before edit ends with:

```xml
    <Project Path="Namotion.Interceptor.WebSocket.Tests/Namotion.Interceptor.WebSocket.Tests.csproj" />
  </Folder>
```

After edit:

```xml
    <Project Path="Namotion.Interceptor.WebSocket.Tests/Namotion.Interceptor.WebSocket.Tests.csproj" />
    <Project Path="Namotion.Interceptor.ConnectorTester.Tests/Namotion.Interceptor.ConnectorTester.Tests.csproj" />
  </Folder>
```

- [ ] **Step 4: Build and run the placeholder test**

Run: `dotnet test src/Namotion.Interceptor.ConnectorTester.Tests/Namotion.Interceptor.ConnectorTester.Tests.csproj`
Expected: build succeeds, 1 test passes.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.ConnectorTester.Tests/ src/Namotion.Interceptor.slnx
git commit -m "build(connector-tester): add ConnectorTester.Tests xUnit project"
```

---

### Task 3: Pin the `Capture` contract with unit tests

**Files:**
- Modify: `src/Namotion.Interceptor.ConnectorTester.Tests/Engine/SnapshotComparerTests.cs`

This task writes the eight tests that exercise `Capture`-side behaviour (sorting, root-ID normalization, structural-timestamp stripping, value preservation). The four `SnapshotsMatch`-side tests (null-timestamp rule cases, key mismatch) come in Task 5 after `SnapshotsMatch` exists.

The tests build small `TestNode` graphs in two separate `IInterceptorSubjectContext` instances so root subject IDs differ across "participants", which mirrors the real test scenario.

Replace the placeholder file content with this. (Step 1 first, then add tests one at a time.)

- [ ] **Step 1: Replace the placeholder with imports + helpers**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Lifecycle;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine;

public class SnapshotComparerTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithLifecycle();

    private static TestNode CreateLeaf(IInterceptorSubjectContext context, string stringValue, int intValue)
    {
        return new TestNode(context)
        {
            StringValue = stringValue,
            IntValue = intValue,
            DecimalValue = intValue,
            LongValue = intValue
        };
    }
}
```

Run: `dotnet build src/Namotion.Interceptor.ConnectorTester.Tests/Namotion.Interceptor.ConnectorTester.Tests.csproj`
Expected: builds (the placeholder test is gone but that's fine; the next step adds the first real test).

- [ ] **Step 2: Add `WhenSnapshotsAreIdentical_ThenCapturesMatch`**

Add inside the class:

```csharp
    [Fact]
    public void WhenSnapshotsAreIdentical_ThenCapturesMatch()
    {
        // Arrange
        var contextA = CreateContext();
        var rootA = CreateLeaf(contextA, "x", 1);

        var contextB = CreateContext();
        var rootB = CreateLeaf(contextB, "x", 1);

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.Equal(snapshotA, snapshotB);
    }
```

Run: `dotnet test src/Namotion.Interceptor.ConnectorTester.Tests/Namotion.Interceptor.ConnectorTester.Tests.csproj --filter FullyQualifiedName~WhenSnapshotsAreIdentical_ThenCapturesMatch`
Expected: PASS.

- [ ] **Step 3: Add `WhenRootIdDiffersAcrossParticipants_ThenCapturesMatch`**

```csharp
    [Fact]
    public void WhenRootIdDiffersAcrossParticipants_ThenCapturesMatch()
    {
        // Arrange (each participant has its own context, so root subject IDs differ).
        var contextA = CreateContext();
        var rootA = CreateLeaf(contextA, "x", 1);

        var contextB = CreateContext();
        var rootB = CreateLeaf(contextB, "x", 1);

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert that per-participant root IDs are normalized to "ROOT".
        Assert.Equal(snapshotA, snapshotB);
        Assert.Contains("\"root\":\"ROOT\"", snapshotA);
        Assert.DoesNotContain("\"root\":null", snapshotA);
    }
```

Run: `dotnet test --filter FullyQualifiedName~WhenRootIdDiffersAcrossParticipants_ThenCapturesMatch`
Expected: PASS.

- [ ] **Step 4: Add `WhenValueDiffers_ThenCapturesDiffer`**

```csharp
    [Fact]
    public void WhenValueDiffers_ThenCapturesDiffer()
    {
        // Arrange
        var contextA = CreateContext();
        var rootA = CreateLeaf(contextA, "x", 1);

        var contextB = CreateContext();
        var rootB = CreateLeaf(contextB, "y", 1); // StringValue differs

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.NotEqual(snapshotA, snapshotB);
    }
```

Run: `dotnet test --filter FullyQualifiedName~WhenValueDiffers_ThenCapturesDiffer`
Expected: PASS.

- [ ] **Step 5: Add `WhenStructuralPropertyTimestampsDiffer_ThenCapturesMatch`**

This proves Collection/Dictionary/Object timestamps are stripped during normalization. Two graphs created at different wall-clock times must still snapshot-equal.

```csharp
    [Fact]
    public async Task WhenStructuralPropertyTimestampsDiffer_ThenCapturesMatch()
    {
        // Arrange: populate both graphs with structural state at different wall-clock times.
        var contextA = CreateContext();
        var rootA = new TestNode(contextA)
        {
            StringValue = "x",
            IntValue = 1,
            DecimalValue = 1,
            LongValue = 1,
            Collection = [CreateLeaf(contextA, "child", 1)],
            Items = new Dictionary<string, TestNode> { ["a"] = CreateLeaf(contextA, "a", 1) }
        };

        // Force a wall-clock gap so structural timestamps would differ if not stripped.
        await Task.Delay(20);

        var contextB = CreateContext();
        var rootB = new TestNode(contextB)
        {
            StringValue = "x",
            IntValue = 1,
            DecimalValue = 1,
            LongValue = 1,
            Collection = [CreateLeaf(contextB, "child", 1)],
            Items = new Dictionary<string, TestNode> { ["a"] = CreateLeaf(contextB, "a", 1) }
        };

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.Equal(snapshotA, snapshotB);
    }
```

Run: `dotnet test --filter FullyQualifiedName~WhenStructuralPropertyTimestampsDiffer_ThenCapturesMatch`
Expected: PASS.

- [ ] **Step 6: Add `WhenDictionaryItemOrderDiffers_ThenCapturesMatch`**

Dictionaries are inherently unordered; the items array sort by `"index"` makes captures equal regardless of insertion order.

```csharp
    [Fact]
    public void WhenDictionaryItemOrderDiffers_ThenCapturesMatch()
    {
        // Arrange: same key/value pairs, different insertion order.
        var contextA = CreateContext();
        var rootA = new TestNode(contextA)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Items = new Dictionary<string, TestNode>
            {
                ["alpha"] = CreateLeaf(contextA, "a", 1),
                ["bravo"] = CreateLeaf(contextA, "b", 2)
            }
        };

        var contextB = CreateContext();
        var rootB = new TestNode(contextB)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Items = new Dictionary<string, TestNode>
            {
                ["bravo"] = CreateLeaf(contextB, "b", 2),
                ["alpha"] = CreateLeaf(contextB, "a", 1)
            }
        };

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.Equal(snapshotA, snapshotB);
    }
```

Run: `dotnet test --filter FullyQualifiedName~WhenDictionaryItemOrderDiffers_ThenCapturesMatch`
Expected: PASS.

- [ ] **Step 7: Add `WhenCollectionItemOrderDiffers_ThenCapturesDiffer`**

Pins the asymmetry against accidental future change: Collection is ordered, sorting it would mask divergence.

```csharp
    [Fact]
    public void WhenCollectionItemOrderDiffers_ThenCapturesDiffer()
    {
        // Arrange: same items, different order.
        var contextA = CreateContext();
        var rootA = new TestNode(contextA)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Collection =
            [
                CreateLeaf(contextA, "a", 1),
                CreateLeaf(contextA, "b", 2)
            ]
        };

        var contextB = CreateContext();
        var rootB = new TestNode(contextB)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Collection =
            [
                CreateLeaf(contextB, "b", 2),
                CreateLeaf(contextB, "a", 1)
            ]
        };

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.NotEqual(snapshotA, snapshotB);
    }
```

Run: `dotnet test --filter FullyQualifiedName~WhenCollectionItemOrderDiffers_ThenCapturesDiffer`
Expected: PASS.

- [ ] **Step 8: Add `WhenGraphHasCycle_ThenCaptureIsStable`**

Cycle handling lives in `SubjectUpdate.CreateCompleteUpdate`; this test pins the assumption so a regression there surfaces in tester tests too.

```csharp
    [Fact]
    public void WhenGraphHasCycle_ThenCaptureIsStable()
    {
        // Arrange: a -> b -> a cycle via ObjectRef.
        var context = CreateContext();
        var leafB = CreateLeaf(context, "b", 2);
        var rootA = new TestNode(context)
        {
            StringValue = "a", IntValue = 1, DecimalValue = 1, LongValue = 1,
            ObjectRef = leafB
        };
        leafB.ObjectRef = rootA;

        // Act: capture twice; deterministic output should match.
        var snapshot1 = SnapshotComparer.Capture(rootA);
        var snapshot2 = SnapshotComparer.Capture(rootA);

        // Assert
        Assert.Equal(snapshot1, snapshot2);
    }
```

Run: `dotnet test --filter FullyQualifiedName~WhenGraphHasCycle_ThenCaptureIsStable`
Expected: PASS.

- [ ] **Step 9: Run full test suite to confirm all `Capture`-side tests pass**

Run: `dotnet test src/Namotion.Interceptor.ConnectorTester.Tests/Namotion.Interceptor.ConnectorTester.Tests.csproj`
Expected: 7 tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/Namotion.Interceptor.ConnectorTester.Tests/Engine/SnapshotComparerTests.cs
git commit -m "test(connector-tester): pin SnapshotComparer.Capture contract"
```

---

### Task 4: Add `SnapshotComparer.SnapshotsMatch` with the null-timestamp rule

**Files:**
- Modify: `src/Namotion.Interceptor.ConnectorTester/Engine/SnapshotComparer.cs`

The contract: fast-path string equality, fallback JSON walk where a `"timestamp"` field matches when both null, both equal non-null, OR exactly one side is null. All other property fields use strict `JsonNode.ToJsonString()` equality.

- [ ] **Step 1: Add `SnapshotsMatch` and helpers**

Append to the `SnapshotComparer` class (after `Capture` but before the private helpers; keep public API at the top):

```csharp
    /// <summary>
    /// Compares two normalized snapshots produced by <see cref="Capture"/>.
    /// Falls back from string equality to a JSON-walking comparison that respects the
    /// architectural null-timestamp contract (see SubjectChangeContext NullTimestampTicks):
    /// a null timestamp on either side matches any timestamp value. All other fields
    /// compare by strict JSON equality.
    /// </summary>
    public static bool SnapshotsMatch(string snapshotA, string snapshotB)
    {
        if (snapshotA == snapshotB)
        {
            return true;
        }

        var subjectsA = JsonNode.Parse(snapshotA)?["subjects"]?.AsObject();
        var subjectsB = JsonNode.Parse(snapshotB)?["subjects"]?.AsObject();

        if (subjectsA is null || subjectsB is null)
        {
            return subjectsA is null && subjectsB is null;
        }

        if (subjectsA.Count != subjectsB.Count)
        {
            return false;
        }

        foreach (var (subjectId, subjectNodeA) in subjectsA)
        {
            if (subjectsB[subjectId] is not JsonObject propertiesB)
            {
                return false;
            }

            var propertiesA = subjectNodeA!.AsObject();
            if (propertiesA.Count != propertiesB.Count)
            {
                return false;
            }

            foreach (var (propertyName, propertyNodeA) in propertiesA)
            {
                if (propertiesB[propertyName] is not JsonObject propertyB)
                {
                    return false;
                }

                if (!PropertiesMatch(propertyNodeA!.AsObject(), propertyB))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool PropertiesMatch(JsonObject propertyA, JsonObject propertyB)
    {
        var keys = new HashSet<string>(propertyA.Select(kvp => kvp.Key));
        keys.UnionWith(propertyB.Select(kvp => kvp.Key));

        foreach (var key in keys)
        {
            var valueA = propertyA[key];
            var valueB = propertyB[key];

            if (key == "timestamp")
            {
                // Architectural null-timestamp rule: null on either side is a legitimate
                // "no explicit write timestamp" state and matches any value. Only fail
                // when both sides are non-null and unequal.
                if (valueA is not null && valueB is not null && !JsonValuesEqual(valueA, valueB))
                {
                    return false;
                }
            }
            else if (!JsonValuesEqual(valueA, valueB))
            {
                return false;
            }
        }

        return true;
    }

    private static bool JsonValuesEqual(JsonNode? a, JsonNode? b)
    {
        if (a is null && b is null)
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        return a.ToJsonString() == b.ToJsonString();
    }
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/Namotion.Interceptor.ConnectorTester/Namotion.Interceptor.ConnectorTester.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.ConnectorTester/Engine/SnapshotComparer.cs
git commit -m "feat(connector-tester): add SnapshotsMatch with null-timestamp rule"
```

---

### Task 5: Pin the `SnapshotsMatch` contract with unit tests

**Files:**
- Modify: `src/Namotion.Interceptor.ConnectorTester.Tests/Engine/SnapshotComparerTests.cs`

These tests exercise the JSON-walk fallback and the four timestamp-rule permutations. They build raw JSON strings rather than going through `Capture`, so each rule is tested in isolation from the normalization logic.

- [ ] **Step 1: Add `WhenPropertyOrderInJsonDiffers_ThenSnapshotsMatch`**

Inside the class:

```csharp
    private const string SampleSnapshotPropertiesAB = """
        {"root":"ROOT","subjects":{"ROOT":{"A":{"kind":"Value","value":1},"B":{"kind":"Value","value":2}}}}
        """;

    private const string SampleSnapshotPropertiesBA = """
        {"root":"ROOT","subjects":{"ROOT":{"B":{"kind":"Value","value":2},"A":{"kind":"Value","value":1}}}}
        """;

    [Fact]
    public void WhenPropertyOrderInJsonDiffers_ThenSnapshotsMatch()
    {
        // Arrange / Act
        var match = SnapshotComparer.SnapshotsMatch(SampleSnapshotPropertiesAB, SampleSnapshotPropertiesBA);

        // Assert: the JSON walk treats objects as unordered, so reordered keys still match.
        Assert.True(match);
    }
```

Run: `dotnet test --filter FullyQualifiedName~WhenPropertyOrderInJsonDiffers_ThenSnapshotsMatch`
Expected: PASS.

- [ ] **Step 1b: Add `WhenSubjectOrderInJsonDiffers_ThenSnapshotsMatch`**

```csharp
    [Fact]
    public void WhenSubjectOrderInJsonDiffers_ThenSnapshotsMatch()
    {
        // Arrange: same content, subjects in different JSON order.
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Object","id":"OTHER"}},"OTHER":{"Q":{"kind":"Value","value":1}}}}""";
        const string b = """{"root":"ROOT","subjects":{"OTHER":{"Q":{"kind":"Value","value":1}},"ROOT":{"P":{"kind":"Object","id":"OTHER"}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert: JSON walk treats the subjects map as unordered.
        Assert.True(match);
    }
```

Run: `dotnet test --filter FullyQualifiedName~WhenSubjectOrderInJsonDiffers_ThenSnapshotsMatch`
Expected: PASS.

- [ ] **Step 2: Add `WhenSubjectKeysDiffer_ThenSnapshotsDoNotMatch`**

```csharp
    [Fact]
    public void WhenSubjectKeysDiffer_ThenSnapshotsDoNotMatch()
    {
        // Arrange
        const string a = """{"root":"ROOT","subjects":{"ROOT":{}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{},"OTHER":{}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert
        Assert.False(match);
    }
```

Run: `dotnet test --filter FullyQualifiedName~WhenSubjectKeysDiffer_ThenSnapshotsDoNotMatch`
Expected: PASS.

- [ ] **Step 3: Add `WhenPropertyKeysDiffer_ThenSnapshotsDoNotMatch`**

```csharp
    [Fact]
    public void WhenPropertyKeysDiffer_ThenSnapshotsDoNotMatch()
    {
        // Arrange
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"A":{"kind":"Value","value":1}}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{"A":{"kind":"Value","value":1},"B":{"kind":"Value","value":2}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert
        Assert.False(match);
    }
```

Run: `dotnet test --filter FullyQualifiedName~WhenPropertyKeysDiffer_ThenSnapshotsDoNotMatch`
Expected: PASS.

- [ ] **Step 4: Add `WhenBothValueTimestampsAreNonNullAndEqual_ThenSnapshotsMatch`**

```csharp
    [Fact]
    public void WhenBothValueTimestampsAreNonNullAndEqual_ThenSnapshotsMatch()
    {
        // Arrange
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":"2026-01-01T00:00:00+00:00"}}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":"2026-01-01T00:00:00+00:00"}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert
        Assert.True(match);
    }
```

Run: `dotnet test --filter FullyQualifiedName~WhenBothValueTimestampsAreNonNullAndEqual_ThenSnapshotsMatch`
Expected: PASS.

- [ ] **Step 5: Add `WhenBothValueTimestampsAreNonNullAndDiffer_ThenSnapshotsDoNotMatch`**

```csharp
    [Fact]
    public void WhenBothValueTimestampsAreNonNullAndDiffer_ThenSnapshotsDoNotMatch()
    {
        // Arrange: same value, different non-null timestamps. Real divergence.
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":"2026-01-01T00:00:00+00:00"}}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":"2026-01-02T00:00:00+00:00"}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert
        Assert.False(match);
    }
```

Run: `dotnet test --filter FullyQualifiedName~WhenBothValueTimestampsAreNonNullAndDiffer_ThenSnapshotsDoNotMatch`
Expected: PASS.

- [ ] **Step 6: Add `WhenOneValueTimestampIsNull_ThenSnapshotsMatch`**

```csharp
    [Fact]
    public void WhenOneValueTimestampIsNull_ThenSnapshotsMatch()
    {
        // Arrange: same value, one side has explicit null (NullTimestampTicks contract).
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":null}}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":"2026-01-01T00:00:00+00:00"}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert: null-timestamp rule applies.
        Assert.True(match);
    }
```

Run: `dotnet test --filter FullyQualifiedName~WhenOneValueTimestampIsNull_ThenSnapshotsMatch`
Expected: PASS.

- [ ] **Step 7: Add `WhenBothValueTimestampsAreNull_ThenSnapshotsMatch`**

```csharp
    [Fact]
    public void WhenBothValueTimestampsAreNull_ThenSnapshotsMatch()
    {
        // Arrange: both sides preserve the explicit-null state.
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":null}}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":null}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert
        Assert.True(match);
    }
```

Run: `dotnet test --filter FullyQualifiedName~WhenBothValueTimestampsAreNull_ThenSnapshotsMatch`
Expected: PASS.

- [ ] **Step 8: Add `WhenValueDiffersAndTimestampsAreNull_ThenSnapshotsDoNotMatch`**

Confirms the rule applies only to the `"timestamp"` field; differing values still fail even when both timestamps are null.

```csharp
    [Fact]
    public void WhenValueDiffersAndTimestampsAreNull_ThenSnapshotsDoNotMatch()
    {
        // Arrange
        const string a = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":null}}}}""";
        const string b = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":2,"timestamp":null}}}}""";

        // Act
        var match = SnapshotComparer.SnapshotsMatch(a, b);

        // Assert
        Assert.False(match);
    }
```

Run: `dotnet test --filter FullyQualifiedName~WhenValueDiffersAndTimestampsAreNull_ThenSnapshotsDoNotMatch`
Expected: PASS.

- [ ] **Step 9: Run the full test suite**

Run: `dotnet test src/Namotion.Interceptor.ConnectorTester.Tests/Namotion.Interceptor.ConnectorTester.Tests.csproj`
Expected: 16 tests pass (7 from Task 3 + 9 added here).

- [ ] **Step 10: Commit**

```bash
git add src/Namotion.Interceptor.ConnectorTester.Tests/Engine/SnapshotComparerTests.cs
git commit -m "test(connector-tester): pin SnapshotsMatch null-timestamp rule contract"
```

---

### Task 6: Switch `VerificationEngine` over to `SnapshotComparer`

**Files:**
- Modify: `src/Namotion.Interceptor.ConnectorTester/Engine/VerificationEngine.cs`

Replace the inline `CreateSnapshot` (lines ~232-259 on master) with calls to `SnapshotComparer.Capture`. Replace the two raw string-equality checks (lines ~170, ~208) with `SnapshotComparer.SnapshotsMatch`. Remove now-unused imports.

This is a behaviour-preserving migration for current OPC UA / MQTT runs (those continue to pass) and unblocks the WebSocket / OPC UA structural-mutation rebases.

- [ ] **Step 1: Replace the convergence-loop comparison**

Open `src/Namotion.Interceptor.ConnectorTester/Engine/VerificationEngine.cs`. At line 169-170:

```csharp
                var firstSnapshot = snapshots[0].Snapshot;
                if (snapshots.All(snapshot => snapshot.Snapshot == firstSnapshot))
```

Replace with:

```csharp
                if (snapshots.All(snapshot => SnapshotComparer.SnapshotsMatch(snapshots[0].Snapshot, snapshot.Snapshot)))
```

- [ ] **Step 2: Replace the failure-path mismatch check**

At line 207-209:

```csharp
                foreach (var snapshot in snapshots.Skip(1))
                {
                    if (snapshot.Snapshot != referenceSnapshot.Snapshot)
```

Replace with:

```csharp
                foreach (var snapshot in snapshots.Skip(1))
                {
                    if (!SnapshotComparer.SnapshotsMatch(referenceSnapshot.Snapshot, snapshot.Snapshot))
```

- [ ] **Step 3: Replace the inline `CreateSnapshot` method with a call to `SnapshotComparer.Capture`**

Find the private `CreateSnapshot` method (lines ~232-259):

```csharp
    private static string CreateSnapshot(TestNode root)
    {
        var update = SubjectUpdate.CreateCompleteUpdate(root, []);

        // Strip timestamps from structural properties (Collection, Dictionary, Object).
        // These are set during local graph creation and are inherently different per participant.
        // Value property timestamps ARE compared and must converge via source timestamps.
        if (update.Subjects != null)
        {
            foreach (var subject in update.Subjects.Values)
            {
                if (subject == null)
                {
                    continue;
                }

                foreach (var property in subject.Values)
                {
                    if (property.Kind != SubjectPropertyUpdateKind.Value)
                    {
                        property.Timestamp = null;
                    }
                }
            }
        }

        return JsonSerializer.Serialize(update, SnapshotJsonOptions);
    }
```

Delete the method entirely. Update the two callers in `ExecuteAsync` to call `SnapshotComparer.Capture` directly:

At line 164-167:

```csharp
                var snapshots = _participants
                    .Select(participant => (
                        Name: participant.Key,
                        Snapshot: CreateSnapshot(participant.Value)))
                    .ToList();
```

Replace `CreateSnapshot(participant.Value)` with `SnapshotComparer.Capture(participant.Value)`. Apply the same change to the second occurrence at line 196-199.

- [ ] **Step 4: Remove the now-unused `SnapshotJsonOptions` field and unused imports**

`SnapshotJsonOptions` (line 22-25) is no longer referenced. Delete those lines.

Remove the `using System.Text.Json;` import at line 4 if no other code in the file uses it. (Search for `JsonSerializer`, `JsonSerializerOptions`, etc. before deleting.) Also remove `using Namotion.Interceptor.Connectors.Updates;` if `SubjectUpdate` is no longer referenced inline (it isn't after the swap).

After this step the file should still compile.

- [ ] **Step 5: Build to verify**

Run: `dotnet build src/Namotion.Interceptor.ConnectorTester/Namotion.Interceptor.ConnectorTester.csproj`
Expected: build succeeds.

- [ ] **Step 6: Run unit tests to verify the comparer is unchanged**

Run: `dotnet test src/Namotion.Interceptor.ConnectorTester.Tests/Namotion.Interceptor.ConnectorTester.Tests.csproj`
Expected: 16 tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.ConnectorTester/Engine/VerificationEngine.cs
git commit -m "refactor(connector-tester): use SnapshotComparer in VerificationEngine"
```

---

### Task 7: Add per-cycle JSON snapshot dump on failure

**Files:**
- Modify: `src/Namotion.Interceptor.ConnectorTester/Engine/VerificationEngine.cs`

On convergence failure, write `logs/cycle{N:D3}-fail-{participant}.json` with formatted JSON for each participant. Replaces the current single-line snapshot logging at line 217-219 (which dumps the full normalized JSON inline as a log message; useful only for very small graphs).

- [ ] **Step 1: Add a static field for the indented serializer options**

Near the top of the class (in the existing fields region):

```csharp
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };

    private const string LogsDirectory = "logs";
```

This requires re-adding `using System.Text.Json;` (Task 6 may have removed it) and `using System.Text.Json.Nodes;`.

- [ ] **Step 2: Add the dump helper**

Append this method to the class (after `CompactHeapAndLogCycle`):

```csharp
    /// <summary>
    /// Writes formatted JSON snapshots to disk for each participant, so failures can
    /// be diffed with any text tool. Runs only on convergence failure; never replaces
    /// the failure signal.
    /// </summary>
    private async Task LogFailureSnapshotsAsync(
        List<(string Name, string Snapshot)> snapshots,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(LogsDirectory);

            foreach (var snapshot in snapshots)
            {
                var fileName = $"cycle{_cycleNumber:D3}-fail-{snapshot.Name}.json";
                var filePath = Path.Combine(LogsDirectory, fileName);

                // Re-serialize with indentation for readability.
                var node = JsonNode.Parse(snapshot.Snapshot);
                var formatted = node?.ToJsonString(IndentedJsonOptions) ?? snapshot.Snapshot;

                await File.WriteAllTextAsync(filePath, formatted, cancellationToken);
                _logger.LogError("Snapshot [{Name}] written to {FilePath}", snapshot.Name, filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write failure snapshots to disk");
        }
    }
```

- [ ] **Step 3: Replace the inline snapshot-logging with a call to the new helper**

In the failure block (around line 215-219 on master):

```csharp
                // Log full snapshots
                foreach (var snapshot in snapshots)
                {
                    _logger.LogError("Snapshot [{Name}]: {Snapshot}", snapshot.Name, snapshot.Snapshot);
                }
```

Replace with:

```csharp
                await LogFailureSnapshotsAsync(snapshots, stoppingToken);
```

- [ ] **Step 4: Build and run unit tests**

Run: `dotnet build src/Namotion.Interceptor.ConnectorTester/Namotion.Interceptor.ConnectorTester.csproj`
Expected: build succeeds.

Run: `dotnet test src/Namotion.Interceptor.ConnectorTester.Tests/Namotion.Interceptor.ConnectorTester.Tests.csproj`
Expected: 16 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.ConnectorTester/Engine/VerificationEngine.cs
git commit -m "feat(connector-tester): write per-cycle JSON snapshots on convergence failure"
```

---

### Task 8: Add `LogPropertyDiffsWithTimestamps`

**Files:**
- Modify: `src/Namotion.Interceptor.ConnectorTester/Engine/VerificationEngine.cs`

Walks the JSON diff. For each property that differs between reference and another participant, reads the actual write timestamp from each side's `ISubjectRegistry` and logs a single line. Distinguishes "never written" (`written never`) from "written but converged differently."

- [ ] **Step 1: Add the helper method to the class**

Append this method after `LogFailureSnapshotsAsync`:

```csharp
    /// <summary>
    /// Diffs the snapshots and logs write timestamps for each diverged property.
    /// Helps distinguish "never written" (null timestamp) from "overwritten with default value".
    /// </summary>
    private void LogPropertyDiffsWithTimestamps(List<(string Name, string Snapshot)> snapshots)
    {
        try
        {
            var reference = JsonNode.Parse(snapshots[0].Snapshot)!["subjects"]!.AsObject();
            var referenceParticipant = _participants[snapshots[0].Name];

            for (var i = 1; i < snapshots.Count; i++)
            {
                var other = JsonNode.Parse(snapshots[i].Snapshot)!["subjects"]!.AsObject();
                var otherParticipant = _participants[snapshots[i].Name];

                foreach (var (subjectId, refSubjectNode) in reference)
                {
                    if (refSubjectNode is not JsonObject refProperties)
                    {
                        continue;
                    }

                    if (!other.ContainsKey(subjectId))
                    {
                        _logger.LogError(
                            "  Subject {SubjectId}: missing from {Participant}",
                            subjectId, snapshots[i].Name);
                        continue;
                    }

                    var otherProperties = other[subjectId]!.AsObject();
                    foreach (var (propertyName, refValue) in refProperties)
                    {
                        var otherValue = otherProperties[propertyName];
                        if (refValue?.ToJsonString() == otherValue?.ToJsonString())
                        {
                            continue;
                        }

                        var refTimestamp = TryGetWriteTimestamp(referenceParticipant, subjectId, propertyName);
                        var otherTimestamp = TryGetWriteTimestamp(otherParticipant, subjectId, propertyName);

                        _logger.LogError(
                            "  {SubjectId}.{Property}: {Reference}={RefValue} (written {RefTimestamp}), {Other}={OtherValue} (written {OtherTimestamp})",
                            subjectId, propertyName,
                            snapshots[0].Name, refValue?.ToJsonString() ?? "null", refTimestamp,
                            snapshots[i].Name, otherValue?.ToJsonString() ?? "null", otherTimestamp);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log property diffs with timestamps");
        }
    }

    private static string TryGetWriteTimestamp(TestNode root, string subjectId, string propertyName)
    {
        var registry = ((IInterceptorSubject)root).Context.TryGetService<ISubjectRegistry>();
        if (registry is null)
        {
            return "no-registry";
        }

        if (!registry.KnownSubjects.TryGetValue(subjectId, out var registeredSubject))
        {
            return "unknown-subject";
        }

        if (!registeredSubject.Properties.TryGetValue(propertyName, out var registeredProperty))
        {
            return "unknown-property";
        }

        var timestamp = registeredProperty.Property.TryGetWriteTimestamp();
        return timestamp?.ToString("O") ?? "never";
    }
```

This requires the following `using` directives at the top of the file:

```csharp
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
```

(Add these only if not already present.)

- [ ] **Step 2: Wire the helper into the failure path**

In the failure block, after the snapshot dump call, add:

```csharp
                await LogFailureSnapshotsAsync(snapshots, stoppingToken);
                LogPropertyDiffsWithTimestamps(snapshots);
```

- [ ] **Step 3: Verify the registry lookup API**

Open `src/Namotion.Interceptor.Registry/SubjectRegistry.cs` and confirm the public surface used here exists:

- `ISubjectRegistry` interface has `KnownSubjects` (returns `IReadOnlyDictionary<string, RegisteredSubject>`).
- `RegisteredSubject` has `Properties` (returns `IReadOnlyDictionary<string, RegisteredSubjectProperty>`).
- `RegisteredSubjectProperty` has a `Property` of type `PropertyReference` with `TryGetWriteTimestamp()`.

If the property names on the registry types differ on master (e.g. `Subjects` instead of `KnownSubjects`), update the helper accordingly.

Run: `grep -n "KnownSubjects\|public.*Properties\|class RegisteredSubject\|class RegisteredSubjectProperty" src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubject.cs src/Namotion.Interceptor.Registry/SubjectRegistry.cs`
Expected: confirms property names. Adjust the helper code if names differ.

- [ ] **Step 4: Build**

Run: `dotnet build src/Namotion.Interceptor.ConnectorTester/Namotion.Interceptor.ConnectorTester.csproj`
Expected: build succeeds.

- [ ] **Step 5: Run unit tests**

Run: `dotnet test src/Namotion.Interceptor.ConnectorTester.Tests/Namotion.Interceptor.ConnectorTester.Tests.csproj`
Expected: 16 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.ConnectorTester/Engine/VerificationEngine.cs
git commit -m "feat(connector-tester): add per-property failure diff with write timestamps"
```

---

### Task 9: Add `LogReSyncCheck`

**Files:**
- Modify: `src/Namotion.Interceptor.ConnectorTester/Engine/VerificationEngine.cs`

After convergence failure, take the reference participant's complete state via `SubjectUpdate.CreateCompleteUpdate`, apply it to each diverged participant via `ApplySubjectUpdate(...)`, re-snapshot, and classify the result. Mutates state on the diverged participant; acceptable because the process is shutting down.

- [ ] **Step 1: Add the helper method to the class**

Append this method after `LogPropertyDiffsWithTimestamps`:

```csharp
    /// <summary>
    /// Re-sync diagnostic. Takes the reference participant's complete update and applies
    /// it to each diverged participant, then re-compares.
    /// "Match after re-apply" => suspect connector wire (lost or out-of-order messages).
    /// "Still diverged" => suspect snapshot logic, ApplySubjectUpdate, or the model.
    /// Mutates participant state intentionally; runs only after the cycle has failed
    /// and the process is shutting down.
    /// </summary>
    private void LogReSyncCheck(List<(string Name, string Snapshot)> snapshots)
    {
        try
        {
            var referenceRoot = _participants[snapshots[0].Name];
            var completeUpdate = SubjectUpdate.CreateCompleteUpdate(referenceRoot, []);

            for (var i = 1; i < snapshots.Count; i++)
            {
                if (SnapshotComparer.SnapshotsMatch(snapshots[i].Snapshot, snapshots[0].Snapshot))
                {
                    continue;
                }

                var otherRoot = _participants[snapshots[i].Name];
                otherRoot.ApplySubjectUpdate(completeUpdate, DefaultSubjectFactory.Instance);

                var refReSnapshot = SnapshotComparer.Capture(referenceRoot);
                var otherReSnapshot = SnapshotComparer.Capture(otherRoot);

                if (SnapshotComparer.SnapshotsMatch(refReSnapshot, otherReSnapshot))
                {
                    _logger.LogWarning(
                        "Re-sync check: {Participant} converged after applying reference complete update -> transient delivery gap",
                        snapshots[i].Name);
                }
                else
                {
                    _logger.LogError(
                        "Re-sync check: {Participant} still diverged after applying reference complete update -> suspect snapshot logic, ApplySubjectUpdate, or model",
                        snapshots[i].Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to perform re-sync check");
        }
    }
```

This requires the `using Namotion.Interceptor.Connectors.Updates;` import at the top of the file (re-add if Task 6 removed it). `DefaultSubjectFactory` lives in `Namotion.Interceptor.Connectors`; check whether an additional `using Namotion.Interceptor.Connectors;` import is needed.

- [ ] **Step 2: Verify `DefaultSubjectFactory.Instance` exists and is accessible**

Run: `grep -rn "class DefaultSubjectFactory\|Instance" src/Namotion.Interceptor.Connectors/ | grep -i "DefaultSubjectFactory" | head -5`
Expected: confirms the class and its public `Instance` field/property. Adjust namespace import accordingly.

- [ ] **Step 3: Wire the helper into the failure path**

After `LogPropertyDiffsWithTimestamps`, add:

```csharp
                await LogFailureSnapshotsAsync(snapshots, stoppingToken);
                LogPropertyDiffsWithTimestamps(snapshots);
                LogReSyncCheck(snapshots);
```

- [ ] **Step 4: Build**

Run: `dotnet build src/Namotion.Interceptor.ConnectorTester/Namotion.Interceptor.ConnectorTester.csproj`
Expected: build succeeds.

- [ ] **Step 5: Run unit tests**

Run: `dotnet test src/Namotion.Interceptor.ConnectorTester.Tests/Namotion.Interceptor.ConnectorTester.Tests.csproj`
Expected: 16 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.ConnectorTester/Engine/VerificationEngine.cs
git commit -m "feat(connector-tester): add re-sync diagnostic to classify convergence failures"
```

---

### Task 10: Run an OPC UA integration cycle to confirm no regression

**Files:** none modified.

The unit tests prove the comparer rules. The smoke test proves the swap is behaviour-preserving for the existing OPC UA chaos profile (which is master's primary integration signal for the tester).

This task is operator-driven (the test suite is interactive: it runs for a configured number of mutate/converge cycles). Run it once and observe.

- [ ] **Step 1: Run the OPC UA chaos profile**

Run: `dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile opcua -c Release`

Expected:
- Cycle log shows `=== Cycle 1: PASS ===` within the configured `ConvergenceTimeout` (default 1 minute on master).
- No new error-level log output beyond what master produces.
- `logs/cycles.csv` has a new entry with `Result=PASS`.

If the cycle fails: re-read the log carefully. The new failure diagnostics now print per-property diffs and a re-sync classification. If the re-sync classification says `transient delivery gap` the failure is on the connector wire (not a regression in this PR). If it says `still diverged after re-apply` and the values are different, that points at a real model-level issue surfaced by the new comparer; investigate before proceeding.

- [ ] **Step 2: Cancel the run after Cycle 1 (or 2) passes**

Stop with Ctrl+C once the first cycle has passed; the loop runs indefinitely otherwise.

- [ ] **Step 3: No commit for this task** (observation only).

---

### Task 11: Update `docs/connector-tester.md`

**Files:**
- Modify: `docs/connector-tester.md`

Three sections to rewrite. Use plain sentences (no em dashes per CLAUDE.md style). Use `(parentheses)`, commas, or sentence breaks instead.

- [ ] **Step 1: Rewrite the "Mutate/Converge Cycle" verification paragraph**

Find the paragraph at line 66-68 starting with "Converge phase":

```markdown
2. **Converge phase**: The VerificationEngine pauses all engines via the TestCycleCoordinator, recovers any active chaos disruptions, waits a grace period (20s for OPC UA) for reconnection, then polls snapshots every 5 seconds. Each snapshot serializes the full object graph using `SubjectUpdate.CreateCompleteUpdate()`. Structural property timestamps (Collection, Dictionary, Object) are stripped since they represent local creation time, not synced state. Value property timestamps are preserved and must converge.

A cycle **passes** when all participant snapshots are identical JSON. It **fails** if the convergence timeout expires. On failure, the process logs snapshot diffs, gracefully shuts down all hosted services, and exits with code 1.
```

Replace with:

```markdown
2. **Converge phase**: The VerificationEngine pauses all engines via the TestCycleCoordinator, recovers any active chaos disruptions, waits a grace period (20s for OPC UA) for reconnection, then polls snapshots every 5 seconds. `SnapshotComparer.Capture` produces a normalized JSON per participant from `SubjectUpdate.CreateCompleteUpdate()`. Normalization strips timestamps from Collection/Dictionary/Object properties (these are local graph-creation moments and never sync), replaces each participant's root subject ID with a constant placeholder (`"ROOT"`), sorts dictionary items by their key, and sorts the subjects map and per-subject property keys for deterministic ordering. Collection items keep their source order: order is part of equality.

`SnapshotComparer.SnapshotsMatch` decides convergence. It first tries string equality (the common case after normalization). On mismatch it walks the JSON tree and compares fields. The only special case is the `timestamp` field on Value properties: a null timestamp on either side matches any value (the explicit "never written" state preserved by `SubjectChangeContext.NullTimestampTicks`); two non-null timestamps must be equal. All other fields use strict JSON equality, so any new field added to `SubjectPropertyUpdate` is automatically part of the comparison.

A cycle **passes** when all participant snapshots match by these rules. It **fails** if the convergence timeout expires. On failure, the process writes per-participant JSON snapshots to disk, logs per-property diffs with write timestamps, runs a re-sync diagnostic, gracefully shuts down all hosted services, and exits with code 1.
```

- [ ] **Step 2: Update the "Failure" output section**

Find the block around line 145 starting with "**Failure**":

```markdown
**Failure**: Prints `FAIL` with snapshot diffs, then exits with code 1:

```
=== Cycle 3: FAIL (did not converge within 00:01:00) ===
```

Replace with:

```markdown
**Failure**: Prints `FAIL`, runs failure diagnostics, then exits with code 1:

```
=== Cycle 3: FAIL (did not converge within 00:01:00) ===
Snapshot [server] written to logs/cycle003-fail-server.json
Snapshot [client-a] written to logs/cycle003-fail-client-a.json
  ROOT.IntValue: server=42 (written 12:34:56.789), client-a=37 (written 12:34:55.110)
Re-sync check: client-a converged after applying reference complete update -> transient delivery gap
```

The per-cycle JSON files are formatted (indented) and can be diffed with any text tool. The `cycleNNN-fail-{participant}.json` files are the canonical artifact for investigating divergence.
```

- [ ] **Step 3: Update "What the Tester Detects"**

Find the section header (around line 370) and add bullets describing the new diagnostics. The exact insertion point depends on the existing layout; add a short subsection or extend the existing list:

```markdown
The failure diagnostics localize bugs:

- **Per-cycle JSON snapshots** (`logs/cycleNNN-fail-{participant}.json`): full normalized state per participant for diff investigation.
- **Per-property diffs with timestamps**: lists every diverged property with each side's value and write timestamp. `written never` means the property was never written via the interceptor chain on that participant. `written T` means it was written at time T.
- **Re-sync classifier**: applies the reference participant's complete state to each diverged participant and re-compares. If the result matches, the failure is a **transient delivery gap** (look at the connector wire: lost or out-of-order messages, missed reconnect catch-up). If it still diverges, the failure is in the snapshot logic, `SubjectUpdate.CreateCompleteUpdate`, `ApplySubjectUpdate`, or the `TestNode` model itself: the connector wire is exonerated.
```

- [ ] **Step 4: Build sanity-check (markdown only, no compilation)**

Run: `git diff docs/connector-tester.md | wc -l`
Expected: nontrivial diff.

Run: `grep -n "—\|–" docs/connector-tester.md | head` (em / en dash check per CLAUDE.md)
Expected: no output (or only pre-existing matches outside the touched sections).

- [ ] **Step 5: Commit**

```bash
git add docs/connector-tester.md
git commit -m "docs(connector-tester): document SnapshotComparer rules and failure diagnostics"
```

---

### Task 12: Final verification

**Files:** none modified.

- [ ] **Step 1: Build the entire solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: build succeeds.

- [ ] **Step 2: Run all non-integration tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: all tests pass (existing tests + 16 new SnapshotComparer tests).

- [ ] **Step 3: Sanity-check the final diff**

Run: `git diff master --stat`
Expected: changes confined to `src/Namotion.Interceptor.ConnectorTester/`, `src/Namotion.Interceptor.ConnectorTester.Tests/` (new), `src/Namotion.Interceptor.slnx`, `docs/connector-tester.md`. Roughly:
- `SnapshotComparer.cs`: ~180 lines added
- `SnapshotComparerTests.cs`: ~280 lines added
- `Namotion.Interceptor.ConnectorTester.Tests.csproj`: ~30 lines added
- `VerificationEngine.cs`: ~150 lines added (helpers), ~30 lines deleted (inline `CreateSnapshot` + inline mismatch logging)
- `slnx`: 1 line added
- `connector-tester.md`: ~3 sections rewritten

If files outside this set are modified, investigate before continuing.

- [ ] **Step 4: No commit for this task** (observation only).

---

## Out of scope (do NOT do in this PR)

- Do not modify `MutationEngine`, `TestNode`, `Configuration/`, `Program.cs`, or any connector code (OPC UA / MQTT / WebSocket source files).
- Do not remove the load-test infrastructure (`PerformanceProfiler.cs`, load profiles, `cycles.csv`) added by PR #279.
- Do not add WebSocket-specific diagnostics (`LogSequenceDiagnostics`); that belongs on PR #197.
- Do not enable structural mutations on OPC UA / MQTT; that is separate work.
