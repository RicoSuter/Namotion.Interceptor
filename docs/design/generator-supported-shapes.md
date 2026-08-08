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

## Why the subject plumbing is emitted once per hierarchy

The generator used to emit the whole `IInterceptorSubject` block into every subject in a hierarchy.
That is what issue #437 was: the most derived explicit implementation wins the interface map, so the
generated constructor's `((IInterceptorSubject)this).Context.AddFallbackContext(context)` only ever
populated the most derived `_context`. Every base class kept a permanently null field and its
generated accessors took the documented no-interception fast path, while `PropertyChanged` kept
firing, because the setter raises it directly rather than through the chain. The property value was
right and the interceptors never ran.

The block is now emitted in one of two modes, chosen per class in `SubjectBaseContract.Resolve`.

**Root mode** emits everything: `_context`, `_properties`, the explicit `Context`, `Data`,
`SyncRoot`, `Properties` and `AddProperties`, plus `GetInstanceProperties()` and the three helpers
`GetPropertyValue`, `SetPropertyValue` and `InvokeMethod`. The helpers changed from `private` to
`protected` so a subject below can call them. `_context` and `_properties` stay private, because only
the root's own members touch them.

**Derived mode** emits one line of that block, the explicit `IInterceptorSubject.Properties`, and
inherits the rest. The class still re-lists `IInterceptorSubject` in its base list, because an
explicit interface implementation is only legal in a class that lists the interface itself (CS0540),
and inheriting the interface does not count.

Two smaller pieces move with it. `AddProperties` merges from `((IInterceptorSubject)this).Properties`
rather than from `_properties`, so that the merge in the root starts from the most derived
`DefaultProperties`. And in a `sealed` subject every member that would be `protected` is emitted
`private` instead, including `RaisePropertyChanged`, because a protected member in a sealed class is
CS0628 and `src/Directory.Build.props` turns that into a build error. Sealedness comes from
`typeSymbol.IsSealed` rather than from the attributed declaration's modifiers, since `sealed` may sit
on any partial declaration.

### Why `Properties` cannot move to the root

`DefaultProperties` is a `static` hidden by `new` at each level, so the expression
`GetInstanceProperties() ?? DefaultProperties` binds at compile time to whichever class it was
emitted into. Each level concatenates only its nearest subject ancestor, which has already folded in
its own, so the leaf's static holds every level. Emitted in the leaf, the expression reports all
levels. Emitted only in the root, every derived subject would report the root's property set alone,
which trades a silent interception bug for a silent metadata bug.

That is also why the class keeps re-listing the interface, and therefore why the re-implementation
hazards below exist at all.

### Why the base class facts come from the nearest subject ancestor

Mode selection and every base class fact are asked of the **nearest subject ancestor**, found by
walking `BaseType` upward and skipping `System.Object`. An ancestor counts as a subject when it
carries `[InterceptorSubject]` **or** declares `IInterceptorSubject` in its own interface list.

That second half reads `INamedTypeSymbol.Interfaces` and deliberately does not recurse into
`BaseType`, unlike the general `SymbolExtensions.ImplementsInterface`. `AllInterfaces` reports
interfaces inherited from a base class, so it would report a plain intermediate class as a subject
whenever the real subject ancestor is a metadata symbol, which is every cross-assembly hierarchy. The
walk would then name the intermediate as the ancestor. That class exposes none of the contract and no
`DefaultProperties`, so the subject would either fall back to emitting its own plumbing or be refused
outright, and the real ancestor's properties would be neither merged nor intercepted. The bug this
change exists to fix would come back for exactly the shapes that cross an assembly boundary.

Reading the immediate base instead of the nearest subject ancestor is what made a plain class between
two subjects fail to build before this change: the intermediate carries no attribute and, at
generation time, implements nothing (the base subject's interface list exists only in its generated
file), so the leaf emitted a full root shape that collided with everything it had inherited, and
`TreatWarningsAsErrors` turned the resulting CS0108 warnings into errors.

### Mode selection, in order

```
let ancestor = nearest subject ancestor, or none

if ancestor is none                                                 -> Root
else if ancestor carries the attribute and will be generated here   -> Derived
else if ancestor exposes the full contract accessibly               -> Derived
else if a usable static DefaultProperties resolves through ancestor -> Root, report NI0012
else                                                                -> suppress, report NI0011
```

