# Source Generator: Supported Shapes

This document explains why the source generator rejects, accepts, or works around specific C# shapes
that it does not treat as obvious. For user-facing documentation, including the diagnostics table and
examples, see [Source Generator](../generator.md). This document is for maintainers of
`Namotion.Interceptor.Generator`.

## Overview

The generator turns a partial class carrying `[InterceptorSubject]` into interception plumbing. Most
of that mapping is mechanical and self-explanatory from the code. A handful of decisions are not: they
were reached by measuring the compiler's actual behaviour, not by reasoning about the language spec in
the abstract, and in one case by getting it wrong three times first. This document preserves that
reasoning so it is not rediscovered by trial and error the next time one of these areas changes.

## Why records are not supported as subjects

`[InterceptorSubject]` on a `record` or `record struct` is a compile error (NI0003). This looks like an
arbitrary restriction until you look at what the generator actually emits into a record.

Every subject gets an auto-property backing field for `IInterceptorSubject.Data` and
`IInterceptorSubject.SyncRoot`, both initialised with `= new()`, plus a lazily created `_context` field
assigned through `InterceptorExecutor.GetOrCreate(ref _context, this)`. Two measured consequences follow
if the containing type is a record:

- **Records synthesise `Equals` over every instance field, including auto-property backing fields.**
  Since `Data` and `SyncRoot` are each initialised with `= new()`, every instance holds distinct
  references for both. No two record subjects are ever equal, not even two positional records
  constructed from identical arguments, because the synthesised equality check always finds these two
  fields unequal.
- **The synthesised copy constructor is a shallow field copy.** `with` produces a clone that shares the
  original's `Data` and `SyncRoot` references and copies `_context` verbatim. Because `_context` is
  bound to the original instance the first time it was created, writes through the clone drive the
  original subject once `_context` is non-null, which is any subject that has been used at all.

Both are fixable by declaring `Equals(T)`, `GetHashCode()`, and a copy constructor, which suppress the
compiler-synthesised versions. So the blocker is not the mechanics, it is what the fixed versions would
mean: a subject is mutable, reference-identified, and tracked by the registry by reference. Value
equality over mutable tracked properties means `GetHashCode` changes whenever a property changes, which
breaks any hash-based collection holding the subject. Supporting records is a feature with its own
design surface (what does `with` mean for an attached subject, is it attached to the same parent), not
a bug fix, which is why NI0003 treats it as a hard error rather than working around it.

## Why the accessibility rule is a symbol query, not a list

The generator emits code that reaches an interface default property or a class-declared explicit
implementation by casting the subject to the declaring interface, for example
`((IHuman)o).Gender`. Whether that cast-and-read compiles depends on whether the member is accessible
from the generated code, which lives in the subject's own assembly, not the interface's. Getting this
guard right took four attempts, and each one broke in a different direction. The sequence is worth
knowing before touching `GetAccessorAccessibility` again.

1. **A hardcoded `Private`/`Protected`/`ProtectedAndInternal` check against the property's own
   accessibility, unconditionally.** This looked reasonable in isolation, but Roslyn reports
   `IPropertySymbol.DeclaredAccessibility` as `Private` for every explicit interface implementation,
   regardless of the implemented member's real visibility, because the CLR requires explicit
   implementations to be emitted private. The naive check therefore skipped every explicit
   implementation, which would have silently reverted the fix for issue #428 (the whole point of this
   branch). Caught by the existing explicit-implementation tests failing before the change was ever
   committed.
2. **Exempting explicit implementations from the check entirely** (commit `d128ad6f`). This fixed the
   regression above but overcorrected: an explicit implementation of a `protected` interface member was
   now kept and emitted unconditionally, producing generated code that fails with CS1540, since a
   protected member is only reachable through a type that derives from its declaring type, which
   generated code never does.
