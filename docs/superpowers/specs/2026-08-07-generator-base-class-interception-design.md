# Base class interception in subject hierarchies (issue #437)

## Problem

In an `[InterceptorSubject]` inheritance hierarchy, properties declared on a base class are never
intercepted. The value is written and `PropertyChanged` still fires, so the failure is silent:
change tracking never records the write, and connectors such as OPC UA and MQTT never see it.

`SubjectCodeGenerator.Generate` emits the `IInterceptorSubject` plumbing unconditionally, so every
class in a hierarchy declares its own `private IInterceptorExecutor? _context` and its own explicit
`IInterceptorSubject.Context`. The most derived explicit implementation wins the interface map, so
the constructor's `((IInterceptorSubject)this).Context.AddFallbackContext(context)` only ever
populates the most derived field. Every base class keeps a permanently null `_context` and its
generated accessors take the documented no-interception fast path.

The same duplication affects three more members. Measured on a three level hierarchy:

| Plumbing member | Copies across 3 levels | Status |
|---|---|---|
| `_context` | 3 | Broken. Only the most derived is ever populated |
| `_properties` | 3 | Dead. Only the most derived is read |
| `Data` backing field | 3 | Dead allocation. One `ConcurrentDictionary` per level |
| `SyncRoot` backing field | 3 | Dead allocation. One `object` per level |

The allocation saving is a side effect, not the motivation. Counted with
`grep -rn '^\s*\[InterceptorSubject' src --include '*.cs'`, excluding `obj`, `bin` and generated
output, the repository declares 360 subjects across 151 files. Subject over subject hierarchies are
rare and none is deeper than two levels; outside tests and samples the only ones are the three
Philips Hue device classes over `HueDevice`. For those the saving is one `ConcurrentDictionary`, one
`object` and two reference fields per instance, and for every subject without a subject base it is
zero. The correctness fix carries this change on its own.

### Adjacent shapes that are broken today

Four shapes fail today, all for the same underlying reason: the emitted plumbing is decided per class
from the immediate base, and a generator cannot see its own output. All four are in scope.

**1. A hand written base without a static `DefaultProperties`.** Does not compile:

```
error CS0117: 'HandBase' does not contain a definition for 'DefaultProperties'
```

`SubjectMetadataExtractor.cs:98-105` sets `BaseClassTypeName` whenever the base merely implements
`IInterceptorSubject`, and `SubjectCodeGenerator.cs:241-244` then emits `.Concat(HandBase.DefaultProperties)`
unconditionally.

**2. A hand written subclass of a generated subject.** Cannot implement an intercepted property:

```
error CS0122: 'GenBase.GetPropertyValue<TProperty>(...)' is inaccessible due to its protection level
```

The members are found, only the modifier blocks the call.

**3. A plain class between two subjects.** Does not build. With `A` a subject, `B` a plain class and
`C` a subject deriving from `B`, run against the committed generator:

```
C.g.cs(27,51): warning CS0108: 'C.PropertyChanged' hides inherited member 'A.PropertyChanged'
C.g.cs(30,24): warning CS0108: 'C.RaisePropertyChanged(string)' hides inherited member 'A.RaisePropertyChanged(string)'
C.g.cs(59,76): warning CS0108: 'C.DefaultProperties' hides inherited member 'A.DefaultProperties'
```

`src/Directory.Build.props` sets `TreatWarningsAsErrors`, so these are build errors. `B` carries no
attribute, and at generation time `B` does not implement `IInterceptorSubject` either, because `A`'s
interface list exists only in `A.g.cs`. So `baseClass` resolves to null and `C` emits a full root
shape that collides with everything it inherited. Even with the warnings silenced, `C.Properties`
would report only `C`'s own properties, because `DefaultProperties` is emitted without the `.Concat`.

**4. A sealed subject.** Does not build:

```
SealedSubject.g.cs(30,24): warning CS0628: 'SealedSubject.RaisePropertyChanged(string)': new protected member declared in sealed type
```

There is no sealed subject anywhere in the repository, so nothing catches it.