The second branch exists because a generator cannot see its own output: for an ancestor declared in
this compilation the generated members do not exist as symbols yet, so the attribute is the only
available evidence, and it is sufficient because the same generator run produces them. "Will be
generated here" is stricter than "declared in source": `WillBeGeneratedInThisCompilation` requires
every declaration to be a `ClassDeclarationSyntax` carrying `partial`, so an attributed ancestor that
NI0001 or NI0003 suppresses does not drag its subclass into derived mode.

The "nearest" qualifier is load-bearing rather than tidy. Asking whether *some* ancestor carries the
attribute selects derived mode even when a hand-written `IInterceptorSubject` implementer sits between
the generated root and the leaf. That shape reproduces #437 silently: `Context` resolves to the
middle's executor because the middle re-implemented the interface, while the inherited helpers still
read the root's field, which nothing populates. With the nearest ancestor being the hand-written
middle, the first branch fails on the attribute, the second on the missing helpers, the third finds a
static `DefaultProperties` through the chain, and the class correctly falls back to root mode with
NI0012.

"Usable" means accessible **and** of a type the emitted `.Concat(...)` accepts, that is
`IReadOnlyDictionary<string, SubjectPropertyMetadata>` or something implementing it. A static field
counts as well as a property, because the emitted call site reads both the same way. Checking only
that some static named `DefaultProperties` resolves lets `public static int DefaultProperties`
through, and the generated code then fails with CS1929, which is exactly the raw compiler error in
generated code the diagnostics exist to replace.

### The `INotifyPropertyChanged` decision is not the same question

`BaseClassHasInpc` decides whether the subject declares its own `PropertyChanged` and
`RaisePropertyChanged`, and it is resolved independently of the mode. It is true when the type itself implements
`IRaisePropertyChanged`, inherited or not, and otherwise when the nearest subject ancestor carries the
attribute and there is real evidence it owns the notify plumbing: either it will be generated in this
compilation, or a callable `RaisePropertyChanged(string)` is reachable from the type's own body.

The attribute on its own is only a promise. An attributed base can be non-partial, so nothing is ever
generated into it, and a hand-written attributed base may carry no notify plumbing at all. Believing
the promise leaves the subject with neither call form available: the simple name is CS0103 and the
interface cast throws at runtime. The interface clause is deliberately asked of the type rather than
of the ancestor, because a base that implements `IRaisePropertyChanged` by hand without implementing
`IInterceptorSubject` is not a subject ancestor at all, and dropping the clause makes its subclass
re-declare `PropertyChanged` and `RaisePropertyChanged`. `ManualInpcPersonBase` in
`Namotion.Interceptor.Tracking.Tests` is exactly that shape and has a live test.

The emitted raise call follows from the same two facts: a simple-name call when the ancestor carries
the attribute and a callable member is reachable, the `((IRaisePropertyChanged)this)` cast when the
chain provides the plumbing some other way, and a simple-name call to the class's own member when it
emits the plumbing itself.

## Why the virtual defaults hook was rejected, and what it measured

`Properties` could have moved into the root behind a hook, with each level overriding it:

```csharp
// root only
IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties
    => _properties ?? GetDefaultProperties();
protected virtual IReadOnlyDictionary<string, SubjectPropertyMetadata> GetDefaultProperties() => DefaultProperties;

// every derived class, replacing the explicit Properties line
protected override IReadOnlyDictionary<string, SubjectPropertyMetadata> GetDefaultProperties() => DefaultProperties;
```

This was built and verified to report all levels correctly. It is genuinely attractive: derived
classes would stop listing `IInterceptorSubject`, CS0540 would stop constraining the design, the
re-implementation hijack and the cross assembly rebuild gap below would both become structurally
impossible, and `GetInstanceProperties()` would disappear from the emitted code and from the base
class contract.

