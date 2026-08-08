# Whole repository regeneration diff for the base class interception fix (#437)

Non-regression evidence for `fix/generator-base-class-interception`. The unit suite only covers the
shapes someone thought to write. This compares the generated output for every `[InterceptorSubject]`
in the repository, including the HomeBlaze device models, against the baseline.

Re-run on 2026-08-08 against branch head `508bcff4` ("Docs: describe base class interception, the
base class contract and its limits"). The first run predated the merge of `origin/master` into the
branch, so both its baseline rationale and its counts were stale; everything below is measured at
the current head.

## Baseline choice

The baseline is `origin/master` (`7b578023`, "Add source monitoring (synchronization state, source
event stream) (#354)"), which is the merge base. Commit `2d2fa82b` merged `origin/master` into the
branch, so `origin/master` is now an ancestor of the branch head:

```bash
git merge-base origin/master HEAD          # 7b5780234b69ed1dd130125191a8509651bfe392
git rev-parse origin/master                # 7b5780234b69ed1dd130125191a8509651bfe392
git merge-base --is-ancestor origin/master HEAD   # exit 0
```

Everything between the two trees is therefore this branch's own work and nothing else. This agrees
with `2026-08-07-hierarchy-benchmark.md`, which uses the same baseline for the same reason.

## Commands

```bash
# Baseline: the merge base tree, materialized outside the repository
SCRATCH=/tmp/regen
mkdir -p $SCRATCH/ni-baseline
git archive origin/master | tar -x -C $SCRATCH/ni-baseline
cd $SCRATCH/ni-baseline
dotnet build src/Namotion.Interceptor.slnx --no-incremental \
  -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=$SCRATCH/ni-generated-base

# Branch
cd <branch worktree>
dotnet build src/Namotion.Interceptor.slnx --no-incremental \
  -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=$SCRATCH/ni-generated-head

diff -ru $SCRATCH/ni-generated-base $SCRATCH/ni-generated-head > $SCRATCH/ni-generated.diff
```

Both builds: 89 projects, `Build succeeded`, 0 warnings, 0 errors.

`--no-incremental` is required. Without it the Razor generator does not re-emit and the comparison
reports Razor files as missing rather than different.

### Path normalization

The raw diff reported 63 changed Razor files on top of the subject files. Every one of those
differences was the absolute source path baked into `#pragma checksum` and `#line` directives, which
differs because the two trees live at different locations. The two trees were copied and the two
root paths rewritten to a common `<REPO>` placeholder before the classification diff:

```bash
cp -R $SCRATCH/ni-generated-base $SCRATCH/ni-norm-base
cp -R $SCRATCH/ni-generated-head $SCRATCH/ni-norm-head
find $SCRATCH/ni-norm-base -name '*.cs' -print0 | xargs -0 perl -pi -e 's{\Q$SCRATCH/ni-baseline/\E}{<REPO>/}g;'
find $SCRATCH/ni-norm-head -name '*.cs' -print0 | xargs -0 perl -pi -e 's{<branch worktree>/}{<REPO>/}g;'
diff -ru $SCRATCH/ni-norm-base $SCRATCH/ni-norm-head > $SCRATCH/ni-generated.diff
```

After normalization, zero Razor files differ. Every remaining difference is in
`Namotion.Interceptor.Generator/Namotion.Interceptor.Generator.InterceptorSubjectGenerator`.

## Scope

| Measure | Baseline | Branch |
| --- | ---: | ---: |
| Generated `.g.cs` files | 335 | 343 |
| Generated subject files | 270 | 278 |
| Changed subject files | | 270 |
| Changed lines (excluding diff headers) | | 4014 |
| Distinct changed lines | | 34 |

All 270 baseline subject files changed. Every changed file matches one of exactly two diff shapes,
verified mechanically by normalizing each file's class name away and grouping the resulting removed
and added line sets: the grouping yields exactly two groups, 256 root mode files (5 removed, 8
added, 13 lines each) and 14 derived mode files (48 removed, 1 added, 49 lines each). Unclassified
files: 0. Line accounting: 256 x 13 + 14 x 49 = 4014, which is the whole diff.

## Classification

| Category | Lines | Files |
| --- | ---: | ---: |
| 1. `private` becoming `protected` on `GetPropertyValue`, `SetPropertyValue`, `InvokeMethod` | 1536 | 256 |
| 2. The new `GetInstanceProperties()` member | 768 | 256 |
| 3. `_properties ?? DefaultProperties` becoming `GetInstanceProperties() ?? DefaultProperties` | 512 | 256 |
| 4. The `AddProperties` operand becoming `((IInterceptorSubject)this).Properties` | 512 | 256 |
| 5. In a derived subject, removal of the whole plumbing block and the helpers | 686 | 14 |
| 6. A `new` modifier or a `.Concat` target that moved to the subject ancestor | 0 | 0 |
| 7. A `new` modifier appearing on a plumbing member | 0 | 0 |
| 8. A `RaisePropertyChanged` call moving to the `((IRaisePropertyChanged)this)` form | 0 | 0 |
| **Total** | **4014** | **270** |

Categories 7 and 8 were added to the original six because two later commits on the branch can
produce them. Neither appears in this repository. Unclassified lines: 0.

### Category 1 (1536 lines)

Three helper signatures, removed as `private` and re-added as `protected`, in each of the 256 root
mode subjects. 256 x 3 removed plus 256 x 3 added.

No subject kept the `private` form. No sealed subject class exists in the repository at all: the
only `sealed` keyword anywhere near a subject is the `sealed override partial string Label` on
`Namotion.Interceptor.Generator.Tests.SealedOverrideSubject`, which is a sealed *member* on a
non-sealed class and has nothing to do with this rule, since the generator keys off
`typeSymbol.IsSealed`. So the sealed-stays-private rule has no instance in the repository output and
is covered by the generator unit suite instead, which asserts on a real Roslyn emit and would see
the CS0628 a regression would produce.

### Category 2 (768 lines)

Three added lines per root mode subject: the `[MethodImpl(MethodImplOptions.AggressiveInlining)]`
attribute, the `protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;`
member, and the blank line separating it from the next member.

### Category 3 (512 lines)

One removed and one added line per root mode subject, on the explicit
`IInterceptorSubject.Properties` implementation.

### Category 4 (512 lines)

One removed and one added line per root mode subject, inside `IInterceptorSubject.AddProperties`.
`_properties = (_properties ?? DefaultProperties)` becomes
`_properties = ((IInterceptorSubject)this).Properties`.

### Category 5 (686 lines)

The 14 derived subjects drop the `_context` and `_properties` fields, the explicit `Context`,
`Data` and `SyncRoot` implementations, the `AddProperties` body and all three helper methods, and
keep a single re-implemented `IInterceptorSubject.Properties` that now reads
`GetInstanceProperties() ?? DefaultProperties`. `DefaultProperties` is a `static` hidden by `new` at
each level, so that one line must stay per class in order to bind to the right static.

The 14 derived subjects:

- `Namotion.Devices.Philips.Hue.HueButtonDevice`
- `Namotion.Devices.Philips.Hue.HueLightbulb`
- `Namotion.Devices.Philips.Hue.HueMotionDevice`
- `Namotion.Interceptor.Connectors.Tests.Models.Employee`
- `Namotion.Interceptor.Generator.Tests.Models.Contractor`
- `Namotion.Interceptor.Generator.Tests.Models.Employee`
- `Namotion.Interceptor.Generator.Tests.Models.EmployeeWithVirtualHooks`
- `Namotion.Interceptor.Generator.Tests.OverrideDerived`
- `Namotion.Interceptor.Generator.Tests.SealedOverrideSubject`
- `Namotion.Interceptor.Registry.Tests.Models.DimmableLight`
- `Namotion.Interceptor.Registry.Tests.Models.Teacher`
- `Namotion.Interceptor.Tests.PolymorphicDerived`
- `Namotion.Interceptor.Tests.VirtualEmployee`
- `Namotion.Interceptor.Tests.VirtualManager`

Three of those are production types (the Philips Hue devices, all deriving from `HueDevice`); the
other eleven are test models.

### Category 6 (0 lines)

No `.Concat` target changed. All 14 subjects whose `DefaultProperties` concatenates a base
`DefaultProperties` are exactly the 14 derived subjects above, and every one of them names the same
base type before and after (`HueDevice`, `Person`, `PersonBase`, `PersonWithVirtualHooks`,
`OverrideBase`, `SealedOverrideBase`, `Light`, `PolymorphicBase`, `VirtualPerson`,
`VirtualEmployee`). No `new` modifier moved: the changed line set contains no line carrying a `new`
member modifier at all. The only `new` keyword among changed lines is in the three object creation
expressions inside the removed derived-mode block (`new KeyValuePair<...>`, `new()` for `Data` and
`new object()` for `SyncRoot`), none of which is a modifier.

The count of `public new static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties`
goes from 14 to 19, and all five additions are in files that exist only on the branch (see below),
not in any pre-existing subject.

### Category 7 (0 lines)

No `new` modifier appears on any plumbing member. That shape needs a base type that already carries
a colliding member of its own (an older-generator base, a hand-written member with the same name, or
an MVVM style base carrying `PropertyChanged` and `RaisePropertyChanged`), which no subject in this
repository has. It is covered by the generator unit suite through inline sources and by NI0013 and
NI0014.

### Category 8 (0 lines)

No `RaisePropertyChanged` call moved to the interface form. No changed line mentions
`RaisePropertyChanged` at all. Exactly one subject in the repository emits
`((IRaisePropertyChanged)this).RaisePropertyChanged(...)`,
`Namotion.Interceptor.Tracking.Tests.Models.ManualInpcDerivedPerson`, and it already emitted that
form on the baseline. The line is byte-identical in both trees. The interface form fix therefore
changes no repository subject; it is exercised only by inline generator test sources.

## Files that exist only on the branch

Eight generated files have no baseline counterpart. All eight are the output for fixture types that
this branch's own test and benchmark sources add, not regenerations of anything that existed before.

- `Namotion.Interceptor.Generator.Tests.HierarchyRoot.g.cs`
- `Namotion.Interceptor.Generator.Tests.HierarchyMiddle.g.cs`
- `Namotion.Interceptor.Generator.Tests.HierarchyLeaf.g.cs`
- `Namotion.Interceptor.Generator.Tests.HierarchyChild.g.cs`
- `Namotion.Interceptor.Generator.Tests.HierarchyContextLeaf.g.cs`
- `Namotion.Interceptor.Benchmark.BenchmarkRoot.g.cs`
- `Namotion.Interceptor.Benchmark.BenchmarkMiddle.g.cs`
- `Namotion.Interceptor.Benchmark.BenchmarkLeaf.g.cs`

No baseline file is missing from the branch output.

## Property sets

Two checks, because `.Concat(Base.DefaultProperties)` puts the base last and `ToFrozenDictionary` is
last wins, so a changed entry would not show up as a key difference.

Resolved metadata entry lines across every generated file:

```bash
find $SCRATCH/ni-norm-base -name '*.g.cs' -print0 | xargs -0 grep -h '^\s*\["' | sort > /tmp/ni-keys-base.txt
find $SCRATCH/ni-norm-head -name '*.g.cs' -print0 | xargs -0 grep -h '^\s*\["' | sort > /tmp/ni-keys-head.txt
diff /tmp/ni-keys-base.txt /tmp/ni-keys-head.txt
```

1189 entries at baseline, 1198 on the branch. The diff is nine pure additions and nothing else:
`Child`, `ChildName`, `ContextLeafProperty`, `LeafProperty`, `LeafValue`, `MiddleProperty`,
`MiddleValue`, `RootProperty` and `RootValue`, all belonging to the eight branch-only fixture files
above. No entry was removed and none changed.

Whole `DefaultProperties` initializer blocks, compared per file rather than per key, so that a
changed metadata argument or a changed `.Concat` target would be caught even when the key text is
identical. The block is taken from the line containing `DefaultProperties { get; } =` through
`.ToFrozenDictionary();`:

- Shared subject files compared: 270
- `DefaultProperties` blocks that differ: 0
- Files whose `DefaultProperties` `.Concat` target changed: 0

## Verdict

Pass. Every one of the 4014 changed lines falls into exactly one expected category, with zero
unclassified lines and zero unclassified files. No property set changed for any pre-existing
subject. No defect found.
