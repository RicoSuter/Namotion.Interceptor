# Source generator: explicit interface implementations and diagnostics

Date: 2026-08-06
Issue: [#428](https://github.com/RicoSuter/Namotion.Interceptor/issues/428)

## Summary

The source generator emits code that does not compile when a subject reaches a property through an
explicit interface implementation. Investigating the report surfaced a wider family of inputs where the
generator emits broken code, silently generates nothing, or produces code that compiles and then throws
at runtime.

Three phases, each shippable on its own:

1. **Correctness.** Fix every input that produces non-compiling or crashing output.
2. **Diagnostics.** Introduce diagnostic infrastructure so unsupported input produces a clear message
   instead of broken code or silence.
3. **Documentation.** Update `docs/generator.md` to match the implemented behaviour.

Every claim in this document was reproduced against the current generator or verified empirically. The
case table records the measurement, not an expectation.

## The reported defect

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

emits:

```csharp
{
    "Repro.IHuman.Gender",
    new SubjectPropertyMetadata(
        typeof(global::Repro.IMale).GetProperty(nameof(global::Repro.IMale.Repro.IHuman.Gender), ...)!,
        (o) => ((global::Repro.IMale)o).Repro.IHuman.Gender,
        ...
}
```

failing with `CS0117` and `CS1061`.

### Root cause

For an explicit interface implementation, `IPropertySymbol.Name` is the fully qualified
`"Repro.IHuman.Gender"`, not `"Gender"`. `SubjectMetadataExtractor.ExtractInterfaceDefaultProperties`
stores that value in `PropertyMetadata.Name`, and `SubjectCodeGenerator.EmitDefaultProperties`
interpolates it into four positions: the dictionary key, the `nameof` argument, the getter lambda, and
the setter lambda at `SubjectCodeGenerator.cs:184` when the property has one.

The **interface branch** of the emitter template is already correct and was fed wrong values. The class
branch is not: it emits an unqualified `nameof({property.Name})` at `SubjectCodeGenerator.cs:210`, which
is why case B additionally fails with `CS0103`.

### Why the fix proposed in the issue is not sufficient

The issue suggests `nameof(global::IMale.Gender)` and `((global::IMale)o).Gender`. That compiles, but the
reflection lookup returns null, because reflection on an interface type does not search base interfaces:

```
typeof(IMale).GetProperty("Gender", Public | NonPublic | Instance)   ->  null
typeof(IHuman).GetProperty("Gender", Public | NonPublic | Instance)  ->  ok, public
```

`SubjectPropertyMetadata`'s `PropertyInfo` overload dereferences `propertyInfo.Name`
(`SubjectPropertyMetadata.cs:62-74`), so a null lookup throws inside a static initializer. The cast and
the reflection lookup must both target the **implemented** interface (`IHuman`), not the declaring
interface (`IMale`).

Targeting the implemented interface also makes `PropertyInfo.Name` equal `"Gender"`, matching the
dictionary key, and keeps the member public. Runtime dispatch resolves to the most specific
implementation regardless of which type declared it, verified by case I.

## Investigated cases

"Broken" means the generated code does not compile. "Crashes" means it compiles and throws at runtime.

### Explicit interface implementation family

| Case | Shape | Current behaviour |
|------|-------|-------------------|
| A | Explicit implementation in a sub-interface (the report) | Broken |
| B | Explicit implementation on the subject class | Broken, separate code path, also `CS0103` |
| C | Class declares the property and inherits an explicit implementation | Broken |
| D | Explicit implementation of a nested interface | Broken |
| F | Explicit implementation of a generic interface | Broken, key is `"Repro.IHuman<System.Int32>.Value"` |
| Z | Class declares the property **and** explicitly implements the same member | **Compiles today, crashes** |
| AA | Two explicit implementations of one generic interface at different instantiations | Broken today, would crash after a naive fix |
| AB | Explicit implementation of a `protected` interface member | Broken, and still broken under the naive fix (`CS1540`) |
| AC | `[Derived]` or validation attribute on an explicit implementation | Attribute silently lost |

### Correct today, must not regress

| Case | Shape | Current behaviour |
|------|-------|-------------------|
| G | Reabstraction plus a class property | Correct |
| H | `partial` plus explicit implementation | User error, `CS0754`. Generator adds a spurious `CS9249` |
| I | Base class explicit implementation plus a default **declared directly on the interface** | Correct |
| N | `[InterceptorSubject]` on a `struct` | Compiler reports `CS0592` |
| AD | Base class implements, derived class re-declares the property | Correct, and the two genuinely differ |

### Other broken or silent shapes

| Case | Shape | Current behaviour |
|------|-------|-------------------|
| E | Interface with a default indexer | Broken, emits `((IBag)o).this[]` |
| J | Subject class in the global namespace | Broken, generates into `namespace YourDefaultNamespace` |
| K | `[InterceptorSubject]` on a non-partial class | Generates anyway, `CS0260` |
| L | Non-partial containing type | Generates anyway, `CS0260` |
| M | `[InterceptorSubject]` on a `record` | Silently generates nothing |
| O | Method named exactly `WithoutInterceptor` | Broken, emits `public void ()` |
| P, Q, R | Subject nested in a `record`, `struct` or `interface` | Broken, `CS0261` |
| S | Non-public subject class | Broken, `CS0262` |
| T | Generic subject class | Broken, `CS9248` and `CS9249` |
| U | Generic containing type | Broken, same |
| V | `static` interface property with a body | Broken, `CS0176` |
| W | `private` or `protected` default interface member | Broken, `CS0122` |
| X | `file` interface or `file` subject class | Broken, `CS0234` and `CS9249` |
| Y | `WithoutInterceptor` method that is `static`, generic, an explicit implementation, or has `ref`/`out`/`in` parameters | Broken |
| AE | One generic interface at two instantiations, both with defaults | Compiles, one property silently dropped |
| AF | `new`-shadowed default interface member | Compiles, winner depends on `AllInterfaces` order |

Case I is load bearing and must be read precisely: the shape that compiles today is a base class
explicit implementation plus a default **declared directly on the interface**. It resolves
`((IHuman)o).Name` to the base class implementation rather than the interface default, confirming that
casting to the implemented interface is dispatch correct in every direction. The variant with an explicit
implementation *inside* the interface is broken exactly like A.

### Safety

Most broken cases fail to build today, so fixing them cannot change behaviour of code that currently
compiles. There are two exceptions, and both constrain the design:

**Case Z compiles today and crashes at runtime.** `EmitDefaultProperties` builds a `Dictionary<string, ...>`
collection initializer, so duplicate keys compile and throw on first access:

```csharp
[InterceptorSubject]
public partial class Impl : IFoo
{
    public partial string Kind { get; set; }
    string IFoo.Kind => "explicit";     // Identifier.ValueText is also "Kind"
}
```
```
System.TypeInitializationException
 ---> System.ArgumentException: An item with the same key has already been added. Key: Kind
```

**Case AA would regress from a build error to a crash.** `class C : IFoo<int>, IFoo<string>` with two
explicit implementations fails to build today. Under a naive fix both resolve to `resolvedName = "Kind"`,
which compiles and then throws. Layer 2's compile-clean assertion cannot see this, which is why 1.2 is a
required part of phase 1 rather than a refinement.

**Case M** compiles green today while doing nothing. Making it an error is still correct: the record
never implements `IInterceptorSubject`, so any use of it as a subject already fails to compile, and a
green build means the attribute is dead intent.

## Phase 1: correctness

### 1.1 Explicit interface implementations (A, B, C, D, F)

In `ExtractInterfaceDefaultProperties`, resolve the name and the accessor interface from the symbol:

```csharp
var explicitImplementation = property.ExplicitInterfaceImplementations.FirstOrDefault();
var resolvedName      = explicitImplementation?.Name ?? property.Name;
var accessorInterface = explicitImplementation?.ContainingType ?? interfaceType;
```

`resolvedName` feeds the dictionary key and `PropertyMetadata.Name`. `accessorInterface` feeds
`PropertyMetadata.InterfaceTypeName`. Both come from the symbol rather than from string manipulation of
a qualified name, so case F needs no special handling.

**The guard lookups must change, not only the values stored.** `SubjectMetadataExtractor.cs:234` and
`:239` currently test `classPropertyNames.Contains(property.Name)` and
`processedPropertyNames.Contains(property.Name)`. Both comparisons switch to `resolvedName`, and both
sets are populated with `resolvedName`. Leaving the lookups on `property.Name` while storing
`resolvedName` produces two `"Gender"` keys and converts case C from a build error into a runtime crash.

`ExplicitInterfaceImplementations` is single valued for C# source. It can hold multiple entries for
metadata authored in other languages; `FirstOrDefault` is acceptable, and case AA's diagnostic covers the
ambiguous outcome.

Case B is handled in `CollectProperties`. When `PropertyDeclarationSyntax.ExplicitInterfaceSpecifier` is
present, resolve the interface through the semantic model and emit the property with
`IsFromInterface: true` and that interface as `InterfaceTypeName`, routing it onto the interface template.
`IsPartial` is forced to false, so the metadata carries `isIntercepted: false`. This is accurate: an
explicitly implemented member cannot be routed through the executor, and `CS0754` makes `partial` plus
explicit implementation illegal anyway.

#### Case C is not a policy choice

An earlier draft claimed `john.Gender` and `((IHuman)john).Gender` differ for case C. Measured, they do
not:

```
john.Gender           = Female
((IHuman)john).Gender = Female
```

Because `John`'s interface set includes `IHuman` through `IMale`, the public class property **implicitly
implements** `IHuman.Gender`, and a class implementation always beats a default implementation. "The
class property wins" is therefore the only possible outcome, not a decision, and it needs no diagnostic.

The shape where the two genuinely differ is case AD, base class implements and derived class re-declares:

```
derived.Gender           = Female
((IHuman)derived).Gender = Male
```

That is the shape NI0005 covers.

### 1.2 Duplicate key elimination (Z, AA)

Required, because 1.1 would otherwise introduce case AA as a crash.

`CollectProperties` returns one entry per declaration, so a class declaring both `public partial string Kind`
and `string IFoo.Kind` yields two entries named `Kind`. The `classPropertyNames` guard lives only in
`ExtractInterfaceDefaultProperties` and never applies within class properties.

Two changes:

- **Deduplicate class properties by name before emission**, applying class-wins: a non-explicit
  declaration beats an explicit one. This resolves Z to the tracked property, matching case C.
- **Emit into a dictionary that cannot throw on duplicates.** Replace the collection initializer in
  `EmitDefaultProperties` with indexer assignment, so a residual collision is last-wins rather than a
  `TypeInitializationException`. This is defence in depth; the extractor should already have prevented it.

Collisions the extractor cannot resolve, that is two explicit implementations with no non-explicit
declaration to prefer (case AA), are reported by NI0008 and both entries are dropped.

### 1.3 Global namespace (J)

`GetNamespace` returns the literal `"YourDefaultNamespace"` when a class has no namespace, so the
generated partial lands in a different namespace and never joins the user's class.

`GetNamespace` returns `null` for the global namespace, `SubjectMetadata.NamespaceName` becomes nullable,
and `EmitNamespaceOpening`/`EmitNamespaceClosing` skip the block when it is null.

Indentation is left unchanged. Every emitter hardcodes its indentation, including three raw string
literals (`SubjectCodeGenerator.cs:123-132`, `:137-164`, `:370-397`), and the output compiles at any
indentation. Re-indenting is cosmetic and is explicitly out of scope.

`GetFileName` omits the leading `.` so the hint name is `John.g.cs`.

### 1.4 Subject class accessibility (S)

`SubjectCodeGenerator.cs:106` hardcodes:

```csharp
builder.AppendLine($"    public partial class {metadata.ClassName} : {interfaces}");
```

so an `internal`, `private` or `protected` subject fails with `CS0262`. Capture the declared accessibility
in `SubjectMetadata` and emit it. No subject in this repository is non-public, which is why this went
unnoticed; it is likely the most commonly hit shape in this document.

### 1.5 Skipped members (E, O, V, W, Y)

Members the generator cannot support are skipped rather than emitted broken. Each skip is reported by
NI0006 in phase 2, so nothing is dropped silently.

- **Indexers (E).** Guard `IPropertySymbol.IsIndexer` in `ExtractInterfaceDefaultProperties`. The class
  path needs no guard: an indexer parses as `IndexerDeclarationSyntax`, which the existing
  `OfType<PropertyDeclarationSyntax>()` filter already excludes.
- **Static interface properties (V).** The `hasDefaultImplementation` test at
  `SubjectMetadataExtractor.cs:245-247` passes for a `static` property with a body, because
  `IsAbstract` is false. Add an `IsStatic` guard. `static abstract` is already correctly skipped.
- **Non-public default interface members (W).** Skip when `DeclaredAccessibility` is not `Public`.
- **`WithoutInterceptor` methods (O, Y).** `CollectMethods` at `:184-206` checks only the name suffix.
  Skip when the trimmed name is empty (O), and when the method is `static`, generic, an explicit
  interface implementation, or has a `ref`, `out` or `in` parameter, all of which the emitter drops or
  mangles.

### 1.6 Containing type kinds (P, Q, R)

`EmitContainingTypeOpening` hardcodes `partial class {type}`, so a subject nested in a record, struct or
interface fails with `CS0261`. This is new capability rather than a regression fix, included because the
cost is small.

`SubjectMetadata.ContainingTypes` changes from `string[]` to
`ContainingType(string Keyword, string Name)[]`, and `GetContainingTypes` captures
`TypeDeclarationSyntax.Keyword.ValueText`.

For records the keyword alone is insufficient. `RecordDeclarationSyntax.Keyword` is `record` and
`ClassOrStructKeyword` is `class`, `struct`, or empty. `partial record Outer` is valid for a
`record class` because `record` defaults to a class, but produces `CS0261` for a `record struct`. The rule
is: emit `Keyword`, followed by `ClassOrStructKeyword` when present. Other modifiers
(`static`, `sealed`, `abstract`, `readonly`, `ref`) need not be repeated on the generated partial.

For a plain `class` container this emits `partial class Outer`, byte identical to today, so **the existing
nested class snapshots do not change.**

### 1.7 Out of scope, diagnosed instead

Each of the following is reported by a phase 2 diagnostic and left unsupported.

**Records as subjects (M), NI0003.** Supporting them is achievable but is a feature with its own design
surface, because the generated plumbing breaks record semantics in two ways, both verified:

- Records synthesise `Equals` over all instance fields, including auto property backing fields. Since
  `Data` and `SyncRoot` are initialised with `= new()`, every instance holds distinct references and no
  two record subjects are ever equal, positional ones included.
- The synthesised copy constructor is a shallow field copy, so `with` yields a clone sharing the
  original's `Data` and `SyncRoot`, and copying `_context` verbatim. Since `_context` is created by
  `InterceptorExecutor.GetOrCreate(ref _context, this)` bound to the original instance, writes through
  the clone would drive the original subject. This bites whenever the subject has been used at all,
  because `_context` is non-null from that point on.

Both are fixable, since declaring `Equals(T)`, `GetHashCode()` and `protected R(R other)` suppresses the
synthesised members. The open question is semantic rather than technical: a subject is mutable, reference
identified, graph attached and registry tracked by reference. Value equality over mutable tracked
properties means `GetHashCode` changes when a property changes, and `with` on an attached subject has no
defined answer for whether the clone is attached and to what parent.

**Generic subject and containing types (T, U), NI0009.** `ClassName` drops the type parameter list, so
`Box<T>` generates a separate non-generic `Box`. Supporting them means threading type parameters and
constraints through `SubjectMetadata` and every emitter. Deferred as its own piece of work.

**`file` types (X), NI0010.** The generated file cannot see a `file` type, and a `public partial class`
cannot join a `file partial class`.

**`protected` interface members with explicit implementations (AB), NI0006.** The fixed emission
`((IHuman)o).Secret` fails with `CS1540`, because a protected member can only be accessed through the
derived type. `internal` members and `private` nested interfaces as cast targets both compile fine, so the
gap is specifically non-public *members*. Skipped by the 1.5 accessibility guard.

**Attributes on an explicit implementation (AC), NI0007.** `GetCustomAttributesIncludingInterfacesCore`
(`PropertyInfoExtensions.cs:59-61`) walks `declaringType.GetInterfaces()` and matches by name. Passing
`IHuman.Gender` means `declaringType` is `IHuman`, whose `GetInterfaces()` is empty, so an attribute on
`IMale`'s explicit implementation is never seen. `PropertyMetadata.IsDerived` is assigned at
`SubjectMetadataExtractor.cs:271` but never read by the emitter, so nothing compensates, and a `[Derived]`
or validation attribute on an explicit implementation is silently lost.

Preserving it needs either a new `SubjectPropertyMetadata` overload accepting an explicit name alongside
a `PropertyInfo`, or a union of attributes from the implementation and the implemented member. Both are
more than a fix. NI0007 tells the user to declare the attribute on the interface member, which is where
the contract belongs.

**`new`-shadowed and multi-instantiation defaults (AE, AF), NI0008.** Deduplication is by name, so
`IFoo<int>` and `IFoo<string>` collide and one is dropped. Roslyn happens to yield derived-first for
`new`-shadowed members in both declaration orders, so today's behaviour is right by accident and nothing
pins it. NI0008 reports the collision.

## Phase 2: diagnostics

### 2.1 Prerequisite

The generator sets `EnforceExtendedAnalyzerRules` (`Namotion.Interceptor.Generator.csproj:8`) and
`src/Directory.Build.props:4` sets `TreatWarningsAsErrors`, so the first `DiagnosticDescriptor` fails the
build. Verified in both directions:

```
error RS2008: Enable analyzer release tracking for the analyzer project containing rule 'NI0001'
```

Add `AnalyzerReleases.Shipped.md` and `AnalyzerReleases.Unshipped.md` to the generator project as
`AdditionalFiles`, listing every rule in the unshipped file.

### 2.2 Plumbing

`SubjectMetadataExtractor.Extract` becomes a pure function returning:

```csharp
internal sealed record ExtractionResult(
    SubjectMetadata? Metadata,
    IReadOnlyList<Diagnostic> Diagnostics);
```

`RegisterSourceOutput` reports the diagnostics and calls `AddSource` only when `Metadata` is non-null.

Widening the syntax provider from `ClassDeclarationSyntax` to `TypeDeclarationSyntax`, so records reach
NI0003 instead of being silently skipped, also requires changes at `InterceptorSubjectGenerator.cs:23`
(the cast), `SubjectMetadataExtractor.Extract`'s parameter type (`:20`), and three
`OfType<ClassDeclarationSyntax>()` filters (`:48`, `:117`, `:176`). Generation itself stays restricted to
classes.

### 2.3 Rules

| ID | Condition | Severity |
|----|-----------|----------|
| NI0001 | Subject type is not `partial` (K) | Error |
| NI0002 | Containing type is not `partial` (L) | Error |
| NI0003 | `[InterceptorSubject]` on an unsupported type kind (M) | Error |
| NI0004 | The generator threw while generating a subject | Error |
| NI0005 | A derived class re-declares a property already implemented by its base class (AD) | Info |
| NI0006 | A member was skipped as unsupported (E, O, V, W, Y, AB) | Warning |
| NI0007 | Attributes on an explicit interface implementation are ignored (AC) | Warning |
| NI0008 | Two interface members collide on one property name (AA, AE, AF) | Warning |
| NI0009 | Generic subject or containing type is not supported (T, U) | Error |
| NI0010 | `file` types are not supported (X) | Error |

Category for all rules: `Namotion.Interceptor`.

NI0003 fires for any non-class type declaration carrying the attribute. For `struct`, `interface` and
`record struct` the compiler additionally reports `CS0592`, since `InterceptorSubjectAttribute` is
declared class only. The duplication is accepted; the record case is the one that is silent today.

NI0004 replaces the handler at `InterceptorSubjectGenerator.cs:89-95`, which catches every exception and
emits a source file containing `/* {ex} */`. **The file with the full `ex.ToString()` is still emitted, in
addition to the diagnostic.** Diagnostic messages render as effectively a single line in most surfaces, so
NI0004 carries a one line summary with the exception type, the message, and the subject type, while the
file retains the frames. When the generator throws, the partial class cannot be completed, so consequent
`CS9248` style errors appear either way; NI0004 puts the real reason at the top.

**NI0005 is `Info`, not `Warning`, deliberately.** Case AD is a legal and supported shape, and it must
appear as real source in the layer 3 test project. Since `src/Directory.Build.props:4` sets
`TreatWarningsAsErrors`, a warning there would fail the test assembly's build before any test ran.
`Info` conveys the same information without that consequence.

## Phase 3: documentation

Update `docs/generator.md`:

- Remove the limitations row `Explicit interface implementation not supported | Use implicit implementation`,
  which phase 1 retires.
- Extend **Interface Default Properties** with an explicit implementation example, noting that the
  property is keyed by its member name, is not intercepted, and that attributes belong on the interface
  member (NI0007).
- Extend **Nested Classes** to cover containing records, structs and interfaces.
- Note that subjects in the global namespace and non-public subjects are supported.
- Add a **Diagnostics** section listing NI0001 to NI0010 with cause and fix.
- Add records, generic subjects and `file` types to the limitations table, pointing at NI0003, NI0009 and
  NI0010.
- Update **Troubleshooting** so "Compilation errors in generated code" points at the diagnostics.

## Testing

The reason #428 shipped is that the tests assert on generated **text**. `Assert.Contains(@"""Status""")`
passes happily on code that cannot compile.

There is no shared helper today. Three private copies of `GenerateCode` exist, at
`SourceGeneratorTests.cs:222`, `InterfaceDefaultPropertyTests.cs:202` and `VirtualPartialTests.cs:163`.
Only the second uses `out _, out _`; the other two capture the out parameters and ignore them.
**Consolidating them into one helper is the first step**, not an implied side effect.

### Layer 1: snapshots

Verify snapshots of the full generated source for every case. Phase 2 extends this to snapshot the
reported diagnostics, that is ID, severity, location and message, alongside the source, so a severity or
wording change shows as a diff. Verify.Xunit is already referenced.

### Layer 2: compile clean assertion

The consolidated helper asserts that `outputCompilation.GetDiagnostics()` contains no errors. This catches
A, B, C, D, E, F, J, K, L, O, P, Q, R, S, T, U, V, W, X and Y.

It requires fixing the harness references first: the current compilation is missing `System.Text.Json`, so
generated code referencing `JsonIgnore` produces unrelated errors and there is no clean baseline.

The assertion applies only to cases whose **input** is valid C#. Cases H and N are invalid by
construction, `CS0754` and `CS0592`, so the compilation can never be clean. They are asserted as: the
expected compiler error is present, the generator does not throw, and no *additional* generator-caused
error appears beyond it.

Case H needs care. It remains a `partial` class carrying a supported attribute, and only its property is
invalid, so phase 1.1 forces that property to `IsPartial: false` and still returns metadata. None of
NI0001 to NI0003 suppresses generation, so source **is** emitted. The assertion must allow that rather
than requiring no output.

Cases Z and AA cannot be caught by this layer at all, since both compile. They need layer 3.

### Layer 3: real subjects

Declare the models directly in the test project so the real generator compiles them during the build, then
assert behaviour through the registry:

- A: the `Gender` key is present and resolves to `Male`.
- B: an explicit implementation on the class is present and reads correctly.
- C: the tracked class property is exposed.
- I: the base class implementation still wins over the interface default.
- P: a subject nested in a record compiles and works.
- S: an `internal` subject compiles and works.
- **Z: `DefaultProperties` is accessed and does not throw, with exactly one `Kind` key.**
- **AA: two explicit implementations at different instantiations are reported by NI0008.**
- AD: the base and derived values differ as measured, and NI0005 is `Info` so the build survives.

A regression here does not produce a failing test, it produces a failing build, because the test project
cannot compile against a broken generator.

The negative cases (K, L, M, T, U, X, and the skipped members) cannot live here, since they would break
the test project's own build. They are covered by layers 1 and 2 only.

### Snapshot layout

Thirteen `.verified.txt` files sit in the test project root and three sit in `Snapshots/`, as only
`VirtualPartialTests` calls `UseDirectory("Snapshots")`. Consolidate all into `Snapshots/` and add
`UseDirectory("Snapshots")` to the two classes lacking it. A file move with no content change, as its own
commit.

Use `DiffEngine_Disabled=true` when accepting snapshots.

### Test conventions

Per `AGENTS.md`: `When<Condition>_Then<ExpectedBehavior>` naming, explicit `// Arrange`, `// Act`,
`// Assert` comments, no hardcoded waits.

## Commit sequence

Each step is release safe on its own and adds no public API whose callers land later.

1. Test harness: consolidate the three `GenerateCode` copies, add missing references, assert compile
   clean, consolidate snapshots.
2. Phase 1.2: duplicate key elimination. **Before 1.1**, since 1.1 would otherwise introduce case AA as a
   runtime crash.
3. Phase 1.1: explicit interface implementations.
4. Phase 1.3: global namespace.
5. Phase 1.4: subject class accessibility.
6. Phase 1.5: skipped members.
7. Phase 1.6: containing type kinds.
8. Phase 2.1 and 2.2: analyzer release tracking and `ExtractionResult` plumbing.
9. Phase 2.3: the ten rules.
10. Phase 3: documentation.

Step 1 comes first so every later step is verifiable, and on its own it turns the existing broken cases
into visible failures. Step 2 precedes step 3 because the ordering is a correctness requirement, not a
preference.

## Open questions

None.
