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
rare. The deepest is the three level `VirtualPerson` to `VirtualEmployee` to `VirtualManager` test
model (`VirtualPropertyIntegrationTests.cs:70-86`); outside tests and samples the only ones are the
three Philips Hue device classes over `HueDevice`, all one level. For those the saving is one
`ConcurrentDictionary`, one `object` and two reference fields per instance, and for every subject
without a subject base it is zero. The correctness fix carries this change on its own.

### Adjacent shapes that are broken today

Four shapes fail today, all for the same underlying reason: the emitted plumbing is decided per class
from the immediate base, and a generator cannot see its own output. All four are in scope.

**1. A hand written base without a static `DefaultProperties`.** Does not compile:

```
error CS0117: 'HandBase' does not contain a definition for 'DefaultProperties'
warning CS0109: 'GenChild.DefaultProperties' does not hide an accessible member
```

`SubjectMetadataExtractor.cs:98-105` sets `BaseClassTypeName` whenever the base merely implements
`IInterceptorSubject`, and `SubjectCodeGenerator.cs:241-244` then emits `.Concat(HandBase.DefaultProperties)`
unconditionally.

**2. A hand written subclass of a generated subject.** Cannot use the generated helpers to implement an
intercepted property. Routing writes by hand through the public `((IInterceptorSubject)this).Context`
is still possible; what is blocked is the path the generated code itself uses:

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

**4. A sealed root subject.** Does not build. A sealed *derived* subject compiles clean today, because
`RaisePropertyChanged` is gated on `BaseClassHasInpc` and is therefore not emitted into it:

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

**`[MethodImpl]` is not valid on a property declaration**, only on constructors and methods:

```
error CS0592: Attribute 'MethodImpl' is not valid on this declaration type.
```

This constrained an earlier draft in which the new member was a property and the attribute had to move
to its accessor. The final design makes it a method for an unrelated reason, see Naming, so the
attribute sits on the declaration and the constraint no longer binds. It is recorded because it is the
first thing an implementer will trip over if the member is ever turned back into a property.

**A protected member in a `sealed` class is CS0628**, and an unnecessary `new` is CS0109. Both are
warnings, and `src/Directory.Build.props` turns both into build errors, including inside generated
files.

## Design

Split the emitted plumbing by whether a member is per hierarchy instance state or per class
metadata. There are two emission modes, chosen per class.

### Resolving the base class facts

`SubjectMetadataExtractor` currently derives `BaseClassTypeName`, `BaseClassHasInterceptorSubject`
and `BaseClassHasInpc` from `typeSymbol.BaseType` alone (`SubjectMetadataExtractor.cs:98-113`).
`BaseClassTypeName` and `BaseClassHasInterceptorSubject` move to the **nearest subject ancestor**, and
so does mode selection below. The two must use the same definition, or a class can land in derived
mode while its facts come from somewhere else.

`BaseClassHasInpc` is different and must **not** move wholesale. Its current form is

```csharp
var baseClassHasInpc = baseClassHasInterceptorSubject ||
                       ImplementsInterface(typeSymbol, KnownTypes.IRaisePropertyChanged);
```

and the second disjunct is deliberately asked of the **subject**, not of the base
(`SubjectMetadataExtractor.cs:108-113`). Only the first disjunct changes, to "the nearest subject
ancestor carries the attribute". Dropping the second would break a shape with a live test:
`ManualInpcPersonBase` (`src/Namotion.Interceptor.Tracking.Tests/Models/ManualInpcPersonBase.cs:8`)
implements `INotifyPropertyChanged` and `IRaisePropertyChanged` but not `IInterceptorSubject` and
carries no attribute, so it is not a subject ancestor at all. Its subject subclass would re-declare
both members and produce the same two CS0108 as broken shape 3, and at runtime the type would carry
two competing `PropertyChanged` events. Keeping the disjunct also still fixes shape 3, because there
the ancestor does carry the attribute.

