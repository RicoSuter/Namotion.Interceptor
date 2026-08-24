# Post-Review Gaps Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the three defects left open after the single-context lifecycle review: DI-constructed subjects that silently never attach, a lock-order inversion from a late-registered lifecycle, and an admission path that bypasses the reconciler's ownership check.

**Architecture:** Three independent fixes sharing no code. The generator gains the declared constructor signatures in its metadata and mirrors each one with a trailing context parameter, which removes the detached-subject footgun instead of warning about it. The executor resolves its interceptor chain from the same context state its routing decision read, so the two can no longer disagree. Property admission applies the ownership check its non-null sibling already gets through the reconciler.

**Tech Stack:** C# 13 preview, .NET Standard 2.0 for the generator and core, .NET 9 for extensions, Roslyn incremental source generators, xUnit, `PublicApiGenerator` plus `Verify` for API snapshots.

**Spec:** `docs/superpowers/specs/2026-08-24-post-review-gaps-design.md`

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs` | turns syntax into `SubjectMetadata` | modify: capture declared constructor signatures |
| `src/Namotion.Interceptor.Generator/SubjectMetadata.cs` | the generator's per-type model | modify: carry those signatures |
| `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs` | emits the partial half | modify: `EmitConstructors` mirrors each one |
| `src/Namotion.Interceptor.Generator.Tests/` | generator behaviour tests | add: mirroring cases |
| `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs` | routing, attachment, terminals | modify: pin chain to routing snapshot |
| `src/Namotion.Interceptor.Tracking/Lifecycle/PropertyAdmission.cs` | atomic admission of dynamic properties | modify: ownership check on the null branch |
| `docs/design/tracking-lifecycle.md` | internal design doc | modify: record the retry progress properties |
| `docs/superpowers/specs/2026-08-21-single-context-lifecycle-simplification-design.md` | superseded decision 5 wording | modify |

---

## Task 1: Carry declared constructor signatures in generator metadata

The generator currently records only two booleans about constructors (`NeedsGeneratedParameterlessConstructor`, `HasOrWillHaveParameterlessConstructor`, produced by `DetectConstructorState` at `SubjectMetadataExtractor.cs:814`). Mirroring needs the signatures themselves. This task adds them and changes no emitted code, so the whole suite must stay green.

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadata.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`

- [ ] **Step 1: Add the model**

In `SubjectMetadata.cs`, add a record describing one constructor and a collection on the metadata. Use an equatable collection type: `SubjectMetadata` flows through an incremental generator pipeline, and a type without value equality defeats caching and re-runs generation on every keystroke.

```csharp
public sealed record SubjectConstructor(
    string Accessibility,
    EquatableArray<SubjectConstructorParameter> Parameters);

public sealed record SubjectConstructorParameter(
    string FullyQualifiedTypeName,
    string Name);
```

If the project has no `EquatableArray<T>` helper, check how existing collection members of `SubjectMetadata` achieve value equality and follow that pattern exactly rather than inventing a second one.

- [ ] **Step 2: Populate it**

In `SubjectMetadataExtractor.cs`, alongside `DetectConstructorState`, collect every `ConstructorDeclarationSyntax` across `allTypeDeclarations`, and for each resolve its parameters through the semantic model to fully qualified type names.

Use the semantic model, not the syntax text. A parameter written `List<Foo>` in a file with `using System.Collections.Generic;` must be emitted as `global::System.Collections.Generic.List<global::Namespace.Foo>`, because the generated partial half may not have that `using`. Use `ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)`.

Skip static constructors (they have no parameters and cannot be mirrored) and copy the declared accessibility verbatim.

- [ ] **Step 3: Build and run the full suite**

Run: `dotnet build src/Namotion.Interceptor.slnx -c Debug` then `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration" > /tmp/t1.log 2>&1; echo $?`
Then: `grep -oE "Total: +[0-9]+" /tmp/t1.log | awk '{s+=$2} END {print s}'` and `grep -E "Failed: +[1-9]" /tmp/t1.log`