3. **Checking the accessibility of the implemented member instead of the implementation** (commit
   `6fd3f9ff`), still with the same hardcoded `Private`/`Protected`/`ProtectedAndInternal` list. This
   fixed the protected case but the hand-rolled list only understands same-assembly accessibility. It
   missed a cross-assembly `internal` member (CS0122, since `internal` is only accessible with a
   matching `InternalsVisibleTo`) and accessor-level accessibility, such as `string Probe { get; private
   set; }`, where the setter alone is unreachable (CS0272).
4. **`Compilation.IsSymbolAccessibleWithin(member, subjectType, throughType: accessorInterface)`, plus a
   separate check per accessor** (commit `7334ee06`). This is the version that ships. Passing
   `throughType` is load-bearing: without it, `IsSymbolAccessibleWithin` answers "is this member
   reachable from the subject type at all", which for a protected member is true through inheritance,
   and the protected case regresses back to CS1540. With `throughType` it answers the question the
   generated code actually asks: "is this member reachable through a cast to this specific interface".
   The per-accessor pair (`GetAccessorAccessibility` in `SubjectMetadataExtractor.cs`) exists because a
   getter and setter can have independently weaker accessibility than the property itself; the emitter
   drops just the inaccessible half rather than the whole member.

   Because the check goes through the compiler's own symbol model, it also correctly honours
   `InternalsVisibleTo` between assemblies, which no hardcoded accessibility list could express. This
   is pinned by
   `WhenInterfaceDefaultMemberIsInternalInReferencedAssemblyWithInternalsVisibleTo_ThenMemberIsKept` in
   `GeneratorShapeTests.cs`.
5. **The identical defect existed, unfixed, on the sibling code path** (commit `2f55079f`).
   `ExtractInterfaceDefaultProperties` (interface default properties) got steps 1 to 4 above, but
   `CollectProperties` (class-declared explicit implementations, `string IFoo.Kind => ...` written
   directly on the subject class) had no accessibility guard at all, and produced the same CS1540 for a
   class explicitly implementing a protected interface member. The fix extracted
   `GetAccessorAccessibility` as a shared helper so both paths run the same check instead of carrying
   two copies that can drift independently, which is exactly how this took four tries in the first
   place: the second bug fix did not touch the code path that needed it.

The lesson generalises beyond this one guard: an accessibility rule that does not go through
`IsSymbolAccessibleWithin` (or an equivalent compiler query) is testing a proxy for the real question,
and every proxy tried here was wrong in a different assembly-boundary or accessor-level way.

## Why the cast targets the implemented interface, not the declaring one

For `interface IMale : IHuman { Gender IHuman.Gender => Gender.Male; }`, the generator reaches the
property through `IHuman`, the interface that declares the member being implemented, not `IMale`, the
interface the explicit implementation syntax appears on. This is not a stylistic choice.

`IPropertySymbol.Name` for an explicit implementation is the fully qualified `"Namespace.IHuman.Gender"`,
not the simple name `"Gender"`. The name and the cast target both come from
`property.ExplicitInterfaceImplementations.FirstOrDefault()`, which is the implemented member's own
symbol, so both resolve correctly from the symbol rather than by string-splitting the qualified name.

Casting to `IMale` instead, which is what a naive fix based on the qualified name suggests, compiles but
breaks at a different layer. Reflection on an interface type does not search base interfaces:

```
typeof(IMale).GetProperty("Gender", Public | NonPublic | Instance)   ->  null
typeof(IHuman).GetProperty("Gender", Public | NonPublic | Instance)  ->  ok, public
```

`SubjectPropertyMetadata`'s `PropertyInfo` overload dereferences `propertyInfo.Name` inside a static
initializer, so a null lookup throws at type-load time, not at the call site, which makes it a
particularly unpleasant failure to diagnose from a stack trace alone.