## Goals

1. A property declared on a base subject is intercepted, at any depth.
2. Per instance plumbing is allocated once per subject, not once per inheritance level.
3. No regression on the steady state property read and write paths.
4. A hand written base class can host generated subclasses, and a hand written subclass can extend
   a generated base, both against a documented contract.
5. Shapes that cannot work produce a generator diagnostic naming the problem, rather than a raw
   compiler error inside generated code. Where a diagnostic suppresses generation, the user's own
   partial properties still report CS9248, exactly as they do for NI0001 today; the promise is a
   named cause, not a single diagnostic.
6. The plain intermediate class and sealed subject shapes build.

## Non-goals

- Making `Data` lazily allocated. It is allocated eagerly for every subject and most likely never
  used by subjects that are never registered, so laziness would beat the duplicate removal in
  absolute terms, but it changes a shipped member's initialisation semantics and is orthogonal to
  this issue. File separately.
- Changing `DynamicSubject` so generated subjects can derive from it. It holds its properties per
  instance and initialises `_properties` eagerly to an empty frozen dictionary, so a generated
  subclass's static `DefaultProperties` would be swallowed and the subclass would report zero
  properties. The supported path for a fixed model plus runtime properties is `[InterceptorSubject]`
  plus `AddProperties`, which needs no different base class. NI0011's message points there.
- Issues #434 (generator performance), #435 (diagnostics for shapes surfacing as raw compiler
  errors) and #436 (analyzer proposal).

## Constraints discovered by probing

These were measured, not assumed, and each one pins part of the design.

**A derived class cannot drop `IInterceptorSubject` from its base list.** An explicit interface
member implementation is only legal in a class that lists the interface itself, and inheriting it
does not count:

```
error CS0540: 'DerivedNoRelist.ISubject.Properties': containing type does not implement interface 'ISubject'
```

Since `Properties` must stay per class, the derived class keeps re-listing the interface, exactly as
it does today, and interface re-implementation stays part of the emitted shape.

**Re-implementation preserves inherited explicit implementations, unless a public member in a strictly
more derived class matches.** Interface mapping walks class by class and prefers a class's explicit
implementation over its own public members, so a public member declared in the same class as the
explicit implementation does not displace it. A matching public member in any class between the
explicit implementation and the re-implementing class does:

```
RootWithPublic : SyncRoot=EXPLICIT       public member in the same class as the explicit impl loses
DerivedRelist  : SyncRoot=Object         base explicit impl still wins when nothing matches
DerivedHijack  : SyncRoot=USER-OWNED     public object SyncRoot { get; } in the derived class wins
LeafOverPlain  : SyncRoot=MIDDLE-OWNED   and so does one in a plain class in between
```

This hazard does not exist today, because the derived class emits its own explicit implementation of
every member. NI0014 exists to catch it, and the distinction above is what keeps NI0014 from firing
on the root itself.

**A protected member may share a name with an explicit interface implementation.** An explicit
implementation does not occupy the class's simple name namespace, so `protected ... Properties` and
`IInterceptorSubject.Properties` compile side by side on one type. The new member is still not named
`Properties`, for the reasons under Naming below.

**`[MethodImpl]` is not valid on a property declaration**, only on constructors and methods, so the
attribute goes on the accessor:

```
error CS0592: Attribute 'MethodImpl' is not valid on this declaration type.
```

**A protected member in a `sealed` class is CS0628**, and an unnecessary `new` is CS0109. Both are
warnings, and `src/Directory.Build.props` turns both into build errors, including inside generated
files.

## Design

Split the emitted plumbing by whether a member is per hierarchy instance state or per class
metadata. There are two emission modes, chosen per class.

### Resolving the base class facts

`SubjectMetadataExtractor` currently derives `BaseClassTypeName`, `BaseClassHasInterceptorSubject`
and `BaseClassHasInpc` from `typeSymbol.BaseType` alone (`SubjectMetadataExtractor.cs:98-113`). All
three move to the **nearest subject ancestor**, found by walking the base chain upward past
`System.Object` and taking the first class that either carries `[InterceptorSubject]` or implements
`IInterceptorSubject`. Plain classes in between are skipped rather than treated as the base.

