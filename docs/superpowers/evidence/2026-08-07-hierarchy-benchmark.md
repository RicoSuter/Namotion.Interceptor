# Hierarchy benchmark: emitting the subject plumbing once per hierarchy

Evidence for the benchmark gate in
`docs/superpowers/specs/2026-08-07-generator-base-class-interception-design.md`.

The generator used to emit the `IInterceptorSubject` plumbing once per class in a hierarchy, so only
the most derived `_context` was ever populated and every base declared property silently took the no
interception fast path. It now emits that plumbing once per hierarchy, with `GetPropertyValue`,
`SetPropertyValue` and `InvokeMethod` `protected` instead of `private` so derived classes reach the
inherited ones, plus a `protected GetInstanceProperties()` accessor for the root's `_properties` field.

The gate is five flat rows and one improvement. A sixth row is expected to get slower, and that row is
the bug being fixed rather than a regression.

## Machine and runtime

```
BenchmarkDotNet v0.15.5, macOS 26.5.2 (25F84) [Darwin 25.5.0]
Apple M4 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.203
  [Host]     : .NET 9.0.10 (9.0.10, 9.0.1025.47515), Arm64 RyuJIT armv8.0-a
  Job        : .NET 9.0.10 (9.0.10, 9.0.1025.47515), Arm64 RyuJIT armv8.0-a
```

Benchmark project target framework is `net9.0`. The machine was otherwise idle apart from the editor
and shell hosting the run (load average around 3.5 of 16 cores at the start of the first run). No
other build, test or container workload ran during any measurement, and the runs were executed
strictly one after another, never in parallel.

## Configuration

`WarmupCount=5  IterationCount=15  LaunchCount=1`, with `[MemoryDiagnoser]`, BenchmarkDotNet defaults
for everything else (500 ms iteration time, unroll factor 16, default outlier removal).

```
dotnet run --project src/Namotion.Interceptor.Benchmark -c Release --no-build -- \
  --filter "*SubjectHierarchyBenchmark*" --warmupCount 5 --iterationCount 15 --launchCount 1
```

This is shorter than the BenchmarkDotNet default (which auto tunes warmup and runs at least 15
iterations after a pilot) and it keeps a full pass at just under six minutes. Statistical strength
comes from repetition instead: every configuration below was run twice in separate processes, and the
master to master spread is used as the noise floor rather than the reported error bars, which only
describe within process variance.

Baseline tree: a throwaway worktree at `origin/master` (`7b578023`), which is an ancestor of the
branch head, with `SubjectHierarchyBenchmark.cs` and `PropertiesDispatchShapeBenchmark.cs` copied in
unchanged so both trees compile the same benchmark source and differ only in generator output.

## Noise floor

Two full master runs, in separate processes, against each other. This is what "flat" has to be
measured against.

| Row | master run 1 | master run 2 | spread |
|---|---:|---:|---:|
| RootOnlyGet | 15.5044 ns | 15.9230 ns | 0.419 ns (2.7%) |
| RootOnlySet | 11.8338 ns | 12.1208 ns | 0.287 ns (2.4%) |
| DerivedDeclaredGet | 15.8671 ns | 15.2004 ns | 0.667 ns (4.4%) |
| DerivedDeclaredSet | 11.9646 ns | 12.1703 ns | 0.206 ns (1.7%) |
| PropertiesAccess | 0.0000 ns | 0.0000 ns | not measurable, see below |
| ConstructThreeLevel | 4431.65 ns | 4667.41 ns | 235.8 ns (5.3%) |
| BaseDeclaredSetThenGet | 3.8518 ns | 3.7419 ns | 0.110 ns (2.9%) |

Two head runs against each other agree: 1.6%, 0.8%, 0.1%, 1.9%, 2.8% and 3.9% on the same rows.

**Noise floor used throughout: 5% on time.** A difference smaller than that is reported as flat, not
as a percentage. Allocated bytes are not subject to this: both master runs reported exactly 6537 B and
both head runs exactly 4409 B, so the allocation column is deterministic and a difference there is
real at any size.