Expected: 3,425 total, and no line from the second grep except the known flake `ChangeQueueProcessorTests.WhenTheTeardownWriteBlocks_ThenStopEndsAtTheConfiguredTimeout`, which passes in isolation. Nothing emitted changed, so any other failure means the metadata change broke pipeline caching or equality.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Generator/Models/SubjectMetadata.cs src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs
git commit -m "refactor: carry declared constructor signatures in generator metadata

Mirroring a constructor needs its parameters, and the metadata recorded only
whether a parameterless one existed. Types resolve through the semantic model
to fully qualified names, because the generated half may not share the
declaring file's usings."
```

---

## Task 2: Mirror each declared constructor with a context parameter

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs` (`EmitConstructors`)
- Create: `src/Namotion.Interceptor.Generator.Tests/ConstructorMirroringTests.cs`

- [ ] **Step 1: Write the failing tests**

Follow the existing conventions in `src/Namotion.Interceptor.Generator.Tests/`: read `GeneratedExecutorTests.cs` first and match how it compiles a subject and inspects the result. Write three cases:

1. A subject whose only constructor takes parameters, built through `ActivatorUtilities.CreateInstance` with the context registered as a service, is attached afterwards (`TryGetContext()` is not null).
2. A subject that already declares the mirrored signature by hand gets no duplicate emitted, so the code still compiles.
3. A subject with both a parameterless and a parameterized constructor gets a mirrored form of each.

Case 1 is the regression that broke HomeBlaze, so it is the one that must fail before the fix.

- [ ] **Step 2: Run to verify case 1 fails**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~ConstructorMirroringTests"`
Expected: case 1 FAILS, because no context-taking constructor exists for that type so the subject is never attached. Cases 2 and 3 may pass or fail depending on how you wrote them; only case 1's failure proves the bug.

- [ ] **Step 3: Emit the mirrors**

In `EmitConstructors`, after the existing parameterless block, emit one mirrored constructor per declared constructor:

```csharp
foreach (var constructor in metadata.Constructors)
{
    if (metadata.Constructors.Any(other => MirrorsSignatureOf(other, constructor)))
    {
        continue;
    }

    var parameters = string.Join(", ", constructor.Parameters.Select(p => $"{p.FullyQualifiedTypeName} {p.Name}"));
    var arguments = string.Join(", ", constructor.Parameters.Select(p => p.Name));

    builder.AppendLine($"        {constructor.Accessibility} {metadata.ClassName}({parameters}, IInterceptorSubjectContext context) : this({arguments})");
    builder.AppendLine("        {");
    // Provisional, matching the parameterless form: dependency injection now selects this
    // constructor for every subject it builds, and an explicit anchor would make each an
    // unreleasable root.
    builder.AppendLine("            InterceptorSubjectExtensions.AttachToContext(this, context, SubjectAnchorKind.Provisional);");
    builder.AppendLine("        }");
    builder.AppendLine();
}
```

**Parameter shapes the metadata does not capture, decided here so they are not discovered mid-implementation.** `SubjectConstructorParameter` carries only a fully qualified type name and a name.

- **Optional parameters lose their default on the mirror.** `Foo(A a, B b = null)` mirrors as `Foo(A a, B b, IInterceptorSubjectContext context)`. This is legal, creates no ambiguity, and dependency injection still satisfies the parameter from the container. It matters because the motivating case has one: `FluentStorageContainer` takes `ILogger<FluentStorageContainer>? logger = null`. If that logger type is not registered, `ActivatorUtilities` falls back to the original constructor and the subject is silently detached again, the very failure the mirror exists to prevent, so the mirror only helps when every parameter type is resolvable.
- **Skip any constructor with a `ref`, `out`, `in` or `params` parameter.** The metadata does not record those modifiers, so a mirror would either fail to compile or silently change the calling convention. Detect them during collection and omit the constructor from `Constructors` entirely, so `EmitConstructors` never sees it. No subject in this repository uses one.
- **Primary constructors are not covered.** They are not `ConstructorDeclarationSyntax`, so `CollectConstructors` does not see them and no mirror is emitted. No subject in this repository uses one today. Record this as a known limitation in the commit message rather than expanding scope.