The ancestor chain is `typeSymbol.BaseType` walked upward, excluding `System.Object`. An ancestor is
a subject ancestor when it carries `[InterceptorSubject]`, **or declares `IInterceptorSubject` in its
own interface list**. The second half must read `INamedTypeSymbol.Interfaces`, not `AllInterfaces`,
and must not recurse into `BaseType`. The existing `ImplementsInterface`
(`SubjectMetadataExtractor.cs:889-908`) does both and therefore reports inherited interfaces, so it
would stop the walk at a plain intermediate `B` whenever the real subject ancestor `A` is a metadata
symbol, which is every cross-assembly hierarchy. `BaseClassHasInterceptorSubject` would then be false
and setters would emit the interface dispatch rather than the direct protected call, which is exactly
the case this change exists to fix.

Plain classes in between are skipped rather than treated as the base.

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

// CHANGED: reads the protected accessor instead of the field directly. Member order in this block
// is unchanged from today's emission, so snapshots churn only on the changed lines.
[JsonIgnore]
IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => GetInstanceProperties() ?? DefaultProperties;

[JsonIgnore]
object IInterceptorSubject.SyncRoot { get; } = new object();

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

// NEW: the only state a derived class needs to reach. A method rather than a property, see Naming.
[MethodImpl(MethodImplOptions.AggressiveInlining)]
protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;

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

Sealedness must be read from `typeSymbol.IsSealed`, not from the attributed declaration's syntax
modifiers, because `sealed` may sit on any partial declaration. `DetectConstructorState` already scans
every declaration and `accessModifier` already comes from the symbol, so this matches how the extractor
resolves the rest of the class's shape.

Sealed constrains only what can sit *below* a class, never what sits above it. A sealed subject is
commonly the leaf of a hierarchy and is therefore frequently in derived mode itself, so NI0013 and
NI0014 apply to it exactly as they do to any other derived subject. What a sealed class cannot be is a
contract provider, since nothing can derive from it.

### Derived mode

Used when an ancestor provides the contract. The class declaration is unchanged, still re-listing
`IInterceptorSubject` because CS0540 requires it. From the whole plumbing block the class emits one
line:

```csharp
[JsonIgnore]
IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => GetInstanceProperties() ?? DefaultProperties;
```

Not emitted: `_context`, `_properties`, `Context`, `Data`, `SyncRoot`, `AddProperties`,
`GetInstanceProperties()`, and the three helpers. The `INotifyPropertyChanged` block is already gated on
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

Emitted in the leaf, `GetInstanceProperties() ?? DefaultProperties` resolves to `Leaf.DefaultProperties`
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
becomes structurally impossible, and `GetInstanceProperties()` disappears from both the emitted code and
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

**The rejection is reasoning, not measurement, and the reasoning cuts against a correctness benefit**,
since the alternative makes the hijack structurally impossible and `AGENTS.md` ranks correctness above
performance. To keep the decision honest the benchmark gate below measures the alternative once,
alongside the chosen design. If the `Properties` row comes out flat, the trade should be revisited
before merge rather than left as an assertion.

### Naming

The new member is `GetInstanceProperties()`, not `Properties` and not `AddedProperties`.

`AddedProperties` is inaccurate: `AddProperties` stores the merged result, defaults concatenated with
the additions, so the field holds everything after one call, not the additions alone.

`Properties` is legal but puts two same named members with different types and meanings on every
subject: `this.Properties` would be nullable and defaults free, `((IInterceptorSubject)this).Properties`
non-null and complete. The audience for the member is the hand written subclass author, who would
reach for the first and be silently wrong. It also makes the emitted line fragile against an ordinary
user property named `Properties`, which would hide the inherited member and rebind that line.

`GetInstanceProperties()` pairs with `DefaultProperties`, static and per type against nullable and per
instance, and the line reads as its own documentation.

**It is a method rather than a property, and that is not cosmetic.** `DynamicSubjectFactory.cs:33-48`
reflects over `GetType().GetProperties(BindingFlags.Instance | Public | NonPublic)` and turns every
property not already in `Properties` into a `SubjectPropertyMetadata` with `isIntercepted: true`.
That filter returns inherited **protected** properties, since only private base members are excluded:

```
property harvested: InstanceProperties (declared on GenRoot)
property harvested: Real (declared on GenRoot)
```