`ConstructThreeLevel` is the noisiest row by a wide margin, and BenchmarkDotNet flagged its
distribution as bimodal on both trees (mValue 3.43). Construction allocates enough to trigger
collections inside the measurement window, so its mean carries an error bar of 600 to 800 ns. This is
exactly why the gate for that row is on allocated bytes rather than on time.

## Before and after

Each number is the mean of the two runs.

| Row | master | head | delta | verdict |
|---|---:|---:|---:|---|
| RootOnlyGet | 15.714 ns | 15.064 ns | -0.650 ns (-4.1%) | **flat** (inside noise, head not slower) |
| RootOnlySet | 11.977 ns | 11.838 ns | -0.139 ns (-1.2%) | **flat** |
| DerivedDeclaredGet | 15.534 ns | 14.885 ns | -0.648 ns (-4.2%) | **flat** (inside noise, head not slower) |
| DerivedDeclaredSet | 12.067 ns | 11.861 ns | -0.206 ns (-1.7%) | **flat** |
| PropertiesAccess | 0.0000 ns | 0.0000 ns | none | **inconclusive as written**, see the substitute row |
| ConstructThreeLevel | 4549.5 ns, 6537 B | 3460.6 ns, 4409 B | -2128 B (-32.6%) | **improved** |
| BaseDeclaredSetThenGet | 3.797 ns | 30.696 ns | +26.90 ns (8.1x) | **not a gate**, this is the fix |

Full per run tables:

master run 1

| Method                 | Mean          | Error       | StdDev      | Gen0   | Gen1   | Allocated |
|----------------------- |--------------:|------------:|------------:|-------:|-------:|----------:|
| RootOnlyGet            |    15.5044 ns |   0.1492 ns |   0.1395 ns |      - |      - |         - |
| RootOnlySet            |    11.8338 ns |   0.0248 ns |   0.0207 ns |      - |      - |         - |
| DerivedDeclaredGet     |    15.8671 ns |   0.1176 ns |   0.1042 ns |      - |      - |         - |
| DerivedDeclaredSet     |    11.9646 ns |   0.0342 ns |   0.0320 ns |      - |      - |         - |
| PropertiesAccess       |     0.0000 ns |   0.0000 ns |   0.0000 ns |      - |      - |         - |
| ConstructThreeLevel    | 4,431.6514 ns | 676.4581 ns | 599.6625 ns | 0.4311 | 0.2174 |    6537 B |
| BaseDeclaredSetThenGet |     3.8518 ns |   0.0312 ns |   0.0260 ns |      - |      - |         - |

master run 2

| Method                 | Mean          | Error       | StdDev      | Gen0   | Gen1   | Allocated |
|----------------------- |--------------:|------------:|------------:|-------:|-------:|----------:|
| RootOnlyGet            |    15.9230 ns |   0.1890 ns |   0.1768 ns |      - |      - |         - |
| RootOnlySet            |    12.1208 ns |   0.0531 ns |   0.0497 ns |      - |      - |         - |
| DerivedDeclaredGet     |    15.2004 ns |   0.2046 ns |   0.1914 ns |      - |      - |         - |
| DerivedDeclaredSet     |    12.1703 ns |   0.0691 ns |   0.0646 ns |      - |      - |         - |
| PropertiesAccess       |     0.0000 ns |   0.0000 ns |   0.0000 ns |      - |      - |         - |
| ConstructThreeLevel    | 4,667.4133 ns | 731.6283 ns | 684.3655 ns | 0.4311 | 0.2174 |    6537 B |
| BaseDeclaredSetThenGet |     3.7419 ns |   0.0263 ns |   0.0246 ns |      - |      - |         - |

head run 1