This is what fixes broken shape 3, and it is the same walk mode selection needs, so it is one piece
of work rather than two. `.Concat(...)` targets the subject ancestor's fully qualified name, and the
`new` modifier on `DefaultProperties` is driven by that ancestor rather than by the immediate base.

### Root mode

Used when the class has no subject ancestor, and as the fallback described under NI0012. Identical
to today except for the highlighted lines.

```csharp
private IInterceptorExecutor? _context;
private IReadOnlyDictionary<string, SubjectPropertyMetadata>? _properties;

[JsonIgnore]
IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _context, this);

[JsonIgnore]
ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();

[JsonIgnore]
object IInterceptorSubject.SyncRoot { get; } = new object();

// CHANGED: reads the protected accessor instead of the field directly.
[JsonIgnore]
IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => InstanceProperties ?? DefaultProperties;

void IInterceptorSubject.AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
{
    lock (((IInterceptorSubject)this).SyncRoot)
    {
        // CHANGED: was (_properties ?? DefaultProperties). Dispatching through the interface
        // makes the merge start from the most derived DefaultProperties, which is what lets
        // this method live in the root.
        _properties = ((IInterceptorSubject)this).Properties
            .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
            .ToFrozenDictionary();
    }
}

// NEW: the only state a derived class needs to reach. Attribute on the accessor, not the
// declaration, because CS0592.
protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? InstanceProperties
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    get => _properties;
}

// CHANGED: private to protected, bodies unchanged.
[MethodImpl(MethodImplOptions.AggressiveInlining)]
protected TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue) { ... }

[MethodImpl(MethodImplOptions.AggressiveInlining)]
protected bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> setValue) { ... }

[MethodImpl(MethodImplOptions.AggressiveInlining)]
protected object? InvokeMethod(string methodName, Func<IInterceptorSubject, object?[], object?> invokeMethod, params object?[] parameters) { ... }
```

`_context` and `_properties` stay private. Only the root's own members touch them.

Root mode governs the plumbing block only. `EmitDefaultProperties` is independent of the mode, so a
class in root mode that still has a subject ancestor, which is exactly the NI0012 fallback, keeps
emitting the `new` modifier and the `.Concat(...)` link.

### Sealed subjects

When the subject class is `sealed`, every member that would be `protected` is emitted `private`
instead: the four members above and `RaisePropertyChanged`. No subclass can exist to need the access,
and CS0628 is a build error here. The `void IRaisePropertyChanged.RaisePropertyChanged(string)`
explicit forwarder is unaffected and still emitted. This fixes broken shape 4.

### Derived mode

Used when an ancestor provides the contract. The class declaration is unchanged, still re-listing
`IInterceptorSubject` because CS0540 requires it. From the whole plumbing block the class emits one
line:

```csharp
[JsonIgnore]
IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => InstanceProperties ?? DefaultProperties;
```

Not emitted: `_context`, `_properties`, `Context`, `Data`, `SyncRoot`, `AddProperties`,
`InstanceProperties`, and the three helpers. The `INotifyPropertyChanged` block is already gated on
`BaseClassHasInpc` and stays gated, now driven by the subject ancestor.

`DefaultProperties`, the constructors, the partial properties, the methods and the partial hooks are
all emitted as today, with the `new` modifier and the `.Concat(...)` target coming from the subject
ancestor.

### Why `Properties` cannot move

`DefaultProperties` is a `static` hidden by `new` at each level, so it binds at compile time to
whichever class the expression was emitted into. Each level concatenates only its nearest subject
ancestor, which has already folded in its own, so `Leaf.DefaultProperties` holds every level:

```csharp
public new static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; } =
    new Dictionary<string, SubjectPropertyMetadata> { ["LeafProperty"] = ... }
    .Concat(global::Repro.MiddleSubject.DefaultProperties)
    .ToFrozenDictionary();
```

