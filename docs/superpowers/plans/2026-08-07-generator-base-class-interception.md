# Base class interception in subject hierarchies: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make properties declared on a base `[InterceptorSubject]` class actually intercepted, by emitting the per instance plumbing once in the root of a subject hierarchy instead of once per class.

**Architecture:** The generator gains two emission modes. A subject with no subject ancestor emits the full `IInterceptorSubject` plumbing (root mode) with its helpers `protected` instead of `private`. A subject whose ancestor provides that plumbing emits only one line, its own explicit `IInterceptorSubject.Properties`, and inherits everything else (derived mode). Mode selection and all base class facts are resolved from the nearest subject ancestor rather than the immediate base class, which also repairs two shapes that do not build today. Four diagnostics cover the cases that cannot work.

**Tech Stack:** C# 13, Roslyn incremental source generators (`Microsoft.CodeAnalysis.CSharp` 4.14.0), xUnit, Verify snapshot testing, BenchmarkDotNet.

**Spec:** `docs/superpowers/specs/2026-08-07-generator-base-class-interception-design.md`. Read it before starting. Every rule in this plan is justified there, usually with the compiler output that proves it.

**Worktree:** `/Users/ricosuter/Projects/GitHub/Namotion.Interceptor/.claude/worktrees/generator-base-class-interception`, branch `fix/generator-base-class-interception`. Do not work in the main checkout; another session uses it.

## Global Constraints

- `src/Directory.Build.props` sets `TreatWarningsAsErrors`. **Every compiler warning is a build error, including inside generated files.** CS0108, CS0109 and CS0628 are the ones this work can produce.
- Commit messages, PR descriptions and GitHub comments must never mention AI tooling, and carry no `Co-Authored-By` trailer and no "Generated with" footer.
- No em dashes in any documentation, README or PR description.
- Test naming is `When<Condition>_Then<ExpectedBehavior>`. Test bodies carry explicit `// Arrange`, `// Act`, `// Assert` comments (`// Act & Assert` for exception tests).
- No `Task.Delay` or `Thread.Sleep` in tests.
- Priority order when things conflict: correctness, then performance (allocations first), then style.
- Diagnostic IDs continue from NI0010. Every new rule must be added to `src/Namotion.Interceptor.Generator/AnalyzerReleases.Unshipped.md` or RS2008 fails the build.
- The generator targets `netstandard2.0`. No `System.Range`, no collection expressions in generator code, no `record` positional syntax beyond what is already used.
- Run unit tests with `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`. Per project runs are fine during a task.
- Snapshot loops: set `DiffEngine_Disabled=true` so Verify does not try to open a diff tool.

## File structure

**Production, generator:**

| File | Responsibility |
|---|---|
| `src/Namotion.Interceptor.Generator/SubjectBaseContract.cs` | **New.** The ancestor walk, the contract check, the `new` modifier lookup, and the usable `DefaultProperties` check. Kept out of `SubjectMetadataExtractor.cs`, which is already 925 lines. |
| `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs` | Calls into `SubjectBaseContract` to resolve base facts and emission mode; reports NI0011 to NI0014. |
| `src/Namotion.Interceptor.Generator/Models/SubjectMetadata.cs` | Carries the three new facts the emitter needs. |
| `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs` | Root mode versus derived mode emission, sealed modifiers, per member `new`. |
| `src/Namotion.Interceptor.Generator/Diagnostics.cs` | NI0011 to NI0014 descriptors. |
| `src/Namotion.Interceptor.Generator/AnalyzerReleases.Unshipped.md` | Four new rows. |

**Tests:**

| File | Responsibility |
|---|---|
| `src/Namotion.Interceptor.Generator.Tests/GeneratorTestHost.cs` | Gains a no-warnings assertion and an opt-in generator pass over the library compilation. |
| `src/Namotion.Interceptor.Generator.Tests/BaseClassInterceptionBehaviorTests.cs` | **New.** The behaviour gate for #437. |
| `src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs` | **New.** Plain intermediate, sealed, cross assembly, hand written base and subclass. |
| `src/Namotion.Interceptor.Generator.Tests/SubjectBaseDiagnosticsTests.cs` | **New.** NI0011 to NI0014. |
| `src/Namotion.Interceptor.Generator.Tests/GeneratorShapeBehaviorTests.cs` | The KNOWN GAP comment is deleted and its assertion upgraded. |
| `src/Namotion.Interceptor.Tests/VirtualPropertyIntegrationTests.cs` | Existing three level hierarchy upgraded from value assertions to interceptor observation. |
| `src/Namotion.Interceptor.Dynamic.Tests/DynamicSubjectTests.cs` | Gains the proxied generated subject property set assertion. |
| `src/Namotion.Interceptor.Benchmark/SubjectHierarchyBenchmark.cs` | **New.** The non regression gate and the rejected alternative measurement. |

**Docs:** `docs/generator.md`, `docs/subject-guidelines.md`, `docs/design/generator-supported-shapes.md`.

## Task order and why

Task 1 pins what already works, before any generator change, so that everything after it has a tripwire. Tasks 2 to 4 are the three emission changes, smallest blast radius first: base facts, then sealed, then the split that actually fixes #437. Tasks 5 and 6 add the diagnostics that depend on the contract check. Task 7 proves the hand written scenarios. Task 8 upgrades two existing tests that look like coverage and are not. Task 9 is the whole repository regeneration gate. Task 10 benchmarks. Task 11 documents.

Tests that pin **existing correct** behaviour live in Task 1. Tests that pin **broken** behaviour live in the task that fixes them, so the suite is never left red between tasks.

---

### Task 1: Test harness and green pins

**Files:**
- Modify: `src/Namotion.Interceptor.Generator.Tests/GeneratorTestHost.cs`
- Create: `src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs`
- Modify: `src/Namotion.Interceptor.Dynamic.Tests/DynamicSubjectTests.cs`

**Interfaces:**
- Produces: `GeneratorTestHost.RunExpectingNoWarnings(string source)` returning `GeneratorRunResult`; `GeneratorTestHost.RunWithLibraryReference(string librarySource, string mainSource, bool runGeneratorOverLibrary = false)`; `GeneratorRunResult.CompilationWarnings`.

Every failure mode this work can introduce is a compiler **warning**, and `GeneratorRunResult.CompilationErrors` filters to `Severity == Error` (`GeneratorTestHost.cs:20-22`). Without this task, a change that breaks every consumer build passes green.

- [ ] **Step 1: Add the warning surface to `GeneratorRunResult`**

In `src/Namotion.Interceptor.Generator.Tests/GeneratorTestHost.cs`, after the `CompilationErrors` property (around line 22), add:

```csharp
    /// <summary>
    /// Warnings from the generated compilation, minus the ones the test host itself causes.
    /// CS8632 is excluded because <see cref="GeneratorTestHost"/> builds without a nullable
    /// context, so every existing test source that uses '?' reports it (SourceGeneratorTests.cs:16
    /// among many). Everything else must be empty: src/Directory.Build.props sets
    /// TreatWarningsAsErrors for consumers, so a warning in generated code is a broken build.
    /// </summary>
    public IReadOnlyList<Diagnostic> CompilationWarnings { get; } = CompilationDiagnostics
        .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning)
        .Where(diagnostic => diagnostic.Id != "CS8632")
        .ToList();
```

- [ ] **Step 2: Add the assertion helper**

In the same file, after `RunExpectingCleanCompilation` (around line 105), add:

```csharp
    /// <summary>
    /// Same as <see cref="RunExpectingCleanCompilation"/>, and additionally fails on any warning
    /// other than CS8632. Use this for any shape where the risk is a hiding or sealed-member
    /// warning (CS0108, CS0109, CS0628), which a consumer build turns into an error.
    /// </summary>
    public static GeneratorRunResult RunExpectingNoWarnings(string source)
    {
        var result = RunExpectingCleanCompilation(source);

        Assert.True(
            result.CompilationWarnings.Count == 0,
            "Generated code compiled with warnings, which are errors in consumer builds:" + Environment.NewLine +
            string.Join(Environment.NewLine, result.CompilationWarnings.Select(d => d.ToString())));

        return result;
    }
```

- [ ] **Step 3: Make the library compilation able to run the generator**

Replace the signature and body of `RunWithLibraryReference` (`GeneratorTestHost.cs:48-67`) with:

```csharp
    public static GeneratorRunResult RunWithLibraryReference(
        string librarySource,
        string mainSource,
        bool runGeneratorOverLibrary = false)
    {
        var libraryCompilation = CSharpCompilation.Create(
            assemblyName: "TestLibrary",
            syntaxTrees: [CSharpSyntaxTree.ParseText(librarySource)],
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Opt in, not automatic. NI0012's stale-base fixture is a base built by an OLDER generator:
        // running the current generator over it would emit protected helpers, satisfy the contract,
        // and make NI0012 unreachable.
        if (runGeneratorOverLibrary)
        {
            GeneratorDriver libraryDriver = CSharpGeneratorDriver.Create(new InterceptorSubjectGenerator());
            libraryDriver.RunGeneratorsAndUpdateCompilation(libraryCompilation, out var updated, out _);
            libraryCompilation = (CSharpCompilation)updated;
        }

        using var libraryStream = new MemoryStream();
        var emitResult = libraryCompilation.Emit(libraryStream);
        Assert.True(
            emitResult.Success,
            "Library compilation did not compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, emitResult.Diagnostics.Select(d => d.ToString())));

        libraryStream.Position = 0;
        var libraryReference = MetadataReference.CreateFromStream(libraryStream);

        return RunCore(mainSource, References.Append(libraryReference).ToList());
    }
```

- [ ] **Step 4: Build the test project to confirm the harness compiles**

Run: `dotnet build src/Namotion.Interceptor.Generator.Tests`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Pin the INPC-only base, which the base-fact change could break**

`ManualInpcPersonBase` (`src/Namotion.Interceptor.Tracking.Tests/Models/ManualInpcPersonBase.cs:8`) implements `INotifyPropertyChanged` and `IRaisePropertyChanged` but not `IInterceptorSubject`, and carries no attribute. It is therefore **not** a subject ancestor. If Task 2 drops the `ImplementsInterface(typeSymbol, IRaisePropertyChanged)` disjunct, its subject subclass re-declares both members and emits two CS0108.

Create `src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs`:

```csharp
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectBaseShapeTests
{
    [Fact]
    public void WhenBaseImplementsRaisePropertyChangedWithoutBeingASubject_ThenNoNotifyPlumbingIsRedeclared()
    {
        // Arrange: the base is INPC + IRaisePropertyChanged but NOT IInterceptorSubject and has no
        // attribute, so it is not a subject ancestor. BaseClassHasInpc must still be true, because
        // its second disjunct is asked of the subject, not of the ancestor.
        const string source = """
            using System.ComponentModel;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public abstract class ManualBase : INotifyPropertyChanged, IRaisePropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;

                    public void RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                }

                [InterceptorSubject]
                public partial class ManualDerived : ManualBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var generated = result.SingleSource();

        // Assert
        Assert.DoesNotContain("public event PropertyChangedEventHandler? PropertyChanged;", generated);
        Assert.DoesNotContain("protected void RaisePropertyChanged(string propertyName)", generated);
        Assert.Contains("((IRaisePropertyChanged)this).RaisePropertyChanged(nameof(Name))", generated);
    }

    [Fact]
    public void WhenSubjectIsSealedAndDerived_ThenItCompilesWithoutWarnings()
    {
        // Arrange: a sealed DERIVED subject is legal today, because RaisePropertyChanged is gated
        // on BaseClassHasInpc and so is not emitted into it. Only a sealed ROOT fails (Task 3).
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class BaseSubject
                {
                    public partial string BaseName { get; set; }
                }

                [InterceptorSubject]
                public sealed partial class SealedLeaf : BaseSubject
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act & Assert
        GeneratorTestHost.RunExpectingNoWarnings(source);
    }
}
```

- [ ] **Step 6: Run both pins and confirm they pass against the untouched generator**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~SubjectBaseShapeTests"`
Expected: PASS, 2 tests. If either fails now, stop: the premise of the whole plan is wrong and the spec needs revisiting.

- [ ] **Step 7: Pin the proxied generated subject's property set**

`DynamicSubjectFactory.cs:33-48` turns every reflected instance property not already in `Properties` into a `SubjectPropertyMetadata` with `isIntercepted: true`, and `GetProperties(Instance|Public|NonPublic)` returns inherited **protected** properties. Task 4 adds the first protected member to a generated subject, so this pins that none of them leaks into the property set.

Append to `src/Namotion.Interceptor.Dynamic.Tests/DynamicSubjectTests.cs`, inside the existing test class:

```csharp
    [Fact]
    public void WhenProxyingAGeneratedSubject_ThenNoGeneratedPlumbingMemberBecomesAProperty()
    {
        // Arrange & Act: Motor is [InterceptorSubject], so the proxy's base is generated code.
        var motor = DynamicSubjectFactory.CreateSubject<Motor>(typeof(IMotor), typeof(ISensor));

        // Assert: the generator's own members must never be harvested as subject properties.
        var propertyNames = ((IInterceptorSubject)motor).Properties.Keys;
        Assert.DoesNotContain("InstanceProperties", propertyNames);
        Assert.DoesNotContain("GetInstanceProperties", propertyNames);
        Assert.DoesNotContain("DefaultProperties", propertyNames);
        Assert.DoesNotContain("Context", propertyNames);
        Assert.DoesNotContain("SyncRoot", propertyNames);
        Assert.DoesNotContain("Data", propertyNames);
    }
```

- [ ] **Step 8: Run the Dynamic suite**

Run: `dotnet test src/Namotion.Interceptor.Dynamic.Tests`
Expected: PASS, including the new test.

- [ ] **Step 9: Commit**

```bash
git add src/Namotion.Interceptor.Generator.Tests/GeneratorTestHost.cs \
        src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs \
        src/Namotion.Interceptor.Dynamic.Tests/DynamicSubjectTests.cs
git commit -m "Test: pin the shapes the base class interception fix must not break"
```

---

### Task 2: Resolve base class facts from the nearest subject ancestor

**Files:**
- Create: `src/Namotion.Interceptor.Generator/SubjectBaseContract.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs:98-113`
- Test: `src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs`

**Interfaces:**
- Produces: `SubjectBaseContract.FindNearestSubjectAncestor(INamedTypeSymbol typeSymbol)` returning `INamedTypeSymbol?`; `SubjectBaseContract.HasInterceptorSubjectAttribute(INamedTypeSymbol? type)` returning `bool`.
- Consumes: nothing from earlier tasks.

Today `baseClass` is read from `typeSymbol.BaseType` alone. A plain class between two subjects therefore looks like a plain base, and the subject below it emits a full root shape that collides with everything it inherited: three CS0108, which are build errors.

- [ ] **Step 1: Write the failing test**

Append to `src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs`:

```csharp
    [Fact]
    public void WhenAPlainClassSitsBetweenTwoSubjects_ThenTheDerivedSubjectCompilesAndMergesBaseProperties()
    {
        // Arrange: A is a subject, B is an ordinary class, C is a subject. At generation time B
        // neither carries the attribute nor implements IInterceptorSubject, because A's interface
        // list lives only in A.g.cs, so the immediate base tells the generator nothing.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class A
                {
                    public partial string P { get; set; }
                }

                public class B : A { }

                [InterceptorSubject]
                public partial class C : B
                {
                    public partial string Q { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var derived = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.C.g.cs")).SourceText.ToString();

        // Assert: the base facts come from A, not from B.
        Assert.Contains("public new static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties", derived);
        Assert.Contains(".Concat(global::Repro.A.DefaultProperties)", derived);
        Assert.DoesNotContain("public event PropertyChangedEventHandler? PropertyChanged;", derived);
    }
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenAPlainClassSitsBetweenTwoSubjects"`
Expected: FAIL, with three CS0108 in the assertion message ("C.PropertyChanged hides inherited member A.PropertyChanged", the same for `RaisePropertyChanged(string)`, and "C.DefaultProperties hides inherited member A.DefaultProperties").

- [ ] **Step 3: Create the ancestor walk**

Create `src/Namotion.Interceptor.Generator/SubjectBaseContract.cs`:

```csharp
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// Everything the generator needs to know about the class a subject inherits from: which ancestor
/// owns the shared plumbing, and whether that ancestor exposes enough of it to be inherited from.
/// </summary>
internal static class SubjectBaseContract
{
    /// <summary>
    /// The first ancestor that is a subject, skipping ordinary classes in between. Plain classes
    /// between two subjects are common enough to matter and reading the immediate base instead
    /// makes the generator emit a second copy of everything it already inherited.
    /// </summary>
    public static INamedTypeSymbol? FindNearestSubjectAncestor(INamedTypeSymbol typeSymbol)
    {
        for (var ancestor = typeSymbol.BaseType;
             ancestor is { SpecialType: not SpecialType.System_Object };
             ancestor = ancestor.BaseType)
        {
            if (HasInterceptorSubjectAttribute(ancestor) || DeclaresInterceptorSubject(ancestor))
            {
                return ancestor;
            }
        }

        return null;
    }

    public static bool HasInterceptorSubjectAttribute(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        return type
            .GetAttributes()
            .Any(a => SymbolExtensions.IsTypeOrInheritsFrom(a.AttributeClass, KnownTypes.InterceptorSubjectAttribute));
    }

    /// <summary>
    /// Whether the type itself declares IInterceptorSubject, directly or through an interface it
    /// declares. Deliberately not AllInterfaces and deliberately no BaseType recursion: those
    /// report interfaces inherited from a base class, which would stop the ancestor walk at a
    /// plain intermediate whenever the real subject ancestor comes from a metadata reference,
    /// that is, in every cross-assembly hierarchy.
    /// </summary>
    private static bool DeclaresInterceptorSubject(INamedTypeSymbol type)
    {
        return type.Interfaces.Any(declared =>
            IsInterceptorSubject(declared) || declared.AllInterfaces.Any(IsInterceptorSubject));
    }

    private static bool IsInterceptorSubject(INamedTypeSymbol interfaceType)
    {
        return interfaceType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == KnownTypes.IInterceptorSubject;
    }
}
```

- [ ] **Step 4: Wire it into the extractor**

In `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`, replace lines 98 to 113 (the block starting `var baseType = typeSymbol.BaseType;` and ending with the `baseClassHasInpc` assignment) with:

```csharp
        // The nearest subject ancestor, not the immediate base: a plain class may sit between two
        // subjects, and reading the immediate base makes this class re-emit plumbing it already
        // inherited (three CS0108, which are build errors under TreatWarningsAsErrors).
        var subjectAncestor = SubjectBaseContract.FindNearestSubjectAncestor(typeSymbol);

        var baseClassTypeName = subjectAncestor?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var baseClassHasInterceptorSubject = SubjectBaseContract.HasInterceptorSubjectAttribute(subjectAncestor);

        // Only the first disjunct follows the ancestor. The second is deliberately asked of the
        // SUBJECT: a base that implements IRaisePropertyChanged by hand without implementing
        // IInterceptorSubject is not a subject ancestor at all, and dropping this would make its
        // subclass re-declare PropertyChanged and RaisePropertyChanged. ManualInpcPersonBase in
        // Namotion.Interceptor.Tracking.Tests is exactly that shape and has a live test.
        var baseClassHasInpc = baseClassHasInterceptorSubject ||
                               ImplementsInterface(typeSymbol, KnownTypes.IRaisePropertyChanged);
```

- [ ] **Step 5: Delete the now-duplicated helper**

`HasInterceptorSubjectAttribute` now lives in `SubjectBaseContract`. Delete the private copy in `SubjectMetadataExtractor.cs` (the method at roughly line 877) and update its remaining call sites to `SubjectBaseContract.HasInterceptorSubjectAttribute(...)`.

Run: `dotnet build src/Namotion.Interceptor.Generator`
Expected: Build succeeded, 0 errors. If a call site was missed the compiler names it.

- [ ] **Step 6: Run the new test and the Task 1 pins**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~SubjectBaseShapeTests"`
Expected: PASS, 3 tests. The INPC pin from Task 1 passing here is the point: it is what proves the second disjunct survived.

- [ ] **Step 7: Run the whole generator suite**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests`
Expected: PASS. Snapshot tests may report differences only if a fixture has a plain class between two subjects; none does today, so expect no snapshot churn.

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor.Generator/SubjectBaseContract.cs \
        src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs \
        src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs
git commit -m "Fix: resolve base class facts from the nearest subject ancestor"
```

---

### Task 3: Sealed root subjects

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/Models/SubjectMetadata.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs:131-148`
- Test: `src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs`

**Interfaces:**
- Produces: `SubjectMetadata.IsSealed` (bool), positioned after `AccessModifier`.
- Consumes: nothing from earlier tasks.

A `protected` member in a sealed class is CS0628, which is a build error here. `RaisePropertyChanged` is already emitted `protected`, so a sealed **root** subject does not build today. Task 4 adds four more protected members, so this rule has to exist before them.

- [ ] **Step 1: Write the failing test**

Append to `src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs`:

```csharp
    [Fact]
    public void WhenSubjectIsSealedAndIsARoot_ThenProtectedMembersAreEmittedPrivate()
    {
        // Arrange: a sealed root emits protected RaisePropertyChanged today, which is CS0628 and
        // therefore a build error for consumers.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public sealed partial class SealedRoot
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var generated = result.SingleSource();

        // Assert
        Assert.Contains("private void RaisePropertyChanged(string propertyName)", generated);
        Assert.DoesNotContain("protected void RaisePropertyChanged(string propertyName)", generated);
        Assert.Contains("void IRaisePropertyChanged.RaisePropertyChanged(string propertyName)", generated);
    }
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenSubjectIsSealedAndIsARoot"`
Expected: FAIL, with `warning CS0628: 'SealedRoot.RaisePropertyChanged(string)': new protected member declared in sealed type` in the message.