| Method                 | Mean          | Error       | StdDev      | Gen0   | Gen1   | Allocated |
|----------------------- |--------------:|------------:|------------:|-------:|-------:|----------:|
| RootOnlyGet            |    15.1824 ns |   0.0668 ns |   0.0593 ns |      - |      - |         - |
| RootOnlySet            |    11.7903 ns |   0.0557 ns |   0.0521 ns |      - |      - |         - |
| DerivedDeclaredGet     |    14.8898 ns |   0.2152 ns |   0.1797 ns |      - |      - |         - |
| DerivedDeclaredSet     |    11.7497 ns |   0.0340 ns |   0.0284 ns |      - |      - |         - |
| PropertiesAccess       |     0.0000 ns |   0.0000 ns |   0.0000 ns |      - |      - |         - |
| ConstructThreeLevel    | 3,412.1988 ns | 617.8307 ns | 577.9193 ns | 0.1755 | 0.0896 |    4409 B |
| BaseDeclaredSetThenGet |    31.2934 ns |   0.1358 ns |   0.1134 ns |      - |      - |         - |

head run 2

| Method                 | Mean          | Error       | StdDev      | Gen0   | Gen1   | Allocated |
|----------------------- |--------------:|------------:|------------:|-------:|-------:|----------:|
| RootOnlyGet            |    14.9457 ns |   0.1059 ns |   0.0991 ns |      - |      - |         - |
| RootOnlySet            |    11.8865 ns |   0.1191 ns |   0.1056 ns |      - |      - |         - |
| DerivedDeclaredGet     |    14.8808 ns |   0.0982 ns |   0.0870 ns |      - |      - |         - |
| DerivedDeclaredSet     |    11.9732 ns |   0.1090 ns |   0.0967 ns |      - |      - |         - |
| PropertiesAccess       |     0.0000 ns |   0.0000 ns |   0.0000 ns |      - |      - |         - |
| ConstructThreeLevel    | 3,508.9336 ns | 795.1549 ns | 743.7884 ns | 0.1755 | 0.0896 |    4409 B |
| BaseDeclaredSetThenGet |    30.0995 ns |   0.1349 ns |   0.1262 ns |      - |      - |         - |

## Verdict per gate row

**RootOnlyGet, RootOnlySet, DerivedDeclaredGet, DerivedDeclaredSet: flat.** All four moved by less
than the 5% noise floor, and all four moved in the faster direction on head, so there is no regression
to argue about. This is the predicted result: a non virtual call to an inherited `protected` method
inlines exactly like the same class `private` one it replaced, and `_context` sits at a fixed offset
either way. The four rows also confirm that the steady state cost of a hierarchy is now the same as
the steady state cost of a root only subject, since `DerivedDeclaredGet` and `RootOnlyGet` agree to
within 0.2 ns on head.

**PropertiesAccess: flat, but the row as written proves nothing.** It reported exactly 0.0000 ns on
both trees. `((IInterceptorSubject)_leaf).Properties.Count` reads one field of one instance of one
type, so the JIT hoists the whole expression out of the measurement loop and the row is folded away.
That is flat by the letter of the gate and empty in substance, since it is equally 0.0000 ns whether
the emitted member is a field read or a chain of calls.

The row matters, because head really does change that member: master emitted
`_properties ?? DefaultProperties` per class, head emits `GetInstanceProperties() ?? DefaultProperties`
per class with the field now living in the root behind a `protected` accessor. So the row was measured
again in a form the JIT cannot fold, as `GeneratedPropertiesCountPolymorphic` in
`PropertiesDispatchShapeBenchmark`: the same read, over an `IInterceptorSubject[]` holding a
`BenchmarkRoot`, a `BenchmarkMiddle` and a `BenchmarkLeaf`, so the call site sees three types and has
to dispatch. Three accesses per operation.

| Tree | run 1 | run 2 | mean |
|---|---:|---:|---:|
| master | 3.533 ns | 3.357 ns | 3.445 ns |
| head | 3.442 ns | 3.479 ns | 3.461 ns |

Delta 0.016 ns (0.5%) for three accesses, against a master to master spread on this same row of
0.176 ns (5.1%). **Flat, and now on evidence rather than on an unmeasurable zero.**

**ConstructThreeLevel: improved.** 6537 B to 4409 B, a drop of 2128 B or 32.6%, identical in both runs
on both trees. Gen0 collections per 1000 operations fell from 0.4311 to 0.1755 and Gen1 from 0.2174 to
0.0896. Time fell from 4549.5 ns to 3460.6 ns (-23.9%), which is well outside the 5.3% noise floor for
this row but is reported second because the distribution is bimodal and the allocation number is the
reliable one.