Emitted in the leaf, `InstanceProperties ?? DefaultProperties` resolves to `Leaf.DefaultProperties`
and reports all three levels. Emitted only in the root, the same expression resolves to
`Base.DefaultProperties` and every derived subject would report one level, trading a silent
interception bug for a silent metadata bug. `EmitDefaultProperties` keeps today's semantics.

### Rejected alternative: a virtual defaults hook

`Properties` could move to the root entirely by putting a virtual instance member in front of the
static:

```csharp
// root only
IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties
    => _properties ?? GetDefaultProperties();
protected virtual IReadOnlyDictionary<string, SubjectPropertyMetadata> GetDefaultProperties() => DefaultProperties;

// every derived class, replacing the explicit Properties line
protected override IReadOnlyDictionary<string, SubjectPropertyMetadata> GetDefaultProperties() => DefaultProperties;
```

This was built and verified to report all levels correctly. It is attractive: derived classes stop
listing `IInterceptorSubject`, so CS0540 stops constraining the design, the re-implementation hijack
becomes structurally impossible, and `InstanceProperties` disappears from both the emitted code and
the base class contract.

It is rejected on performance. `IInterceptorSubject.Properties` is not a cold metadata path. It is
read on every intercepted write through `PropertyReference.Metadata`, which is deliberately uncached:

```
src/Namotion.Interceptor/PropertyReference.cs:25   Subject.Properties.TryGetValue(Name, out var metadata)
src/Namotion.Interceptor/PropertyReference.cs:20   "Looks up the property metadata ... on each access; the result is not cached"
```

with callers at `IWriteInterceptor.cs:258` and `:280`, `LifecycleInterceptor.cs:296`, and
`DerivedPropertyChangeHandler.cs:156`. The alternative therefore adds a virtual call inside a member
already reached through an interface dispatch, several times per intercepted write, for every subject
including the large majority that have no base class at all. The design keeps the hot path untouched
and pays for it with NI0014 and the named residual risks below.

### Naming

The new member is `InstanceProperties`, not `Properties` and not `AddedProperties`.

`AddedProperties` is inaccurate: `AddProperties` stores the merged result, defaults concatenated with
the additions, so the field holds everything after one call, not the additions alone.

`Properties` is legal but puts two same named members with different types and meanings on every
subject: `this.Properties` would be nullable and defaults free, `((IInterceptorSubject)this).Properties`
non-null and complete. The audience for the member is the hand written subclass author, who would
reach for the first and be silently wrong. It also makes the emitted line fragile against an ordinary
user property named `Properties`, which would hide the inherited member and rebind that line.

`InstanceProperties` pairs with `DefaultProperties`, static and per type against nullable and per
instance, and the line reads as its own documentation.

## The subject base class contract

A class may host generated subclasses when it exposes all of the following. The generated root mode
satisfies it by construction.

| Member | Needed by |
|---|---|
| implements `IInterceptorSubject` | everything else |
| implements `IRaisePropertyChanged` | suppressing the generated `INotifyPropertyChanged` block |
| `protected TProperty GetPropertyValue<TProperty>(string, Func<IInterceptorSubject, TProperty>)` | generated getters |
| `protected bool SetPropertyValue<TProperty>(string, TProperty, TProperty, Action<IInterceptorSubject, TProperty>)` | generated setters |
| `protected object? InvokeMethod(string, Func<IInterceptorSubject, object?[], object?>, params object?[])` | generated methods |
| `protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? InstanceProperties` | the per class `Properties` line |
| `public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties` | the `.Concat(...)` merge |
| `protected void RaisePropertyChanged(string)` | **only when the ancestor carries `[InterceptorSubject]`** |

`IRaisePropertyChanged` is listed because `SubjectMetadataExtractor.cs:112` keys the
`INotifyPropertyChanged` suppression off that interface, not off `INotifyPropertyChanged`. A base that
implements `INotifyPropertyChanged` and a `RaisePropertyChanged` method but not `IRaisePropertyChanged`
gets both members re-declared in the subclass, which is two CS0108 and therefore two build errors.

