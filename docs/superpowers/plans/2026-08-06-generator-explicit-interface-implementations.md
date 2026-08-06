# Generator Explicit Interface Implementations and Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the source generator emit compiling, correct code for explicit interface implementations and the wider family of inputs that currently produce broken output, and report every unsupported input as a diagnostic instead of silence.

**Architecture:** Three layers of the generator change. `SubjectMetadataExtractor` resolves property names and accessor interfaces from Roslyn symbols rather than raw `IPropertySymbol.Name`, and gains guards for members that cannot be supported. `SubjectCodeGenerator` emits through indexer assignment, honours the declared accessibility and containing type keyword, and skips the namespace block for global-namespace subjects. `InterceptorSubjectGenerator` gains a diagnostic pipeline via a new `ExtractionResult` return type.

**Tech Stack:** C# 13, .NET Standard 2.0 (generator), .NET 9.0 (tests), Microsoft.CodeAnalysis.CSharp 4.14.0, xUnit, Verify.Xunit 31.3.0.

**Spec:** `docs/superpowers/specs/2026-08-06-generator-explicit-interface-implementations-design.md`

**Branch:** `fix/generator-explicit-interface-implementations`

## Global Constraints

- Target frameworks: generator is `netstandard2.0`, tests are `net9.0`. Generator code must not use .NET 9 only APIs.
- `src/Directory.Build.props` sets `TreatWarningsAsErrors`. Any warning in any project under `src/` fails the build, including the test project.
- The generator project sets `EnforceExtendedAnalyzerRules`. Every `DiagnosticDescriptor` must be listed in `AnalyzerReleases.Unshipped.md` or `RS2008` fails the build.
- Test naming: `When<Condition>_Then<ExpectedBehavior>`. Explicit `// Arrange`, `// Act`, `// Assert` comments. Use `// Act & Assert` for exception tests.
- No hardcoded waits. Not expected to arise in this work.
- Never include AI attribution in commit messages. No `Co-Authored-By`, no "Generated with" footers.
- No em dashes in any documentation file.
- Diagnostic ID prefix is `NI`, category is `Namotion.Interceptor`.
- Accept Verify snapshots with `DiffEngine_Disabled=true` in the environment.
- Run tests with: `dotnet test src/Namotion.Interceptor.Generator.Tests`

## Ordering note: this plan deviates from the spec's commit sequence

The spec proposed the test harness first, then duplicate keys, then the global namespace fix. While planning I found that **14 of the 16 existing `.verified.txt` snapshots contain `namespace YourDefaultNamespace`**, because nearly every existing test source declares its subject in the global namespace. Those generated files do not compile today (`CS9248`, `CS9249`).

Consequence: a compile-clean assertion added in the first task would fail on 14 pre-existing tests before any fix lands. So the global namespace fix (spec 1.3) moves ahead of the compile-clean assertion, which becomes its own task once a clean baseline is actually achievable.

Verified: with the reference strategy in Task 1 and the namespace fix in Task 2, representative subject, interface-default and nested-class sources all compile with zero errors.

## File Structure

**Generator (`src/Namotion.Interceptor.Generator/`)**

| File | Responsibility | Tasks |
|------|----------------|-------|
| `SubjectMetadataExtractor.cs` | Symbol to metadata. Name resolution, guards, deduplication, diagnostics collection | 2, 4, 5, 6, 7, 8, 10, 11 |
| `SubjectCodeGenerator.cs` | Metadata to C# text. Namespace, accessibility, containing types, dictionary emission | 2, 4, 5, 6, 8 |
| `InterceptorSubjectGenerator.cs` | Incremental pipeline. Reports diagnostics, decides whether to emit | 9, 10, 11 |
| `Models/SubjectMetadata.cs` | Metadata record. Gains nullable namespace, access modifier, `ContainingType[]` | 2, 6, 8 |
| `Models/PropertyMetadata.cs` | Property record. Gains `ExplicitInterfaceTypeName` | 4 |
| `Models/ContainingType.cs` | New. Keyword plus name for a containing type | 8 |
| `Models/ExtractionResult.cs` | New. Metadata plus diagnostics | 9 |
| `Diagnostics.cs` | New. All `DiagnosticDescriptor` definitions | 9, 10, 11 |
| `AnalyzerReleases.Shipped.md` | New. Empty shipped release file | 9 |
| `AnalyzerReleases.Unshipped.md` | New. Lists NI0001 to NI0010 | 9, 10, 11 |

**Tests (`src/Namotion.Interceptor.Generator.Tests/`)**

| File | Responsibility | Tasks |
|------|----------------|-------|
| `GeneratorTestHost.cs` | New. The single generator invocation helper. Replaces three private copies | 1, 3 |
| `Snapshots/` | All Verify snapshots, consolidated from the project root | 1 |
| `ExplicitInterfaceTests.cs` | New. Cases A, B, C, D, F, Z, AA as generator tests | 4, 5 |
| `ExplicitInterfaceBehaviorTests.cs` | New. Real subjects for A, B, C, I, Z, AD | 4, 5, 11 |
| `GeneratorShapeTests.cs` | New. Cases E, J, O, P, Q, R, S, V, W, Y | 2, 6, 7, 8 |
| `GeneratorShapeBehaviorTests.cs` | New. Real subjects for P, S, W and override inheritance | 6, 7, 8 |
| `DiagnosticTests.cs` | New. One test per rule | 10, 11 |

---

### Task 1: Consolidate the test harness

Three private generator-invocation helpers exist with different names and different reference strategies. They are replaced by one host. No behavioural assertion is added yet; that is Task 3.

**Files:**
- Create: `src/Namotion.Interceptor.Generator.Tests/GeneratorTestHost.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/SourceGeneratorTests.cs` (remove private `GeneratedSourceCode`, around line 222 to the end of the class)
- Modify: `src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs` (remove private `GenerateCode`, around line 202 to the end of the class)
- Modify: `src/Namotion.Interceptor.Generator.Tests/VirtualPartialTests.cs` (remove private `CreateCompilation` and `GenerateCode`, around line 140 to the end of the class)
- Move: all 13 `*.verified.txt` files from the project root into `Snapshots/`

**Interfaces:**
- Produces: `GeneratorTestHost.Run(string source) -> GeneratorRunResult`
- Produces: `GeneratorRunResult(IReadOnlyList<GeneratedSourceResult> Sources, IReadOnlyList<Diagnostic> GeneratorDiagnostics, IReadOnlyList<Diagnostic> CompilationErrors)` with helpers `SingleSource()` and `AllSources()`

- [ ] **Step 1: Create the host**

Create `src/Namotion.Interceptor.Generator.Tests/GeneratorTestHost.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Namotion.Interceptor.Generator.Tests;

/// <summary>
/// The outcome of running the generator over a single source snippet.
/// </summary>
internal sealed record GeneratorRunResult(
    IReadOnlyList<GeneratedSourceResult> Sources,
    IReadOnlyList<Diagnostic> GeneratorDiagnostics,
    IReadOnlyList<Diagnostic> CompilationErrors)
{
    public string SingleSource() => Sources.Single().SourceText.ToString();

    public string AllSources() => string.Join("\n\n", Sources.Select(s => s.SourceText));
}

/// <summary>
/// Runs the incremental generator against an in-memory compilation.
/// </summary>
internal static class GeneratorTestHost
{
    private static readonly IReadOnlyList<MetadataReference> References = CreateReferences();

    public static GeneratorRunResult Run(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [syntaxTree],
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new InterceptorSubjectGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        var runResult = driver.GetRunResult();

        return new GeneratorRunResult(
            runResult.Results.SelectMany(result => result.GeneratedSources).ToList(),
            runResult.Diagnostics.ToList(),
            outputCompilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToList());
    }

    /// <summary>
    /// The trusted platform assembly set is used instead of the loaded assemblies, because
    /// System.Text.Json is not loaded in the test AppDomain and the generated code needs
    /// JsonIgnore to resolve.
    /// </summary>
    private static IReadOnlyList<MetadataReference> CreateReferences()
    {
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(path => !string.IsNullOrWhiteSpace(path));

        var loadedAssemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(assembly => !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location))
            .Select(assembly => assembly.Location);

        return trustedAssemblies
            .Concat(loadedAssemblies)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
    }
}
```

- [ ] **Step 2: Point the three test classes at the host**

In `SourceGeneratorTests.cs`, delete the private `GeneratedSourceCode` method and replace every call site. A call that was:

```csharp
var generated = GeneratedSourceCode(source);
var generatedSource = generated.Single().SourceText.ToString();
return Verify(generatedSource).UseDirectory("Snapshots");
```

becomes:

```csharp
var generated = GeneratorTestHost.Run(source);
return Verify(generated.SingleSource()).UseDirectory("Snapshots");
```

A call that joined multiple sources becomes `Verify(generated.AllSources()).UseDirectory("Snapshots")`.

In `InterfaceDefaultPropertyTests.cs`, delete the private `GenerateCode` method. Calls that were:

```csharp
var generated = GenerateCode(source);
var generatedSource = generated.Single().SourceText.ToString();
```

become:

```csharp
var generated = GeneratorTestHost.Run(source);
var generatedSource = generated.SingleSource();
```

and every `Verify(...)` in the file gains `.UseDirectory("Snapshots")`.

In `VirtualPartialTests.cs`, delete the private `CreateCompilation` and `GenerateCode` methods and switch call sites to `GeneratorTestHost.Run`. Its `Verify(...).UseDirectory("Snapshots")` calls already exist and stay as they are.

- [ ] **Step 3: Move the snapshots**

```bash
cd /Users/ricosuter/Projects/GitHub/Namotion.Interceptor
git mv src/Namotion.Interceptor.Generator.Tests/*.verified.txt src/Namotion.Interceptor.Generator.Tests/Snapshots/
```

Expected: 13 files move. `Snapshots/` then holds 16.

- [ ] **Step 4: Run the full generator test suite**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests`

Expected: all tests pass. Snapshot **content** is unchanged by this task, only the file location and the helper. If a snapshot mismatch appears, the `UseDirectory("Snapshots")` call is missing from that test.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Generator.Tests
git commit -m "Test: consolidate the three generator helpers into one host

Replaces three private copies with differing reference strategies. The host
references the trusted platform assembly set so System.Text.Json resolves,
which is a precondition for asserting that generated code compiles.

Also moves the 13 root-level snapshots into Snapshots/ alongside the three
already there."
```

---