The shape behind the number is the one the design predicted. On master each of the three classes
emitted its own plumbing, so constructing a `BenchmarkLeaf` ran three `Data` ConcurrentDictionary
initializers and three `SyncRoot` object initializers, and the instance carried twelve reference
fields (`_context`, `_properties`, the `Data` backing field and the `SyncRoot` backing field, three
times over). On head it runs one of each and carries four. Only one `InterceptorExecutor` was ever
created on either tree, because `((IInterceptorSubject)this).Context` in the generated constructor
already bound to the most derived explicit implementation, which is the same defect that caused the
interception bug.

**Gate result: pass.** Five rows flat, construction allocation down. Nothing regressed, so there was
no reason to stop.

## What the base declared row means

`BaseDeclaredSetThenGet` went from 3.797 ns to 30.696 ns, a factor of 8.1. It is not a gate row and it
is not a regression.

On master, `_leaf.RootValue = "x"` reached `BenchmarkRoot.SetPropertyValue`, which tested
`BenchmarkRoot._context`. That field is never populated: the generated constructor calls
`((IInterceptorSubject)this).Context`, which binds to the most derived explicit implementation, so
only `BenchmarkLeaf._context` was ever assigned. The base declared write therefore took the null
context branch every time. 3.797 ns is the cost of a field store plus a field load with no equality
check, no derived property change detection, no property change subscription and no lifecycle
handling. That is the bug in issue #437 expressed as a number: a property declared on a base class was
not intercepted at all.

On head there is exactly one `_context`, it is populated, and a base declared write runs the same
interceptor chain a leaf declared write runs. The arithmetic confirms it: on head,
`DerivedDeclaredSet` plus `DerivedDeclaredGet` is 26.75 ns, and this row, which is one set plus one
get on a base declared property, is 30.70 ns. Same order, same work. The row got slower because it
started doing the work it was always supposed to do, and any before and after comparison that averages
it into the others is measuring the fix and calling it a cost.

## Rejected alternative: the virtual defaults hook

The design rejected, on reasoning alone, moving `IInterceptorSubject.Properties` into the root behind
a hook:

```csharp
// root only
IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties
    => _properties ?? GetDefaultProperties();
protected virtual IReadOnlyDictionary<string, SubjectPropertyMetadata> GetDefaultProperties() => DefaultProperties;

// every derived class, replacing the explicit Properties line
protected override IReadOnlyDictionary<string, SubjectPropertyMetadata> GetDefaultProperties() => DefaultProperties;
```

The stated reason was that `Properties` is read on every intercepted write through
`PropertyReference.Metadata`, which is deliberately uncached (`src/Namotion.Interceptor/PropertyReference.cs:20-29`,
callers at `IWriteInterceptor.cs:258` and `:280`, `LifecycleInterceptor.cs:296` and
`DerivedPropertyChangeHandler.cs:156`), so the alternative would add a virtual call inside a member
already reached through an interface dispatch, several times per write, for every subject including
the majority that have no base class.

### What was measured

**This is not generator output.** The alternative was never emitted, so
`src/Namotion.Interceptor.Benchmark/PropertiesDispatchShapeBenchmark.cs` contains two hand written
three level hierarchies that implement `IInterceptorSubject` directly and reproduce the two dispatch
shapes: `ChosenRoot`/`ChosenMiddle`/`ChosenLeaf` re-implement the explicit interface property per class
and reach the root's field through a non virtual `protected GetInstanceProperties()`, while
`AlternativeRoot`/`AlternativeMiddle`/`AlternativeLeaf` implement the property once in the root and
reach the per type defaults through the `protected virtual GetDefaultProperties()` hook. Everything
else about the two families is identical, including the frozen dictionaries they return. What is
measured is the dispatch shape, not the generator.

Both a monomorphic and a polymorphic call site are measured, because the answer differs between them.
The polymorphic one is the representative case: `PropertyReference.Metadata` is a single member in
`Namotion.Interceptor`, so its `Subject.Properties` read is one shared call site that every subject
type in the process passes through.

