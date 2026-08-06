# Source generator: explicit interface implementations and diagnostics

Date: 2026-08-06
Issue: [#428](https://github.com/RicoSuter/Namotion.Interceptor/issues/428)

## Summary

The source generator emits code that does not compile when a subject reaches a property through an
explicit interface implementation. Investigating the report surfaced a wider family of inputs where
the generator emits broken code or silently does nothing.

The work is split into three phases, each shippable on its own:

1. **Correctness.** Fix every input that produces non-compiling output.
2. **Diagnostics.** Introduce diagnostic infrastructure so unsupported input produces a clear message
   instead of broken code or silence.
3. **Documentation.** Update `docs/generator.md` to match the implemented behaviour.

## The reported defect

Given this model:

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
}
```

the generator emits:

```csharp
{
    "Repro.IHuman.Gender",
    new SubjectPropertyMetadata(
        typeof(global::Repro.IMale).GetProperty(nameof(global::Repro.IMale.Repro.IHuman.Gender), ...)!,
        (o) => ((global::Repro.IMale)o).Repro.IHuman.Gender,
        ...
}
```

which fails with `CS0117` and `CS1061`.

### Root cause

For an explicit interface implementation, `IPropertySymbol.Name` is the fully qualified
`"Repro.IHuman.Gender"`, not `"Gender"`. `SubjectMetadataExtractor.ExtractInterfaceDefaultProperties`
stores that value verbatim in `PropertyMetadata.Name`, and `SubjectCodeGenerator.EmitDefaultProperties`
interpolates it into three positions: the dictionary key, the `nameof` argument, and the accessor
lambda.

The emitter template is already correct. It was fed wrong values.

### Why the fix proposed in the issue is not sufficient

The issue suggests emitting `nameof(global::IMale.Gender)` and `((global::IMale)o).Gender`. That
compiles, but the reflection lookup returns null. Reflection on an interface type does not search base
interfaces:

```
typeof(IMale).GetProperty("Gender", Public | NonPublic | Instance)   ->  null
typeof(IHuman).GetProperty("Gender", Public | NonPublic | Instance)  ->  ok, public
```

`SubjectPropertyMetadata`'s `PropertyInfo` constructor overload dereferences `propertyInfo.Name`, so a
null lookup throws inside a static initializer. The cast and the reflection lookup must both target the
**implemented** interface (`IHuman`), not the declaring interface (`IMale`).

Targeting the implemented interface is also better on three counts. `PropertyInfo.Name` becomes
`"Gender"`, matching the dictionary key. The property is public, so `SubjectPropertyMetadata.IsPublic`
is correct. And runtime dispatch resolves to the most specific implementation regardless of which type
declared it, which is verified by case I below.

## Investigated cases

Each case was reproduced against the current generator. "Broken" means the generated code does not
compile.

| Case | Input shape | Current behaviour |
|------|-------------|-------------------|
| A | Explicit implementation in a sub-interface (the report) | Broken |
| B | Explicit implementation on the `[InterceptorSubject]` class | Broken, separate code path |
| C | Class declares the property and inherits an explicit implementation | Broken, and the deduplication misses it |
| D | Explicit implementation of a nested interface | Broken |
| E | Interface with a default indexer | Broken, emits `((IBag)o).this[]` |
| F | Explicit implementation of a generic interface | Broken, key is `"Repro.IHuman<System.Int32>.Value"` |
| G | Reabstraction plus a class property | Correct, no change needed |
| H | `partial` plus explicit implementation | Rejected by the compiler (`CS0754`), not our concern |
| I | Base class explicit implementation plus an interface default | Correct, no change needed |
| J | Subject class in the global namespace | Broken, generates into `namespace YourDefaultNamespace` |
| K | `[InterceptorSubject]` on a non-partial class | Generates anyway, `CS0260` |
| L | Non-partial containing type | Generates anyway, `CS0260` |
| M | `[InterceptorSubject]` on a `record` | Silently generates nothing |
| N | `[InterceptorSubject]` on a `struct` | Compiler reports `CS0592`, no change needed |
| O | Method named exactly `WithoutInterceptor` | Broken, emits `public void ()` |
| P | Subject nested in a `record` | Broken, emits `partial class Outer`, `CS0261` |
| Q | Subject nested in a `struct` | Broken, `CS0261` |
| R | Subject nested in an `interface` | Broken, emits `partial class IOuter`, `CS0261` |

Case I is load bearing. It compiles today and resolves `((IHuman)o).Name` to the base class
implementation rather than the interface default, which confirms that casting to the implemented
interface is dispatch correct in every direction. The fix must leave this case untouched.

### Safety

Every case marked broken fails to build today. A dotted property name is only ever produced by an
explicit interface implementation, and all such shapes hard error at build time. No code that currently
compiles can change behaviour as a result of phase 1.

The single exception is case M. A record carrying `[InterceptorSubject]` with no partial properties
compiles green today while doing nothing. Making it an error is nonetheless correct: because the record
never implements `IInterceptorSubject`, any use of it as a subject already fails to compile, so a green
build means the attribute is dead intent. See NI0003.

## Phase 1: correctness

### 1.1 Explicit interface implementations (A, B, C, D, F)

In `SubjectMetadataExtractor.ExtractInterfaceDefaultProperties`, resolve the name and the accessor
interface from the symbol rather than reading `property.Name` directly:

```csharp
var explicitImplementation = property.ExplicitInterfaceImplementations.FirstOrDefault();
var resolvedName      = explicitImplementation?.Name ?? property.Name;
var accessorInterface = explicitImplementation?.ContainingType ?? interfaceType;
```

`resolvedName` feeds the dictionary key, `PropertyMetadata.Name`, and both deduplication sets.
`accessorInterface` feeds `PropertyMetadata.InterfaceTypeName`.

Because both values come from the symbol rather than from string manipulation of a qualified name,
case F (generic interfaces) is handled without special casing.

`SubjectCodeGenerator` needs no change for A, D and F. Its template already reads:

```csharp
typeof({InterfaceTypeName}).GetProperty(nameof({InterfaceTypeName}.{Name}), ...)!,
(o) => (({InterfaceTypeName})o).{Name}
```

Case C requires no separate work. Once `resolvedName` is `"Gender"`, the existing `classPropertyNames`
guard matches and the class property wins.

**Decision: the class property wins over an inherited explicit implementation, silently in phase 1 and
with a warning from phase 2 (NI0005).** The two genuinely differ at runtime: `john.Gender` reads the
tracked property while `((IHuman)john).Gender` returns the interface constant. The class property is the
intercepted one, it is what a caller holding a `John` observes, and it matches the existing guard's
intent.

Case B is handled in `SubjectMetadataExtractor.CollectProperties`. When
`PropertyDeclarationSyntax.ExplicitInterfaceSpecifier` is present, resolve the interface through the
semantic model and emit the property with `IsFromInterface: true` and that interface as
`InterfaceTypeName`, routing it onto the same template. `IsPartial` is forced to false, so the emitted
metadata carries `isIntercepted: false`. This is accurate: an explicitly implemented member cannot be
routed through the executor. `CS0754` makes `partial` plus explicit implementation illegal, so nothing
is lost by forcing the flag.

### 1.2 Global namespace (J)

`SubjectMetadataExtractor.GetNamespace` returns the literal `"YourDefaultNamespace"` when a class has no
namespace. The generated partial then lands in a different namespace than the user's class, so the two
halves never join and the user sees `CS9248` and `CS9249`.

Change `GetNamespace` to return `null` for the global namespace, make `SubjectMetadata.NamespaceName`
nullable, and:

- `SubjectCodeGenerator.EmitNamespaceOpening` and `EmitNamespaceClosing` skip the block when null.
- Class and containing type bodies are emitted without the extra indentation level.
- `GetFileName` omits the leading `.` so the hint name is `John.g.cs`, not `.John.g.cs`.

### 1.3 Interface indexers (E)

Skip `IPropertySymbol.IsIndexer` in `ExtractInterfaceDefaultProperties`. An indexer has no usable name
and is parameterised, so it cannot be a subject property.

The class path needs no guard: an indexer parses as `IndexerDeclarationSyntax`, which the existing
`OfType<PropertyDeclarationSyntax>()` filter already excludes.

### 1.4 Bare `WithoutInterceptor` method (O)

In `CollectMethods`, skip any method whose name is exactly `WithoutInterceptor`, which currently yields
an empty method name and emits `public void ()`.

### 1.5 Containing type kinds (P, Q, R)

`SubjectCodeGenerator.EmitContainingTypeOpening` hardcodes `partial class {type}`, so a subject nested
in a record, struct or interface emits a mismatched keyword and fails with `CS0261`.

This is new capability rather than a regression fix, since the affected shapes cannot compile today.
It is included because the cost is small and nesting a subject inside a record is plausible.

`SubjectMetadata.ContainingTypes` changes from `string[]` to `ContainingType(string Keyword, string Name)[]`.
`GetContainingTypes` captures `TypeDeclarationSyntax.Keyword.ValueText`.

For records the keyword alone is not always sufficient. `RecordDeclarationSyntax.Keyword` is `record`
and `ClassOrStructKeyword` is `class`, `struct`, or empty. Emitting `partial record Outer` for a
`record class` is valid because `record` defaults to a class, but for a `record struct` it produces
`CS0261` again. The rule is therefore: emit `Keyword`, followed by `ClassOrStructKeyword` when present.

This re-baselines the two existing nested class snapshots. Their content changes only in that the
containing type keyword becomes explicit.

### 1.6 Out of scope

**Records as subjects (M).** Deferred, diagnosed by NI0003. Supporting them is achievable but is a
feature with its own design surface, because the generated plumbing breaks record semantics in two ways
that were verified by hand:

- Records synthesise `Equals` over all instance fields, including auto property backing fields. Since
  `Data` and `SyncRoot` are initialised with `= new()`, every instance holds distinct references and no
  two record subjects are ever equal, including positional ones with identical data. The defining
  feature of a record stops working.
- The synthesised copy constructor is a shallow field copy, so `with` produces a clone that shares the
  original's `Data` dictionary and `SyncRoot`. `_context` is copied the same way, and it is created by
  `InterceptorExecutor.GetOrCreate(ref _context, this)` bound to the original instance, so writes
  through the clone would drive the original subject.

Both are fixable, because declaring `Equals(T)`, `GetHashCode()` and `protected R(R other)` suppresses
the synthesised members. The open question is not technical but semantic: a subject is mutable,
reference identified, graph attached and tracked by the registry by reference. Value equality over
mutable tracked properties means `GetHashCode` changes when a property changes, and `with` on an
attached subject has no defined answer for whether the clone is attached and to what parent. That
deserves its own spec.

## Phase 2: diagnostics

### 2.1 Prerequisite

The generator project sets `EnforceExtendedAnalyzerRules`, and `src/Directory.Build.props` sets
`TreatWarningsAsErrors`. The first `DiagnosticDescriptor` therefore fails the build with `RS2008`. This
was verified, along with the fix:

```
error RS2008: Enable analyzer release tracking for the analyzer project containing rule 'NI0001'
```

Add `AnalyzerReleases.Shipped.md` and `AnalyzerReleases.Unshipped.md` to
`src/Namotion.Interceptor.Generator/`, registered as `AdditionalFiles` in the project file, with every
rule listed in the unshipped file. Adding them makes the build succeed.

### 2.2 Plumbing

`SubjectMetadataExtractor.Extract` becomes a pure function returning:

```csharp
internal sealed record ExtractionResult(
    SubjectMetadata? Metadata,
    IReadOnlyList<Diagnostic> Diagnostics);
```

`InterceptorSubjectGenerator.RegisterSourceOutput` reports the diagnostics and calls `AddSource` only
when `Metadata` is non-null, so a diagnosed error yields one clear message rather than a cascade of
`CS0260` or `CS9248`.

Type kind checks (K, M) run in the syntax provider, which currently matches only
`ClassDeclarationSyntax`. It widens to `TypeDeclarationSyntax` so records reach the diagnostic instead of
being silently skipped, while generation itself stays restricted to classes.

NI0003 therefore fires for any non-class type declaration carrying the attribute. For a `struct` the
compiler additionally reports `CS0592`, since `InterceptorSubjectAttribute` is declared class only. The
resulting duplication is accepted: the record case is the one that is silent today, and suppressing
NI0003 for structs would add a special case to guard against a message the user benefits from anyway.

### 2.3 Rules

| ID | Condition | Severity | Replaces |
|----|-----------|----------|----------|
| NI0001 | Subject type is not `partial` (K) | Error | `CS0260` |
| NI0002 | Containing type is not `partial` (L) | Error | `CS0260` |
| NI0003 | `[InterceptorSubject]` on an unsupported type kind, that is a record (M) | Error | silence |
| NI0004 | The generator threw while generating a subject | Error | a `/* {ex} */` comment |
| NI0005 | A class property shadows an inherited explicit interface implementation (C) | Warning | silence |
| NI0006 | A member was skipped as unsupported, that is an indexer or a bare `WithoutInterceptor` method (E, O) | Warning | broken code |

Category for all rules: `Namotion.Interceptor`.

NI0004 replaces the current handler at `InterceptorSubjectGenerator.cs:89-95`, which catches every
exception and emits a source file containing `/* {ex} */`. The defect was never that the stack trace was
lost but that it was recorded only in a comment nobody reads.

**The generated file with the full `ex.ToString()` is still emitted, in addition to the diagnostic.**
Diagnostic messages render as effectively a single line in most surfaces, so NI0004 carries a one line
summary containing the exception type, the message, and the subject type being generated, while the file
retains the full frames for deeper investigation. Note that when the generator throws, the partial class
cannot be completed, so the user still sees consequent `CS9248` style errors either way. NI0004 puts the
real reason at the top of the list.

## Phase 3: documentation

Update `docs/generator.md`:

- Remove the limitations row `Explicit interface implementation not supported | Use implicit implementation`.
  Phase 1 retires it.
- Extend **Interface Default Properties** with an explicit interface implementation example, stating that
  the property is keyed by its member name, that it is not intercepted, and that a class declaration of
  the same property wins.
- Extend **Nested Classes** to cover containing records, structs and interfaces.
- Note that subjects in the global namespace are supported.
- Add a **Diagnostics** section listing NI0001 to NI0006 with the cause and the fix for each.
- Add records to the limitations table, pointing at NI0003 and noting the equality and `with` reasons.
- Update **Troubleshooting** so "Compilation errors in generated code" points at the diagnostics.

## Testing

The reason #428 shipped is that the test helper discards both compilation out parameters
(`out _, out _`) and the tests assert on generated **text**. `Assert.Contains(@"""Status""")` passes
happily on code that cannot compile.

Three layers, each catching what the others cannot.

### Layer 1: snapshots

Verify snapshots of the full generated source for every case A to R. Phase 2 extends this to snapshot
the reported diagnostics, that is ID, severity, location and message, alongside the source, so a
severity or wording change shows as a diff.

Verify.Xunit is already referenced by the test project.

### Layer 2: compile clean assertion

The shared `GenerateCode` helper asserts that `outputCompilation.GetDiagnostics()` contains no errors.
This alone catches A, B, C, D, E, F, J, O, P, Q and R.

This requires fixing the harness references first. The current compilation is missing `System.Text.Json`,
so generated code referencing `JsonIgnore` produces unrelated errors and there is no clean baseline to
assert against.

The assertion applies only to cases whose **input** is valid C#. Cases H and N are invalid by
construction, `CS0754` and `CS0592` respectively, so the compilation can never be clean regardless of
what the generator does. Those two are asserted differently: the generator must not throw, and must emit
nothing.

### Layer 3: real subjects

Declare the models directly in the test project, as `InterfaceDefaultPropertyBehaviorTests` already does,
so the real generator compiles them during the build, then assert behaviour through the registry:

- Case A: the `Gender` key is present and resolves to `Male`.
- Case B: an explicit implementation on the class is present and reads correctly.
- Case C: the tracked class property is exposed, not the interface constant.
- Case I: the base class implementation still wins over the interface default.
- Case P: a subject nested in a record compiles and works.

This layer has a property worth stating: a regression does not produce a failing test, it produces a
failing build, because the test project cannot compile against a broken generator.

The negative cases (K, L, M, and the skipped members in E and O) cannot live here, since they would break
the test project's own build. They are covered by layers 1 and 2 only.

### Snapshot layout

Thirteen `.verified.txt` files currently sit in the test project root and three sit in `Snapshots/`, as
only `VirtualPartialTests` calls `UseDirectory("Snapshots")`. Since this work roughly doubles the count,
consolidate all snapshots into `Snapshots/` and add `UseDirectory("Snapshots")` to the two classes that
lack it. This is a file move with no content change and should be its own commit.

Use `DiffEngine_Disabled=true` when accepting snapshots.

### Test conventions

Follow `AGENTS.md`: `When<Condition>_Then<ExpectedBehavior>` naming, explicit `// Arrange`, `// Act`,
`// Assert` comments, and no hardcoded waits.

## Commit sequence

Each phase is release safe on its own, and no phase adds public API whose callers land later.

1. Test harness: add missing references, assert compile clean, consolidate snapshots.
2. Phase 1.1: explicit interface implementations.
3. Phase 1.2: global namespace.
4. Phase 1.3 and 1.4: skip indexers and the bare `WithoutInterceptor` method.
5. Phase 1.5: containing type kinds.
6. Phase 2.1 and 2.2: analyzer release tracking and `ExtractionResult` plumbing.
7. Phase 2.3: the six rules.
8. Phase 3: documentation.

Step 1 comes first deliberately. It makes every later step verifiable, and on its own it turns the
existing broken cases into visible failures.

## Open questions

None.