A generated subject has no protected instance property today, so nothing is harvested. Adding one
would give every Castle proxied generated subject a phantom property named `InstanceProperties`,
flowing into the registry, OPC UA, MQTT and serialization. This is reachable in the repository's own
suite: `DynamicSubjectTests.cs:103` proxies `Motor`, which is `[InterceptorSubject]`. Methods are not
returned by that filter, so the method form removes the exposure at the source rather than patching
one known consumer, and any other reflection over subject properties is covered by the same move.
Both forms inline to a field load, so there is no runtime difference.

## The subject base class contract

A class may host generated subclasses when it exposes all of the following. The generated root mode
satisfies it by construction.

| Member | Needed by |
|---|---|
| implements `IInterceptorSubject` | everything else |
| implements `IRaisePropertyChanged`, on the chain or on the subject itself | suppressing the generated `INotifyPropertyChanged` block |
| `protected TProperty GetPropertyValue<TProperty>(string, Func<IInterceptorSubject, TProperty>)` | generated getters |
| `protected bool SetPropertyValue<TProperty>(string, TProperty, TProperty, Action<IInterceptorSubject, TProperty>)` | generated setters |
| `protected object? InvokeMethod(string, Func<IInterceptorSubject, object?[], object?>, params object?[])` | generated methods |
| `protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties()` | the per class `Properties` line |
| `static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties`, accessible from the subject | the `.Concat(...)` merge |
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

`InvokeMethod`'s trailing parameter must be checked with `IsParams`, not by signature alone: the
emitted call site uses expanded form, `InvokeMethod("M", lambda, p1, p2)`, so a base declaring the same
parameter types without `params` satisfies a signature match and then fails at the call.

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
   `GetInstanceProperties()` returns. A base that merges from its own field passes every symbol check and
   then silently drops the subclass's `DefaultProperties` on the first call. `DynamicSubject.AddProperties`
   is written that way today.
2. The three helpers must route through the same executor that `IInterceptorSubject.Context` publishes
   for that same instance. A base that keeps a second executor passes the symbol check and reproduces
   #437 verbatim, which is the bug being fixed.
3. `IInterceptorSubject.Context` must return an `IInterceptorExecutor` constructed for that instance.
   `DynamicSubjectFactory.cs:66` casts it unguarded, and `InterceptorExecutor` binds its subject at
   construction (`InterceptorExecutor.cs:30-33`), so a borrowed or shared context misroutes every
   `PropertyReference`.

### The subclass side

A hand written subclass of a generated subject may call the four protected members, and must call
`((IInterceptorSubject)this).AddProperties(...)` to register its own property metadata, since nothing
generates a `DefaultProperties` for it. The four members are generated implementation detail: they are
documented so the scenario is usable, and documented as subject to change, not as a stable API.

The registration must happen **before the first intercepted write**, not merely somewhere in the
class. The ancestor's generated `Subject(IInterceptorSubjectContext)` constructor publishes `_context`
before the subclass constructor body runs, so a write in that body reaches the executor, which builds
a `PropertyReference` whose `Metadata` throws `InvalidOperationException` when the name is not
registered (`PropertyReference.cs:25-29`).

### Mode selection

Every branch is asked of the **nearest subject ancestor** as defined above, never of "some ancestor"
and never of the immediate base.

```
let ancestor = nearest subject ancestor, or none

if none                                                                              -> Root
if ancestor carries [InterceptorSubject] and is declared in source in this compilation -> Derived
else if ancestor exposes the full contract accessibly                                 -> Derived
else if a usable static DefaultProperties resolves through ancestor                   -> Root, report NI0012
else                                                                                  -> suppress, report NI0011
```

The nearest qualifier is load-bearing, not tidiness. Asking "does *some* ancestor carry the attribute
in source" selects derived mode even when a hand written `IInterceptorSubject` implementer sits
between the generated root and the leaf, which goal 4 explicitly invites. That shape silently
reproduces #437:

```
Ctx=MIDDLE-CTX  Props=LEAF-PROPS  helperReads=ROOT-FIELD
```