| Row | run 1 | run 2 | mean |
|---|---:|---:|---:|
| ChosenPropertiesCount (monomorphic) | 0.0000 ns | 0.0000 ns | folded by the JIT |
| AlternativePropertiesCount (monomorphic) | 0.0000 ns | 0.0000 ns | folded by the JIT |
| ChosenMetadataLookup (monomorphic, through `PropertyReference.Metadata`) | 4.8530 ns | 4.7481 ns | 4.801 ns |
| AlternativeMetadataLookup (monomorphic, through `PropertyReference.Metadata`) | 4.7949 ns | 4.5671 ns | 4.681 ns |
| ChosenPropertiesCountPolymorphic (3 accesses) | 3.4064 ns | 3.4394 ns | 3.423 ns |
| AlternativePropertiesCountPolymorphic (3 accesses) | 3.8673 ns | 3.7790 ns | 3.823 ns |

### The number

**At a monomorphic call site the virtual hook is free.** Through the real
`PropertyReference.Metadata`, the alternative measured 4.681 ns against the chosen design's 4.801 ns.
The alternative is 0.120 ns *faster*, which is 2.5% and inside the noise floor. The JIT devirtualizes
the hook when it sees a single type, and the reported difference has no sign worth trusting. Call it
flat.

**At a polymorphic call site the virtual hook costs 0.133 ns per `Properties` read.** The polymorphic
rows do three reads per operation, so the 0.400 ns gap between 3.823 ns and 3.423 ns is 0.133 ns per
read. That gap reproduced in both runs (0.461 ns and 0.340 ns) and is larger than either arm's run to
run spread (0.033 ns for the chosen shape, 0.088 ns for the alternative), so it is a real difference,
not noise. As a fraction it is 11.7% of a bare `Properties` read, which sounds large only because a
bare `Properties` read is about 1.14 ns.

Scaled to a write: the design lists four `PropertyReference.Metadata` call sites on the write path. At
two to four reads per intercepted write, the alternative would add roughly 0.27 ns to 0.53 ns to a
write measured at 11.86 ns, so about 2% to 4%. That figure is arithmetic on the measured per read
delta, not a measured write, and it sits at or below the 5% noise floor of this machine, meaning a
write benchmark on this hardware would most likely not be able to resolve it at all.

### What this means for the decision

The rejection was written as though the alternative would cost something meaningful on the hot path.
The measurement does not support that reading. The honest summary is:

- Monomorphic sites: **flat**, no measurable cost.
- Polymorphic sites: **0.133 ns per `Properties` read**, real and reproducible, and roughly 2% to 4%
  of an intercepted write if a write reads `Properties` two to four times.

The design says explicitly that "if the `Properties` row comes out flat, the trade should be revisited
before merge rather than left as an assertion", and that `AGENTS.md` ranks correctness above
performance while the alternative makes the re-implementation hijack structurally impossible and
removes both `GetInstanceProperties()` and the CS0540 constraint. The measured cost is not zero, so
the row is not flat in the strict sense, but it is small enough that "rejected on performance" is a
weaker claim than the design assumed. **This should be raised and decided deliberately before merge,
not treated as settled by these numbers in either direction.**

Two caveats on the alternative measurement. The hand written families are mimics, so they pin the cost
of the dispatch shape and not of any code the generator actually emits. And the polymorphic array
holds three types, which is the shape the runtime's guarded devirtualization handles best; a call site
seeing many more subject types could behave differently in either direction, and nothing here measures
that.

## Reproducing

```bash
dotnet run --project src/Namotion.Interceptor.Benchmark -c Release -- \
  --filter "*SubjectHierarchyBenchmark*" --warmupCount 5 --iterationCount 15 --launchCount 1

dotnet run --project src/Namotion.Interceptor.Benchmark -c Release -- \
  --filter "*PropertiesDispatchShapeBenchmark*" --warmupCount 5 --iterationCount 15 --launchCount 1
```

For the master side, create a worktree at `origin/master`, copy both benchmark files into
`src/Namotion.Interceptor.Benchmark/` there, and run the same two commands.