While rewriting `EmitConstructors`, also subsume a pre-existing quirk if it is cheap: `DetectConstructorState` inspects only the FIRST declared constructor, so a type declaring `(int x)` before `()` is treated as having no parameterless constructor and gets no context constructor at all today. Mirroring every declared constructor makes that irrelevant for the mirrored forms, but check whether the parameterless path still misreads it.

`MirrorsSignatureOf(other, constructor)` returns true when `other` has exactly `constructor`'s parameters followed by one `IInterceptorSubjectContext`. Write it as a small private static helper comparing the fully qualified type name lists. This is what makes a hand-written context constructor win over a generated one.

Skip a constructor whose last parameter is already `IInterceptorSubjectContext`: mirroring it would produce a two-context signature that helps nobody.

- [ ] **Step 4: Run to verify all three pass**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter "FullyQualifiedName~ConstructorMirroringTests"`
Expected: PASS, 3 tests.

- [ ] **Step 5: Run the full suite and accept the API snapshots**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration" > /tmp/t2.log 2>&1; echo $?`

Expect `VerifyChecksTests.PublicApi` failures: every generated subject with a parameterized constructor gains a public constructor. **Inspect each diff before accepting it.** You are looking for exactly one added constructor per declared constructor, with the context parameter last. Anything else, such as a changed or removed constructor, means the emission is wrong, so stop and report rather than accepting.

Accept by copying each `.received.txt` over its `.verified.txt`, then re-run.

- [ ] **Step 6: Run HomeBlaze, which is where this bug came from**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests` (expect 221), `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests` (expect 50), `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests` (expect 23).

Run the E2E suite in the foreground and let it finish; do not background it. If it is slow, check for leftover browser or test-host processes first, since contention has made this suite take twenty times longer before.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs src/Namotion.Interceptor.Generator.Tests/ConstructorMirroringTests.cs src/*/VerifyChecksTests.PublicApi.verified.txt
git commit -m "feat: mirror declared constructors with a context parameter

A subject whose only constructor takes dependencies had no context-taking
constructor, so dependency injection produced a permanently detached subject
with no diagnostic: no lifecycle, no registry entry, no hosted services. That
is what broke HomeBlaze on startup. Mirroring is additive, existing call sites
are unaffected, and a hand-written context constructor still wins."
```

---

## Task 3: Pin the interceptor chain to the routing snapshot

**Files:**
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`

- [ ] **Step 1: Establish the current shape**

Read `SetStructuralPropertyValue` and the write path it calls. The routing decision reads attachment state lock-free and decides whether a lifecycle is involved; the write later resolves the interceptor chain from a fresh `Volatile.Read(ref _state)`. Record the two line numbers in your commit message, because they are the whole bug.

- [ ] **Step 2: Try to write a failing test, and say plainly if you cannot**

The window is between two reads on one thread, so hitting it deterministically needs a pause between them that production code does not provide. Attempt it. If it cannot be done without adding production hooks, **do not write a timing-dependent test**: say so in your report and pin the fix structurally instead, with a test asserting that the routing decision and the chain derive from the same state instance.

A timing-dependent test that passes by luck is worse than an honest structural one, and this repository has been burned by exactly that.

- [ ] **Step 3: Make the write use the routing snapshot**

Resolve the chain from the same `ContextState` the routing decision read, rather than re-reading `_state`. Thread the snapshot through the call rather than reading it twice.

Then correct the comment at the routing site, which currently claims a lifecycle registered after the resolution is not seen by this write. After this change that claim becomes true; today it is false, which is what made the inversion invisible.

- [ ] **Step 4: Verify**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration" > /tmp/t3.log 2>&1; echo $?`
Then: `grep -E "Failed: +[1-9]" /tmp/t3.log`

Expected: exit 0, no output beyond the known flake. Pay attention to `StructuralWriteLockOrderTests`, which drives the write path concurrently at 3,000 iterations and is the closest existing coverage.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs
git commit -m "fix: resolve the write chain from the routing snapshot