Casting to the implemented interface is also dispatch-correct, not merely reflection-safe. When a base
class provides an implicit implementation and an interface separately declares a default for the same
member, the base class implementation wins at runtime regardless of which interface reference is used to
call through, because a class implementation always beats a default interface implementation. The
generator relies on this: it never needs to special-case "does a class implementation shadow this
default", because casting to the implemented interface and letting normal dispatch resolve the call
produces the right answer either way.

## Known gaps

These are not fixed by this work and are not tracked by a diagnostic. They were found incidentally while
implementing the shapes above, are out of scope for issue #428, and should become their own issues if
they turn out to matter in practice.

- **A `*WithoutInterceptor` method whose stripped name collides with an existing method on the class**
  emits a duplicate member and fails with CS0111. For example, declaring both `void Probe()` and `void
  ProbeWithoutInterceptor()` on the same subject: the generator strips the suffix and emits a second
  `void Probe()` wrapper, which the compiler rejects as a duplicate. The generator does not currently
  check for this collision before emitting the wrapper.
- **The emitter cannot repeat a `new` modifier on the generated half of a partial property.**
  `public new partial string Origin { get; set; }` fails with CS8800, because `PropertyMetadata` tracks
  `IsVirtual` and `IsOverride` but has no `IsNew`, so the generated partial declaration omits the
  modifier that the hand-written declaration requires to match.
- **An interface property with an inaccessible accessor on both sides, such as `{ protected get; init;
  }`, explicitly implemented by a class, yields a metadata entry with both accessor lambdas null.** This
  is a degenerate but valid entry (the property key exists, reading or writing it via the metadata does
  nothing observable), and is a strict improvement over the previous behaviour, which was CS1540.

## Why the test strategy has three layers

Issue #428 shipped because the existing tests asserted on generated **text**:
`Assert.Contains(@"""Status""")` passes on code that can never compile. A wrong string embedded in a
larger wrong string still contains the substring being asserted. Fixing that required more than adding
regression tests for the reported shape; it required a strategy that cannot pass on broken output by
construction.

1. **Verify snapshots of the full generated source**, for every shape, using Verify.Xunit's
   `.verified.txt`/`.received.txt` workflow. This catches unintended output changes for already-working
   shapes, but a snapshot can itself capture code that does not compile, so it is necessary and not
   sufficient.
2. **A compile-clean assertion** (`GeneratorTestHost.RunExpectingCleanCompilation` and its
   library-reference variant), which fails the test if `outputCompilation.GetDiagnostics()` contains any
   error. This is the layer that would have caught the original #428 defect directly, since the
   generated code for a sub-interface explicit implementation never compiled.
3. **Real subject models compiled by the generator inside the test project**, exercised through the
   registry at runtime rather than through the generator's own output. A regression here is not a
   failing test, it is a failing build, because the test project itself cannot compile against a broken
   generator. This is the layer that catches behavioural regressions that layer 2 cannot see at all,
   because the input is valid C# either way, so a regression there would still compile clean: case Z (a
   class that both declares a property and explicitly implements the same interface member with a
   colliding name) is one dictionary-emission change away from throwing `TypeInitializationException` on
   a duplicate key at type-load time, and case AA (two explicit implementations of one generic interface
   at different instantiations) is one deduplication change away from silently dropping a member that
   currently resolves. Both compile cleanly today and are pinned by layer 3 tests asserting the correct
   runtime value, so a regression in either shows up as a failing test rather than a silent behaviour
   change that only a diligent code reviewer would catch.

Three inputs are invalid C# by construction and are asserted the opposite way: instead of demanding a
clean compilation, the test asserts that the expected compiler error is present, the generator itself
does not throw, and no additional generator-caused error appears beyond the expected one. `partial`
combined with an explicit interface implementation is illegal (CS0754); `[InterceptorSubject]` on a
`struct` or `interface` is rejected by the compiler before the generator's own diagnostics apply
(CS0592); and a non-attributed class declaring partial properties without `[InterceptorSubject]` is not
a generator input at all, it is the contrast case proving the generator ignores types it was not asked
to process (CS9248).