`IInterceptorSubject.Context` resolves to the middle's executor because the middle re-implemented the
interface, while the inherited helpers still read the root's `_context`, which nothing populates. No
diagnostic fires, because NI0014 inspects only public members and an explicit interface
implementation is not one. With the nearest ancestor being the hand written middle, the first branch
fails on the attribute, the second fails on the missing helpers, the third finds a static
`DefaultProperties` through the chain, and the class correctly falls back to root mode with NI0012.

The first branch exists because a generator cannot see its own output: for an ancestor declared in
this compilation the generated members do not exist as symbols yet, so the attribute is the only
available evidence, and it is sufficient because the same generator run produces them. This is also
exactly why broken shape 3 exists today and why every base class fact has to come from the chain
rather than from the immediate base.

"Usable" means accessible **and** of a type the emitted `.Concat(...)` accepts, that is
`IEnumerable<KeyValuePair<string, SubjectPropertyMetadata>>`. Checking only that some static named
`DefaultProperties` resolves lets a base declaring `public static int DefaultProperties` through, and
the generated code then fails with CS1929, which is precisely the raw-compiler-error-in-generated-code
that goal 5 exists to remove. The same applies to the contract table row.

**Locating the contract provider.** NI0013 and NI0014 are scoped against the class that provides a
contract member, and two things make that class non-obvious. The contract may be satisfied piecewise,
so different members can have different providers, and the scope is therefore defined per member: the
classes strictly more derived than the one declaring that member, up to and including the subject. And
in the first branch the provider's members do not exist as symbols at all, which is the branch's whole
premise, so the provider cannot be found by looking for them. It is found by walking upward and
re-running mode selection at each ancestor until one resolves to root mode; that ancestor is the
provider for every member. Once the walk leaves the compilation the members do exist as symbols, so
from there the provider is the class actually declaring each member, found by ordinary lookup.

Accepted consequence: if an in source ancestor carries the attribute but its own generation is
suppressed by NI0001, NI0002, NI0003, NI0009 or NI0010, the derived class emits calls to members that
were never generated. The build is already failing on that ancestor's own diagnostic and on CS9248
for its partial properties, so this adds noise to an already red build rather than hiding anything.

## Diagnostics

Four new rules, continuing from NI0010, category `Namotion.Interceptor`, appended to
`AnalyzerReleases.Unshipped.md`.

**NI0011, base class does not satisfy the subject base contract.** Error, generation suppressed. The
message lists the missing members by signature. Fires when the nearest subject ancestor fails the
contract and no usable static `DefaultProperties` resolves through it.
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

In the fallback the four root mode members carry `new` **only where the emitted member actually hides
something**. A blanket `new` produces CS0109 on both of the inputs above, since neither base exposes
anything to hide, and CS0109 is a build error here.

The test is C#'s hiding rule, not the contract lookup, and the difference matters:

```
warning CS0108: 'D2.Prop()' hides inherited member 'B1.Prop'        same name, different kind, hides
warning CS0108: 'D3.Gen<T>(string, Func<object,T>)' hides ...       same name, same signature, hides
(no diagnostic)  D1.Foo(int) over B1.Foo(string)                    same name, different signature, does not hide
```

The contract check matches by signature, so a base exposing `GetPropertyValue` as a field, a property
or a different arity method fails the contract, lands in this fallback, and does need `new`, while a
signature lookup finds nothing to hide. Emit `new` when the chain exposes an accessible inherited
member of the same name that is either not a method, or is a method whose signature matches after
substitution.

The fallback does not repair an `INotifyPropertyChanged` collision. `PropertyChanged` and
`RaisePropertyChanged` are gated by `BaseClassHasInpc`, not by this lookup, so a base that implements
`INotifyPropertyChanged` and a `RaisePropertyChanged` method but not `IRaisePropertyChanged` still
produces two CS0108, exactly as it does today. That is why the contract lists `IRaisePropertyChanged`,
and the second bullet's "works today" claim is bounded by it.

**NI0013, member hides an inherited generated member.** Error. Fires in derived mode when the subject,
or any class between it and the ancestor that provides the contract, declares any member named
`GetPropertyValue`, `SetPropertyValue`, `InvokeMethod` or `GetInstanceProperties()`.