The routing decision read attachment state lock-free, and the write then
resolved the chain from a fresh read. A lifecycle registered between the two
landed in the chain but not the routing, so it took the lifecycle gate while
the attachment monitor was held and the gate was not, inverting the documented
total order."
```

---

## Task 4: Close the admission null-baseline hole, and correct the retry documentation

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/PropertyAdmission.cs` (the direct `SetBaseline(property, null)` at roughly line 76)
- Modify: `docs/design/tracking-lifecycle.md`
- Modify: `docs/superpowers/specs/2026-08-21-single-context-lifecycle-simplification-design.md`

- [ ] **Step 1: Write the failing test**

Add to `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/`, following the existing style. A subject with a structural property whose getter releases the admitting subject when invoked during `CaptureStructuralValues`, and whose captured value is null. Assert no baseline entry survives for the released subject, using the internal `Graph` accessor that `DownstreamWriteInterceptorReleaseTests` already uses.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~<your test name>"`
Expected: FAIL, an orphaned baseline entry survives for a subject the graph no longer owns.

If it passes before the fix, the hole is not reachable the way the spec describes. Stop and report rather than adding a check for a case that cannot happen.

- [ ] **Step 3: Add the check**

Guard the direct null-baseline write with the same condition the reconciler entry uses, so both admission branches behave alike:

```csharp
                if (value is null)
                {
                    // Same guard the reconciler applies at entry: a getter invoked during capture
                    // runs at callback depth zero and can release this subject, and a baseline
                    // written for a subject the graph no longer owns is never removed.
                    if (!graph.IsOwned(subject))
                    {
                        return;
                    }

                    graph.SetBaseline(property, null);
                }
```

- [ ] **Step 4: Run to verify it passes, then the full suite**

Run the single test, then: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration" > /tmp/t4.log 2>&1; echo $?` and `grep -E "Failed: +[1-9]" /tmp/t4.log`
Expected: exit 0, nothing beyond the known flake.

- [ ] **Step 5: Correct the retry documentation**

In `docs/superpowers/specs/2026-08-21-single-context-lifecycle-simplification-design.md`, decision 5 says "Ordering is preferred over retry, which can livelock under sustained attach churn." The shipped code retries. Correct the decision to match the code.

In `docs/design/tracking-lifecycle.md`, in the write protocol or lock ordering section, record the progress properties precisely. Keep each paragraph on one line, no em dashes:

```markdown
The structural write retries rather than orders. The loop is livelock-free: every retry is caused by another thread completing an attachment transition, and each transition requires the lifecycle gate, so a retry is evidence of progress elsewhere rather than of mutual blocking. It is not starvation-free, and no attempt bound is enforced: sustained attach and detach churn on one subject, concurrent with structural writes to that same subject, could in principle starve a writer. That is a pathological workload rather than a plausible one, and the lock-order tests drive the loop at 3,000 iterations without observing it. If it is ever observed, the mitigation is to order rather than retry, which is a rework of the write protocol and not a tuning change.
```

This stays in the internal design doc. It is not a consumer concern, it never surfaces in practice, and putting it in user documentation or release notes would alarm without informing.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Lifecycle/PropertyAdmission.cs \
        src/Namotion.Interceptor.Tracking.Tests/Lifecycle/ \
        docs/design/tracking-lifecycle.md \
        docs/superpowers/specs/2026-08-21-single-context-lifecycle-simplification-design.md
git commit -m "fix: guard the admission null-baseline write, and state the retry properties

The non-null admission branch goes through the reconciler and gets its
ownership check; the direct null write bypassed it, so a getter that releases
the admitting subject left a baseline entry nothing removes. Also corrects
decision 5, which claimed ordering was preferred over retry while the code
retries, and records that the loop is livelock-free but deliberately not
starvation-free."
```

---

## Definition of done

- A subject whose only constructor takes dependencies attaches when built through dependency injection with the context registered, proven by a test that failed before the change.
- A hand-written context constructor still wins over the generated mirror.
- Public API snapshots accepted with each diff inspected, showing only added constructors.
- The interceptor chain and the routing decision derive from one state read, and the comment at the routing site is true.
- No admission path writes a baseline for a subject the graph does not own.
- No document claims ordering is preferred over retry, and the internal design doc states what retry does and does not guarantee.
- Unit suite green at 3,425 plus the new tests, and HomeBlaze green at Services 221, Storage 50, E2E 23.