**It was not rejected on a measured cliff, and the record should not read as though it was.**
`IInterceptorSubject.Properties` is read on every intercepted write through
`PropertyReference.Metadata`, which is deliberately uncached (`PropertyReference.cs:20-29`, with
callers in `IWriteInterceptor`, `LifecycleInterceptor` and `DerivedPropertyChangeHandler`), so the
first draft of this design assumed the hook would cost something meaningful there. It was then
measured, against two hand-written three level hierarchies reproducing the two dispatch shapes
(`src/Namotion.Interceptor.Benchmark/PropertiesDispatchShapeBenchmark.cs`):

- At a **monomorphic** call site, the hook is free. Through the real `PropertyReference.Metadata` the
  alternative measured 4.681 ns against the chosen design's 4.801 ns, which is inside the noise floor
  and has no sign worth trusting. The JIT devirtualizes the hook when it sees one type.
- At a **polymorphic** call site the hook costs **0.133 ns per `Properties` read**. The polymorphic
  rows do three reads per operation, so the 0.400 ns gap between 3.823 ns and 3.423 ns is 0.133 ns per
  read. The gap reproduced in both runs and is larger than either arm's run to run spread, so it is
  real rather than noise.
- Scaled to a write: with two to four `Properties` reads per intercepted write, that is roughly
  0.27 ns to 0.53 ns on a write measured at 11.86 ns, so **about 2 to 4 percent**. That figure is
  arithmetic on the measured per read delta, not a measured write, and it sits at or below this
  machine's 5 percent noise floor.

The polymorphic number is the representative one, because `PropertyReference.Metadata` is a single
member in `Namotion.Interceptor` and its `Subject.Properties` read is one shared call site that every
subject type in the process passes through. Two caveats belong with it: the two families are hand
written mimics, so they pin the cost of the dispatch shape and not of any code the generator emits,
and the polymorphic array holds three types, which is the shape guarded devirtualization handles best.

The decision to keep the current design was taken deliberately with those numbers in hand, not by
default. The cost is small but it is paid by every subject forever, including the large majority that
have no base class at all, while the hazard the hook would remove is caught at compile time by NI0014
for every consumer that recompiles. `AGENTS.md` ranks correctness above performance, so this is a
close call rather than an obvious one, and anyone revisiting it should start from the numbers above
rather than from the phrase "rejected on performance".

Full method and machine details are in
`docs/superpowers/evidence/2026-08-07-hierarchy-benchmark.md`.

## Residual risks accepted with the per hierarchy plumbing

**Interface re-implementation can move a slot.** Because each subject re-lists `IInterceptorSubject`,
the interface map is recomputed at that class, and a public member matching `Context`, `Data`,
`SyncRoot` or `AddProperties`, or an explicit implementation of one of them, takes the slot from the
root. `Context` is what makes the severity: the inherited helpers keep reading the root's `_context`,
which nothing populates, so interception dies entirely and the unguarded `(IInterceptorExecutor)`
casts in `DynamicSubjectFactory` and `RegisteredSubject` throw. `SyncRoot`, `Data` and `AddProperties`
are milder than they look, since every product consumer of `SyncRoot` reads it through the interface
and a hijack therefore redirects all of them to the same object rather than splitting the lock; they
stay in the rule for consistency and because a user-owned lock object is still an aliasing hazard.
NI0014 makes all of it a build error.

`Properties` is excluded from NI0014, because the class emits its own explicit implementation of it,
which always wins. Members declared in the same class as the explicit implementation they would
displace are excluded for the same reason, which is why the scan starts at the subject and stops
before the ancestor that provides the plumbing.

**Cross assembly rebuild.** NI0014 fires where the derived subject is compiled, so a member added to
a base assembly afterwards is not seen. The risk is narrower than a first reading suggests. All four
of these have to hold:

1. the referenced assembly's subject hierarchy is more than one level;
2. the new member is public, non-static and instance, and is added to a class **between** the root and
   the consuming subject rather than to the root itself, because a class's own explicit implementation
   beats its own public members;
3. it matches an `IInterceptorSubject` member by name and signature exactly;
4. the consuming assembly ships without being recompiled.

Recompiling the consumer turns it into an NI0014 build error, so the exposure window is precisely
"shipped, not rebuilt". The virtual hook above would make it structurally impossible.

**Interface evolution.** Any member added to `IInterceptorSubject` in future has to be evaluated for
the same hijack question and added to NI0014's list, because derived subjects keep re-listing the
interface.