### Task 2: Global namespace (spec 1.3, case J)

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs:81-93` (`GetNamespace`)
- Modify: `src/Namotion.Interceptor.Generator/Models/SubjectMetadata.cs:7` (`NamespaceName` becomes nullable)
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs:17,29,37-43,73-82`
- Create: `src/Namotion.Interceptor.Generator.Tests/GeneratorShapeTests.cs`
- Modify: 14 snapshots under `Snapshots/`

**Interfaces:**
- Consumes: `GeneratorTestHost.Run` from Task 1
- Produces: `SubjectMetadata.NamespaceName` is now `string?`, null meaning the global namespace

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Generator.Tests/GeneratorShapeTests.cs`:

```csharp
namespace Namotion.Interceptor.Generator.Tests;

public class GeneratorShapeTests
{
    [Fact]
    public void WhenSubjectIsInGlobalNamespace_ThenGeneratedCodeCompiles()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class GlobalSubject
{
    public partial string Name { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain("YourDefaultNamespace", generated.SingleSource());
        Assert.Empty(generated.CompilationErrors);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenSubjectIsInGlobalNamespace"`

Expected: FAIL. The generated source contains `namespace YourDefaultNamespace` and `CompilationErrors` holds `CS9248` and `CS9249`.

- [ ] **Step 3: Make the namespace nullable in the extractor**

In `SubjectMetadataExtractor.cs`, replace `GetNamespace`:

```csharp
    private static string? GetNamespace(ClassDeclarationSyntax classDeclaration)
    {
        // Walk up past containing types to find namespace
        SyntaxNode? current = classDeclaration.Parent;
        while (current is TypeDeclarationSyntax)
        {
            current = current.Parent;
        }

        // null means the global namespace: the generated file must not declare one.
        return (current as NamespaceDeclarationSyntax)?.Name.ToString() ??
               (current as FileScopedNamespaceDeclarationSyntax)?.Name.ToString();
    }
```

- [ ] **Step 4: Make the metadata field nullable**

In `Models/SubjectMetadata.cs`, change line 7:

```csharp
    string? NamespaceName,
```

- [ ] **Step 5: Skip the namespace block in the emitter**

In `SubjectCodeGenerator.cs`, replace `EmitNamespaceOpening` and `EmitNamespaceClosing`:

```csharp
    private static void EmitNamespaceOpening(StringBuilder builder, string? namespaceName)
    {
        if (namespaceName is null)
        {
            return;
        }

        builder.AppendLine($"namespace {namespaceName}");
        builder.AppendLine("{");
    }

    private static void EmitNamespaceClosing(StringBuilder builder, string? namespaceName)
    {
        if (namespaceName is null)
        {
            return;
        }

        builder.AppendLine("}");
    }
```

Update the call at line 29 to pass the namespace:

```csharp
        EmitNamespaceClosing(builder, metadata.NamespaceName);
```

- [ ] **Step 6: Fix the generated file name**

In `SubjectCodeGenerator.cs`, replace `GetFileName`:

```csharp
    public static string GetFileName(SubjectMetadata metadata)
    {
        var containingTypesPath = metadata.ContainingTypes.Length > 0
            ? string.Join(".", metadata.ContainingTypes) + "."
            : "";
        var namespacePrefix = metadata.NamespaceName is null
            ? ""
            : metadata.NamespaceName + ".";

        return $"{namespacePrefix}{containingTypesPath}{metadata.ClassName}.g.cs";
    }
```

- [ ] **Step 7: Run the new test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenSubjectIsInGlobalNamespace"`

Expected: PASS.

- [ ] **Step 8: Re-baseline the affected snapshots**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Generator.Tests`

Expected: 14 snapshot tests fail and write `.received.txt` files. Accept them:

```bash
cd src/Namotion.Interceptor.Generator.Tests/Snapshots
for f in *.received.txt; do mv "$f" "${f%.received.txt}.verified.txt"; done
```

Verify the diff removes the `namespace YourDefaultNamespace` wrapper and nothing else:

```bash
cd /Users/ricosuter/Projects/GitHub/Namotion.Interceptor
git diff --stat src/Namotion.Interceptor.Generator.Tests/Snapshots
```

Expected: 14 files changed. The two nested-class snapshots (`WhenGeneratingNestedClass...`, `WhenGeneratingDeepNestedClass...`) are **not** among them, because their sources declare a namespace.

- [ ] **Step 9: Run the full suite**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests`

Expected: all pass.

- [ ] **Step 10: Commit**

```bash
git add src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests
git commit -m "Generate subjects in the global namespace instead of a placeholder one

GetNamespace fell back to the literal string YourDefaultNamespace, so the
generated partial landed in a different namespace than the user's class and
the two halves never joined. A null namespace now means the global namespace
and the generated file omits the block entirely.

Re-baselines 14 snapshots whose test sources declare their subject in the
global namespace and therefore never compiled."
```

---

### Task 3: Assert that generated code compiles

Now that a clean baseline exists, make it the default assertion. This is the guard that would have caught #428.

**Files:**
- Modify: `src/Namotion.Interceptor.Generator.Tests/GeneratorTestHost.cs`

**Interfaces:**
- Consumes: `GeneratorTestHost.Run` from Task 1
- Produces: `GeneratorTestHost.RunExpectingCleanCompilation(string source) -> GeneratorRunResult`

- [ ] **Step 1: Add the asserting entry point**

Append to `GeneratorTestHost`:

```csharp
    /// <summary>
    /// Runs the generator and fails the test if the resulting compilation has any error.
    /// Use for inputs that are themselves valid C#. Inputs that are invalid by construction
    /// (CS0754, CS0592) must use <see cref="Run"/> and assert on the expected error instead.
    /// </summary>
    public static GeneratorRunResult RunExpectingCleanCompilation(string source)
    {
        var result = Run(source);

        Assert.True(
            result.CompilationErrors.Count == 0,
            "Generated code did not compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, result.CompilationErrors.Select(d => d.ToString())));

        return result;
    }
```

- [ ] **Step 2: Switch the snapshot tests to the asserting entry point**

In `SourceGeneratorTests.cs`, `InterfaceDefaultPropertyTests.cs` and `VirtualPartialTests.cs`, replace every `GeneratorTestHost.Run(source)` with `GeneratorTestHost.RunExpectingCleanCompilation(source)`.

Leave `GeneratorShapeTests.WhenSubjectIsInGlobalNamespace_ThenGeneratedCodeCompiles` as it is; it asserts `Empty(CompilationErrors)` directly.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests`

Expected: all pass. If a test fails here, its source is itself one of the broken shapes and the failure is real information. Record which case it is and stop; do not weaken the assertion.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Generator.Tests
git commit -m "Test: fail when generated code does not compile

The existing tests asserted on generated text, so a test could pass on code
that could never build. That is how the explicit interface implementation
defect shipped."
```

---

### Task 4: Duplicate key elimination (spec 1.2, cases Z and AA)

Comes before the name resolution fix, because that fix would otherwise turn case AA from a build error into a runtime crash.

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/Models/PropertyMetadata.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs:52,124-161`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs:175-217`
- Create: `src/Namotion.Interceptor.Generator.Tests/ExplicitInterfaceBehaviorTests.cs`
- Modify: all 16 snapshots under `Snapshots/`

**Interfaces:**
- Consumes: `GeneratorTestHost.RunExpectingCleanCompilation` from Task 3
- Produces: `PropertyMetadata.ExplicitInterfaceTypeName` (`string?`, null when the declaration is not an explicit implementation). Task 5 uses it to route onto the interface template.

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Generator.Tests/ExplicitInterfaceBehaviorTests.cs`:

```csharp
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests;

#region Case Z: a class that declares a property and explicitly implements the same member

public interface ICaseZKind
{
    string Kind { get; }
}

[InterceptorSubject]
public partial class CaseZSubject : ICaseZKind
{
    public partial string Kind { get; set; }

    string ICaseZKind.Kind => "explicit";
}

#endregion

public class ExplicitInterfaceBehaviorTests
{
    [Fact]
    public void WhenClassDeclaresAndExplicitlyImplementsSameProperty_ThenSinglePropertyIsExposed()
    {
        // Arrange
        var subject = new CaseZSubject { Kind = "tracked" };

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Single(properties.Where(p => p.Key == "Kind"));
        Assert.Equal("tracked", properties["Kind"].GetValue?.Invoke(subject));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenClassDeclaresAndExplicitlyImplementsSameProperty"`

Expected: FAIL with `TypeInitializationException` whose inner exception is `ArgumentException: An item with the same key has already been added. Key: Kind`.

- [ ] **Step 3: Record the explicit interface on class properties**

In `Models/PropertyMetadata.cs`, add a parameter after `InterfaceTypeName`:

```csharp
namespace Namotion.Interceptor.Generator.Models;

internal sealed record PropertyMetadata(
    string Name,
    string FullTypeName,
    string AccessModifier,
    bool IsPartial,
    bool IsVirtual,
    bool IsOverride,
    bool IsDerived,
    bool IsRequired,
    bool HasGetter,
    bool HasSetter,
    bool HasInit,
    bool IsFromInterface,
    string? GetterAccessModifier,
    string? SetterAccessModifier,
    string? InterfaceTypeName = null,
    string? ExplicitInterfaceTypeName = null);
```

In `SubjectMetadataExtractor.CollectProperties`, immediately after `var propertyName = property.Identifier.ValueText;` add:

```csharp
                var explicitInterfaceTypeName = property.ExplicitInterfaceSpecifier is { } explicitSpecifier
                    ? declarationModel
                        .GetTypeInfo(explicitSpecifier.Name, cancellationToken)
                        .Type?
                        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    : null;
```

and pass it as the final argument of the `new PropertyMetadata(...)` call in that method:

```csharp
                    getterAccessModifier,
                    setterAccessModifier,
                    InterfaceTypeName: null,
                    ExplicitInterfaceTypeName: explicitInterfaceTypeName));
```

- [ ] **Step 4: Deduplicate class properties, preferring the non-explicit declaration**

In `SubjectMetadataExtractor`, add:

```csharp
    /// <summary>
    /// Two declarations can share a name when a class declares a property and also explicitly
    /// implements the same interface member. Emitting both produces duplicate dictionary keys,
    /// so the non-explicit declaration wins, matching what the runtime resolves.
    /// </summary>
    private static IReadOnlyList<PropertyMetadata> DeduplicateByName(IReadOnlyList<PropertyMetadata> properties)
    {
        var result = new List<PropertyMetadata>();
        var indexByName = new Dictionary<string, int>();

        foreach (var property in properties)
        {
            if (!indexByName.TryGetValue(property.Name, out var index))
            {
                indexByName[property.Name] = result.Count;
                result.Add(property);
                continue;
            }

            if (result[index].ExplicitInterfaceTypeName is not null &&
                property.ExplicitInterfaceTypeName is null)
            {
                result[index] = property;
            }
        }

        return result;
    }
```

and change line 52 in `Extract`:

```csharp
        var classProperties = DeduplicateByName(CollectProperties(typeSymbol, semanticModel, cancellationToken));
```

- [ ] **Step 5: Emit through the dictionary indexer**

In `SubjectCodeGenerator.EmitDefaultProperties`, both branches change from collection-initializer entries to indexer assignment. The interface branch becomes:

```csharp
                builder.AppendLine($"                    [\"{property.Name}\"] = new SubjectPropertyMetadata(");
                builder.AppendLine($"                        typeof({property.InterfaceTypeName}).GetProperty(nameof({property.InterfaceTypeName}.{property.Name}), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!,");
                builder.AppendLine($"                        {getterLambda},");
                builder.AppendLine($"                        {setterLambda},");
                builder.AppendLine("                        isIntercepted: false,");
                builder.AppendLine("                        isDynamic: false),");
```

and the class branch:

```csharp
                builder.AppendLine($"                    [\"{property.Name}\"] = new SubjectPropertyMetadata(");
                builder.AppendLine($"                        typeof({metadata.ClassName}).GetProperty(nameof({property.Name}), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!,");
                builder.AppendLine($"                        {getterLambda},");
                builder.AppendLine($"                        {setterLambda},");
                builder.AppendLine($"                        isIntercepted: {(property.IsPartial ? "true" : "false")},");
                builder.AppendLine("                        isDynamic: false),");
```

Delete the `builder.AppendLine("                {");` and `builder.AppendLine("                },");` lines that wrapped each entry in both branches.

Indexer assignment inside an object initializer is last-wins rather than throwing, so a residual collision degrades to a wrong value rather than a crash. The extractor should already have prevented one.

- [ ] **Step 6: Run the new test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenClassDeclaresAndExplicitlyImplementsSameProperty"`

Expected: PASS.

- [ ] **Step 6b: Pin the inheritance regression**

An `override` partial property puts the same key in the derived dictionary and, through `Concat`, in
the base one. `ToFrozenDictionary` tolerates that, unlike the within-class collection initializer this
task replaces, so the guard is that switching to indexer assignment does not disturb it.

Add to `ExplicitInterfaceBehaviorTests.cs`, above the test class:

```csharp
#region Inheritance regression: an override partial property must not duplicate the base key

[InterceptorSubject]
public partial class OverrideBase
{
    public virtual partial string Name { get; set; }
}

[InterceptorSubject]
public partial class OverrideDerived : OverrideBase
{
    public override partial string Name { get; set; }
}

#endregion
```

and this test to the class:

```csharp
    [Fact]
    public void WhenDerivedOverridesPartialProperty_ThenSingleKeyIsExposed()
    {
        // Arrange
        var subject = new OverrideDerived { Name = "value" };

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Single(properties.Where(p => p.Key == "Name"));
        Assert.Equal("value", properties["Name"].GetValue?.Invoke(subject));
    }
```

Case AA, two explicit implementations colliding on one name, is deliberately **not** added here. Its
model cannot compile until Task 5 routes explicitly implemented class properties through the interface,
so it belongs to Task 5 Step 8.

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~ExplicitInterfaceBehaviorTests"`

Expected: PASS.

- [ ] **Step 7: Re-baseline all snapshots**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Generator.Tests`

Expected: all 16 snapshot tests fail on the entry syntax change. Accept:

```bash
cd src/Namotion.Interceptor.Generator.Tests/Snapshots
for f in *.received.txt; do mv "$f" "${f%.received.txt}.verified.txt"; done
```

Confirm the diff is only `{ "X", new SubjectPropertyMetadata(` becoming `["X"] = new SubjectPropertyMetadata(` plus brace removal.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests`

Expected: all pass.

- [ ] **Step 9: Commit**

```bash
git add src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests
git commit -m "Answer a duplicated property name from one entry instead of crashing

A class that declares a property and also explicitly implements the same
interface member produced two dictionary entries under one key, which compiled
and then threw TypeInitializationException on first access. The non-explicit
declaration now wins, matching what the runtime resolves.

DefaultProperties is emitted through the dictionary indexer so a residual
collision is last-wins rather than a crash."
```

---

### Task 5: Explicit interface implementations (spec 1.1, cases A, B, C, D, F)

The reported defect.

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs:215-284` (`ExtractInterfaceDefaultProperties`)
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs:175-217` (branch selection)
- Create: `src/Namotion.Interceptor.Generator.Tests/ExplicitInterfaceTests.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/ExplicitInterfaceBehaviorTests.cs`

**Interfaces:**
- Consumes: `PropertyMetadata.ExplicitInterfaceTypeName` from Task 4, `GeneratorTestHost.RunExpectingCleanCompilation` from Task 3

- [ ] **Step 1: Write the failing generator tests**

Create `src/Namotion.Interceptor.Generator.Tests/ExplicitInterfaceTests.cs`:

```csharp
namespace Namotion.Interceptor.Generator.Tests;

public class ExplicitInterfaceTests
{
    [Fact]
    public void WhenSubInterfaceExplicitlyImplementsMember_ThenGeneratedCodeCompiles()
    {
        // Arrange (case A, the shape reported in issue 428)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public enum Gender { Male, Female }
    public interface IHuman { Gender Gender { get; } }
    public interface IMale : IHuman { Gender IHuman.Gender => Gender.Male; }

    [InterceptorSubject]
    public partial class John : IMale { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Gender""]", generatedSource);
        Assert.Contains("((global::Repro.IHuman)o).Gender", generatedSource);
        Assert.DoesNotContain("IHuman.Gender)", generatedSource);
    }

    [Fact]
    public void WhenClassExplicitlyImplementsMember_ThenGeneratedCodeCompiles()
    {
        // Arrange (case B)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public enum Gender { Male, Female }
    public interface IHuman { Gender Gender { get; } }

    [InterceptorSubject]
    public partial class John : IHuman
    {
        Gender IHuman.Gender => Gender.Male;
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Contains("((global::Repro.IHuman)o).Gender", generated.SingleSource());
    }

    [Fact]
    public void WhenClassDeclaresPropertyAndInheritsExplicitImplementation_ThenGeneratedCodeCompiles()
    {
        // Arrange (case C)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public enum Gender { Male, Female }
    public interface IHuman { Gender Gender { get; } }
    public interface IMale : IHuman { Gender IHuman.Gender => Gender.Male; }

    [InterceptorSubject]
    public partial class John : IMale
    {
        public partial Gender Gender { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert: the tracked class property wins, so it is intercepted
        var generatedSource = generated.SingleSource();
        Assert.Contains("isIntercepted: true", generatedSource);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(generatedSource, @"\[""Gender""\]"));
    }

    [Fact]
    public void WhenExplicitImplementationTargetsNestedInterface_ThenGeneratedCodeCompiles()
    {
        // Arrange (case D)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public enum Gender { Male, Female }
    public partial class Outer { public interface IHuman { Gender Gender { get; } } }
    public interface IMale : Outer.IHuman { Gender Outer.IHuman.Gender => Gender.Male; }

    [InterceptorSubject]
    public partial class John : IMale { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Contains(@"[""Gender""]", generated.SingleSource());
    }

    [Fact]
    public void WhenExplicitImplementationTargetsGenericInterface_ThenGeneratedCodeCompiles()
    {
        // Arrange (case F)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman<T> { T Value { get; } }
    public interface IIntHuman : IHuman<int> { int IHuman<int>.Value => 42; }

    [InterceptorSubject]
    public partial class John : IIntHuman { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Value""]", generatedSource);
        Assert.Contains("((global::Repro.IHuman<int>)o).Value", generatedSource);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~ExplicitInterfaceTests"`

Expected: all five FAIL. Cases A, C, D and F fail on `CS0117` and `CS1061`. Case B fails on `CS1061` and `CS0103`.

- [ ] **Step 3: Resolve the name and accessor interface from the symbol**

In `SubjectMetadataExtractor.ExtractInterfaceDefaultProperties`, inside the `foreach (var member in interfaceType.GetMembers())` loop, replace the block from the `classPropertyNames` guard through the `processedPropertyNames.Add(...)` call with:

```csharp
                // For an explicit implementation, IPropertySymbol.Name is the fully qualified
                // "Namespace.IHuman.Gender". The implemented member carries the simple name, and
                // its containing type is the interface the accessor must cast through: reflection
                // on the declaring interface does not find the member, and the implemented one
                // dispatches correctly in every direction.
                var explicitImplementation = property.ExplicitInterfaceImplementations.FirstOrDefault();
                var resolvedName = explicitImplementation?.Name ?? property.Name;
                var accessorInterface = explicitImplementation?.ContainingType ?? interfaceType;

                // Skip properties already declared in the class
                if (classPropertyNames.Contains(resolvedName))
                {
                    continue;
                }

                // Skip properties already processed from another interface (diamond inheritance)
                if (processedPropertyNames.Contains(resolvedName))
                {
                    continue;
                }

                // A property has a default implementation if any accessor is not abstract
                var hasDefaultImplementation =
                    property.GetMethod is { IsAbstract: false } ||
                    property.SetMethod is { IsAbstract: false };
                if (!hasDefaultImplementation)
                {
                    continue;
                }

                processedPropertyNames.Add(resolvedName);
```

Then change the two lines that build the metadata:

```csharp
                var interfaceTypeName = accessorInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
```

and the `new PropertyMetadata(` call's first argument:

```csharp
                interfaceProperties.Add(new PropertyMetadata(
                    resolvedName,
```

- [ ] **Step 4: Route explicitly implemented class properties onto the interface template**

In `SubjectCodeGenerator.EmitDefaultProperties`, replace the branch condition. The loop body becomes:

```csharp
        foreach (var property in metadata.Properties)
        {
            // An explicitly implemented member is unreachable through the class, so it is emitted
            // through the interface exactly like an interface default property.
            var accessorInterfaceTypeName = property.IsFromInterface
                ? property.InterfaceTypeName
                : property.ExplicitInterfaceTypeName;

            if (accessorInterfaceTypeName is not null)
            {
                var getterLambda = property.HasGetter
                    ? $"(o) => (({accessorInterfaceTypeName})o).{property.Name}"
                    : "null";
                var setterLambda = property.HasSetter
                    ? $"(o, v) => (({accessorInterfaceTypeName})o).{property.Name} = ({property.FullTypeName})v"
                    : "null";

                builder.AppendLine($"                    [\"{property.Name}\"] = new SubjectPropertyMetadata(");
                builder.AppendLine($"                        typeof({accessorInterfaceTypeName}).GetProperty(nameof({accessorInterfaceTypeName}.{property.Name}), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!,");
                builder.AppendLine($"                        {getterLambda},");
                builder.AppendLine($"                        {setterLambda},");
                builder.AppendLine("                        isIntercepted: false,");
                builder.AppendLine("                        isDynamic: false),");
            }
            else
            {
                var getterLambda = property.HasGetter
                    ? $"(o) => (({metadata.ClassName})o).{property.Name}"
                    : "null";
                // Note: init-only properties cannot have a setter lambda because they can only be set during construction
                var setterLambda = property.HasSetter
                    ? $"(o, v) => (({metadata.ClassName})o).{property.Name} = ({property.FullTypeName})v"
                    : "null";

                builder.AppendLine($"                    [\"{property.Name}\"] = new SubjectPropertyMetadata(");
                builder.AppendLine($"                        typeof({metadata.ClassName}).GetProperty(nameof({property.Name}), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!,");
                builder.AppendLine($"                        {getterLambda},");
                builder.AppendLine($"                        {setterLambda},");
                builder.AppendLine($"                        isIntercepted: {(property.IsPartial ? "true" : "false")},");
                builder.AppendLine("                        isDynamic: false),");
            }
        }
```

- [ ] **Step 5: Force explicitly implemented class properties to non-partial**

`CS0754` makes `partial` plus an explicit implementation illegal, so the emitter must never generate a partial body for one. In `SubjectMetadataExtractor.CollectProperties`, change the `isPartial` computation:

```csharp
                var isPartial = property.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)) &&
                                property.ExplicitInterfaceSpecifier is null;
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~ExplicitInterfaceTests"`

Expected: all five PASS.

- [ ] **Step 7: Add the behaviour tests**

Append to `ExplicitInterfaceBehaviorTests.cs`, above the existing test class:

```csharp
#region Case A: explicit implementation in a sub-interface

public enum CaseAGender { Male, Female }

public interface ICaseAHuman
{
    CaseAGender Gender { get; }
}

public interface ICaseAMale : ICaseAHuman
{
    CaseAGender ICaseAHuman.Gender => CaseAGender.Male;
}

[InterceptorSubject]
public partial class CaseAJohn : ICaseAMale
{
}

#endregion

#region Case I: a base class implementation beats an interface default

public interface ICaseIHuman
{
    string Origin => "interface-default";
}

public class CaseIBase : ICaseIHuman
{
    string ICaseIHuman.Origin => "base-class-explicit";
}

[InterceptorSubject]
public partial class CaseIDerived : CaseIBase
{
}

#endregion

#region Case AA: two explicit implementations of one generic interface, at different instantiations

// Deduplication (Task 4) keeps this from emitting duplicate dictionary keys once the name
// resolution below makes both entries resolve to "Kind". NI0008 reports the collision from
// Task 11; the suppression is placed now so that task does not break this file's build.
#pragma warning disable NI0008

public interface ICaseAAFoo<T>
{
    string Kind { get; }
}

[InterceptorSubject]
public partial class CaseAASubject : ICaseAAFoo<int>, ICaseAAFoo<string>
{
    string ICaseAAFoo<int>.Kind => "int";
    string ICaseAAFoo<string>.Kind => "string";
}

#pragma warning restore NI0008

#endregion
```

and add these tests to the class:

```csharp
    [Fact]
    public void WhenSubInterfaceExplicitlyImplementsMember_ThenSubjectExposesItByMemberName()
    {
        // Arrange
        var john = new CaseAJohn();

        // Act
        var properties = ((IInterceptorSubject)john).Properties;

        // Assert
        Assert.True(properties.ContainsKey("Gender"));
        Assert.Equal(CaseAGender.Male, properties["Gender"].GetValue?.Invoke(john));
    }

    [Fact]
    public void WhenBaseClassImplementsAndInterfaceHasDefault_ThenBaseClassImplementationWins()
    {
        // Arrange
        var derived = new CaseIDerived();

        // Act
        var value = ((IInterceptorSubject)derived).Properties["Origin"].GetValue?.Invoke(derived);

        // Assert
        Assert.Equal("base-class-explicit", value);
    }

    [Fact]
    public void WhenTwoExplicitImplementationsCollideOnName_ThenOneEntryIsExposed()
    {
        // Arrange (case AA)
        var subject = new CaseAASubject();

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert: first declaration wins, and reading DefaultProperties does not throw
        Assert.Single(properties.Where(p => p.Key == "Kind"));
        Assert.Equal("int", properties["Kind"].GetValue?.Invoke(subject));
    }
```

- [ ] **Step 8: Pin the reported issue's shape end to end**

The tests above each isolate one case. This one reproduces the shape from issue #428 as a whole, with
different names, and it is the acceptance test for the issue.

Three details of that shape are deliberately preserved, because each could break independently:

1. The property is named after its own type (`Gender Gender { get; }` in the report), so the emitted
   `nameof({interface}.{name})` and `(({interface})o).{name}` both sit where a type name and a member
   name are spelled identically.
2. The subject class is **empty**. Its only property arrives through the interface.
3. The sample is in the **global namespace**, so the reported code exercises the Task 2 defect as well.
   Fixing only the explicit implementation would still leave this sample broken.

Create `src/Namotion.Interceptor.Generator.Tests/ReportedIssueTests.cs`. Note the model is declared in
the global namespace on purpose, so the file has no namespace declaration around it:

```csharp
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

// Shape reported in https://github.com/RicoSuter/Namotion.Interceptor/issues/428, renamed:
// an enum, a base interface whose property is named after its own type, a sub-interface that
// supplies the value through an explicit implementation, and an empty subject class with no
// namespace.

public enum Rank { Junior, Senior }

public interface IEmployee
{
    Rank Rank { get; }
}

public interface ISenior : IEmployee
{
    Rank IEmployee.Rank => Rank.Senior;
}

[InterceptorSubject]
public partial class Alice : ISenior
{
}

namespace Namotion.Interceptor.Generator.Tests
{
    public class ReportedIssueTests
    {
        [Fact]
        public void WhenSubjectInheritsExplicitImplementationFromSubInterface_ThenPropertyIsExposed()
        {
            // Arrange
            var alice = new Alice();

            // Act
            var properties = ((IInterceptorSubject)alice).Properties;

            // Assert
            Assert.True(properties.ContainsKey("Rank"));
            Assert.Equal(Rank.Senior, properties["Rank"].GetValue?.Invoke(alice));
        }

        [Fact]
        public void WhenSubjectInheritsExplicitImplementationFromSubInterface_ThenPropertyIsNotIntercepted()
        {
            // Arrange
            var alice = new Alice();

            // Act
            var metadata = ((IInterceptorSubject)alice).Properties["Rank"];

            // Assert: an explicitly implemented member cannot be routed through the executor
            Assert.False(metadata.IsIntercepted);
            Assert.Equal("Rank", metadata.PropertyInfo?.Name);
        }
    }
}
```

Also add the generator-level equivalent to `ExplicitInterfaceTests`, which asserts the shape compiles
without the test project having to build it:

```csharp
    [Fact]
    public void WhenReportedIssueShapeIsUsed_ThenGeneratedCodeCompiles()
    {
        // Arrange: issue 428's shape, renamed, including the global namespace
        const string source = @"
using Namotion.Interceptor.Attributes;

public enum Rank { Junior, Senior }

public interface IEmployee { Rank Rank { get; } }

public interface ISenior : IEmployee { Rank IEmployee.Rank => Rank.Senior; }

[InterceptorSubject]
public partial class Alice : ISenior { }";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Rank""]", generatedSource);
        Assert.Contains("((global::IEmployee)o).Rank", generatedSource);
        Assert.Contains("nameof(global::IEmployee.Rank)", generatedSource);
        Assert.DoesNotContain("YourDefaultNamespace", generatedSource);
    }
```

- [ ] **Step 9: Run the full suite**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests`

Expected: all pass. Snapshots do not change: `ExplicitInterfaceImplementations` is empty for a non-explicit property, so `resolvedName` and `accessorInterface` produce the values already in use.

If `ReportedIssueTests` fails to **build**, the generator is still emitting broken code for the reported
shape and no amount of test adjustment is the right response.

- [ ] **Step 10: Commit**

```bash
git add src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests
git commit -m "Reach an explicitly implemented property through the interface it implements

IPropertySymbol.Name is fully qualified for an explicit implementation, so the
generator emitted nameof(IMale.IHuman.Gender) and ((IMale)o).IHuman.Gender.
The name now comes from the implemented member and the cast from that member's
containing interface.

The implemented interface is the only correct cast target: reflection on the
declaring interface returns null for the member, and dispatch through the
implemented one resolves to the most specific implementation.

Fixes #428."
```

---

### Task 6: Subject class accessibility (spec 1.4, case S)

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/Models/SubjectMetadata.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs:24-27,67-78`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs:100-108`
- Modify: `src/Namotion.Interceptor.Generator.Tests/GeneratorShapeTests.cs`
- Create: `src/Namotion.Interceptor.Generator.Tests/GeneratorShapeBehaviorTests.cs`

**Interfaces:**
- Produces: `SubjectMetadata.AccessModifier` (`string`), the subject type's declared accessibility

- [ ] **Step 1: Write the failing test**

Add to `GeneratorShapeTests.cs`:

```csharp
    [Fact]
    public void WhenSubjectIsInternal_ThenGeneratedCodeCompiles()
    {
        // Arrange (case S)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    internal partial class InternalSubject
    {
        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains("internal partial class InternalSubject", generated.SingleSource());
        Assert.Empty(generated.CompilationErrors);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenSubjectIsInternal"`

Expected: FAIL with `CS0262: Partial declarations of 'InternalSubject' have conflicting accessibility modifiers`.

- [ ] **Step 3: Capture the accessibility**

In `Models/SubjectMetadata.cs`, add a parameter after `ClassName`:

```csharp
    string AccessModifier,
```

In `SubjectMetadataExtractor.Extract`, after `var className = classDeclaration.Identifier.ValueText;`:

```csharp
        // Use the symbol rather than the syntax modifiers: a top-level class without a modifier
        // defaults to internal, a nested one to private.
        var accessModifier = GetAccessModifierFromAccessibility(typeSymbol.DeclaredAccessibility);
```

and pass `accessModifier` as the second argument of the `new SubjectMetadata(` call.

- [ ] **Step 4: Emit it**

In `SubjectCodeGenerator.EmitClassDeclaration`, replace line 106:

```csharp
        builder.AppendLine($"    {metadata.AccessModifier} partial class {metadata.ClassName} : {interfaces}");
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenSubjectIsInternal"`

Expected: PASS.

- [ ] **Step 6: Add the behaviour test**

Create `src/Namotion.Interceptor.Generator.Tests/GeneratorShapeBehaviorTests.cs`:

```csharp
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests;

#region Case S: a non-public subject

[InterceptorSubject]
internal partial class InternalSubject
{
    public partial string Name { get; set; }
}

#endregion

public class GeneratorShapeBehaviorTests
{
    [Fact]
    public void WhenSubjectIsInternal_ThenPropertiesAreTracked()
    {
        // Arrange
        var subject = new InternalSubject { Name = "value" };

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Equal("value", properties["Name"].GetValue?.Invoke(subject));
    }
}
```

- [ ] **Step 7: Run the full suite and re-baseline if needed**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Generator.Tests`

Expected: all pass with no snapshot changes, because every existing snapshot's subject is `public`. If a `.received.txt` appears, inspect it before accepting.

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests
git commit -m "Emit the subject's declared accessibility instead of always public

An internal, private or protected subject failed with CS0262 because the
generated partial was always public. The accessibility comes from the symbol
rather than the syntax modifiers, so a class with no modifier gets internal at
top level and private when nested."
```

---

### Task 7: Skipped members (spec 1.5, cases E, O, V, W, Y)

Members the generator cannot support are skipped rather than emitted broken. Task 11 adds NI0006 so the skip is reported.

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs:225-253,184-206`
- Modify: `src/Namotion.Interceptor.Generator.Tests/GeneratorShapeTests.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/GeneratorShapeBehaviorTests.cs`

**Interfaces:**
- None new.

- [ ] **Step 1: Write the failing tests**

Add to `GeneratorShapeTests.cs`:

```csharp
    [Fact]
    public void WhenInterfaceHasDefaultIndexer_ThenIndexerIsSkipped()
    {
        // Arrange (case E)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IBag { string this[int i] => ""x""; }

    [InterceptorSubject]
    public partial class Bag : IBag { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain("this[]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceHasStaticProperty_ThenPropertyIsSkipped()
    {
        // Arrange (case V)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasStatic { static string Version => ""1.0""; }

    [InterceptorSubject]
    public partial class Thing : IHasStatic { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain(@"[""Version""]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceHasPrivateDefaultMember_ThenMemberIsSkipped()
    {
        // Arrange (case W)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasPrivate
    {
        double Value { get; set; }
        private string Hidden => ""h"";
    }

    [InterceptorSubject]
    public partial class Thing : IHasPrivate
    {
        public partial double Value { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain(@"[""Hidden""]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceHasInternalDefaultMember_ThenMemberIsKept()
    {
        // Arrange (regression guard: internal members are reachable from generated code)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasInternal
    {
        double Value { get; set; }
        internal string Status => ""s"";
        protected internal string Label => ""l"";
    }

    [InterceptorSubject]
    public partial class Thing : IHasInternal
    {
        public partial double Value { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Status""]", generatedSource);
        Assert.Contains(@"[""Label""]", generatedSource);
    }

    [Fact]
    public void WhenMethodIsNamedExactlyWithoutInterceptor_ThenMethodIsSkipped()
    {
        // Arrange (case O)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Thing
    {
        public void WithoutInterceptor() { }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain("public void ()", generated.SingleSource());
    }

    [Fact]
    public void WhenWithoutInterceptorMethodIsUnsupportedShape_ThenMethodIsSkipped()
    {
        // Arrange (case Y: static, generic and by-reference parameters)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Thing
    {
        public static void StaticWithoutInterceptor() { }
        public void GenericWithoutInterceptor<T>(T value) { }
        public void RefWithoutInterceptor(ref int value) { }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.DoesNotContain("public void Static(", generatedSource);
        Assert.DoesNotContain("public void Generic(", generatedSource);
        Assert.DoesNotContain("public void Ref(", generatedSource);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~GeneratorShapeTests"`

Expected: the indexer, static, private and `WithoutInterceptor` tests FAIL on compilation errors. `WhenInterfaceHasInternalDefaultMember_ThenMemberIsKept` PASSES already; it is the regression guard.

- [ ] **Step 3: Guard the interface property collection**

In `SubjectMetadataExtractor.ExtractInterfaceDefaultProperties`, immediately after the `if (member is not IPropertySymbol property) continue;` block, insert:

```csharp
                // An indexer has no usable name and is parameterised.
                if (property.IsIndexer)
                {
                    continue;
                }

                // A static property with a body is not abstract, so it passes the default
                // implementation test below, but it cannot be read from an instance.
                if (property.IsStatic)
                {
                    continue;
                }

                // The generated code lives in the same assembly, so internal and protected
                // internal members are reachable. Private and protected ones are not.
                if (property.DeclaredAccessibility is Accessibility.Private
                    or Accessibility.Protected
                    or Accessibility.ProtectedAndInternal)
                {
                    continue;
                }
```

- [ ] **Step 4: Guard the method collection**

In `SubjectMetadataExtractor.CollectMethods`, replace the suffix check:

```csharp
                var fullMethodName = method.Identifier.Text;
                if (!fullMethodName.EndsWith(InterceptedMethodPostfix))
                {
                    continue;
                }

                // A method named exactly "WithoutInterceptor" would yield an empty wrapper name.
                if (fullMethodName.Length == InterceptedMethodPostfix.Length)
                {
                    continue;
                }

                // The emitter drops static, generic and by-reference shapes, and cannot route an
                // explicit interface implementation through the executor.
                if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) ||
                    method.TypeParameterList is not null ||
                    method.ExplicitInterfaceSpecifier is not null ||
                    method.ParameterList.Parameters.Any(parameter => parameter.Modifiers.Any(m =>
                        m.IsKind(SyntaxKind.RefKeyword) ||
                        m.IsKind(SyntaxKind.OutKeyword) ||
                        m.IsKind(SyntaxKind.InKeyword))))
                {
                    continue;
                }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~GeneratorShapeTests"`

Expected: all PASS.

- [ ] **Step 6: Add the accessibility regression behaviour test**

Add to `GeneratorShapeBehaviorTests.cs`, above the test class:

```csharp
#region Case W: internal and protected internal default members stay supported

public interface IAccessibleDefaults
{
    double Value { get; set; }

    internal string InternalStatus => "internal-" + Value;

    protected internal string ProtectedInternalStatus => "protected-internal-" + Value;
}

[InterceptorSubject]
public partial class AccessibleDefaultsSubject : IAccessibleDefaults
{
    public partial double Value { get; set; }
}

#endregion
```

and to the class:

```csharp
    [Fact]
    public void WhenDefaultMemberIsInternalOrProtectedInternal_ThenItRemainsExposed()
    {
        // Arrange
        var subject = new AccessibleDefaultsSubject { Value = 3 };

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Equal("internal-3", properties["InternalStatus"].GetValue?.Invoke(subject));
        Assert.Equal("protected-internal-3", properties["ProtectedInternalStatus"].GetValue?.Invoke(subject));
    }
```

- [ ] **Step 7: Run the full suite**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests`

Expected: all pass, no snapshot changes.

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests
git commit -m "Skip interface and method shapes the emitter cannot express

Indexers, static interface properties, private and protected default members,
and static, generic, explicitly implemented or by-reference WithoutInterceptor
methods all produced code that could not compile.

Internal and protected internal default members are deliberately kept: the
generated code is in the same assembly and they work today. A regression test
pins both."
```

---

### Task 8: Containing type kinds (spec 1.6, cases P, Q, R)

**Files:**
- Create: `src/Namotion.Interceptor.Generator/Models/ContainingType.cs`
- Modify: `src/Namotion.Interceptor.Generator/Models/SubjectMetadata.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs:95-105`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs:37-43,84-91`
- Modify: `src/Namotion.Interceptor.Generator.Tests/GeneratorShapeTests.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/GeneratorShapeBehaviorTests.cs`

**Interfaces:**
- Produces: `ContainingType(string Keyword, string Name)`. `SubjectMetadata.ContainingTypes` changes from `string[]` to `ContainingType[]`.

- [ ] **Step 1: Write the failing test**

Add to `GeneratorShapeTests.cs`:

```csharp
    [Theory]
    [InlineData("record")]
    [InlineData("struct")]
    [InlineData("interface")]
    public void WhenSubjectIsNestedInNonClassType_ThenGeneratedCodeCompiles(string containerKeyword)
    {
        // Arrange (cases P, Q, R)
        var source = $@"
using Namotion.Interceptor.Attributes;
namespace Repro
{{
    public partial {containerKeyword} Outer
    {{
        [InterceptorSubject]
        public partial class Nested
        {{
            public partial string Name {{ get; set; }}
        }}
    }}
}}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Contains($"partial {containerKeyword} Outer", generated.SingleSource());
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenSubjectIsNestedInNonClassType"`

Expected: all three FAIL with `CS0261: Partial declarations of 'Outer' must be all classes, all record classes, all structs, all record structs, or all interfaces`.

- [ ] **Step 3: Add the model**

Create `src/Namotion.Interceptor.Generator/Models/ContainingType.cs`:

```csharp
namespace Namotion.Interceptor.Generator.Models;

/// <summary>
/// A type that lexically contains an interceptor subject. The keyword is carried because the
/// generated partial declaration must repeat it: "partial class" against a record container is
/// a CS0261.
/// </summary>
internal sealed record ContainingType(string Keyword, string Name);
```

- [ ] **Step 4: Change the metadata type**

In `Models/SubjectMetadata.cs`, change `string[] ContainingTypes` to:

```csharp
    ContainingType[] ContainingTypes,
```

- [ ] **Step 5: Capture the keyword**

In `SubjectMetadataExtractor`, replace `GetContainingTypes` and add a helper:

```csharp
    private static ContainingType[] GetContainingTypes(SyntaxNode node)
    {
        var types = new List<ContainingType>();
        var parent = node.Parent;
        while (parent is TypeDeclarationSyntax typeDeclaration)
        {
            types.Insert(0, new ContainingType(
                GetTypeKeyword(typeDeclaration),
                typeDeclaration.Identifier.ValueText));
            parent = parent.Parent;
        }
        return types.ToArray();
    }

    /// <summary>
    /// "record" alone is correct for a record class, because record defaults to a class, but a
    /// record struct needs both tokens or the partial declarations conflict.
    /// </summary>
    private static string GetTypeKeyword(TypeDeclarationSyntax typeDeclaration)
    {
        if (typeDeclaration is not RecordDeclarationSyntax recordDeclaration)
        {
            return typeDeclaration.Keyword.ValueText;
        }

        var classOrStructKeyword = recordDeclaration.ClassOrStructKeyword.ValueText;
        return string.IsNullOrEmpty(classOrStructKeyword)
            ? recordDeclaration.Keyword.ValueText
            : $"{recordDeclaration.Keyword.ValueText} {classOrStructKeyword}";
    }
```

- [ ] **Step 6: Emit the keyword**

In `SubjectCodeGenerator`, replace `EmitContainingTypeOpening` and the signature of `EmitContainingTypeClosing`:

```csharp
    private static void EmitContainingTypeOpening(StringBuilder builder, ContainingType[] containingTypes)
    {
        foreach (var containingType in containingTypes)
        {
            builder.AppendLine($"    partial {containingType.Keyword} {containingType.Name}");
            builder.AppendLine("    {");
        }
    }

    private static void EmitContainingTypeClosing(StringBuilder builder, ContainingType[] containingTypes)
    {
        foreach (var _ in containingTypes)
        {
            builder.AppendLine("    }");
        }
    }
```

and update `GetFileName` to project the name:

```csharp
        var containingTypesPath = metadata.ContainingTypes.Length > 0
            ? string.Join(".", metadata.ContainingTypes.Select(t => t.Name)) + "."
            : "";
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenSubjectIsNestedInNonClassType"`

Expected: all three PASS.

- [ ] **Step 8: Add the behaviour test**

Add to `GeneratorShapeBehaviorTests.cs`, above the test class:

```csharp
#region Case P: a subject nested in a record

public partial record RecordContainer
{
    [InterceptorSubject]
    public partial class NestedSubject
    {
        public partial string Name { get; set; }
    }
}

#endregion
```

and to the class:

```csharp
    [Fact]
    public void WhenSubjectIsNestedInRecord_ThenPropertiesAreTracked()
    {
        // Arrange
        var subject = new RecordContainer.NestedSubject { Name = "value" };

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Equal("value", properties["Name"].GetValue?.Invoke(subject));
    }
```

- [ ] **Step 9: Run the full suite**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Generator.Tests`

Expected: all pass with **no snapshot changes**. For a plain class container the emitter produces `partial class Outer`, byte identical to before. If the two nested-class snapshots report a diff, `GetTypeKeyword` is returning the wrong token.

- [ ] **Step 10: Commit**

```bash
git add src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests
git commit -m "Repeat the containing type's own keyword on the generated partial

A subject nested in a record, struct or interface emitted 'partial class Outer'
and failed with CS0261. A record struct needs both tokens, since 'partial
record' alone means record class.

Nesting in a plain class is byte identical to before, so the existing nested
class snapshots are unchanged."
```

---

### Task 9: Diagnostic infrastructure (spec 2.1 and 2.2)

No rules yet. This task only makes reporting possible and proves the release-tracking prerequisite is satisfied.

**Files:**
- Create: `src/Namotion.Interceptor.Generator/AnalyzerReleases.Shipped.md`
- Create: `src/Namotion.Interceptor.Generator/AnalyzerReleases.Unshipped.md`
- Create: `src/Namotion.Interceptor.Generator/Diagnostics.cs`
- Create: `src/Namotion.Interceptor.Generator/Models/ExtractionResult.cs`
- Modify: `src/Namotion.Interceptor.Generator/Namotion.Interceptor.Generator.csproj`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs:18-79`
- Modify: `src/Namotion.Interceptor.Generator/InterceptorSubjectGenerator.cs:71-96`

**Interfaces:**
- Produces: `ExtractionResult(SubjectMetadata? Metadata, IReadOnlyList<Diagnostic> Diagnostics)`
- Produces: `SubjectMetadataExtractor.Extract(...) -> ExtractionResult` (was `SubjectMetadata`)
- Produces: `Diagnostics` static class holding all descriptors. Tasks 10 and 11 add fields to it.

- [ ] **Step 1: Add the release tracking files**

Create `src/Namotion.Interceptor.Generator/AnalyzerReleases.Shipped.md`:

```markdown
; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
```

Create `src/Namotion.Interceptor.Generator/AnalyzerReleases.Unshipped.md`:

```markdown
; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
```

- [ ] **Step 2: Register them as additional files**

In `src/Namotion.Interceptor.Generator/Namotion.Interceptor.Generator.csproj`, add before the existing `InternalsVisibleTo` item group:

```xml
	<ItemGroup>
		<AdditionalFiles Include="AnalyzerReleases.Shipped.md" />
		<AdditionalFiles Include="AnalyzerReleases.Unshipped.md" />
	</ItemGroup>

```

- [ ] **Step 3: Add the descriptor holder**

Create `src/Namotion.Interceptor.Generator/Diagnostics.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// Every rule must also be listed in AnalyzerReleases.Unshipped.md, or RS2008 fails the build.
/// </summary>
internal static class Diagnostics
{
    public const string Category = "Namotion.Interceptor";
}
```

- [ ] **Step 4: Verify the prerequisite holds**

Run: `dotnet build src/Namotion.Interceptor.Generator`

Expected: build succeeds. The descriptor-free state is trivially fine; Task 10 is where `RS2008` would bite if the release files were wrong.

- [ ] **Step 5: Add the extraction result**

Create `src/Namotion.Interceptor.Generator/Models/ExtractionResult.cs`:

```csharp
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator.Models;

/// <summary>
/// The outcome of inspecting one candidate subject type. A null <see cref="Metadata"/> means no
/// source should be emitted, which is how a suppressing diagnostic prevents a cascade of
/// consequent compiler errors.
/// </summary>
internal sealed record ExtractionResult(
    SubjectMetadata? Metadata,
    IReadOnlyList<Diagnostic> Diagnostics);
```

- [ ] **Step 6: Return it from the extractor**

In `SubjectMetadataExtractor.Extract`, change the return type to `ExtractionResult`, declare a diagnostics list at the top, and wrap the return:

```csharp
    public static ExtractionResult Extract(
        INamedTypeSymbol typeSymbol,
        ClassDeclarationSyntax classDeclaration,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();

        // The body from `var className = ...` through `var methods = ...` is unchanged. Only the
        // declaration of `diagnostics` above and the return statement below are new: the locals
        // className, accessModifier, containingTypes, namespaceName, fullTypeName, baseClass,
        // baseClassTypeName, baseClassHasInterceptorSubject, baseClassHasInpc, classProperties,
        // interfaceProperties, properties, methods, needsGeneratedParameterlessConstructor and
        // hasOrWillHaveParameterlessConstructor all keep their existing definitions.

        return new ExtractionResult(
            new SubjectMetadata(
                className,
                accessModifier,
                namespaceName,
                fullTypeName,
                containingTypes,
                needsGeneratedParameterlessConstructor,
                hasOrWillHaveParameterlessConstructor,
                baseClassTypeName,
                baseClassHasInterceptorSubject,
                baseClassHasInpc,
                properties,
                methods),
            diagnostics);
    }
```

- [ ] **Step 7: Report from the generator**

In `InterceptorSubjectGenerator.cs`, replace the `RegisterSourceOutput` body:

```csharp
        context.RegisterSourceOutput(classWithAttributeProvider, (spc, cls) =>
        {
            if (cls is null) return;

            try
            {
                var extraction = SubjectMetadataExtractor.Extract(
                    cls.TypeSymbol,
                    cls.ClassNode,
                    cls.Model,
                    spc.CancellationToken);

                foreach (var diagnostic in extraction.Diagnostics)
                {
                    spc.ReportDiagnostic(diagnostic);
                }

                if (extraction.Metadata is null)
                {
                    return;
                }

                var fileName = SubjectCodeGenerator.GetFileName(extraction.Metadata);
                var generatedCode = SubjectCodeGenerator.Generate(extraction.Metadata);

                spc.AddSource(fileName, SourceText.From(generatedCode, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                // The full stack is preserved in the emitted file; Task 11 adds NI0004 so the
                // failure is also visible in the build output.
                var className = cls.ClassNode.Identifier.ValueText;
                spc.AddSource($"{className}.g.cs", SourceText.From($"/* {ex} */", Encoding.UTF8));
            }
        });
```

- [ ] **Step 8: Run the full suite**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests`

Expected: all pass. No behaviour change; `Diagnostics` is always empty so far.

- [ ] **Step 9: Commit**

```bash
git add src/Namotion.Interceptor.Generator
git commit -m "Carry diagnostics out of metadata extraction

Extract returns an ExtractionResult so it stays a pure function while still
being able to report. A null metadata means no source is emitted, which lets a
suppressing rule replace a cascade of consequent compiler errors with one
message.

Adds the analyzer release tracking files that RS2008 requires before any
descriptor can exist, since the project sets EnforceExtendedAnalyzerRules and
warnings are errors."
```

---

### Task 10: Suppressing rules (spec 2.3: NI0001, NI0002, NI0003, NI0009, NI0010)

These five stop generation, replacing a cascade of compiler errors with one message.

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/Diagnostics.cs`
- Modify: `src/Namotion.Interceptor.Generator/AnalyzerReleases.Unshipped.md`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`
- Modify: `src/Namotion.Interceptor.Generator/InterceptorSubjectGenerator.cs:17-69` (widen to `TypeDeclarationSyntax`)
- Create: `src/Namotion.Interceptor.Generator.Tests/DiagnosticTests.cs`

**Interfaces:**
- Consumes: `ExtractionResult`, `Diagnostics.Category` from Task 9
- Produces: `Diagnostics.NotPartial`, `ContainingTypeNotPartial`, `UnsupportedTypeKind`, `GenericTypeNotSupported`, `FileTypeNotSupported`

- [ ] **Step 1: Write the failing tests**

Create `src/Namotion.Interceptor.Generator.Tests/DiagnosticTests.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator.Tests;

public class DiagnosticTests
{
    [Fact]
    public void WhenSubjectIsNotPartial_ThenNI0001IsReported()
    {
        // Arrange (case K)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public class NotPartial { }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0001");
        Assert.Equal(DiagnosticSeverity.Error, generated.GeneratorDiagnostics.Single(d => d.Id == "NI0001").Severity);
        Assert.Empty(generated.Sources);
    }

    [Fact]
    public void WhenContainingTypeIsNotPartial_ThenNI0002IsReported()
    {
        // Arrange (case L)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public class Outer
    {
        [InterceptorSubject]
        public partial class Nested { }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0002");
        Assert.Empty(generated.Sources);
    }

    [Fact]
    public void WhenAttributeIsOnRecord_ThenNI0003IsReported()
    {
        // Arrange (case M)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial record NotAClass { }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0003");
        Assert.Empty(generated.Sources);
    }

    [Fact]
    public void WhenSubjectIsGeneric_ThenNI0009IsReported()
    {
        // Arrange (case T)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Box<T>
    {
        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0009");
        Assert.Empty(generated.Sources);
    }

    [Fact]
    public void WhenSubjectIsFileLocal_ThenNI0010IsReported()
    {
        // Arrange (case X)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    file partial class FileLocal
    {
        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0010");
        Assert.Empty(generated.Sources);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~DiagnosticTests"`

Expected: all five FAIL. No diagnostic is reported and, for all but the record case, sources are emitted.

- [ ] **Step 3: Declare the descriptors**

Add to `Diagnostics.cs`:

```csharp
    public static readonly DiagnosticDescriptor NotPartial = new(
        id: "NI0001",
        title: "Interceptor subject must be partial",
        messageFormat: "Interceptor subject '{0}' must be declared partial",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generator emits the subject's implementation as a second partial declaration.");

    public static readonly DiagnosticDescriptor ContainingTypeNotPartial = new(
        id: "NI0002",
        title: "Containing type of an interceptor subject must be partial",
        messageFormat: "Containing type '{0}' of interceptor subject '{1}' must be declared partial",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated file re-declares every containing type.");

    public static readonly DiagnosticDescriptor UnsupportedTypeKind = new(
        id: "NI0003",
        title: "InterceptorSubject is only supported on classes",
        messageFormat: "'{0}' is a {1}, and InterceptorSubject is only supported on classes",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Records are excluded because the generated plumbing breaks value equality and with-expressions.");

    public static readonly DiagnosticDescriptor GenericTypeNotSupported = new(
        id: "NI0009",
        title: "Generic interceptor subjects are not supported",
        messageFormat: "Interceptor subject '{0}' is generic, which is not supported",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated declaration does not carry type parameters or constraints.");

    public static readonly DiagnosticDescriptor FileTypeNotSupported = new(
        id: "NI0010",
        title: "File-local interceptor subjects are not supported",
        messageFormat: "Interceptor subject '{0}' is file-local, which is not supported",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A generated partial declaration cannot join a file-local type.");
```

- [ ] **Step 4: Register the rules**

Replace the table in `AnalyzerReleases.Unshipped.md`:

```markdown
Rule ID | Category | Severity | Notes
--------|----------|----------|------
NI0001 | Namotion.Interceptor | Error | Interceptor subject must be partial
NI0002 | Namotion.Interceptor | Error | Containing type of an interceptor subject must be partial
NI0003 | Namotion.Interceptor | Error | InterceptorSubject is only supported on classes
NI0009 | Namotion.Interceptor | Error | Generic interceptor subjects are not supported
NI0010 | Namotion.Interceptor | Error | File-local interceptor subjects are not supported
```

- [ ] **Step 5: Verify RS2008 is satisfied**

Run: `dotnet build src/Namotion.Interceptor.Generator`

Expected: build succeeds. If it fails with `RS2008`, a rule ID in `Diagnostics.cs` is missing from the table.

- [ ] **Step 6: Widen the syntax provider**

In `InterceptorSubjectGenerator.cs`, change the predicate and cast at lines 19 and 23 so records reach NI0003:

```csharp
                predicate: (node, _) => node is TypeDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: (ctx, ct) =>
                {
                    var model = ctx.SemanticModel;
                    var typeDeclaration = (TypeDeclarationSyntax)ctx.Node;
```

Update the rest of that lambda to use `typeDeclaration`, and change the `.OfType<ClassDeclarationSyntax>()` filter at line 33 to `.OfType<TypeDeclarationSyntax>()`. Change `ClassNode`'s type accordingly.

In `SubjectMetadataExtractor`, change `Extract`'s parameter from `ClassDeclarationSyntax classDeclaration` to `TypeDeclarationSyntax typeDeclaration`, rename every use inside the method, and change the three `.OfType<ClassDeclarationSyntax>()` filters at lines 48, 117 and 176 to `.OfType<TypeDeclarationSyntax>()`.

Two private helpers also need widening, or the file will not compile once `Extract`'s parameter type changes. `GetNamespace` currently takes `ClassDeclarationSyntax`:

```csharp
    private static string? GetNamespace(TypeDeclarationSyntax typeDeclaration)
    {
        // Walk up past containing types to find namespace
        SyntaxNode? current = typeDeclaration.Parent;
        while (current is TypeDeclarationSyntax)
        {
            current = current.Parent;
        }

        // null means the global namespace: the generated file must not declare one.
        return (current as NamespaceDeclarationSyntax)?.Name.ToString() ??
               (current as FileScopedNamespaceDeclarationSyntax)?.Name.ToString();
    }
```

`GetContainingTypes` already takes `SyntaxNode` and needs no change. `DetectConstructorState` takes `ClassDeclarationSyntax[]`; widen it to `TypeDeclarationSyntax[]`, since its body only reads `Members`.

- [ ] **Step 7: Report the five rules**

At the top of `SubjectMetadataExtractor.Extract`, after `var diagnostics = new List<Diagnostic>();`:

```csharp
        var location = typeDeclaration.Identifier.GetLocation();

        if (typeDeclaration is not ClassDeclarationSyntax)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.UnsupportedTypeKind, location,
                typeSymbol.Name, typeDeclaration.Keyword.ValueText));
            return new ExtractionResult(null, diagnostics);
        }

        if (!typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.NotPartial, location, typeSymbol.Name));
            return new ExtractionResult(null, diagnostics);
        }

        if (typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.FileKeyword)))
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.FileTypeNotSupported, location, typeSymbol.Name));
            return new ExtractionResult(null, diagnostics);
        }

        if (typeSymbol.IsGenericType)
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.GenericTypeNotSupported, location, typeSymbol.Name));
            return new ExtractionResult(null, diagnostics);
        }

        for (var parent = typeDeclaration.Parent; parent is TypeDeclarationSyntax outer; parent = parent.Parent)
        {
            if (!outer.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.ContainingTypeNotPartial, location,
                    outer.Identifier.ValueText, typeSymbol.Name));
                return new ExtractionResult(null, diagnostics);
            }

            if (outer.TypeParameterList is not null)
            {
                diagnostics.Add(Diagnostic.Create(Diagnostics.GenericTypeNotSupported, location, typeSymbol.Name));
                return new ExtractionResult(null, diagnostics);
            }
        }
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~DiagnosticTests"`

Expected: all five PASS.

- [ ] **Step 9: Run the full suite**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests`

Expected: all pass. If a pre-existing test source is non-partial or nested in a non-partial type it now yields no source, which is correct; fix the test source.

- [ ] **Step 10: Commit**

```bash
git add src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests
git commit -m "Report unsupported subject declarations instead of emitting broken code

A non-partial subject, a non-partial containing type, a generic subject and a
file-local subject each produced a generated file that could not compile, and
the attribute on a record was ignored in total silence.

Generation stops for all five, so the user sees one message rather than a
cascade of consequent errors. The syntax provider now matches any type
declaration so a record reaches NI0003."
```

---

### Task 11: Advisory rules (spec 2.3: NI0004, NI0005, NI0006, NI0007, NI0008)

These report without stopping generation.

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/Diagnostics.cs`
- Modify: `src/Namotion.Interceptor.Generator/AnalyzerReleases.Unshipped.md`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`
- Modify: `src/Namotion.Interceptor.Generator/InterceptorSubjectGenerator.cs` (NI0004)
- Modify: `src/Namotion.Interceptor.Generator.Tests/DiagnosticTests.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/ExplicitInterfaceBehaviorTests.cs`

**Interfaces:**
- Produces: `Diagnostics.GeneratorFailed`, `ShadowsBaseImplementation`, `MemberSkipped`, `ExplicitImplementationAttributesIgnored`, `PropertyNameCollision`

- [ ] **Step 1: Write the failing tests**

Add to `DiagnosticTests.cs`:

```csharp
    [Fact]
    public void WhenMemberIsSkippedAsUnsupported_ThenNI0006IsReported()
    {
        // Arrange (case E)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IBag { string this[int i] => ""x""; }

    [InterceptorSubject]
    public partial class Bag : IBag { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0006");
    }

    [Fact]
    public void WhenAttributeIsOnExplicitImplementation_ThenNI0007IsReported()
    {
        // Arrange (case AC)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman { string Label { get; } }
    public interface IMale : IHuman { [Derived] string IHuman.Label => ""m""; }

    [InterceptorSubject]
    public partial class John : IMale { }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0007");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void WhenTwoInterfaceMembersCollideOnName_ThenNI0008IsReported()
    {
        // Arrange (case AE)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IFoo<T> { string Kind => ""k""; }

    [InterceptorSubject]
    public partial class Impl : IFoo<int>, IFoo<string> { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0008");
        Assert.Contains(@"[""Kind""]", generated.SingleSource());
    }

    [Fact]
    public void WhenDerivedRedeclaresBaseImplementedProperty_ThenNI0005IsReported()
    {
        // Arrange (case AD)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman { string Origin => ""interface""; }
    public class BaseSubject : IHuman { }

    [InterceptorSubject]
    public partial class DerivedSubject : BaseSubject
    {
        public partial string Origin { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0005");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~DiagnosticTests"`

Expected: the four new tests FAIL, no diagnostic reported.

- [ ] **Step 3: Declare the descriptors**

Add to `Diagnostics.cs`:

```csharp
    public static readonly DiagnosticDescriptor GeneratorFailed = new(
        id: "NI0004",
        title: "Interceptor subject generation failed",
        messageFormat: "Generating '{0}' failed with {1}: {2}. The generated file contains the full stack trace",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An unhandled exception in the generator. Please report it.");

    public static readonly DiagnosticDescriptor ShadowsBaseImplementation = new(
        id: "NI0005",
        title: "Property re-declares a member already implemented by the base class",
        messageFormat: "'{0}' re-declares '{1}', which the base class already implements, so the subject and the interface report different values",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Reading through the interface resolves to the base class implementation, not this property.");

    public static readonly DiagnosticDescriptor MemberSkipped = new(
        id: "NI0006",
        title: "Unsupported member skipped",
        messageFormat: "'{0}' was skipped because {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The member is not part of the subject's properties.");

    public static readonly DiagnosticDescriptor ExplicitImplementationAttributesIgnored = new(
        id: "NI0007",
        title: "Attributes on an explicit interface implementation are ignored",
        messageFormat: "Attributes on the explicit implementation of '{0}' are ignored; declare them on the interface member instead",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Property metadata reflects the interface member, so a Derived or validation attribute on the implementation would be silently lost.");

    public static readonly DiagnosticDescriptor PropertyNameCollision = new(
        id: "NI0008",
        title: "Two interface members collide on one property name",
        messageFormat: "'{0}' is provided by more than one interface member; the first declaration wins",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Subject properties are keyed by simple name, so only one of the colliding members is reachable.");
```

- [ ] **Step 4: Register the rules**

Append to the table in `AnalyzerReleases.Unshipped.md`:

```markdown
NI0004 | Namotion.Interceptor | Error | Interceptor subject generation failed
NI0005 | Namotion.Interceptor | Warning | Property re-declares a member already implemented by the base class
NI0006 | Namotion.Interceptor | Warning | Unsupported member skipped
NI0007 | Namotion.Interceptor | Error | Attributes on an explicit interface implementation are ignored
NI0008 | Namotion.Interceptor | Warning | Two interface members collide on one property name
```

- [ ] **Step 5: Report NI0006 at each skip site**

In `ExtractInterfaceDefaultProperties`, each `continue` added in Task 7 gains a diagnostic. The method needs the diagnostics list and a location, so change its signature to accept them and pass `typeDeclaration.Identifier.GetLocation()` from `Extract`. For example the indexer guard becomes:

```csharp
                if (property.IsIndexer)
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.MemberSkipped, location,
                        $"{interfaceType.Name}.{property.Name}", "indexers cannot be subject properties"));
                    continue;
                }
```

Use these reasons: `"indexers cannot be subject properties"`, `"static members cannot be read from an instance"`, `"the member is not accessible from generated code"`. Apply the same pattern in `CollectMethods` with the reason `"the method shape is not supported"`.

- [ ] **Step 6: Report NI0007, NI0008 and NI0005**

In `ExtractInterfaceDefaultProperties`, after resolving `explicitImplementation`:

```csharp
                if (explicitImplementation is not null && property.GetAttributes().Length > 0)
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.ExplicitImplementationAttributesIgnored, location, resolvedName));
                }
```

Change the `processedPropertyNames` guard to report before skipping:

```csharp
                if (processedPropertyNames.Contains(resolvedName))
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.PropertyNameCollision, location, resolvedName));
                    continue;
                }
```

In `Extract`, after both property collections exist, report NI0005 for a class property that a base class already implements through an interface:

```csharp
        if (baseClass is not null)
        {
            foreach (var property in classProperties)
            {
                var interfaceMember = baseClass.AllInterfaces
                    .SelectMany(i => i.GetMembers(property.Name))
                    .OfType<IPropertySymbol>()
                    .FirstOrDefault();

                if (interfaceMember is not null &&
                    baseClass.FindImplementationForInterfaceMember(interfaceMember) is not null)
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.ShadowsBaseImplementation, location,
                        typeSymbol.Name, property.Name));
                }
            }
        }
```

- [ ] **Step 7: Report NI0004 from the generator**

In `InterceptorSubjectGenerator.cs`, extend the catch block:

```csharp
            catch (Exception ex)
            {
                var className = cls.ClassNode.Identifier.ValueText;

                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.GeneratorFailed,
                    cls.ClassNode.Identifier.GetLocation(),
                    className,
                    ex.GetType().Name,
                    ex.Message));

                // The file keeps the full frames; a diagnostic message renders as one line.
                spc.AddSource($"{className}.g.cs", SourceText.From($"/* {ex} */", Encoding.UTF8));
            }
```

- [ ] **Step 8: Suppress the advisory rules in the behaviour test models**

The test project sets `TreatWarningsAsErrors`, so the case AD model must opt out. Add to `ExplicitInterfaceBehaviorTests.cs`:

```csharp
#region Case AD: base implements, derived re-declares. Intentional, so NI0005 is suppressed.

#pragma warning disable NI0005

public interface ICaseADHuman
{
    string Origin => "interface-default";
}

public class CaseADBase : ICaseADHuman
{
}

[InterceptorSubject]
public partial class CaseADDerived : CaseADBase
{
    public partial string Origin { get; set; }
}

#pragma warning restore NI0005

#endregion
```

and the test:

```csharp
    [Fact]
    public void WhenDerivedRedeclaresBaseImplementedProperty_ThenSubjectAndInterfaceDiffer()
    {
        // Arrange
        var derived = new CaseADDerived { Origin = "derived" };

        // Act
        var throughInterface = ((ICaseADHuman)derived).Origin;

        // Assert
        Assert.Equal("derived", derived.Origin);
        Assert.Equal("interface-default", throughInterface);
    }
```

- [ ] **Step 9: Run the full suite**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests`

Expected: all pass. If the test project fails to **build** with `NI0005`, `NI0006`, `NI0007` or `NI0008` promoted to an error, the offending model needs a scoped `#pragma warning disable` naming its case. Do not disable a rule project-wide.

- [ ] **Step 10: Commit**

```bash
git add src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests
git commit -m "Report skipped members, lost attributes and name collisions

A generator crash was recorded only as a comment inside the generated file, a
skipped member vanished without trace, an attribute on an explicit
implementation was silently dropped, and two interface members colliding on one
name resolved to whichever came first.

NI0007 is an error because a lost Derived attribute changes runtime behaviour.
NI0008 keeps first-wins rather than dropping the property, since dropping would
remove a member that resolves today."
```

---

### Task 12: Documentation (spec phase 3)

**Files:**
- Modify: `docs/generator.md`

**Interfaces:**
- None.

- [ ] **Step 1: Retire the limitation**

In `docs/generator.md`, delete this row from the Limitations table at line 307:

```markdown
| Explicit interface implementation not supported | Use implicit implementation |
```

and add these rows:

```markdown
| Records cannot be subjects | Use a class. See NI0003 |
| Generic subjects are not supported | Use a non-generic subject. See NI0009 |
| File-local subjects are not supported | Remove the `file` modifier. See NI0010 |
| Attributes on an explicit implementation are ignored | Declare them on the interface member. See NI0007 |
```

- [ ] **Step 2: Document explicit interface implementations**

Append to the **Interface Default Properties** section, after the "Supported scenarios" list at line 163:

````markdown
Explicit interface implementations are supported and are keyed by the member's simple name:

```csharp
public enum Gender { Male, Female }

public interface IHuman
{
    Gender Gender { get; }
}

public interface IMale : IHuman
{
    Gender IHuman.Gender => Gender.Male;
}

[InterceptorSubject]
public partial class John : IMale
{
    // "Gender" is included in DefaultProperties and reads as Gender.Male
}
```

The property is reached by casting to the interface that declares the member, so the value always
comes from the most specific implementation. Such a property is not intercepted, because an
explicitly implemented member cannot be routed through the interceptor.

Attributes such as `[Derived]` must be declared on the interface member rather than on the explicit
implementation. Placing them on the implementation reports NI0007.
````

- [ ] **Step 3: Document the remaining supported shapes**

Extend the **Nested Classes** section at line 239 with a note and example:

````markdown
The containing type can be a class, record, struct or interface, as long as every containing type is
`partial`:

```csharp
public partial record Outer
{
    [InterceptorSubject]
    public partial class Nested
    {
        public partial string Name { get; set; }
    }
}
```
````

Add to the **Supported Features** section:

````markdown
### Namespaces and Accessibility

Subjects can be declared in a namespace, in a file-scoped namespace, or in the global namespace.
Any accessibility is supported:

```csharp
[InterceptorSubject]
internal partial class InternalSubject
{
    public partial string Name { get; set; }
}
```
````

- [ ] **Step 4: Add the diagnostics reference**

Insert a new section before **Requirements** at line 312:

```markdown
## Diagnostics

| ID | Severity | Cause | Fix |
|----|----------|-------|-----|
| NI0001 | Error | The subject is not `partial` | Add the `partial` modifier |
| NI0002 | Error | A containing type is not `partial` | Add `partial` to every containing type |
| NI0003 | Error | The attribute is on a record, struct or interface | Use a class |
| NI0004 | Error | The generator threw | Report the issue. The generated file holds the stack trace |
| NI0005 | Warning | A derived class re-declares a property its base class already implements | Rename the property, or suppress if intended |
| NI0006 | Warning | An unsupported member was skipped | Remove the member, or ignore if the skip is intended |
| NI0007 | Error | An attribute sits on an explicit interface implementation | Move it to the interface member |
| NI0008 | Warning | Two interface members share one property name | Rename one, or suppress to accept first-wins |
| NI0009 | Error | The subject or a containing type is generic | Use non-generic types |
| NI0010 | Error | The subject or its interface is `file`-local | Remove the `file` modifier |

Suppress a rule at the point of use with `#pragma warning disable NI0005`, or project-wide through
`<NoWarn>`.
```

- [ ] **Step 5: Point troubleshooting at the diagnostics**

Replace the **Compilation errors in generated code** section at line 344:

```markdown
### Compilation errors in generated code

1. Check the build output for an `NI####` diagnostic first. It names the cause directly
2. Ensure you're using C# 13 or later
3. Check that property types are accessible from the generated code
4. Verify namespace imports are correct
```

- [ ] **Step 6: Verify no em dashes were introduced**

Run: `grep -c "—" docs/generator.md`

Expected: `0`.

- [ ] **Step 7: Commit**

```bash
git add docs/generator.md
git commit -m "Docs: explicit interface implementations, supported shapes and diagnostics

Retires the 'explicit interface implementation not supported' limitation and
documents what replaced it, along with global-namespace and non-public
subjects, non-class containing types, and a reference table for NI0001 to
NI0010."
```

---

## Verification

After Task 12, run the full solution build and unit test pass:

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: build succeeds with no warnings (warnings are errors), all non-integration tests pass.

Integration tests are not required: no connector implementation or HomeBlaze UI code is touched.

### Acceptance for issue #428

The reported shape is pinned in two places, both added in Task 5 Step 8:

```bash
dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~ReportedIssueTests"
dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenReportedIssueShapeIsUsed"
```

Expected: all three tests pass.

`ReportedIssueTests` is the stronger of the two, because its model is compiled by the real generator as
part of the test project. A regression there is a build failure, not a test failure.

Note that the reported sample exercises **two** defects at once, the explicit implementation and the
global namespace, so it can only pass once both Task 2 and Task 5 have landed.