The rule is deliberately over-approximate: name only, any member kind, no signature test. A precise
rule is both harder to implement and unsound, because capture has two distinct routes and only one of
them is loud. A `new` annotated member of the same shape captures the generated reference and produces
**no diagnostic at all**, which is the dangerous case, and an applicable overload with a different
signature can win overload resolution without hiding anything and without `new`. Reporting the name is
simple, implementable, and covers both. The false positive it admits, an unrelated member with one of
those four names, is a name nobody chooses by accident.

One filter is still required. On the subject's own members no filter is needed, since a member always
hides. On an intermediate class the scan must be restricted to members accessible from the subject: a
`private` member named `InvokeMethod` on an intermediate neither hides nor is found by member lookup,
so nothing is captured, and firing an error there would be a pure false positive.

`RaisePropertyChanged` is deliberately outside this rule, and not because CS0108 covers it, which it
does equally for the four above. It is excluded because it is already inherited today whenever
`BaseClassHasInpc` is set, so a user who writes `protected new void RaisePropertyChanged(string)` is
relying on existing behaviour that may well be deliberate, and this change did not create that
situation.

**This is also a flagged breaking change, and it is asymmetric.** A subject may legally declare
`public void InvokeMethod(string name)` today: the signature differs from the generated private helper,
so there is no CS0111 and no CS0108. After this change the same source is a build error when the
subject is in derived mode, and remains legal in root mode. The asymmetry is not an oversight: in root
mode the class declares the helpers itself, so a member that could capture the generated call is
already a hard CS0111 collision and a diagnostic would be noise. `InvokeMethod` in particular is not a
name nobody picks, so this belongs in the release notes next to NI0014 rather than being presented as a
pure safety net. No subject in the repository currently trips it.

**NI0014, member hijacks an inherited interface implementation.** Error. Fires in derived mode when the
subject, or any class between it and the contract provider, declares either of the following for
`IInterceptorSubject.Context`, `Data`, `SyncRoot` or `AddProperties`:

- a public non static member matching by name and signature, or
- an explicit interface implementation of that member, when the declaring class is not itself a
  subject. A non subject class is never generated into, so its symbols are always complete and this is
  always decidable.

Either one takes the interface slot under re-implementation, silently displacing the root's
implementation. The second form is the one that makes a hand written class between two subjects
dangerous, and it is invisible to a public-members-only rule because an explicit implementation is not
a public member.

`Properties` is excluded because the class emits its own explicit implementation of it, which always
wins. Members declared in the same class as the explicit implementation they would displace are
excluded for the same reason, which is why the scope is per member: the classes strictly more derived
than the one declaring that member, up to and including the subject.

**This is a flagged breaking change.** A derived subject declaring `public object SyncRoot { get; }`
compiles clean today with no warnings, because the derived class emits its own explicit implementation
that wins over its own public member. After this change that member takes the interface slot.

Error rather than warning is right, but the argument runs through `Context`, not `SyncRoot`. Hijacking
`Context` under the new emission is catastrophic and silent: the inherited helpers keep reading the
root's `_context`, which nothing populates, so interception dies entirely and the unguarded
`(IInterceptorExecutor)` casts at `DynamicSubjectFactory.cs:66` and `RegisteredSubject.cs:336-337`
throw `InvalidCastException`. Measured under the proposed emission:

```
Context hijack: writes observed = []
Context hijack: cast to IInterceptorExecutor -> InvalidCastException
```

`SyncRoot`, `Data` and `AddProperties` are less severe than they first look, and the spec should not
overstate them. Every product consumer of `SyncRoot` reads it through the interface
(`WriteInterceptorFactory.cs:19` and `:44`, `ReadInterceptorFactory.cs:19`, `DynamicSubject.cs:39`, and
the generated `AddProperties`), so a hijack redirects all of them consistently to the same object
rather than splitting the lock. It becomes a genuine race only if the hijacking member returns a fresh
object per read. They stay in the rule for consistency and because a user-owned lock object is still an
aliasing hazard, but `Context` is what makes the severity.

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

**Writes before the context is published are still not intercepted.** A derived class's field
initializers run before the base constructor, so `_context` is null there and those writes take the
fast path. Same for anything in a constructor that runs before `AddFallbackContext`. Pinned by a test
so it does not read as a regression later.