**Writes before the context is published.** A derived class's field initializers run before the base
constructor, so the context is null there and those writes take the fast path, as does anything in a
constructor body that runs before `AddFallbackContext`. This is narrower than "construction time
writes", and the difference is a deliberate behaviour change: `((IInterceptorSubject)this).Context`
dispatches virtually, so a hand-written `Leaf(IInterceptorSubjectContext context) : base(context)`
publishes the executor inside the base constructor, and a base declared property written afterwards in
the leaf constructor body is now intercepted where before it silently was not. The hand written
subclass contract depends on exactly this ordering.

**A suppressed ancestor produces calls to members that do not exist.** If an in source ancestor carries
the attribute and is partial, but its own generation is suppressed by NI0002, NI0009 or NI0010, the
derived class still selects derived mode and emits calls to members that were never generated. This is
accepted: the build is already failing on the ancestor's own diagnostic and on CS9248 for its partial
properties, so it adds noise to an already red build rather than hiding anything. NI0001 and NI0003 are
not in that list, because `WillBeGeneratedInThisCompilation` already excludes a non-partial declaration
and a record declaration.

## Three breaking changes

Two of them, NI0013 and NI0014, are new errors of this generator, and both reject source that compiled
before. The third is not a diagnostic of this generator at all: sharing the plumbing widened the
inherited helper surface, so the compiler now reports hiding where it reported nothing. All three are
listed here rather than presented as pure safety nets.

**NI0013** fires in derived mode when the subject, or any class between it and its subject ancestor,
declares a member named `GetPropertyValue`, `SetPropertyValue`, `InvokeMethod` or
`GetInstanceProperties`. The rule is deliberately over-approximate: name only, any member kind, no
signature test, statics included. A precise rule would be both harder and unsound, because capture has
two quiet routes. A `new` annotated member of the same shape captures the generated call with no
compiler diagnostic at all, and an applicable overload with a different signature can win overload
resolution without hiding anything. On an intermediate class the scan is restricted to members
accessible from the subject, because a private member there neither hides nor binds and reporting it
would be a pure false positive.

The break is asymmetric. A subject may legally declare `public void InvokeMethod(string name)` today,
since the signature differs from the generated private helper. After this change the same source is an
error in derived mode and stays legal in root mode. The rationale for leaving root mode unguarded is
narrower than it first looks. An *identical* signature there is a hard CS0111, so that half really is
covered by the compiler. A different signature is not covered by anything: a root subject declaring
`protected bool SetPropertyValue(string, string, string, Action<IInterceptorSubject, string>)`
alongside a `string` partial property compiles with zero diagnostics, wins overload resolution against
the generated generic helper, and captures every write to that property. That residual case is a known
gap rather than a guarded one. It is left open because the colliding member and the generated call are
two halves of one class the author owns, so the fix is local and the shape is visible from the
declaration, whereas in derived mode the capturing member and the code it captures live in different
classes. `RaisePropertyChanged` is deliberately outside the rule: it was already inherited whenever
the base provided the notify plumbing, so a `new` annotated override of it may well be deliberate and
predates this change.

**NI0014** fires in the same range for the four hijackable interface members. Two details differ from
the first draft of the design and are worth knowing before the rule is narrowed again. The public
member clause matches only a genuine implicit implementation, comparing the member type, parameter
types and ref kinds, and requiring the accessors to be publicly callable, so an ordinary `string Data`
or a `bool AddProperties(...)` on a domain model is not reported. And the explicit implementation
clause is reported on the subject itself as well, without the "declaring class is not a subject"
qualifier the design first proposed: in derived mode the subject's generated half contains nothing but
`IInterceptorSubject.Properties`, so a hand-written explicit `Context` on the user's half is not a
conflict the compiler catches, it is the severe silent case.

The break here is that a derived subject declaring `public object SyncRoot { get; }` compiled cleanly
before, precisely because that class emitted its own explicit implementation which beat its own public
member. It now takes the slot, and it is now an error.