The last row is conditional because the emitted call differs. When the ancestor carries the attribute,
`SubjectCodeGenerator.cs:335-339` emits a direct `RaisePropertyChanged(...)` call and needs the
protected member. When it does not, which is every hand written base, it emits
`((IRaisePropertyChanged)this).RaisePropertyChanged(...)` and an explicit interface implementation is
enough. Requiring the protected member from a hand written base would reject the idiomatic
implementation and defeat goal 4.

Members may be more accessible than listed. Accessibility is checked with
`Compilation.IsSymbolAccessibleWithin(member, typeSymbol)`, which handles protected through
inheritance and `InternalsVisibleTo`. Lookup walks the ancestor chain, so a contract member inherited
by the immediate base from further up satisfies it, and it runs against the **constructed** type, so a
generic hand written base such as `Base<T>` is checked with its type arguments substituted.

The contract is deliberately free of fields, so a hand written base can satisfy it with ordinary
members and the check is a symbol lookup rather than a field name convention.

### Behavioural invariants the contract cannot check

Three requirements are semantic and invisible to a symbol lookup. They belong in the documentation and
in the conforming base test, and NI0011's description states that it verifies shape only.

1. `AddProperties` must merge starting from `((IInterceptorSubject)this).Properties`, not from its own
   `DefaultProperties` and not from its own backing field, and it must store into the field that
   `InstanceProperties` returns. A base that merges from its own field passes every symbol check and
   then silently drops the subclass's `DefaultProperties` on the first call. `DynamicSubject.AddProperties`
   is written that way today.
2. The three helpers must route through the same executor that `IInterceptorSubject.Context` publishes
   for that same instance. A base that keeps a second executor passes the symbol check and reproduces
   #437 verbatim, which is the bug being fixed.
3. `IInterceptorSubject.Context` must return an `IInterceptorExecutor` constructed for that instance.
   `DynamicSubjectFactory.cs:64` casts it unguarded, and `InterceptorExecutor` binds its subject at
   construction (`InterceptorExecutor.cs:30-33`), so a borrowed or shared context misroutes every
   `PropertyReference`.

### The subclass side

A hand written subclass of a generated subject may call the four protected members, and must call
`((IInterceptorSubject)this).AddProperties(...)` to register its own property metadata, since nothing
generates a `DefaultProperties` for it. The four members are generated implementation detail: they are
documented so the scenario is usable, and documented as subject to change, not as a stable API.

### Mode selection

Let the ancestor chain be `typeSymbol.BaseType` walked upward, excluding `System.Object`.

```
if some ancestor carries [InterceptorSubject] and is declared in source in this compilation  -> Derived
else if the ancestor chain exposes the full contract accessibly                              -> Derived
else if some ancestor implements IInterceptorSubject or carries [InterceptorSubject]:
        if an accessible static DefaultProperties resolves on the chain                      -> Root, report NI0012
        else                                                                                 -> suppress, report NI0011
else                                                                                          -> Root
```

The first branch exists because a generator cannot see its own output: for an ancestor declared in
this compilation the generated members do not exist as symbols yet, so the attribute is the only
available evidence, and it is sufficient because the same generator run produces them. This is also
exactly why broken shape 3 exists today and why every base class fact has to come from the chain
rather than from the immediate base.

Accepted consequence: if an in source ancestor carries the attribute but its own generation is
suppressed by NI0001, NI0002, NI0003, NI0009 or NI0010, the derived class emits calls to members that
were never generated. The build is already failing on that ancestor's own diagnostic and on CS9248
for its partial properties, so this adds noise to an already red build rather than hiding anything.

## Diagnostics

Four new rules, continuing from NI0010, category `Namotion.Interceptor`, appended to
`AnalyzerReleases.Unshipped.md`.

**NI0011, base class does not satisfy the subject base contract.** Error, generation suppressed. The
message lists the missing members by signature. Fires when an ancestor implements `IInterceptorSubject`
or carries the attribute, the contract is not satisfied, and no static `DefaultProperties` resolves.
This replaces today's CS0117 inside generated code. The message points `DynamicSubject` style bases at
`[InterceptorSubject]` plus `AddProperties`. It verifies shape, not the three behavioural invariants
above.