This is narrower than "construction time writes", and the difference is a deliberate behaviour change.
`((IInterceptorSubject)this).Context` dispatches virtually, so a hand written
`Leaf(IInterceptorSubjectContext ctx) : base(ctx)` publishes the executor inside the **base**
constructor. Today a base declared property written afterwards in the leaf constructor body still takes
the fast path, because the base reads its own permanently null `_context`. After this change that write
is intercepted, which is the fix working as intended on the shape a subclass author is most likely to
write. `The subclass side` above depends on exactly this ordering.

All three are documented in `docs/generator.md` together with the reason they are accepted, namely
that the alternative costs a virtual call on the intercepted write path.

## Performance

Correctness first, then allocations, is the ordering in `AGENTS.md`. This change is expected to leave
the steady state where it is and reduce construction cost for hierarchies.

| Path | Today | After | Delta |
|---|---|---|---|
| Root subject get and set | private inlined helper on own field | same code, `protected` modifier | none |
| Derived get and set | private helper in derived, derived's field | inherited helper, root's field | none once inlined |
| `Properties` | `_properties ?? DefaultProperties` | `GetInstanceProperties() ?? DefaultProperties` | none once inlined, and the same laziness: `DefaultProperties` is still only touched when the instance set is null |
| `AddProperties` | field read | one interface dispatch | negligible, rare, and it already allocates a frozen dictionary |
| Construct a 3 level subject | 3 `ConcurrentDictionary` + 3 `object` + 12 reference fields | 1 + 1 + 4 | the improvement |
| Base declared property write | fast path, no interception | intercepted | intended, this is the bug |

All four members keep `[MethodImpl(MethodImplOptions.AggressiveInlining)]`. A non-virtual call to a
base class method inlines exactly like a same class one, and field access is at a fixed offset either
way, so the steady state is unchanged by construction.

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
Non-regression requires five flat rows: root only subject get, root only subject set, derived declared
get, derived declared set, and `Properties` access. The improvement is three level construction, where allocated
bytes must drop. One additional row measures the rejected virtual hook against the chosen design on
`Properties` access, so that rejection rests on a number rather than on reasoning; if it is flat, raise
it before merge. The three level shape is not synthetic: `VirtualPerson` to `VirtualEmployee` to
`VirtualManager` (`VirtualPropertyIntegrationTests.cs:70-86`) is exactly that shape, though it is a
test model rather than product code, and no subject in the repository is deeper than three
levels, so that row demonstrates the mechanism rather than a representative workload.

## Test harness changes

The existing harness cannot see any of the failure modes in this design. Both must be fixed before the
tests below mean anything.

1. `GeneratorRunResult.CompilationErrors` filters `Severity == Error` (`GeneratorTestHost.cs:20-22`),
   and both clean compilation helpers assert only on that list. Every hazard here, CS0108, CS0109,
   CS0628 and CS0108, is a **warning**, so a design that breaks every consumer build passes green.
   Add a helper that asserts no warnings. It cannot be a blanket escalation: `RunCore`
   (`GeneratorTestHost.cs:73-77`) builds the compilation with no nullable context, so every existing
   test source using `?` emits CS8632, `SourceGeneratorTests.cs:16` among many. Either enable
   `NullableContextOptions.Enable` on the test compilation, which changes other diagnostics and needs
   its own sweep, or assert no warnings against an explicit allow list containing CS8632. Note CS0019
   is an error, not a warning, and is already covered by `CompilationErrors`.