Both rules are scoped from the subject up to, but not including, the nearest subject ancestor. The
design first specified a per member contract provider located by re-running mode selection upward; the
implementation uses the nearest subject ancestor for both rules, which is the same class in every shape
where the contract is satisfied as a whole and is simpler to reason about.

**The inherited helper surface is wider.** `GetPropertyValue`, `SetPropertyValue`, `InvokeMethod` and
`GetInstanceProperties` are emitted `protected` instead of `private`, because that is what lets a
derived subject inherit them instead of re-emitting them. A private member is invisible to a subclass
and hides nothing, so on master a hand-written class deriving from a generated subject could name its
own members anything it liked. It now inherits four `protected` members, and a member that genuinely
hides one of them is CS0108. `src/Directory.Build.props` sets `TreatWarningsAsErrors`, so that is a
build failure on source that compiled clean before.

This break reaches a class the generator never scans. NI0013 covers a class that is itself
`[InterceptorSubject]`, or one sitting between a subject and its subject ancestor.
`SubjectBaseContract.Resolve` is only ever asked about a subject, so a plain hand-written subclass of a
generated subject is not examined and no NI0013 can fire on it. That is not a hole, because this break
is loud rather than silent: the compiler names the file, the line and the hidden member. The consumer
adds `new` where the hiding is intended, or renames the member. The one shape CS0108 does not cover is
an overload that differs in signature, and there it is also harmless, because nothing generated calls
the helpers from such a class.

Only two of the four report CS0108 for a member of another kind. `GetPropertyValue` and
`SetPropertyValue` are generic, and the compiler does not report a field or property as hiding a
generic method, so only a method matching their signature is caught. `InvokeMethod` and
`GetInstanceProperties` are not generic, and a field, property or method of that name is caught for
both.

## Known gaps

These are not fixed by this work and are not tracked by a diagnostic. They were found incidentally while
implementing the shapes above, are out of scope for issue #428, and should become their own issues if
they turn out to matter in practice.

- **A `*WithoutInterceptor` method whose stripped name collides with an existing method on the class**
  emits a duplicate member and fails with CS0111. For example, declaring both `void Probe()` and `void
  ProbeWithoutInterceptor()` on the same subject: the generator strips the suffix and emits a second
  `void Probe()` wrapper, which the compiler rejects as a duplicate. The generator does not currently
  check for this collision before emitting the wrapper.
- **An interface property whose only accessible accessor is `init`, such as `{ protected get; init;
  }`, explicitly implemented by a class, yields a metadata entry with both accessor lambdas null.**
  This is not the "both accessors inaccessible" case, which is skipped entirely and never reaches the
  emitter: the property-level accessibility check passes here because `init` is accessible, so the
  property is kept. The getter lambda is then omitted because `protected get` is not reachable from
  generated code, and the setter lambda is *also* omitted, even though the property was kept for the
  setter's sake, because `EmitDefaultProperties` only emits a setter lambda when `HasSetter` is true,
  and an `init` accessor sets `HasInit`, never `HasSetter`; `HasInit` is consulted only when emitting a
  partial property's own accessor, a code path an explicit implementation never reaches. The result is a
  degenerate but valid entry (the property key exists, reading or writing it via the metadata does
  nothing observable), and is a strict improvement over the previous behaviour, which was CS1540.

## Why the test strategy has three layers

Issue #428 shipped because the existing tests asserted on generated **text**:
`Assert.Contains(@"""Status""")` passes on code that can never compile. A wrong string embedded in a
larger wrong string still contains the substring being asserted. Fixing that required more than adding
regression tests for the reported shape; it required a strategy that cannot pass on broken output by
construction.

1. **Verify snapshots of the full generated source**, using Verify.Xunit's `.verified.txt`/`.received.txt`
   workflow. This catches unintended output changes, but only for the sixteen shapes that already had a
   snapshot before this work (ordinary, virtual, override, inheritance, accessor visibility, nesting, and
   interface-default properties). None of the shapes this work added has one: not explicit interface
   implementations, not non-public subjects, not subjects nested in a record, struct or interface, and not
   `in` / `ref readonly` wrapper parameters. Layers 2 and 3 below are what cover those instead. A snapshot
   can also itself capture code that does not compile, so even for the sixteen shapes it does cover, it is
   necessary and not sufficient.
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