**NI0012, base class plumbing cannot be shared.** Warning, and the class falls back to root mode,
which is today's behaviour: it compiles, that one hierarchy keeps the #437 bug, and the message says
to rebuild the base assembly against the current package version or to satisfy the contract. It covers
two inputs that must not become errors:

- A base built with an older generator, carrying the attribute and a static `DefaultProperties` but
  with private helpers.
- A hand written base that implements `IInterceptorSubject` and exposes a public static
  `DefaultProperties` but not the rest of the contract. **This shape compiles and works today**, with
  the subclass emitting its own complete plumbing that shadows the base's, so treating it as an error
  would be an unflagged breaking change.

"It compiles" holds as long as the consumer does not treat warnings as errors, which this repository
does. That caveat is stated in the message and in the docs: the rule is suppressible by ID, and the
alternative, staying silent, would hide a hierarchy that keeps the bug.

In the fallback the four root mode members carry `new` **only where a lookup on the ancestor chain
finds an accessible member of the same name that the emitted one actually hides**. A blanket `new`
produces CS0109 on both of the inputs above, since neither base exposes anything to hide, and CS0109
is a build error here. The lookup is the same one the contract check already performs.

**NI0013, member hides an inherited generated member.** Error. Fires in derived mode when the subject,
or any class between it and the ancestor that provides the contract, declares any member named
`GetPropertyValue`, `SetPropertyValue`, `InvokeMethod` or `InstanceProperties`.

The rule is deliberately over-approximate: name only, any member kind, no signature test. A precise
rule is both harder to implement and unsound, because capture has three distinct routes. A `new`
annotated member of the same shape captures the generated reference and produces **no diagnostic at
all**, which is the dangerous case; an applicable overload with a different signature can win overload
resolution without hiding anything and without `new`; and a same named member of an unrelated kind is
harmless for the three helpers but fatal for `InstanceProperties`, which is read in expression
position. Reporting the name is simple, implementable, and covers all three. The false positive it
admits, an unrelated member with one of those four names, is a name nobody chooses by accident.

`RaisePropertyChanged` is deliberately outside this rule, and not because CS0108 covers it, which it
does equally for the four above. It is excluded because it is already inherited today whenever
`BaseClassHasInpc` is set, so a user who writes `protected new void RaisePropertyChanged(string)` is
relying on existing behaviour that may well be deliberate, and this change did not create that
situation.

**NI0014, member hijacks an inherited interface implementation.** Error. Fires in derived mode when the
subject, or any class between it and the ancestor that provides the contract, declares a public non
static member matching `IInterceptorSubject.Context`, `Data`, `SyncRoot` or `AddProperties` by name and
signature. Interface re-implementation gives such a member the interface slot, silently displacing the
root's explicit implementation.

`Properties` is excluded because the class emits its own explicit implementation of it, which always
wins. Members declared in the same class as the explicit implementation are excluded for the same
reason, which is why the rule is scoped to classes strictly between the subject and the contract
provider, inclusive of the subject itself.

**This is a flagged breaking change.** A derived subject declaring `public object SyncRoot { get; }`
compiles clean today with no warnings, because the derived class emits its own explicit implementation
that wins over its own public member. After this change that member takes the interface slot. Error
rather than warning is still right: `AddProperties` locks `SyncRoot`, and `InterceptorExecutor.cs:19-27`
documents the per subject revision counter as relying on the terminal write holding that exact lock, so
a silent redirect is a data race, not a cosmetic issue.

## Accepted residual risks

**Cross assembly rebuild.** NI0014 fires where the derived subject is compiled. If assembly A ships a
subject base, assembly B compiles a subject deriving from it and builds clean, and A then ships a
version that adds a public `object SyncRoot { get; }`, the runtime recomputes the interface map and
B's lock silently moves to the wrong object without B ever being recompiled. This requires a base
author to add a member with one of four exact names and signatures. The rejected virtual hook would
make it structurally impossible; keeping the hot path clean costs this.