- [ ] **Step 3: Add the fact to the model**

In `src/Namotion.Interceptor.Generator/Models/SubjectMetadata.cs`, add `bool IsSealed,` immediately after `string AccessModifier,`:

```csharp
internal sealed record SubjectMetadata(
    string ClassName,
    string AccessModifier,
    bool IsSealed,
    string? NamespaceName,
    string FullTypeName,
    ContainingType[] ContainingTypes,
    bool NeedsGeneratedParameterlessConstructor,
    bool HasOrWillHaveParameterlessConstructor,
    string? BaseClassTypeName,
    bool BaseClassHasInterceptorSubject,
    bool BaseClassHasInpc,
    IReadOnlyList<PropertyMetadata> Properties,
    IReadOnlyList<MethodMetadata> Methods);
```

- [ ] **Step 4: Populate it from the symbol, not from syntax**

In `SubjectMetadataExtractor.cs`, next to `var accessModifier = GetAccessModifierFromAccessibility(typeSymbol.DeclaredAccessibility);` (line 85), add:

```csharp
        // From the symbol, because 'sealed' may sit on any partial declaration, not necessarily
        // the attributed one. DetectConstructorState already scans every declaration for the same
        // reason.
        var isSealed = typeSymbol.IsSealed;
```

and pass `isSealed` as the third argument of the `new SubjectMetadata(...)` call (immediately after `accessModifier`).

- [ ] **Step 5: Emit the right modifier**

In `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`, change `EmitNotifyPropertyChangedImplementation` to take the metadata and pick the modifier. Replace lines 131 to 148 with:

```csharp
    /// <summary>
    /// A sealed class cannot be derived from, so a protected member in one is CS0628, which is a
    /// build error under TreatWarningsAsErrors. Emit private instead; nothing can need the access.
    /// </summary>
    private static string InheritableModifier(SubjectMetadata metadata) => metadata.IsSealed ? "private" : "protected";

    private static void EmitNotifyPropertyChangedImplementation(StringBuilder builder, SubjectMetadata metadata)
    {
        if (metadata.BaseClassHasInpc)
        {
            return;
        }

        builder.AppendLine("        public event PropertyChangedEventHandler? PropertyChanged;");
        builder.AppendLine();
        builder.AppendLine("        [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        builder.AppendLine($"        {InheritableModifier(metadata)} void RaisePropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, PropertyChangedEventArgsCache.Get(propertyName));");
        builder.AppendLine();
        builder.AppendLine("        void IRaisePropertyChanged.RaisePropertyChanged(string propertyName) => RaisePropertyChanged(propertyName);");
        builder.AppendLine();
    }
```

and update the call in `Generate` (line 20) to `EmitNotifyPropertyChangedImplementation(builder, metadata);`.

- [ ] **Step 6: Run the test**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~SubjectBaseShapeTests"`
Expected: PASS, 4 tests.

- [ ] **Step 7: Accept snapshot churn**

The raw-string block became line-by-line appends, so whitespace must match exactly. Run the snapshot tests:

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Generator.Tests`
Expected: PASS with no snapshot differences. If any `.received.txt` appears, diff it against its `.verified.txt`: the only acceptable difference is none at all, because no fixture is sealed. If whitespace shifted, fix the emitter rather than accepting the snapshot.

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor.Generator/Models/SubjectMetadata.cs \
        src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs \
        src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs \
        src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs
git commit -m "Fix: emit private instead of protected members in sealed subjects"
```

---

### Task 4: Split the plumbing into root mode and derived mode

This is the fix for #437.

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/Models/SubjectMetadata.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs:12-32, 150-180, 401-431`
- Create: `src/Namotion.Interceptor.Generator.Tests/BaseClassInterceptionBehaviorTests.cs`

**Interfaces:**
- Consumes: `SubjectBaseContract.FindNearestSubjectAncestor` and `SubjectMetadata.IsSealed` from Tasks 2 and 3.
- Produces: `SubjectMetadata.EmitsSharedPlumbing` (bool), positioned after `BaseClassHasInpc`. True means root mode.

In derived mode the class emits exactly one line from the plumbing block, `IInterceptorSubject.Properties`, because `DefaultProperties` is a `static` hidden by `new` at each level and therefore binds at compile time to the class the expression was emitted into.

- [ ] **Step 1: Write the failing behaviour test**

Create `src/Namotion.Interceptor.Generator.Tests/BaseClassInterceptionBehaviorTests.cs`:

```csharp
using System.Reflection;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

[InterceptorSubject]
public partial class HierarchyRoot
{
    public partial string RootProperty { get; set; }

    public HierarchyRoot()
    {
        RootProperty = "";
    }
}

[InterceptorSubject]
public partial class HierarchyMiddle : HierarchyRoot
{
    public partial string MiddleProperty { get; set; }

    public HierarchyMiddle()
    {
        MiddleProperty = "";
    }
}

[InterceptorSubject]
public partial class HierarchyLeaf : HierarchyMiddle
{
    public partial string LeafProperty { get; set; }

    public HierarchyLeaf()
    {
        LeafProperty = "";
    }
}

public class BaseClassInterceptionBehaviorTests
{
    [Fact]
    public void WhenPropertyIsDeclaredOnABaseSubject_ThenWritesAreObservedByTheInterceptor()
    {
        // Arrange: the value and PropertyChanged both work today while the bug is present, so the
        // assertion has to be interceptor observation.
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        var leaf = new HierarchyLeaf(context);

        // Act
        leaf.RootProperty = "r";
        leaf.MiddleProperty = "m";
        leaf.LeafProperty = "l";

        // Assert
        Assert.Contains(writeInterceptor.Writes, w => w.PropertyName == "RootProperty" && Equals(w.Value, "r"));
        Assert.Contains(writeInterceptor.Writes, w => w.PropertyName == "MiddleProperty" && Equals(w.Value, "m"));
        Assert.Contains(writeInterceptor.Writes, w => w.PropertyName == "LeafProperty" && Equals(w.Value, "l"));
    }

    [Fact]
    public void WhenPropertyIsDeclaredOnABaseSubject_ThenReadsAreObservedByTheInterceptor()
    {
        // Arrange
        var readInterceptor = new RecordingReadInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => readInterceptor);

        var leaf = new HierarchyLeaf(context);
        leaf.RootProperty = "r";

        // Act
        var value = leaf.RootProperty;

        // Assert
        Assert.Equal("r", value);
        Assert.Contains(readInterceptor.Reads, r => r.PropertyName == "RootProperty");
    }

    [Fact]
    public void WhenHierarchyIsThreeLevelsDeep_ThenPropertiesReportsEveryLevel()
    {
        // Arrange & Act
        var leaf = new HierarchyLeaf();
        var properties = ((IInterceptorSubject)leaf).Properties;

        // Assert: through the interface, which is what catches a regression that moves Properties
        // into the root, and on the statics, which catches a broken Concat chain.
        Assert.Contains("RootProperty", properties.Keys);
        Assert.Contains("MiddleProperty", properties.Keys);
        Assert.Contains("LeafProperty", properties.Keys);
        Assert.Equal(3, HierarchyLeaf.DefaultProperties.Count);
        Assert.Equal(2, HierarchyMiddle.DefaultProperties.Count);
        Assert.Equal(1, HierarchyRoot.DefaultProperties.Count);
    }

    [Fact]
    public void WhenHierarchyIsThreeLevelsDeep_ThenPlumbingIsAllocatedOnce()
    {
        // Arrange & Act
        var leaf = new HierarchyLeaf();

        // Assert: this is the allocation claim. Every extra level used to cost one
        // ConcurrentDictionary and one object per instance.
        Assert.Equal(1, CountInstanceFields(leaf.GetType(), "_context"));
        Assert.Equal(1, CountInstanceFields(leaf.GetType(), "_properties"));
        Assert.Equal(1, CountBackingFields(leaf.GetType(), "Data"));
        Assert.Equal(1, CountBackingFields(leaf.GetType(), "SyncRoot"));
    }

    private static int CountInstanceFields(Type type, string name)
    {
        var count = 0;
        for (var current = type; current is not null; current = current.BaseType)
        {
            count += current
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Count(field => field.Name == name);
        }

        return count;
    }

    private static int CountBackingFields(Type type, string memberName)
    {
        var count = 0;
        for (var current = type; current is not null; current = current.BaseType)
        {
            count += current
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Count(field => field.Name.Contains(memberName) && field.Name.Contains("BackingField"));
        }

        return count;
    }

    private sealed class RecordingWriteInterceptor : IWriteInterceptor
    {
        public List<(string PropertyName, object? Value)> Writes { get; } = [];

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Writes.Add((context.Property.Name, context.NewValue));
            next(ref context);
        }
    }

    private sealed class RecordingReadInterceptor : IReadInterceptor
    {
        public List<(string PropertyName, object? Value)> Reads { get; } = [];

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            var value = next(ref context);
            Reads.Add((context.Property.Name, value));
            return value;
        }
    }
}
```

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~BaseClassInterceptionBehaviorTests"`
Expected: FAIL. `WhenPropertyIsDeclaredOnABaseSubject_ThenWritesAreObservedByTheInterceptor` fails because `RootProperty` and `MiddleProperty` are missing from `Writes`. `WhenHierarchyIsThreeLevelsDeep_ThenPlumbingIsAllocatedOnce` fails with 3 where 1 is expected. The `Properties` test passes already; it is there to stop the fix from trading one silent bug for another.

- [ ] **Step 3: Add the mode to the model**

In `Models/SubjectMetadata.cs`, add `bool EmitsSharedPlumbing,` immediately after `bool BaseClassHasInpc,`.

- [ ] **Step 4: Decide the mode in the extractor**

In `SubjectMetadataExtractor.cs`, immediately after the `baseClassHasInpc` assignment added in Task 2, add:

```csharp
        // Root mode emits the whole IInterceptorSubject block; derived mode emits only its own
        // Properties line and inherits the rest. Task 5 refines this with the contract check and
        // the NI0011/NI0012 fallbacks; for now, any subject ancestor means derived mode.
        var emitsSharedPlumbing = subjectAncestor is null;
```

and pass `emitsSharedPlumbing` to `new SubjectMetadata(...)` after `baseClassHasInpc`.

- [ ] **Step 5: Split the emitted block**

In `SubjectCodeGenerator.cs`, replace `EmitInterceptorSubjectImplementation` (lines 150 to 180) with:

```csharp
    private static void EmitInterceptorSubjectImplementation(StringBuilder builder, SubjectMetadata metadata)
    {
        // Emitted by every subject, root and derived alike. It is the one member that cannot move
        // to the root: DefaultProperties is a static hidden by 'new' at each level, so this
        // expression binds at compile time to the class it was emitted into. Emitted only in the
        // root, every derived subject would report the root's property set.
        if (!metadata.EmitsSharedPlumbing)
        {
            builder.AppendLine("        [JsonIgnore]");
            builder.AppendLine("        IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => GetInstanceProperties() ?? DefaultProperties;");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("        private IInterceptorExecutor? _context;");
        builder.AppendLine("        private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;");
        builder.AppendLine();
        builder.AppendLine("        [JsonIgnore]");
        builder.AppendLine("        IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);");
        builder.AppendLine();
        builder.AppendLine("        [JsonIgnore]");
        builder.AppendLine("        ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();");
        builder.AppendLine();
        builder.AppendLine("        [JsonIgnore]");
        builder.AppendLine("        IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => GetInstanceProperties() ?? DefaultProperties;");
        builder.AppendLine();
        builder.AppendLine("        [JsonIgnore]");
        builder.AppendLine("        object IInterceptorSubject.SyncRoot { get; } = new object();");
        builder.AppendLine();
        builder.AppendLine("        void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)");
        builder.AppendLine("        {");
        builder.AppendLine("            lock (((IInterceptorSubject)this).SyncRoot)");
        builder.AppendLine("            {");
        // Dispatching through the interface rather than reading _properties directly is what lets
        // this method live in the root: it makes the merge start from the most derived
        // DefaultProperties instead of this class's own.
        builder.AppendLine("                _properties = ((IInterceptorSubject)this).Properties");
        builder.AppendLine("                    .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))");
        builder.AppendLine("                    .ToFrozenDictionary();");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
    }
```

Update the call in `Generate` (line 21) to `EmitInterceptorSubjectImplementation(builder, metadata);`.

- [ ] **Step 6: Split the helpers the same way**

Replace `EmitHelperMethods` (lines 401 to 431) with:

```csharp
    private static void EmitHelperMethods(StringBuilder builder, SubjectMetadata metadata)
    {
        if (!metadata.EmitsSharedPlumbing)
        {
            return;
        }

        var modifier = InheritableModifier(metadata);

        // A method rather than a property: DynamicSubjectFactory reflects over
        // GetProperties(Instance | Public | NonPublic), which returns inherited protected
        // properties and turns every unknown one into an intercepted subject property. A protected
        // property here would give every Castle-proxied generated subject a phantom property.
        builder.AppendLine("        [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        builder.AppendLine($"        {modifier} IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;");
        builder.AppendLine();
        builder.AppendLine("        [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        builder.AppendLine($"        {modifier} TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)");
        builder.AppendLine("        {");
        builder.AppendLine("            return _context is not null ? _context.GetPropertyValue(propertyName, readValue)! : readValue(this)!;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        builder.AppendLine($"        {modifier} bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> setValue)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (_context is null)");
        builder.AppendLine("            {");
        builder.AppendLine("                setValue(this, newValue);");
        builder.AppendLine("                return true;");
        builder.AppendLine("            }");
        builder.AppendLine("            else");
        builder.AppendLine("            {");
        builder.AppendLine("                return _context.SetPropertyValue(propertyName, newValue, currentValue, setValue);");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        builder.AppendLine($"        {modifier} object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod, params object?[] parameters)");
        builder.AppendLine("        {");
        builder.AppendLine("            return _context is not null ? _context.InvokeMethod(methodName, parameters, invokeMethod) : invokeMethod(this, parameters);");
        builder.AppendLine("        }");
        builder.AppendLine();
    }
```

Update the call in `Generate` (line 26) to `EmitHelperMethods(builder, metadata);`.

- [ ] **Step 7: Add the remaining behaviour cases**

Four more shapes the spec requires. Append these to `BaseClassInterceptionBehaviorTests.cs`, and add the subject-typed property and the method to the fixtures above them:

```csharp
// Add to HierarchyRoot:
//     public partial HierarchyChild? Child { get; set; }
//     public partial string Describe(string prefix);
// with the method body:
//     public partial string Describe(string prefix) => prefix + RootProperty;
// and a new fixture:
// [InterceptorSubject] public partial class HierarchyChild { public partial string ChildName { get; set; } }

    [Fact]
    public void WhenMethodIsDeclaredOnABaseSubject_ThenInvocationsAreObservedByTheInterceptor()
    {
        // Arrange
        var methodInterceptor = new RecordingMethodInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => methodInterceptor);

        var leaf = new HierarchyLeaf(context);

        // Act
        var described = leaf.Describe("x:");

        // Assert
        Assert.Equal("x:", described);
        Assert.Contains(methodInterceptor.Invocations, i => i.MethodName == "Describe");
    }

    [Fact]
    public void WhenSubjectTypedPropertyIsDeclaredOnABaseSubject_ThenTheChildIsAttachedToTheRegistry()
    {
        // Arrange: this is where the user-visible damage lived. The registry never saw the
        // assignment, so the child subject was never attached, and neither a value assertion nor a
        // plain interceptor assertion covers it.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var leaf = new HierarchyLeaf(context);
        var child = new HierarchyChild();

        // Act
        leaf.Child = child;

        // Assert
        Assert.Same(context, ((IInterceptorSubject)child).Context.GetFallbackContext());
        Assert.Contains(context.GetRegistry().KnownSubjects, s => ReferenceEquals(s.Subject, child));
    }

    [Fact]
    public void WhenAddPropertiesIsCalledOnADerivedSubject_ThenDefaultsFromEveryLevelSurvive()
    {
        // Arrange: AddProperties now lives in the root and merges from
        // ((IInterceptorSubject)this).Properties, so it must start from the leaf's defaults.
        var leaf = new HierarchyLeaf();
        var added = new SubjectPropertyMetadata(
            "Extra", typeof(string), [], _ => "e", (_, _) => { }, isIntercepted: false, isDynamic: true);

        // Act
        ((IInterceptorSubject)leaf).AddProperties(added);
        var properties = ((IInterceptorSubject)leaf).Properties;

        // Assert
        Assert.Contains("Extra", properties.Keys);
        Assert.Contains("RootProperty", properties.Keys);
        Assert.Contains("MiddleProperty", properties.Keys);
        Assert.Contains("LeafProperty", properties.Keys);
    }

    [Fact]
    public void WhenContextIsPassedToTheBaseConstructor_ThenLaterConstructorWritesAreIntercepted()
    {
        // Arrange: ((IInterceptorSubject)this).Context dispatches virtually, so a ": base(context)"
        // constructor publishes the executor inside the BASE constructor. A base-declared write
        // afterwards is therefore intercepted now, where it took the fast path before. This is the
        // fix working, and it is pinned so it does not read as an accident later.
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        // Act
        var leaf = new HierarchyLeaf(context);
        leaf.RootProperty = "after-construction";

        // Assert
        Assert.Contains(writeInterceptor.Writes, w => w.PropertyName == "RootProperty");
    }

    private sealed class RecordingMethodInterceptor : IMethodInterceptor
    {
        public List<(string MethodName, object?[] Parameters)> Invocations { get; } = [];

        public object? InvokeMethod(MethodInvocationContext context, InvokeMethodInterceptionDelegate next)
        {
            Invocations.Add((context.MethodName, context.Parameters));
            return next(ref context);
        }
    }
```

If `GetFallbackContext()` or `GetRegistry().KnownSubjects` do not exist under those names, use whichever registry assertion `src/Namotion.Interceptor.Registry.Tests` already uses for "this child got attached"; the requirement is that the assertion fails when the child is not registered.

- [ ] **Step 8: Run the behaviour tests**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~BaseClassInterceptionBehaviorTests"`
Expected: PASS, 8 tests.

- [ ] **Step 9: Run the whole generator suite and accept the snapshots**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Generator.Tests`
Expected: snapshot tests fail with `.received.txt` files. Review each diff. The only acceptable changes are: `private` becoming `protected` on the three helpers, the new `GetInstanceProperties()` member, `_properties ?? DefaultProperties` becoming `GetInstanceProperties() ?? DefaultProperties`, the `AddProperties` operand, and in derived-subject snapshots the removal of the whole block. Member order inside the block must be unchanged. Accept by replacing each `.verified.txt` with its `.received.txt`.

- [ ] **Step 10: Run the full unit suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS. A failure here is a real behaviour change somewhere else and must be understood, not accepted.

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "Fix: emit the subject plumbing once per hierarchy instead of once per class"
```

---