2. `RunWithLibraryReference` (`GeneratorTestHost.cs:48-67`) compiles the library with a plain
   `CSharpCompilation.Create` and no generator driver, so a referenced base built by the current
   generator cannot be produced at all. That is the entire point of mode selection branch 2. Run the
   generator over the library compilation, **opt in per call**. It cannot be unconditional: NI0012's
   stale base fixture is a base built by an *older* generator, and running the current generator over
   it would emit `protected` helpers and satisfy the contract, so NI0012 could never fire. That
   fixture is hand written and **does** carry `[InterceptorSubject]`, which is what makes it the
   stale-generator case rather than the hand-written-base case; nothing collides, because the generator
   is opted out for that library.

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
5. `IInterceptorSubject.Properties` reports every level's properties. Assert both
   `((IInterceptorSubject)leaf).Properties`, which is what catches a regression that moves `Properties`
   to the root, and the statics `Leaf.DefaultProperties` against `Middle.DefaultProperties`. Do not
   assert by querying through a base typed reference: interface dispatch resolves to the leaf's
   implementation, so that variant proves nothing.
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
11. A sealed **root** subject compiles with no warnings, which fails today with CS0628, and a sealed
    **derived** subject compiles with no warnings, which passes today and must keep passing.
12. Diagnostics: NI0011 on a non conforming base; NI0012 on a referenced base with the attribute and
    private helpers, and on a hand written base with a static `DefaultProperties` only, both still
    compiling and neither emitting a stray `new`; NI0013 on a same named member of each kind including
    a `new` annotated one, and silent on a `private` member of that name on an intermediate class;
    NI0014 on a public `SyncRoot` declared on the subject, on an intermediate plain class, and on an
    intermediate that implements the member explicitly.
13. A Castle proxy over a **generated** subject reports exactly the expected property set, asserting
    that no plumbing member is harvested. `DynamicSubjectFactory.cs:33-48` turns every reflected
    instance property not already known into an intercepted subject property, and its filter returns
    inherited protected properties, so this pins the reason `GetInstanceProperties()` is a method.
    `DynamicSubjectTests.cs:103` already proxies `Motor`, a generated subject, but asserts only values,
    so the existing suite passing is not a gate.
14. A hand written `IInterceptorSubject` implementer between a generated root and a generated leaf
    falls back to root mode with NI0012 rather than silently reproducing #437.
15. The `Namotion.Interceptor.Dynamic` suite still passes.
16. Shapes that the new rules could regress, each pinned:
    - `ManualInpcPersonBase`, a base implementing `INotifyPropertyChanged` and `IRaisePropertyChanged`
      but not `IInterceptorSubject`. Its subject subclass's generated output must be unchanged. Nothing
      covers this today and the first draft of the base-fact rule broke it.
    - An **attributed** base in a referenced assembly whose helpers are private lands in root mode with
      NI0012, not in derived mode. This pins branch 1's "declared in source" qualifier, without which a
      NuGet referenced older base emits CS0122 calls into generated code.
    - A base whose static `DefaultProperties` has the wrong type reports NI0011 rather than CS1929.
    - A base whose `InvokeMethod` lacks `params` reports NI0011 rather than failing at the call site.
    - A generic hand written base, checked with its type arguments substituted.
    - An `internal` and a `private protected` nested subject in derived mode.
    - A hand written `Leaf(IInterceptorSubjectContext ctx) : base(ctx)` whose constructor body writes a
      base declared property: that write is now intercepted, which is the intended behaviour change,
      while a derived field initializer stays unintercepted.
17. Two existing tests are upgraded rather than supplemented, because both currently look like coverage
    and are not. `VirtualPropertyIntegrationTests.cs:70-86` is already a three level hierarchy asserted
    by value only. `GeneratorShapeBehaviorTests.cs:287-330` carries a "KNOWN GAP" comment describing
    this exact bug and deliberately asserts only value and `PropertyChanged`; that comment is deleted
    and the assertion moved to interceptor observation.
18. Snapshots for the root shape, the derived shape and the sealed shape.
19. Whole repository regeneration diff against master. The expected changes are the modifiers, the
    `GetInstanceProperties()` member, the `AddProperties` operand, the removed block in derived subjects,
    and any `new` or `.Concat` target that moves because a base class fact now comes from the subject
    ancestor. Property key sets must be identical.

    Key set equality is a weaker check than it looks. `.Concat(Base.DefaultProperties)` puts the base
    last and `ToFrozenDictionary` is last wins rather than throwing, so for an `override` or `new`
    property the surviving metadata entry is the base's. That is pre-existing and out of scope here,
    but it means a change in which entry survives would not show up as a key difference. Compare the
    resolved entries, not only the keys.

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