**Interface evolution.** Because derived subjects keep re-listing `IInterceptorSubject`, any member
added to that interface in future has to be evaluated for the same hijack question and added to
NI0014's list.

**Construction time writes are still not intercepted.** A base declared property written inside a
constructor runs before `AddFallbackContext`, so `_context` is null and the write takes the fast path.
This is unchanged by this design and is pinned by a test so it does not read as a regression later.

All three are documented in `docs/generator.md` together with the reason they are accepted, namely
that the alternative costs a virtual call on the intercepted write path.

## Performance

Correctness first, then allocations, is the ordering in `AGENTS.md`. This change is expected to leave
the steady state where it is and reduce construction cost for hierarchies.

| Path | Today | After | Delta |
|---|---|---|---|
| Root subject get and set | private inlined helper on own field | same code, `protected` modifier | none |
| Derived get and set | private helper in derived, derived's field | inherited helper, root's field | none once inlined |
| `Properties` | `_properties ?? DefaultProperties` | `InstanceProperties ?? DefaultProperties` | none once inlined, and the same laziness: `DefaultProperties` is still only touched when the instance set is null |
| `AddProperties` | field read | one interface dispatch | negligible, rare, and it already allocates a frozen dictionary |
| Construct a 3 level subject | 3 `ConcurrentDictionary` + 3 `object` + 12 reference fields | 1 + 1 + 4 | the improvement |
| Base declared property write | fast path, no interception | intercepted | intended, this is the bug |

Both helpers keep `[MethodImpl(MethodImplOptions.AggressiveInlining)]`. A non-virtual call to a base
class method inlines exactly like a same class one, and field access is at a fixed offset either way,
so the steady state is unchanged by construction.

Separately from latency, collapsing `GetPropertyValue<TProperty>` and `SetPropertyValue<TProperty>`
into the root also collapses the value type generic instantiations a hierarchy pays for today, which
reduces JIT work and code size. No benchmark can show this, so it is recorded as reasoning, not as a
measured result.

Two caveats to record rather than discover later. The last table row is not a regression but will read
as one: a benchmark that writes a base declared property gets slower because it was silently skipping
the entire interceptor chain, so any before and after comparison must keep that row separate. And under
ReadyToRun a cross assembly hierarchy may not inline the helper until tiered compilation rejits, so
cold start can differ slightly for that specific shape. NativeAOT is unaffected, since the whole app is
one version bubble.

### Benchmark gate

Add a hierarchy benchmark to `Namotion.Interceptor.Benchmark` and run it on master and on the branch.
Non-regression requires four flat rows: root only subject get, root only subject set, derived declared
get and set, and `Properties` access. The improvement is three level construction, where allocated
bytes must drop. The three level shape is synthetic: no subject in the repository is deeper than two
levels, so that row demonstrates the mechanism rather than a representative workload.

## Test harness changes

The existing harness cannot see any of the failure modes in this design. Both must be fixed before the
tests below mean anything.

1. `GeneratorRunResult.CompilationErrors` filters `Severity == Error` (`GeneratorTestHost.cs:20-22`),
   and both clean compilation helpers assert only on that list. Every hazard here, CS0108, CS0109,
   CS0628 and CS0019, is a warning, so a design that breaks every consumer build passes green. Add a
   helper that asserts no warnings either, or compile the test compilation with
   `GeneralDiagnosticOption.Error`, matching what `src/Directory.Build.props` does to consumers.
2. `RunWithLibraryReference` (`GeneratorTestHost.cs:48-67`) compiles the library with a plain
   `CSharpCompilation.Create` and no generator driver, so a referenced base built by the current
   generator cannot be produced at all. That is the entire point of mode selection branch 2. Run the
   generator over the library compilation too.

## Testing

Behaviour is the gate, not snapshots. Both the property value and `PropertyChanged` work today while
the bug is present, so a test asserting either passes against the broken generator. The assertion has
to be interceptor observation.