### Task 5: The subject base contract, NI0011 and NI0012

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectBaseContract.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`
- Modify: `src/Namotion.Interceptor.Generator/Models/SubjectMetadata.cs`
- Modify: `src/Namotion.Interceptor.Generator/Diagnostics.cs`
- Modify: `src/Namotion.Interceptor.Generator/AnalyzerReleases.Unshipped.md`
- Create: `src/Namotion.Interceptor.Generator.Tests/SubjectBaseDiagnosticsTests.cs`

**Interfaces:**
- Consumes: `SubjectBaseContract.FindNearestSubjectAncestor`, `SubjectMetadata.EmitsSharedPlumbing`.
- Produces: `SubjectBaseContract.SatisfiesContract(INamedTypeSymbol ancestor, INamedTypeSymbol subject, Compilation compilation, out IReadOnlyList<string> missingMembers)` returning `bool`; `SubjectBaseContract.HasUsableDefaultProperties(INamedTypeSymbol ancestor, INamedTypeSymbol subject, Compilation compilation)` returning `bool`; `SubjectMetadata.HiddenPlumbingMemberNames` (`IReadOnlyList<string>`).

Task 4 assumed any subject ancestor means derived mode. That is only safe when the ancestor actually provides the members. This task adds the check and the two fallbacks.

- [ ] **Step 1: Write the failing diagnostics tests**

Create `src/Namotion.Interceptor.Generator.Tests/SubjectBaseDiagnosticsTests.cs`:

```csharp
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class SubjectBaseDiagnosticsTests
{
    private const string NonConformingBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject
            {
                private IInterceptorExecutor? _context;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                object IInterceptorSubject.SyncRoot { get; } = new object();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                {
                    _properties = _properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();
                }
            }
        }
        """;

    [Fact]
    public void WhenBaseImplementsTheInterfaceWithoutTheContract_ThenNI0011IsReported()
    {
        // Arrange: no DefaultProperties, no helpers. Today this shape dies on CS0117 inside
        // generated code, which the user cannot edit.
        var source = NonConformingBase + """

            namespace Repro
            {
                [Namotion.Interceptor.Attributes.InterceptorSubject]
                public partial class GenDerived : HandBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0011");
        Assert.DoesNotContain(result.CompilationErrors, d => d.Id == "CS0117");
    }

    [Fact]
    public void WhenBaseHasOnlyDefaultProperties_ThenNI0012IsReportedAndItStillCompiles()
    {
        // Arrange: this shape compiles and works today, so it must not become an error.
        var source = NonConformingBase.Replace(
            "public class HandBase : IInterceptorSubject",
            """
            public class HandBase : IInterceptorSubject
            {
                public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;
            """.TrimEnd() + Environment.NewLine + "    ") + """

            namespace Repro
            {
                [Namotion.Interceptor.Attributes.InterceptorSubject]
                public partial class GenDerived : HandBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert: warning, root-mode fallback, and no stray 'new' (which would be CS0109).
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0012");
        Assert.Empty(result.CompilationErrors);
        Assert.DoesNotContain(result.CompilationWarnings, d => d.Id == "CS0109");
    }
}
```

The second test needs a base that has `DefaultProperties` and nothing else, so give it its own fixture rather than string-editing the first one. Add this constant to the class and use it in place of `NonConformingBase` in `WhenBaseHasOnlyDefaultProperties_ThenNI0012IsReportedAndItStillCompiles`:

```csharp
    private const string DefaultPropertiesOnlyBase = """
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Frozen;
        using System.Linq;
        using Namotion.Interceptor;
        using Namotion.Interceptor.Interceptors;

        namespace Repro
        {
            public class HandBase : IInterceptorSubject
            {
                private IInterceptorExecutor? _context;
                private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                    = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                object IInterceptorSubject.SyncRoot { get; } = new object();
                IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

                public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                {
                    _properties = _properties
                        .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                        .ToFrozenDictionary();
                }
            }
        }
        """;
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~SubjectBaseDiagnosticsTests"`
Expected: FAIL, both, because NI0011 and NI0012 do not exist yet.

- [ ] **Step 3: Add the four descriptors**

Append to `src/Namotion.Interceptor.Generator/Diagnostics.cs`, before the closing brace:

```csharp
    public static readonly DiagnosticDescriptor BaseDoesNotSatisfyContract = new(
        id: "NI0011",
        title: "Base class does not satisfy the subject base contract",
        messageFormat: "Base class '{0}' cannot host a generated subject: it is missing {1}. Use [InterceptorSubject] on the base, or call AddProperties for runtime properties",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated subclass calls members the base class must provide. This checks their shape, not their behaviour.");

    public static readonly DiagnosticDescriptor BasePlumbingCannotBeShared = new(
        id: "NI0012",
        title: "Base class plumbing cannot be shared",
        messageFormat: "Base class '{0}' does not expose the shared subject plumbing, so '{1}' emits its own and base-declared properties stay unintercepted. Rebuild the base assembly against the current package version, or satisfy the subject base contract",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The subject still compiles and behaves as it did before the plumbing was shared.");

    public static readonly DiagnosticDescriptor HidesGeneratedMember = new(
        id: "NI0013",
        title: "Member hides an inherited generated member",
        messageFormat: "'{0}' declares '{1}', which hides the inherited generated member of the same name and can silently capture the generated call",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated property and method bodies call these members by simple name.");

    public static readonly DiagnosticDescriptor HijacksInterfaceImplementation = new(
        id: "NI0014",
        title: "Member hijacks an inherited interface implementation",
        messageFormat: "'{0}' declares '{1}', which takes the IInterceptorSubject.{1} slot from the base class implementation under interface re-implementation",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Hijacking Context leaves the inherited helpers reading a context that is never populated, so interception silently stops.");
```

- [ ] **Step 4: Register them for release tracking**

Append to `src/Namotion.Interceptor.Generator/AnalyzerReleases.Unshipped.md`, under the existing table:

```
NI0011 | Namotion.Interceptor | Error | Base class does not satisfy the subject base contract
NI0012 | Namotion.Interceptor | Warning | Base class plumbing cannot be shared
NI0013 | Namotion.Interceptor | Error | Member hides an inherited generated member
NI0014 | Namotion.Interceptor | Error | Member hijacks an inherited interface implementation
```

Run: `dotnet build src/Namotion.Interceptor.Generator`
Expected: Build succeeded. An RS2008 error here means a row is missing or misformatted.

- [ ] **Step 5: Implement the contract check**

Append to `SubjectBaseContract.cs`:

```csharp
    private const string PropertyMetadataDictionary = "System.Collections.Generic.IReadOnlyDictionary<string, Namotion.Interceptor.SubjectPropertyMetadata>";

    /// <summary>
    /// The members a class must expose to host a generated subclass. Generated root mode satisfies
    /// this by construction. Lookup walks the ancestor chain, so a member inherited by the ancestor
    /// from further up counts, and runs against the constructed type, so a generic base is checked
    /// with its type arguments substituted.
    /// </summary>
    public static bool SatisfiesContract(
        INamedTypeSymbol ancestor,
        INamedTypeSymbol subject,
        Compilation compilation,
        out IReadOnlyList<string> missingMembers)
    {
        var missing = new List<string>();

        if (!ImplementsInterfaceThroughChain(ancestor, KnownTypes.IInterceptorSubject))
        {
            missing.Add(KnownTypes.IInterceptorSubject);
        }

        if (!ImplementsInterfaceThroughChain(ancestor, KnownTypes.IRaisePropertyChanged) &&
            !ImplementsInterfaceThroughChain(subject, KnownTypes.IRaisePropertyChanged))
        {
            missing.Add(KnownTypes.IRaisePropertyChanged);
        }

        if (!HasAccessibleMethod(ancestor, subject, compilation, "GetPropertyValue", typeParameterCount: 1, parameterCount: 2))
        {
            missing.Add("protected TProperty GetPropertyValue<TProperty>(string, Func<IInterceptorSubject, TProperty>)");
        }

        if (!HasAccessibleMethod(ancestor, subject, compilation, "SetPropertyValue", typeParameterCount: 1, parameterCount: 4))
        {
            missing.Add("protected bool SetPropertyValue<TProperty>(string, TProperty, TProperty, Action<IInterceptorSubject, TProperty>)");
        }

        // IsParams matters: the emitted call site uses expanded form, InvokeMethod("M", lambda, p1),
        // so a base declaring the same parameter types without params passes a signature match and
        // then fails at the call.
        if (!HasAccessibleMethod(ancestor, subject, compilation, "InvokeMethod", typeParameterCount: 0, parameterCount: 3, requireParams: true))
        {
            missing.Add("protected object? InvokeMethod(string, Func<IInterceptorSubject, object?[], object?>, params object?[])");
        }

        if (!HasAccessibleMethod(ancestor, subject, compilation, "GetInstanceProperties", typeParameterCount: 0, parameterCount: 0))
        {
            missing.Add("protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties()");
        }

        if (!HasUsableDefaultProperties(ancestor, subject, compilation))
        {
            missing.Add("public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties");
        }

        missingMembers = missing;
        return missing.Count == 0;
    }

    /// <summary>
    /// A static DefaultProperties that is both accessible and of a type the emitted .Concat(...)
    /// accepts. Checking only that some static of that name resolves lets a base declaring
    /// "public static int DefaultProperties" through, and the generated code then fails with
    /// CS1929, which is exactly the raw compiler error in generated code the diagnostics exist to
    /// replace.
    /// </summary>
    public static bool HasUsableDefaultProperties(INamedTypeSymbol ancestor, INamedTypeSymbol subject, Compilation compilation)
    {
        foreach (var candidate in EnumerateChain(ancestor))
        {
            foreach (var member in candidate.GetMembers("DefaultProperties").OfType<IPropertySymbol>())
            {
                if (!member.IsStatic ||
                    !compilation.IsSymbolAccessibleWithin(member, subject))
                {
                    continue;
                }

                if (member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Contains("SubjectPropertyMetadata"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasAccessibleMethod(
        INamedTypeSymbol ancestor,
        INamedTypeSymbol subject,
        Compilation compilation,
        string name,
        int typeParameterCount,
        int parameterCount,
        bool requireParams = false)
    {
        foreach (var candidate in EnumerateChain(ancestor))
        {
            foreach (var method in candidate.GetMembers(name).OfType<IMethodSymbol>())
            {
                if (method.IsStatic ||
                    method.TypeParameters.Length != typeParameterCount ||
                    method.Parameters.Length != parameterCount ||
                    !compilation.IsSymbolAccessibleWithin(method, subject))
                {
                    continue;
                }

                if (requireParams && !method.Parameters[method.Parameters.Length - 1].IsParams)
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateChain(INamedTypeSymbol type)
    {
        for (var current = type; current is { SpecialType: not SpecialType.System_Object }; current = current.BaseType)
        {
            yield return current;
        }
    }

    private static bool ImplementsInterfaceThroughChain(INamedTypeSymbol type, string interfaceTypeName)
    {
        return type.AllInterfaces.Any(i => i.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == interfaceTypeName);
    }
```

Add `using System.Collections.Generic;` to the file's usings.

- [ ] **Step 6: Implement the `new` modifier lookup**

Append to `SubjectBaseContract.cs`:

```csharp
    /// <summary>
    /// The root-mode member names that need a 'new' modifier because the ancestor chain already
    /// exposes an accessible member of that name. This is C#'s hiding rule, not the contract's
    /// signature match: CS0108 fires for a same-name member of a DIFFERENT kind too, while a blanket
    /// 'new' produces CS0109 when nothing is hidden. Both are build errors under
    /// TreatWarningsAsErrors, so the modifier has to be decided per member.
    /// </summary>
    public static IReadOnlyList<string> FindHiddenPlumbingMembers(
        INamedTypeSymbol? ancestor,
        INamedTypeSymbol subject,
        Compilation compilation)
    {
        if (ancestor is null)
        {
            return [];
        }

        string[] candidates = ["GetInstanceProperties", "GetPropertyValue", "SetPropertyValue", "InvokeMethod"];
        var hidden = new List<string>();

        foreach (var name in candidates)
        {
            var isHidden = EnumerateChain(ancestor)
                .SelectMany(type => type.GetMembers(name))
                .Any(member => !member.IsStatic && compilation.IsSymbolAccessibleWithin(member, subject));

            if (isHidden)
            {
                hidden.Add(name);
            }
        }

        return hidden;
    }
```

- [ ] **Step 7: Add the fact to the model and populate it**

In `Models/SubjectMetadata.cs`, add `IReadOnlyList<string> HiddenPlumbingMemberNames,` immediately after `bool EmitsSharedPlumbing,`.

In `SubjectMetadataExtractor.cs`, replace the `emitsSharedPlumbing` assignment from Task 4 with the full mode selection:

```csharp
        // Mode selection, asked of the nearest subject ancestor and never of "some ancestor": a
        // hand-written IInterceptorSubject implementer between two generated subjects would
        // otherwise select derived mode and silently reproduce this bug, because Context resolves
        // to the middle's executor while the inherited helpers read the root's field.
        var emitsSharedPlumbing = true;
        IReadOnlyList<string> hiddenPlumbingMembers = [];

        if (subjectAncestor is not null)
        {
            var ancestorIsGeneratedHere =
                baseClassHasInterceptorSubject && subjectAncestor.DeclaringSyntaxReferences.Length > 0;

            if (ancestorIsGeneratedHere ||
                SubjectBaseContract.SatisfiesContract(subjectAncestor, typeSymbol, semanticModel.Compilation, out var missingMembers))
            {
                emitsSharedPlumbing = false;
            }
            else if (SubjectBaseContract.HasUsableDefaultProperties(subjectAncestor, typeSymbol, semanticModel.Compilation))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.BasePlumbingCannotBeShared,
                    location,
                    subjectAncestor.ToDisplayString(),
                    typeSymbol.ToDisplayString()));

                hiddenPlumbingMembers = SubjectBaseContract.FindHiddenPlumbingMembers(
                    subjectAncestor, typeSymbol, semanticModel.Compilation);
            }
            else
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.BaseDoesNotSatisfyContract,
                    location,
                    subjectAncestor.ToDisplayString(),
                    string.Join(", ", missingMembers)));

                return new ExtractionResult(null, diagnostics);
            }
        }
```

`new ExtractionResult(null, diagnostics)` is the established suppression shape: `ExtractionResult.Metadata` is nullable and a null means no source is emitted, which is how NI0001, NI0002, NI0003, NI0009 and NI0010 already prevent a cascade of consequent errors (`SubjectMetadataExtractor.cs:35, 41, 50, 60, 67, 77`).

Pass `hiddenPlumbingMembers` to `new SubjectMetadata(...)` after `emitsSharedPlumbing`.

- [ ] **Step 8: Emit `new` where it is needed**

In `SubjectCodeGenerator.cs`, add near `InheritableModifier`:

```csharp
    private static string HidingModifier(SubjectMetadata metadata, string memberName)
        => metadata.HiddenPlumbingMemberNames.Contains(memberName) ? "new " : "";
```

and prefix each of the four members in `EmitHelperMethods` with it, for example:

```csharp
        builder.AppendLine($"        {HidingModifier(metadata, "GetInstanceProperties")}{modifier} IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;");
```

- [ ] **Step 9: Run the diagnostics tests**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~SubjectBaseDiagnosticsTests"`
Expected: PASS, 2 tests.

- [ ] **Step 10: Run everything**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS.

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "Add the subject base contract with NI0011 and NI0012"
```

---

### Task 6: NI0013 and NI0014

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectBaseContract.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/SubjectBaseDiagnosticsTests.cs`

**Interfaces:**
- Consumes: `SubjectMetadata.EmitsSharedPlumbing`, the contract provider from Task 5.
- Produces: nothing later tasks depend on.

Both rules fire only in derived mode. In root mode the class declares the helpers itself, so a capturing member is already a hard CS0111 collision and a diagnostic would be noise.

- [ ] **Step 1: Write the failing tests**

Append to `SubjectBaseDiagnosticsTests.cs`:

```csharp
    [Fact]
    public void WhenDerivedSubjectDeclaresAGeneratedMemberName_ThenNI0013IsReported()
    {
        // Arrange: a 'new' annotated member of the same shape captures the generated call and
        // produces no compiler diagnostic at all, which is why the rule is name-only.
        const string source = """
            using System;
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class RootSubject
                {
                    public partial string RootName { get; set; }
                }

                [InterceptorSubject]
                public partial class LeafSubject : RootSubject
                {
                    public partial string LeafName { get; set; }

                    public string InstanceProperties { get; set; } = "";
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0013");
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresAPublicSyncRoot_ThenNI0014IsReported()
    {
        // Arrange: this compiles clean today, because the derived class emits its own explicit
        // implementation which wins. After the split it takes the interface slot.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class RootSubject
                {
                    public partial string RootName { get; set; }
                }

                [InterceptorSubject]
                public partial class LeafSubject : RootSubject
                {
                    public partial string LeafName { get; set; }

                    public object SyncRoot { get; } = new object();
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0014");
    }

    [Fact]
    public void WhenRootSubjectDeclaresAPublicSyncRoot_ThenNoDiagnosticIsReported()
    {
        // Arrange: interface mapping prefers a class's own explicit implementation over its own
        // public members, so the root is never hijacked by its own member.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class RootSubject
                {
                    public partial string RootName { get; set; }

                    public object SyncRoot { get; } = new object();
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id is "NI0013" or "NI0014");
    }
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~SubjectBaseDiagnosticsTests"`
Expected: the two new positive tests FAIL, the negative one passes.

- [ ] **Step 3: Implement the two scans**

Append to `SubjectBaseContract.cs`:

```csharp
    private static readonly string[] GeneratedMemberNames =
        ["GetInstanceProperties", "GetPropertyValue", "SetPropertyValue", "InvokeMethod"];

    private static readonly string[] HijackableInterfaceMembers =
        ["Context", "Data", "SyncRoot", "AddProperties"];

    /// <summary>
    /// Members named like an inherited generated member. Deliberately name-only, any kind, no
    /// signature test: a 'new' annotated member of the same shape captures the generated call with
    /// no compiler diagnostic at all, and an applicable overload with a different signature can win
    /// overload resolution without hiding anything. Reporting the name covers both. On intermediate
    /// classes the scan is restricted to members accessible from the subject, because a private
    /// member neither hides nor is found by member lookup.
    /// </summary>
    public static IEnumerable<(INamedTypeSymbol Declarer, string MemberName)> FindHidingMembers(
        INamedTypeSymbol subject,
        INamedTypeSymbol contractProvider,
        Compilation compilation)
    {
        foreach (var type in EnumerateBetween(subject, contractProvider))
        {
            foreach (var name in GeneratedMemberNames)
            {
                foreach (var member in type.GetMembers(name))
                {
                    if (member.IsStatic)
                    {
                        continue;
                    }

                    if (!SymbolEqualityComparer.Default.Equals(type, subject) &&
                        !compilation.IsSymbolAccessibleWithin(member, subject))
                    {
                        continue;
                    }

                    yield return (type, name);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Public members, and explicit interface implementations declared on non-subject classes, that
    /// would take an IInterceptorSubject slot from the root under interface re-implementation.
    /// Context is the severe one: hijacking it leaves the inherited helpers reading a context that
    /// is never populated, so interception stops silently and the unguarded IInterceptorExecutor
    /// casts in DynamicSubjectFactory and RegisteredSubject throw.
    /// </summary>
    public static IEnumerable<(INamedTypeSymbol Declarer, string MemberName)> FindHijackingMembers(
        INamedTypeSymbol subject,
        INamedTypeSymbol contractProvider,
        Compilation compilation)
    {
        foreach (var type in EnumerateBetween(subject, contractProvider))
        {
            var typeIsSubject = HasInterceptorSubjectAttribute(type);

            foreach (var name in HijackableInterfaceMembers)
            {
                foreach (var member in type.GetMembers())
                {
                    if (member.IsStatic)
                    {
                        continue;
                    }

                    var isPublicMatch = member.Name == name &&
                                        member.DeclaredAccessibility == Accessibility.Public;

                    var isExplicitMatch = !typeIsSubject && IsExplicitInterceptorSubjectImplementation(member, name);

                    if (isPublicMatch || isExplicitMatch)
                    {
                        yield return (type, name);
                        break;
                    }
                }
            }
        }
    }

    private static bool IsExplicitInterceptorSubjectImplementation(ISymbol member, string name)
    {
        var explicitProperty = (member as IPropertySymbol)?.ExplicitInterfaceImplementations.FirstOrDefault();
        var explicitMethod = (member as IMethodSymbol)?.ExplicitInterfaceImplementations.FirstOrDefault();
        var implemented = (ISymbol?)explicitProperty ?? explicitMethod;

        return implemented is not null &&
               implemented.Name == name &&
               implemented.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == KnownTypes.IInterceptorSubject;
    }

    /// <summary>
    /// The subject and every class between it and the class providing the contract member, which
    /// is where a capturing or hijacking member can sit. Members in the provider itself are
    /// excluded: interface mapping prefers a class's own explicit implementation over its own
    /// public members.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> EnumerateBetween(INamedTypeSymbol subject, INamedTypeSymbol provider)
    {
        for (var current = subject;
             current is { SpecialType: not SpecialType.System_Object } &&
             !SymbolEqualityComparer.Default.Equals(current, provider);
             current = current.BaseType!)
        {
            yield return current;
        }
    }
```

- [ ] **Step 4: Report them**

In `SubjectMetadataExtractor.cs`, inside the `if (subjectAncestor is not null)` block from Task 5, in the branch that sets `emitsSharedPlumbing = false`, add:

```csharp
                foreach (var (declarer, memberName) in SubjectBaseContract.FindHidingMembers(
                             typeSymbol, subjectAncestor, semanticModel.Compilation))
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.HidesGeneratedMember, location, declarer.ToDisplayString(), memberName));
                }

                foreach (var (declarer, memberName) in SubjectBaseContract.FindHijackingMembers(
                             typeSymbol, subjectAncestor, semanticModel.Compilation))
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.HijacksInterfaceImplementation, location, declarer.ToDisplayString(), memberName));
                }
```

Using `subjectAncestor` as the provider is correct for the in-source branch, where the provider's members do not exist as symbols yet. For a metadata ancestor it is the nearest subject ancestor, which is the class that declares them.

- [ ] **Step 5: Run the tests**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~SubjectBaseDiagnosticsTests"`
Expected: PASS, 5 tests.

- [ ] **Step 6: Cover the edge cases the rules were written for**

Append to `SubjectBaseDiagnosticsTests.cs`. The first is the shape that made mode selection use the *nearest* ancestor rather than *some* ancestor; without that qualifier it silently reproduces #437 with no diagnostic at all.

```csharp
    [Fact]
    public void WhenAHandWrittenSubjectSitsBetweenTwoGeneratedSubjects_ThenTheLeafFallsBackToRootMode()
    {
        // Arrange: the middle re-implements IInterceptorSubject by hand, so its Context wins the
        // interface map while the root's helpers still read the root's never-populated field.
        // Selecting derived mode here would reproduce the bug this whole change fixes.
        const string source = """
            using System;
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.Collections.Frozen;
            using System.Linq;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Interceptors;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class GenRoot
                {
                    public partial string RootName { get; set; }
                }

                public class HandMiddle : GenRoot, IInterceptorSubject
                {
                    private IInterceptorExecutor? _context;

                    IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                    object IInterceptorSubject.SyncRoot { get; } = new object();
                    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties
                        => FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) { }
                }

                [InterceptorSubject]
                public partial class GenLeaf : HandMiddle
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);
        var leaf = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.GenLeaf.g.cs")).SourceText.ToString();

        // Assert: root mode, so the leaf owns its own executor rather than reading one nothing fills.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id is "NI0011" or "NI0012");
        Assert.Contains("private IInterceptorExecutor? _context;", leaf);
    }

    [Fact]
    public void WhenAnIntermediateClassDeclaresAPrivateGeneratedMemberName_ThenNI0013IsNotReported()
    {
        // Arrange: a private member on an intermediate neither hides nor is found by member lookup,
        // so nothing is captured and firing an error would be a pure false positive.
        const string source = """
            using System;
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class RootSubject
                {
                    public partial string RootName { get; set; }
                }

                public class PlainMiddle : RootSubject
                {
                    private string InvokeMethod = "";
                }

                [InterceptorSubject]
                public partial class LeafSubject : PlainMiddle
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain(result.GeneratorDiagnostics, d => d.Id == "NI0013");
    }

    [Fact]
    public void WhenBaseDefaultPropertiesHasTheWrongType_ThenNI0011IsReportedRatherThanACompilerError()
    {
        // Arrange: goal 5. Accepting any static named DefaultProperties lets this through and the
        // generated .Concat(...) then fails with CS1929 inside code the user cannot edit.
        const string source = """
            using System;
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.Collections.Frozen;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Interceptors;

            namespace Repro
            {
                public class HandBase : IInterceptorSubject
                {
                    private IInterceptorExecutor? _context;

                    public static int DefaultProperties { get; } = 0;

                    IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                    object IInterceptorSubject.SyncRoot { get; } = new object();
                    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties
                        => FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) { }
                }

                [InterceptorSubject]
                public partial class GenDerived : HandBase
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0011");
        Assert.DoesNotContain(result.CompilationErrors, d => d.Id == "CS1929");
    }
```

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~SubjectBaseDiagnosticsTests"`
Expected: PASS, 8 tests.

- [ ] **Step 7: Run everything**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS. If any existing subject trips NI0013 or NI0014, that is a real finding: report it rather than weakening the rule.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Add NI0013 and NI0014 for members that capture or hijack inherited plumbing"
```

---

### Task 7: Hand written base and hand written subclass, end to end

**Files:**
- Modify: `src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs`

**Interfaces:**
- Consumes: `GeneratorTestHost.RunWithLibraryReference(..., runGeneratorOverLibrary: true)` from Task 1, the contract from Task 5.

Goal 4 of the spec. Both directions must work, and the cross assembly case is the one mode selection branch 2 exists for.

- [ ] **Step 1: Write the hand written subclass test**

Append to `SubjectBaseShapeTests.cs`:

```csharp
    [Fact]
    public void WhenSubclassIsHandWritten_ThenItCanUseTheProtectedHelpers()
    {
        // Arrange: this is CS0122 today, because the helpers are private.
        const string source = """
            using System;
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class GenBase
                {
                    public partial string BaseName { get; set; }
                }

                public class HandDerived : GenBase
                {
                    private string _own = "";

                    public HandDerived()
                    {
                        // Must run before the first intercepted write: PropertyReference.Metadata
                        // throws when the name is not registered.
                        ((IInterceptorSubject)this).AddProperties(
                            new SubjectPropertyMetadata(
                                nameof(Own),
                                typeof(string),
                                [],
                                o => ((HandDerived)o).Own,
                                (o, v) => ((HandDerived)o).Own = (string)v!,
                                isIntercepted: true,
                                isDynamic: false));
                    }

                    public string Own
                    {
                        get => GetPropertyValue(nameof(Own), static o => ((HandDerived)o)._own);
                        set => SetPropertyValue(nameof(Own), value, _own, static (o, v) => ((HandDerived)o)._own = v);
                    }
                }
            }
            """;

        // Act & Assert
        GeneratorTestHost.RunExpectingNoWarnings(source);
    }
```

The constructor used above is `SubjectPropertyMetadata(string name, Type type, IReadOnlyCollection<Attribute> attributes, Func<IInterceptorSubject, object?>? getValue, Action<IInterceptorSubject, object?>? setValue, bool isIntercepted, bool isDynamic)`, `src/Namotion.Interceptor/SubjectPropertyMetadata.cs:78`.

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~WhenSubclassIsHandWritten"`
Expected: it should now PASS, because Task 4 made the helpers protected. If it fails on CS0122 the modifier change did not land.

- [ ] **Step 3: Write the cross assembly test**

Append to `SubjectBaseShapeTests.cs`:

```csharp
    [Fact]
    public void WhenBaseSubjectIsInAReferencedAssembly_ThenTheDerivedSubjectSharesItsPlumbing()
    {
        // Arrange: mode selection branch 2. The library is compiled WITH the generator, so its
        // protected helpers exist as metadata symbols the contract check can see.
        const string librarySource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Library
            {
                [InterceptorSubject]
                public partial class LibraryBase
                {
                    public partial string BaseName { get; set; }
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace App
            {
                [InterceptorSubject]
                public partial class AppLeaf : Library.LibraryBase
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunWithLibraryReference(librarySource, mainSource, runGeneratorOverLibrary: true);
        var generated = result.SingleSource();

        // Assert: derived mode, so no plumbing of its own.
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        Assert.DoesNotContain("private IInterceptorExecutor? _context;", generated);
        Assert.Contains("IInterceptorSubject.Properties => GetInstanceProperties() ?? DefaultProperties;", generated);
    }

    [Fact]
    public void WhenReferencedBaseHasPrivateHelpers_ThenItFallsBackToRootModeWithNI0012()
    {
        // Arrange: an attributed base built by an older generator, so its helpers are private.
        // Branch 1's "declared in source" qualifier is what stops this from selecting derived mode
        // and emitting CS0122 calls into generated code. The generator is NOT run over the library.
        const string librarySource = """
            using System;
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.Collections.Frozen;
            using System.Linq;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Interceptors;

            namespace Library
            {
                [InterceptorSubject]
                public class StaleBase : IInterceptorSubject
                {
                    private IInterceptorExecutor? _context;
                    private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

                    IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                    object IInterceptorSubject.SyncRoot { get; } = new object();
                    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties ?? DefaultProperties;

                    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                        => _properties = (_properties ?? DefaultProperties)
                            .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                            .ToFrozenDictionary();

                    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                        = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                    private TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue) => readValue(this);
                }
            }
            """;

        const string mainSource = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace App
            {
                [InterceptorSubject]
                public partial class AppLeaf : Library.StaleBase
                {
                    public partial string LeafName { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunWithLibraryReference(librarySource, mainSource);

        // Assert: warning, root mode, still compiles, and no stray 'new' that would be CS0109.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NI0012");
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("private IInterceptorExecutor? _context;", result.SingleSource());
    }
```

- [ ] **Step 4: Cover the accessibility and generics shapes**

The contract check runs against the constructed type and uses `IsSymbolAccessibleWithin`, and neither is exercised by the tests above. Append to `SubjectBaseShapeTests.cs`:

```csharp
    [Fact]
    public void WhenHandWrittenBaseIsGeneric_ThenTheContractIsCheckedWithTypeArgumentsSubstituted()
    {
        // Arrange: the subject derives from a constructed GenericBase<string>, so the contract
        // lookup has to see the substituted members, not the open definition's.
        const string source = """
            using System;
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.Collections.Frozen;
            using System.ComponentModel;
            using System.Linq;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;
            using Namotion.Interceptor.Interceptors;

            namespace Repro
            {
                public class GenericBase<T> : IInterceptorSubject, INotifyPropertyChanged, IRaisePropertyChanged
                {
                    private IInterceptorExecutor? _context;
                    private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

                    public event PropertyChangedEventHandler? PropertyChanged;
                    public void RaisePropertyChanged(string propertyName)
                        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

                    public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
                        = FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

                    IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);
                    ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();
                    object IInterceptorSubject.SyncRoot { get; } = new object();
                    IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => GetInstanceProperties() ?? DefaultProperties;

                    void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
                    {
                        lock (((IInterceptorSubject)this).SyncRoot)
                        {
                            _properties = ((IInterceptorSubject)this).Properties
                                .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
                                .ToFrozenDictionary();
                        }
                    }

                    protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;

                    protected TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
                        => _context is not null ? _context.GetPropertyValue(propertyName, readValue)! : readValue(this)!;

                    protected bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> setValue)
                    {
                        if (_context is null)
                        {
                            setValue(this, newValue);
                            return true;
                        }

                        return _context.SetPropertyValue(propertyName, newValue, currentValue, setValue);
                    }

                    protected object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod, params object?[] parameters)
                        => _context is not null ? _context.InvokeMethod(methodName, parameters, invokeMethod) : invokeMethod(this, parameters);
                }

                [InterceptorSubject]
                public partial class GenericDerived : GenericBase<string>
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);

        // Assert: derived mode, so no plumbing of its own.
        Assert.DoesNotContain("private IInterceptorExecutor? _context;", result.SingleSource());
    }

    [Fact]
    public void WhenSubjectsAreInternalAndNested_ThenTheHierarchyStillCompiles()
    {
        // Arrange: accessibility is checked with IsSymbolAccessibleWithin, and nested containing
        // types are re-declared by the generator, so both interact with the derived-mode split.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public partial class Container
                {
                    [InterceptorSubject]
                    internal partial class NestedRoot
                    {
                        public partial string RootName { get; set; }
                    }

                    [InterceptorSubject]
                    private protected partial class NestedLeaf : NestedRoot
                    {
                        public partial string LeafName { get; set; }
                    }
                }
            }
            """;

        // Act & Assert
        GeneratorTestHost.RunExpectingNoWarnings(source);
    }
```

- [ ] **Step 5: Run them**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~SubjectBaseShapeTests"`
Expected: PASS. If the stale-base fixture fails to compile as a library, adjust the fixture until it does; it must be valid C# because `RunWithLibraryReference` asserts the library compiles.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs
git commit -m "Test: cover hand written bases, hand written subclasses and cross assembly hierarchies"
```

---

### Task 8: Upgrade the two tests that look like coverage and are not

**Files:**
- Modify: `src/Namotion.Interceptor.Generator.Tests/GeneratorShapeBehaviorTests.cs:287-330`
- Modify: `src/Namotion.Interceptor.Tests/VirtualPropertyIntegrationTests.cs`

Both currently assert around the bug rather than at it. Left alone they would keep passing while a regression reintroduces #437.

- [ ] **Step 1: Delete the KNOWN GAP comment and upgrade its assertion**

In `GeneratorShapeBehaviorTests.cs`, in `WhenBaseClassIsNamedOnADifferentPartialDeclaration_ThenBaseAndOwnPropertiesAreIntercepted`, delete the entire `KNOWN GAP` paragraph from the Arrange comment (it starts "KNOWN GAP (pre-existing" and ends "which is what actually works today."). Replace it with:

```csharp
        // Both properties are asserted against the interceptors. "FirstName" used to be asserted
        // against value, PropertyChanged and the registry instead, because every subject in a
        // hierarchy emitted its own _context and only the most derived one was ever populated, so
        // base-declared properties took the no-interception fast path (issue #437). The plumbing
        // now lives once in the root, so a base-declared write is observable like any other.
```

Then add, next to the existing `Agency` assertions:

```csharp
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "FirstName" && Equals(write.Value, "Rico"));
        Assert.Contains(readInterceptor.Reads, read => read.PropertyName == "FirstName" && Equals(read.Value, "Rico"));
```

- [ ] **Step 2: Upgrade the three level hierarchy test**

In `src/Namotion.Interceptor.Tests/VirtualPropertyIntegrationTests.cs`, add a test that observes interception on the three level `VirtualPerson` to `VirtualEmployee` to `VirtualManager` chain rather than only asserting values:

```csharp
    [Fact]
    public void WhenWritingThroughAThreeLevelHierarchy_ThenEveryLevelIsIntercepted()
    {
        // Arrange
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        var manager = new VirtualManager(context);

        // Act
        manager.Name = "Rico";
        manager.Department = "Engineering";
        manager.TeamSize = 4;

        // Assert
        Assert.Contains(writeInterceptor.Writes, w => w.PropertyName == "Name");
        Assert.Contains(writeInterceptor.Writes, w => w.PropertyName == "Department");
        Assert.Contains(writeInterceptor.Writes, w => w.PropertyName == "TeamSize");
    }
```

Add a `RecordingWriteInterceptor` private class to that file if one is not already in scope, using the same shape as in Task 4 step 1.

- [ ] **Step 3: Run both suites**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~GeneratorShapeBehaviorTests"`
Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~VirtualPropertyIntegrationTests"`
Expected: PASS both.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Generator.Tests/GeneratorShapeBehaviorTests.cs \
        src/Namotion.Interceptor.Tests/VirtualPropertyIntegrationTests.cs
git commit -m "Test: assert interception on the shapes that previously asserted around it"
```

---

### Task 9: Whole repository regeneration gate

**Files:** none modified. This task produces evidence.

The suite proves the shapes someone thought to ask about. This proves the other 360 subjects are unchanged except in the four expected ways.

- [ ] **Step 1: Generate the baseline from master**

```bash
cd /Users/ricosuter/Projects/GitHub/Namotion.Interceptor
git worktree add /tmp/ni-baseline origin/master
cd /tmp/ni-baseline
dotnet build src/Namotion.Interceptor.slnx -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=/tmp/ni-generated-base
```

- [ ] **Step 2: Generate the branch output**

```bash
cd /Users/ricosuter/Projects/GitHub/Namotion.Interceptor/.claude/worktrees/generator-base-class-interception
dotnet build src/Namotion.Interceptor.slnx -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=/tmp/ni-generated-head
```

- [ ] **Step 3: Diff and classify every change**

```bash
diff -ru /tmp/ni-generated-base /tmp/ni-generated-head > /tmp/ni-generated.diff
grep -c '^[+-]' /tmp/ni-generated.diff
```

Every changed line must fall into exactly one of these categories:

1. `private` becoming `protected` on `GetPropertyValue`, `SetPropertyValue`, `InvokeMethod`.
2. The new `GetInstanceProperties()` member.
3. `_properties ?? DefaultProperties` becoming `GetInstanceProperties() ?? DefaultProperties`.
4. The `AddProperties` operand becoming `((IInterceptorSubject)this).Properties`.
5. In a derived subject, removal of the whole plumbing block and the helpers.
6. A `new` modifier or a `.Concat` target that moved because a base class fact now comes from the subject ancestor rather than the immediate base.

Anything else is a defect. Write the classification into `/tmp/ni-generated-summary.md` with a line count per category.

- [ ] **Step 4: Verify the property sets did not change**

Compare the resolved metadata entries, not only the keys. `.Concat(Base.DefaultProperties)` puts the base last and `ToFrozenDictionary` is last wins, so a changed entry would not show up as a key difference:

```bash
grep -h '^\s*\["' /tmp/ni-generated-base/**/*.g.cs | sort > /tmp/ni-keys-base.txt
grep -h '^\s*\["' /tmp/ni-generated-head/**/*.g.cs | sort > /tmp/ni-keys-head.txt
diff /tmp/ni-keys-base.txt /tmp/ni-keys-head.txt
```

Expected: no output.

- [ ] **Step 5: Clean up the baseline worktree**

```bash
cd /Users/ricosuter/Projects/GitHub/Namotion.Interceptor
git worktree remove --force /tmp/ni-baseline
```

- [ ] **Step 6: Commit the summary**

```bash
cd /Users/ricosuter/Projects/GitHub/Namotion.Interceptor/.claude/worktrees/generator-base-class-interception
mkdir -p docs/superpowers/evidence
cp /tmp/ni-generated-summary.md docs/superpowers/evidence/2026-08-07-regeneration-diff.md
git add docs/superpowers/evidence/2026-08-07-regeneration-diff.md
git commit -m "Docs: record the whole repository regeneration diff for the base class interception fix"
```

---

### Task 10: Benchmarks

**Files:**
- Create: `src/Namotion.Interceptor.Benchmark/SubjectHierarchyBenchmark.cs`

Five rows must be flat, one must improve, and one measures the alternative the spec rejected on reasoning alone.

- [ ] **Step 1: Write the benchmark**

Create `src/Namotion.Interceptor.Benchmark/SubjectHierarchyBenchmark.cs`:

```csharp
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

[InterceptorSubject]
public partial class BenchmarkRoot
{
    public partial string RootValue { get; set; }

    public BenchmarkRoot()
    {
        RootValue = "";
    }
}

[InterceptorSubject]
public partial class BenchmarkMiddle : BenchmarkRoot
{
    public partial string MiddleValue { get; set; }

    public BenchmarkMiddle()
    {
        MiddleValue = "";
    }
}

[InterceptorSubject]
public partial class BenchmarkLeaf : BenchmarkMiddle
{
    public partial string LeafValue { get; set; }

    public BenchmarkLeaf()
    {
        LeafValue = "";
    }
}

[MemoryDiagnoser]
public class SubjectHierarchyBenchmark
{
    private readonly IInterceptorSubjectContext _context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking();

    private BenchmarkRoot _root = null!;
    private BenchmarkLeaf _leaf = null!;

    [GlobalSetup]
    public void Setup()
    {
        _root = new BenchmarkRoot(_context);
        _leaf = new BenchmarkLeaf(_context);
    }

    [Benchmark] public string RootOnlyGet() => _root.RootValue;
    [Benchmark] public void RootOnlySet() => _root.RootValue = "x";
    [Benchmark] public string DerivedDeclaredGet() => _leaf.LeafValue;
    [Benchmark] public void DerivedDeclaredSet() => _leaf.LeafValue = "x";
    [Benchmark] public int PropertiesAccess() => ((IInterceptorSubject)_leaf).Properties.Count;
    [Benchmark] public BenchmarkLeaf ConstructThreeLevel() => new(_context);

    // Not a gate. This row is the one the spec's rejected alternative would change: it is here so
    // the rejection rests on a number rather than on reasoning.
    [Benchmark] public string BaseDeclaredSetThenGet()
    {
        _leaf.RootValue = "x";
        return _leaf.RootValue;
    }
}
```

- [ ] **Step 2: Run it on this branch**

```bash
dotnet run --project src/Namotion.Interceptor.Benchmark -c Release --filter "*SubjectHierarchyBenchmark*"
```

Save the results table to `/tmp/ni-bench-head.md`.

- [ ] **Step 3: Run the same benchmark on master**

```bash
cd /Users/ricosuter/Projects/GitHub/Namotion.Interceptor
git worktree add /tmp/ni-bench-base origin/master
cp .claude/worktrees/generator-base-class-interception/src/Namotion.Interceptor.Benchmark/SubjectHierarchyBenchmark.cs \
   /tmp/ni-bench-base/src/Namotion.Interceptor.Benchmark/
cd /tmp/ni-bench-base
dotnet run --project src/Namotion.Interceptor.Benchmark -c Release --filter "*SubjectHierarchyBenchmark*"
```

Save to `/tmp/ni-bench-base.md`, then `git worktree remove --force /tmp/ni-bench-base`.

- [ ] **Step 4: Compare and record**

`RootOnlyGet`, `RootOnlySet`, `DerivedDeclaredGet`, `DerivedDeclaredSet` and `PropertiesAccess` must be flat within noise. `ConstructThreeLevel` allocated bytes must drop. `BaseDeclaredSetThenGet` will be slower on this branch, and that is the fix working: it was silently skipping the entire interceptor chain. Record all of it, including that explanation, in `docs/superpowers/evidence/2026-08-07-hierarchy-benchmark.md`.

If any of the five flat rows regresses, stop and report it rather than proceeding.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Benchmark/SubjectHierarchyBenchmark.cs \
        docs/superpowers/evidence/2026-08-07-hierarchy-benchmark.md
git commit -m "Benchmark: pin the hierarchy read, write and construction costs"
```

---

### Task 11: Documentation

**Files:**
- Modify: `docs/generator.md`
- Modify: `docs/subject-guidelines.md`
- Modify: `docs/design/generator-supported-shapes.md`

- [ ] **Step 1: Rewrite the misleading sentence**

`docs/generator.md:345` currently reads "The `DefaultProperties` of `Employee` includes properties from both classes. Change notifications from the base class work correctly." That sentence is true and is precisely what made #437 invisible: change notifications working is exactly what masked interception not working. Replace it with:

```markdown
The `DefaultProperties` of `Employee` includes properties from both classes, and properties declared
on `PersonBase` are intercepted like any other: reads and writes go through the interceptor chain, so
change tracking records them and connectors see them. The per instance plumbing is emitted once, in
the class at the root of the hierarchy, and every subject below it inherits that plumbing.

Note that `PropertyChanged` firing is not evidence that a property is intercepted. A subject with no
context still raises it, because the setter calls `RaisePropertyChanged` directly rather than through
the chain. If you are testing whether interception reaches a property, assert on an interceptor.
```

- [ ] **Step 2: Document the four diagnostics**

Add NI0011, NI0012, NI0013 and NI0014 to the diagnostics table in `docs/generator.md`, matching the existing rows' format: ID, severity, what triggers it, how to fix it.

- [ ] **Step 3: Add the hazards and limitations section**

Add a section to `docs/generator.md` covering, each with the reason it is accepted:

- Interface re-implementation means a public member in a derived subject can take an `IInterceptorSubject` slot. NI0014 catches it at compile time.
- The cross assembly rebuild gap: if a base assembly later adds a matching public member, the derived assembly is not recompiled and the hijack happens silently at runtime.
- Interface evolution: any member added to `IInterceptorSubject` has to be considered for the same question.
- Writes in field initializers and in constructors that run before the context is published are not intercepted.

State that the alternative design, a virtual defaults hook, would remove the first two structurally, and that it was rejected because `IInterceptorSubject.Properties` is read on every intercepted write through `PropertyReference.Metadata`, which is deliberately uncached.

- [ ] **Step 4: Document the two contracts**

In `docs/subject-guidelines.md`, add the subject base class contract table, the three behavioural invariants that a symbol check cannot verify, and the subclass side contract including that `AddProperties` must run before the first intercepted write. Give a worked example for each direction.

- [ ] **Step 5: Update the design document**

In `docs/design/generator-supported-shapes.md`, record the root and derived split, why `Properties` stays per class, the rejected virtual hook with its measurement from Task 10, the accepted residual risks, that NI0013 and NI0014 are breaking changes, and the accepted consequence when an in source ancestor's own generation is suppressed.

- [ ] **Step 6: Check the house style**

Run: `grep -n '—' docs/generator.md docs/subject-guidelines.md docs/design/generator-supported-shapes.md`
Expected: no output. Em dashes are not allowed in documentation.

- [ ] **Step 7: Commit**

```bash
git add docs/
git commit -m "Docs: describe base class interception, the base class contract and its limitations"
```

---

## Final verification

- [ ] `dotnet build src/Namotion.Interceptor.slnx` succeeds with zero warnings.
- [ ] `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"` passes.
- [ ] `dotnet test src/Namotion.Interceptor.Generator.Tests` passes with no unaccepted snapshots.
- [ ] The regeneration diff from Task 9 contains only the six expected categories.
- [ ] The five flat benchmark rows are flat and construction allocations dropped.
- [ ] `grep -rn '—' docs/` returns nothing for the files this branch touched.
