# Whole repository regeneration diff for the base class interception fix (#437)

Non-regression evidence for `fix/generator-base-class-interception`. The unit suite only covers the
shapes someone thought to write. This compares the generated output for every `[InterceptorSubject]`
in the repository, including the HomeBlaze device models, against the baseline.

## Baseline choice

The baseline is the merge base `0530d75f` ("generator: Explicit interface implementations and
generator defects (#432)"), not `origin/master`. At the time of this run `origin/master` was
`7b578023` and carried one commit that is not in the branch's history ("Add source monitoring
(synchronization state, source event stream) (#354)"). Diffing against it would have mixed
generated output for subjects that commit adds or changes into the classification, and none of that
belongs to this branch. `git merge-base master fix/generator-base-class-interception` and
`git merge-base origin/master fix/generator-base-class-interception` both resolve to `0530d75f`,
so the merge base isolates exactly this branch's generator change.

## Commands

```bash
# Baseline
cd /Users/ricosuter/Projects/GitHub/Namotion.Interceptor
git worktree add --detach /tmp/ni-baseline 0530d75f74978c2d6a532620f3946881961dae76
cd /tmp/ni-baseline
dotnet build src/Namotion.Interceptor.slnx --no-incremental \
  -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=/tmp/ni-generated-base

# Branch
cd /Users/ricosuter/Projects/GitHub/Namotion.Interceptor/.claude/worktrees/generator-base-class-interception
dotnet build src/Namotion.Interceptor.slnx --no-incremental \
  -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=/tmp/ni-generated-head

diff -ru /tmp/ni-generated-base /tmp/ni-generated-head > /tmp/ni-generated.diff
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
cp -R /tmp/ni-generated-base /tmp/ni-norm-base
cp -R /tmp/ni-generated-head /tmp/ni-norm-head
find /tmp/ni-norm-base -name '*.cs' -print0 | xargs -0 perl -pi -e 's{/private/tmp/ni-baseline/}{<REPO>/}g; s{/tmp/ni-baseline/}{<REPO>/}g;'
find /tmp/ni-norm-head -name '*.cs' -print0 | xargs -0 perl -pi -e 's{/Users/.../generator-base-class-interception/}{<REPO>/}g;'
diff -ru /tmp/ni-norm-base /tmp/ni-norm-head > /tmp/ni-generated.diff
```

After normalization, zero Razor files differ. Every remaining difference is in
`Namotion.Interceptor.Generator/Namotion.Interceptor.Generator.InterceptorSubjectGenerator`.

## Scope

| Measure | Baseline | Branch |
| --- | ---: | ---: |
| Generated `.g.cs` files | 335 | 340 |
| Generated subject files | 269 | 274 |
| Changed subject files | | 269 |
| Changed lines (excluding diff headers) | | 4001 |
| Distinct changed lines | | 34 |

All 269 baseline subject files changed. Every changed file matches one of exactly two diff shapes,
verified mechanically by comparing each file's removed and added line sets against the expected
sets: 255 root mode files (5 removed, 8 added, 13 lines each) and 14 derived mode files
(48 removed, 1 added, 49 lines each). The 14 derived files all share a single identical removal
shape. Unclassified files: 0. Line accounting: 255 x 13 + 14 x 49 = 4001, which is the whole diff.

## Classification

| Category | Lines | Files |
| --- | ---: | ---: |
| 1. `private` becoming `protected` on `GetPropertyValue`, `SetPropertyValue`, `InvokeMethod` | 1530 | 255 |
| 2. The new `GetInstanceProperties()` member | 765 | 255 |
| 3. `_properties ?? DefaultProperties` becoming `GetInstanceProperties() ?? DefaultProperties` | 510 | 255 |
| 4. The `AddProperties` operand becoming `((IInterceptorSubject)this).Properties` | 510 | 255 |
| 5. In a derived subject, removal of the whole plumbing block and the helpers | 686 | 14 |
| 6. A `new` modifier or a `.Concat` target that moved to the subject ancestor | 0 | 0 |
| 7. A `new` modifier appearing on a plumbing member | 0 | 0 |
| 8. A `RaisePropertyChanged` call moving to the `((IRaisePropertyChanged)this)` form | 0 | 0 |
| **Total** | **4001** | **269** |

Categories 7 and 8 were added to the original six because two later commits on the branch can
produce them. Neither appears in this repository. Unclassified lines: 0.

### Category 1 (1530 lines)

Three helper signatures, removed as `private` and re-added as `protected`, in each of the 255 root
mode subjects. 255 x 3 removed plus 255 x 3 added.

No subject kept the `private` form. No sealed subject class exists in the repository at all: the
only `sealed` keyword anywhere near a subject is the `sealed override partial string Label` on
`Namotion.Interceptor.Generator.Tests.SealedOverrideSubject`, which is a sealed *member* on a
non-sealed class and has nothing to do with this rule, since the generator keys off
`typeSymbol.IsSealed`. So the sealed-stays-private rule has no instance in the repository output and
is covered by the generator unit suite instead, which asserts on a real Roslyn emit and would see
the CS0628 a regression would produce.

### Category 2 (765 lines)

Three added lines per root mode subject: the `[MethodImpl(MethodImplOptions.AggressiveInlining)]`
attribute, the `protected IReadOnlyDictionary<string, SubjectPropertyMetadata>? GetInstanceProperties() => _properties;`
member, and the blank line separating it from the next member.

### Category 3 (510 lines)

One removed and one added line per root mode subject, on the explicit
`IInterceptorSubject.Properties` implementation.

### Category 4 (510 lines)

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
member modifier at all. The only `new` keyword among changed lines is the `new KeyValuePair<...>`
expression inside the removed `AddProperties` body, which is object creation, not a modifier.

The count of `public new static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties`
goes from 14 to 17, and all three additions are in files that exist only on the branch (see below),
not in any pre-existing subject.

### Category 7 (0 lines)

No `new` modifier appears on any plumbing member. That shape needs a base type that already carries
a colliding member of its own (an older-generator base, or a hand-written member with the same
name), which no subject in this repository has. It is covered by the generator unit suite through
inline sources and by NI0013 and NI0014.

### Category 8 (0 lines)

No `RaisePropertyChanged` call moved to the interface form. No changed line mentions
`RaisePropertyChanged` at all. Exactly one subject in the repository emits
`((IRaisePropertyChanged)this).RaisePropertyChanged(...)`,
`Namotion.Interceptor.Tracking.Tests.Models.ManualInpcDerivedPerson`, and it already emitted that
form on the baseline. The line is byte-identical in both trees. The interface form fix therefore
changes no repository subject; it is exercised only by inline generator test sources.

## Files that exist only on the branch

Five generated files have no baseline counterpart. All five are the output for test fixture types
that this branch's own test sources add, not regenerations of anything that existed before.

- `Namotion.Interceptor.Generator.Tests.HierarchyRoot.g.cs`
- `Namotion.Interceptor.Generator.Tests.HierarchyMiddle.g.cs`
- `Namotion.Interceptor.Generator.Tests.HierarchyLeaf.g.cs`
- `Namotion.Interceptor.Generator.Tests.HierarchyChild.g.cs`
- `Namotion.Interceptor.Generator.Tests.HierarchyContextLeaf.g.cs`

No baseline file is missing from the branch output.

## Property sets

Two checks, because `.Concat(Base.DefaultProperties)` puts the base last and `ToFrozenDictionary` is
last wins, so a changed entry would not show up as a key difference.

Resolved metadata entry lines across every generated file:

```bash
find /tmp/ni-norm-base -name '*.g.cs' -print0 | xargs -0 grep -h '^\s*\["' | sort > /tmp/ni-keys-base.txt
find /tmp/ni-norm-head -name '*.g.cs' -print0 | xargs -0 grep -h '^\s*\["' | sort > /tmp/ni-keys-head.txt
diff /tmp/ni-keys-base.txt /tmp/ni-keys-head.txt
```

1188 entries at baseline, 1194 on the branch. The diff is six pure additions and nothing else:
`Child`, `ChildName`, `ContextLeafProperty`, `LeafProperty`, `MiddleProperty` and `RootProperty`,
all belonging to the five branch-only fixture files above. No entry was removed and none changed.

Whole `DefaultProperties` initializer blocks, compared per file rather than per key, so that a
changed metadata argument or a changed `.Concat` target would be caught even when the key text is
identical. The block is taken from the line containing `DefaultProperties { get; } =` through
`.ToFrozenDictionary();`:

- Shared subject files compared: 269
- `DefaultProperties` blocks that differ: 0
- Files whose `DefaultProperties` `.Concat` target changed: 0

## Verdict

Pass. Every one of the 4001 changed lines falls into exactly one expected category, with zero
unclassified lines and zero unclassified files. No property set changed for any pre-existing
subject. No defect found.