1. A base declared property write is observed by an `IWriteInterceptor`, a read by an
   `IReadInterceptor`, and a base declared method by an `IMethodInterceptor`. The base declared member
   must **not** be `virtual`, `override`, `new` or `sealed override` at the leaf. Today's
   `GeneratorShapeBehaviorTests.cs:382` looks like it covers this and does not, because
   `SealedOverrideSubject` `sealed override`s the property, so the leaf's own accessor and populated
   `_context` run.
2. Three levels, asserting a property declared at each level, not just two.
3. A base declared **subject typed** property. This is where the user visible damage lives: the
   registry never sees the assignment, so the child subject is never attached. Neither a value
   assertion nor a plain interceptor assertion covers it.
4. A base declared property written inside the base's own constructor stays unintercepted, because
   `_context` is null until `AddFallbackContext` runs. Pinned so it does not read as a regression.
5. `IInterceptorSubject.Properties` reports every level's properties. Assert on the statics
   (`Leaf.DefaultProperties` against `Middle.DefaultProperties`) rather than querying through a base
   typed reference, which resolves to the leaf's implementation and proves nothing.
6. `AddProperties` at runtime on a derived subject: the additions are visible and the base and derived
   defaults are preserved.
7. Reflection over an instance asserts exactly one `_context`, one `Data` backing field and one
   `SyncRoot` backing field across the hierarchy. This is the allocation claim, pinned.
8. A hand written subclass of a generated base implements an intercepted property through the
   protected helpers, registers it with `AddProperties`, and interception fires.
9. Through the generator-enabled `RunWithLibraryReference`: a hand written base satisfying the contract
   with a generated subclass compiles and intercepts through the hand written base; and a generated
   base in a referenced assembly with a generated subclass in the main compilation compiles and
   intercepts, which is mode selection branch 2. A third case asserts behavioural invariant 1, that the
   base's `AddProperties` merges from `((IInterceptorSubject)this).Properties` and so preserves the
   subclass's `DefaultProperties`.
10. A plain non subject class between two subjects compiles with no warnings, intercepts at both
    subject levels, and reports both levels' properties. This fails today with three CS0108.
11. A `sealed` subject compiles with no warnings. This fails today with CS0628.
12. Diagnostics: NI0011 on a non conforming base; NI0012 on a referenced base with the attribute and
    private helpers, and on a hand written base with a static `DefaultProperties` only, both still
    compiling and neither emitting a stray `new`; NI0013 on a same named member of each kind including
    a `new` annotated one; NI0014 on a public `SyncRoot` declared on the subject and on an intermediate
    plain class, and silent when the matching public member sits in the same class as the explicit
    implementation.
13. The `Namotion.Interceptor.Dynamic` suite still passes. `DynamicSubjectFactory` builds Castle class
    proxies over subject types at runtime, so it subclasses subjects outside the generator's view.
14. Snapshots for the root shape, the derived shape and the sealed shape.
15. Whole repository regeneration diff against master. The expected changes are the modifiers, the
    `InstanceProperties` member, the `AddProperties` operand, the removed block in derived subjects,
    and any `new` or `.Concat` target that moves because a base class fact now comes from the subject
    ancestor. Property key sets must be identical.

## Documentation

- `docs/generator.md`: the inheritance section, NI0011 to NI0014 in the diagnostics table, and a
  hazards and limitations section covering the re-implementation hijack, the cross assembly rebuild
  gap, interface evolution, and construction time writes, each stating that the alternative was
  rejected to keep `PropertyReference.Metadata` off a virtual call. Line 345 of that file currently
  reads "Change notifications from the base class work correctly", which is true and is precisely the
  sentence that made #437 invisible; it needs rewriting rather than extending.
- `docs/subject-guidelines.md`: the subject base class contract including the three behavioural
  invariants, the subclass side contract, and the hand written base and hand written subclass
  scenarios, each with a worked example.
- `docs/design/generator-supported-shapes.md`: the split, why `Properties` stays per class, the
  rejected virtual hook with its measurement, the accepted residual risks, the NI0014 breaking change,
  and the accepted consequence of a suppressed ancestor.
