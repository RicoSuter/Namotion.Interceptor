# Context Inheritance as an Owned Parent Link: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split `AddFallbackContext`'s three conflated jobs so it becomes a pure dependency-injection primitive, and make context inheritance an internal parent link owned by the lifecycle system.

**Architecture:** `AddFallbackContext` keeps its signature and loses its attach side effect. Root entry moves to two new core extension methods, `AttachToContext` and `DetachFromContext`. A child's inherited context becomes an internal `Parent` field on `ContextState`, published by `ContextInheritanceHandler` through an internal setter that runs no callbacks. `InterceptorExecutor` gains four fields (the attach context, the owning lifecycle graph, the interceptor set the attach resolved, and the reference count moved off `subject.Data`), all written under the existing `_mutationLock`. The executor's two method overrides become `protected virtual` guard hooks that the base calls from inside its own critical section, so a guard and the publish it protects are one critical section rather than check-then-act across a lock boundary.

**Tech Stack:** C# 13 preview, .NET Standard 2.0 (core), .NET 9.0 (extensions and tests), xUnit, Verify + PublicApiGenerator snapshots, BenchmarkDotNet.

**Source of truth:** `docs/superpowers/specs/2026-08-04-context-inheritance-design.md`. Read it before starting. Section numbers referenced below are that document's.

---

## Global Constraints

- **Branch:** all work lands on `design/context-inheritance-parent-link`. This plan ends at commits and pushes to that branch. Updating GitHub issues and merging PR #419 are **human-gated and out of scope** (spec section 10, "Final step, human gated").
- **No AI attribution** in commit messages, PR descriptions or GitHub comments: no agent names, no `Co-Authored-By` trailers, no "Generated with" footers.
- **No em dashes** (`—`) in any documentation, README or PR description. Restructure into plain sentences.
- **Comments are minimal.** Explain only what is not obvious from the code. Every comment in this plan's code blocks is load-bearing; do not add more.
- **Test naming:** `When<Condition>_Then<ExpectedBehavior>`.
- **Test structure:** explicit `// Arrange`, `// Act`, `// Assert` comments. Use `// Act & Assert` for exception tests.
- **No hardcoded waits.** No `Task.Delay`, no `Thread.Sleep`. Use `AsyncTestHelpers.WaitUntilAsync(() => condition)` from `Namotion.Interceptor.Testing`, or event-based synchronisation (`ManualResetEventSlim`, `CountdownEvent`, `Barrier`).
- **Core targets `netstandard2.0`** (`src/Namotion.Interceptor/Namotion.Interceptor.csproj:4`). No default interface implementations, no `record struct`, no C# 9+ runtime-dependent features in that project.
- **Warnings are errors** (`Directory.Build.props`), nullable enabled everywhere.
- **Default test command:** `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`.
- **Snapshot loop:** set `DiffEngine_Disabled=true` when running Verify tests so no diff tool launches.
- **Port 4840 must be free** before running `Namotion.Interceptor.OpcUa.Tests` with integration tests; it collides with a locally running Demo.Host.

---

## File Structure

### Created

| File | Responsibility |
|---|---|
| `src/Namotion.Interceptor/SubjectAttachmentExtensions.cs` | The four public attach/detach/query extensions plus the internal `GetExecutor` helper. Core, so the generated constructor and a core-only consumer both reach them. |
| `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/AttachOrderCharacterizationTests.cs` | Characterization tests 1, 2, 3, 6, 7 from spec section 9. |
| `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/InheritedContextResolutionTests.cs` | Characterization tests 5 and 8. |
| `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/AttachEdgeLeakTests.cs` | #207 reproductions, both paths, with the `_usedByContexts` probe. |
| `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RootAttachContractTests.cs` | #402 defects 2, 3, 4, 5, the two-graph rejection, re-attach during detach, and the new-API reproductions. |
| `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/KnownGapTests.cs` | Seven of the eight gap shapes in spec section 9, in eight tests because the dark-subject shape needs two. The eighth shape, `DetachFromContext` racing a property attach, is deliberately unwritten: see Task 4 step 4. |
| `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/UsedByContextsProbe.cs` | Reflection accessor for `InterceptorSubjectContext._usedByContexts`, in the style of `Namotion.Interceptor.Tests/Context/ContextStateReflection.cs`. |

### Modified

| File | Change |
|---|---|
| `src/Namotion.Interceptor/Namotion.Interceptor.csproj` | Add `InternalsVisibleTo` for `Namotion.Interceptor.Tracking.Tests`. |
| `src/Namotion.Interceptor/InterceptorSubjectContext.cs` | `ContextState.Parent`, the two guard hooks, the parent-link setters, conditional reverse-entry unregistration, `RemoveAttachEdge`, `_mutationLock` becomes `private protected`. |
| `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs` | Four fields, the guard hook overrides, the attach-record and ownership methods, the reference count. Both method overrides removed. |
| `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs` | Step 0 re-attach check, the ownership claim, the detach `finally`, two private-method renames. |
| `src/Namotion.Interceptor.Tracking/Lifecycle/ContextInheritanceHandler.cs` | Whole body: publishes the link, drives the descent. |
| `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptorExtensions.cs` | Reference count delegates to the executor. |
| `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs:246` | Emits `AttachToContext`. |
| 8 production call sites + subject-facing test call sites | Migrate to `AttachToContext` / `DetachFromContext`. |
| 16 generator `.verified.txt`, 2 `PublicApi.verified.txt`, 1 `InterceptorTests.*.verified.txt` | Accepted snapshot movements. |
| `docs/interceptor.md`, `docs/dynamic.md`, `docs/generator.md`, `docs/tracking.md`, `docs/design/tracking-lifecycle.md` | Consumer and design documentation. |

---

## Decisions surfaced while planning

Three items where the plan deviates from the spec, or resolves something the spec left open. **Read these before Task 1.** Each is implemented as described below; each also needs the spec updated in Task 5 so the two documents agree.

**1. The reference count's new home extends the executor requirement to the whole lifecycle path.** Spec section 7 scopes "requires an `InterceptorExecutor`" to the four new extensions. But moving the count off `subject.Data` means `LifecycleInterceptor.AttachToProperty` now needs an executor for any subject it attaches, where today a hand-written `IInterceptorSubject` supplying a plain `InterceptorSubjectContext.Create()` context works. Nothing in this repository does that (generated subjects and `DynamicSubject` both use an executor), so it is unreachable here, but it is a real behaviour change. **Implemented as:** the public `GetReferenceCount()` keeps returning `0` for a non-executor context, exactly as documented today; the internal increment and decrement throw a clear `InvalidOperationException` naming the requirement. **Add as behaviour change 18** to spec section 8.

**2. The #207 probe cannot live where the spec puts it.** Spec section 9 places it in `Namotion.Interceptor.Tests` because `Namotion.Interceptor.Tracking.Tests` has no `InternalsVisibleTo` grant. But `Namotion.Interceptor.Tests` has no project reference to `Namotion.Interceptor.Tracking` either (`Namotion.Interceptor.Tests.csproj:28-32`), so it cannot call `WithContextInheritance()`. Adding a Tracking reference to the core test project inverts the layering. **Implemented as:** one `InternalsVisibleTo Include="Namotion.Interceptor.Tracking.Tests"` line in core's csproj, and the probe lives in Tracking.Tests with everything else it needs. Correct the evidence row for change 3 in spec section 9.

**4. DECIDED, and flagged for re-review before merge.** A subject constructed with a plain context, `new Person(InterceptorSubjectContext.Create())`, has its generated constructor call `AttachToContext`, which recorded that plain context as the attach context even though it carries no lifecycle interceptor and there is no graph to join. That record then behaved as a claim: composing a tracking context onto the subject afterwards threw twice over, once from `AddFallbackContext` because the tracking context is lifecycle-bearing and is not the recorded attach context, and again from `AttachToContext` because a different context is already recorded. The escape was `DetachFromContext(plainContext)` first, which works but nobody would guess it.

Measured reachability: **zero occurrences in this repository.** All 75 plain-context constructions are terminal, and every compose-later site starts from the parameterless constructor, so no record exists. It is the shape an external consumer hits, not one this codebase does.

**Resolved as: `AttachToContext` skips the record entirely when `interceptors.IsEmpty`** (Task 3 step 7). With no interceptor there is nothing to detach from, so the call degenerates to exactly `AddFallbackContext`, which is what it truthfully is. Three reasons this is the coherent choice rather than the defensive one:

- It is the only option that makes the code agree with the spec. Section 7 already states that a subject holding nothing but an explicit fallback "has neither and reports false"; a plain-context-constructed subject holds exactly that, and recording would have made `IsAttached()` report true.
- It keeps `_attachContext` two-valued, which section 5 states explicitly and which every guard depends on by reading it as a simple identity test.
- It extends the design's one-name-one-meaning thesis to the degenerate case instead of carving an exception out of it.

The cost, stated rather than buried: `DetachFromContext(plainContext)` now returns false silently instead of removing that edge, so the constructor and the detach are not literal inverses in this one case. The inverse of composition is `RemoveFallbackContext`, which still works on it. **This asymmetry is the specific thing to re-review at the end** (Task 6 step 5), with the implemented code in hand, because it is the only part of this decision that a reader could reasonably want the other way.

Unchanged by this decision: `AddFallbackContext(trackingContext)` still throws for a recordless subject. That throw is the steering mechanism and stays. What changes is that following its advice now works on the first try.

**3. The owner claim and the count increment do not share a lock acquisition.** Spec section 4 says the shared monitor "lets the owner claim and the count increment share one acquisition". They cannot: the claim must precede `set.Add(property)` so a cross-graph rejection throws before any mutation, and the increment must follow it so a duplicate property add does not inflate the count. `set.Add`'s early return sits between them. **Implemented as:** two acquisitions of an uncontended `Monitor`. Correct the parenthetical in spec section 4 and the benchmark-gate rationale in section 10.

---

## Task 1: Characterization tests

Everything here must pass against **unmodified `master` production code**. That is the gate: a failure means the test is wrong, not the code. These tests pin the orderings and resolution facts that the whole change is required to leave untouched.

**Files:**
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/AttachOrderCharacterizationTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/InheritedContextResolutionTests.cs`
- Reference (already exists, do not modify): `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RecursiveAttachTests.cs:240` covers characterization test 4.

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: no production API. Later tasks rely on these tests continuing to pass unchanged after Task 3.

- [ ] **Step 1: Write the attach and detach sequence characterization test**

Create `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/AttachOrderCharacterizationTests.cs`:

```csharp
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Pins the traversal orders and the resolution facts that the context inheritance redesign is
/// required to leave bit-identical. Every test here must pass against unmodified master: a failure
/// means the test is wrong, not the production code.
/// </summary>
public class AttachOrderCharacterizationTests
{
    [Fact]
    public void WhenThreeLevelGraphIsAttached_ThenBothChannelsObserveTheDocumentedOrder()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;

        var events = new List<string>();
        lifecycleInterceptor.SubjectAttached += change => events.Add($"EVENT.attached({Name(change.Subject)})");
        lifecycleInterceptor.SubjectDetaching += change => events.Add($"EVENT.detaching({Name(change.Subject)})");

        var handlerLog = new List<string>();
        context.WithService(() => new RecordingLifecycleHandler(handlerLog));

        var m1 = new Person(context) { FirstName = "M1" };
        var m3 = new Person { FirstName = "M3" };
        var m2 = new Person { FirstName = "M2", Mother = m3 };

        // m1's own constructor attach already raised SubjectAttached, so the channels start dirty.
        events.Clear();
        handlerLog.Clear();

        // Act
        m1.Mother = m2;
        var attachEvents = events.ToArray();
        var attachHandlerLog = handlerLog.ToArray();

        events.Clear();
        handlerLog.Clear();
        m1.Mother = null;

        // Assert
        // The recording handler is registered after WithContextInheritance and carries no ordering
        // attribute, so it resolves BEHIND the inheritance handler. The inheritance handler's
        // descent therefore attaches M3 synchronously before the recorder ever sees M2, which is
        // the bottom-up order spec section 2 measured for an after-inheritance handler.
        Assert.Equal(["EVENT.attached(M3)", "EVENT.attached(M2)"], attachEvents);
        Assert.Equal(["handler.att(M3)", "handler.att(M2)"], attachHandlerLog);

        Assert.Equal(["EVENT.detaching(M2)", "EVENT.detaching(M3)"], events);
        Assert.Equal(["handler.det(M3)", "handler.det(M2)"], handlerLog);
    }

    [Fact]
    public void WhenRootIsAttachedWithChildren_ThenTheRootsOwnAttachFiresLast()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var attached = new List<string>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(Name(change.Subject));

        var child = new Person { FirstName = "Child" };
        var root = new Person { FirstName = "Root", Mother = child };

        // Act
        ((IInterceptorSubject)root).Context.AddFallbackContext(context);

        // Assert
        Assert.Equal(["Child", "Root"], attached);
    }

    [Fact]
    public void WhenRegistryIsRegisteredFirst_ThenItResolvesAheadOfContextInheritance()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithParents()
            .WithContextInheritance();

        // Act
        var handlers = context.GetServices<ILifecycleHandler>();

        // Assert
        Assert.Equal(
            ["SubjectRegistry", "ParentTrackingHandler", "ContextInheritanceHandler"],
            handlers.Select(handler => handler.GetType().Name).ToArray());
    }

    [Fact]
    public void WhenRegistryIsRegisteredLast_ThenItResolvesBehindContextInheritance()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithContextInheritance()
            .WithRegistry();

        // Act
        var handlers = context.GetServices<ILifecycleHandler>();

        // Assert
        Assert.Equal(
            ["ParentTrackingHandler", "ContextInheritanceHandler", "SubjectRegistry"],
            handlers.Select(handler => handler.GetType().Name).ToArray());
    }

    [Fact]
    public void WhenHandlerRunsBeforeContextInheritance_ThenTheChildResolvesNothingYet()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithParents()
            .WithContextInheritance();

        var observations = new List<(string name, int registries, int lifecycles)>();
        context.WithService(() => new ProbeAheadOfInheritance(observations));

        var root = new Person(context) { FirstName = "Root" };
        var grandchild = new Person { FirstName = "Grandchild" };
        var child = new Person { FirstName = "Child", Mother = grandchild };

        // Act
        root.Mother = child;

        // Assert
        Assert.All(observations, observation =>
        {
            Assert.Equal(0, observation.registries);
            Assert.Equal(0, observation.lifecycles);
        });
        Assert.Contains(observations, observation => observation.name == "Child");
        Assert.Contains(observations, observation => observation.name == "Grandchild");
    }

    [Fact]
    public void WhenSubjectHasTwoParents_ThenItAttachesOnceAndDetachesOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;

        // Subscribed after the parents exist: each constructor attach raises SubjectAttached, so
        // wiring the counters first would start them at two.
        var parent1 = new Person(context) { FirstName = "P1" };
        var parent2 = new Person(context) { FirstName = "P2" };
        var shared = new Person { FirstName = "Shared" };

        var attachCount = 0;
        var detachCount = 0;
        lifecycleInterceptor.SubjectAttached += _ => attachCount++;
        lifecycleInterceptor.SubjectDetaching += _ => detachCount++;

        // Act
        parent1.Mother = shared;
        parent2.Mother = shared;
        var attachesAfterBoth = attachCount;

        parent1.Mother = null;
        var detachesAfterFirstRemoval = detachCount;

        parent2.Mother = null;

        // Assert
        Assert.Equal(1, attachesAfterBoth);
        Assert.Equal(0, detachesAfterFirstRemoval);
        Assert.Equal(1, detachCount);
        Assert.Equal(0, shared.GetReferenceCount());
    }

    private static string Name(IInterceptorSubject subject)
    {
        return ((Person)subject).FirstName ?? "?";
    }

    private class RecordingLifecycleHandler(List<string> log) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.Property.HasValue)
            {
                return;
            }

            var prefix = change.IsPropertyReferenceAdded ? "att" : "det";
            log.Add($"handler.{prefix}({Name(change.Subject)})");
        }
    }

    [RunsBefore(typeof(ContextInheritanceHandler))]
    private class ProbeAheadOfInheritance(List<(string, int, int)> observations) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsPropertyReferenceAdded)
            {
                return;
            }

            observations.Add((
                Name(change.Subject),
                change.Subject.Context.GetServices<ISubjectRegistry>().Length,
                change.Subject.Context.GetServices<ILifecycleInterceptor>().Length));
        }
    }
}
```

- [ ] **Step 2: Run the sequence tests and confirm they pass on master's production code**

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~AttachOrderCharacterizationTests"
```

Expected: PASS, 6 tests. If any fails, the test encodes the wrong order. Read the actual order out of the failure message and correct the **test**, then record the correction in the commit message. Do not change production code in this task.

If `ProbeAheadOfInheritance` does not land ahead of `ContextInheritanceHandler`, check that `[RunsBefore]` is `Namotion.Interceptor.Attributes.RunsBeforeAttribute` and that the probe is registered via `WithService` after the inheritance handler exists.

- [ ] **Step 3: Write the inherited-resolution characterization tests**

Create `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/InheritedContextResolutionTests.cs`:

```csharp
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The audited external consumer's one hard requirement: a child's and a grandchild's own context
/// must resolve the graph's services, because a source constructor does
/// <c>subject.Context.TryGetLifecycleInterceptor() ?? throw</c>. Must pass against unmodified master.
/// </summary>
public class InheritedContextResolutionTests
{
    [Fact]
    public void WhenGraphHasSettled_ThenChildAndGrandchildContextsResolveTheGraphServices()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var root = new Person(context) { FirstName = "Root" };
        var grandchild = new Person { FirstName = "Grandchild" };
        var child = new Person { FirstName = "Child", Mother = grandchild };

        // Act
        root.Mother = child;

        // Assert
        Assert.NotNull(((IInterceptorSubject)child).Context.TryGetService<ISubjectRegistry>());
        Assert.NotNull(((IInterceptorSubject)child).Context.TryGetLifecycleInterceptor());
        Assert.NotNull(((IInterceptorSubject)grandchild).Context.TryGetService<ISubjectRegistry>());
        Assert.NotNull(((IInterceptorSubject)grandchild).Context.TryGetLifecycleInterceptor());
    }

    [Fact]
    public void WhenSubjectIsSeededLikeAConnectorItem_ThenItIsRegistryVisibleBeforeAssignment()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var parent = new Person(context) { FirstName = "Parent" };
        var item = new Person { FirstName = "Item" };

        // Act
        ((IInterceptorSubject)item).Context.AddFallbackContext(((IInterceptorSubject)parent).Context);
        var registeredBeforeAssignment = ((IInterceptorSubject)item).TryGetRegisteredSubject();

        parent.Mother = item;

        // Assert
        Assert.NotNull(registeredBeforeAssignment);
        Assert.NotNull(((IInterceptorSubject)item).TryGetRegisteredSubject());
        Assert.Equal(1, item.GetReferenceCount());
    }
}
```

- [ ] **Step 4: Run the resolution tests and confirm they pass**

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~InheritedContextResolutionTests"
```

Expected: PASS, 2 tests.

If `TryGetRegisteredSubject` is not found, check its namespace with `grep -rn "TryGetRegisteredSubject" src/Namotion.Interceptor.Registry --include=*.cs` and add the using.

- [ ] **Step 5: Run the whole non-integration suite to confirm nothing else moved**

```bash
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: PASS. New tests only, so no snapshot may move.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Tracking.Tests/Lifecycle/AttachOrderCharacterizationTests.cs \
        src/Namotion.Interceptor.Tracking.Tests/Lifecycle/InheritedContextResolutionTests.cs
git commit -m "Test: pin the traversal orders and inherited resolution the redesign must preserve

Characterization tests for spec section 9. All pass against master's production
code, which is the gate: a failure here means the test is wrong, not the code."
git push
```

---

## Task 2: Reproduction tests expressible against master's API

Everything here must **fail** against unmodified `master`, each for its issue's stated reason. That is the gate: a test that passes means the issue is already fixed or has been misunderstood.

Mark them `[Fact(Skip = ...)]`? **No.** They land red and Task 3 turns them green. Run them explicitly rather than as part of the suite until Task 3 lands, and note in the commit message that the branch is red between commits 2 and 3 by design.

**Files:**
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/UsedByContextsProbe.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/AttachEdgeLeakTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RootAttachContractTests.cs`
- Modify: `src/Namotion.Interceptor/Namotion.Interceptor.csproj`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `UsedByContextsProbe.Count(InterceptorSubjectContext)` returning `int`, used by Task 4's gap tests.

- [ ] **Step 1: Grant Tracking.Tests access to core internals**

Edit `src/Namotion.Interceptor/Namotion.Interceptor.csproj`, inside the existing `InternalsVisibleTo` `ItemGroup`:

```xml
    <ItemGroup>
        <InternalsVisibleTo Include="Namotion.Interceptor.Tests" />
        <InternalsVisibleTo Include="Namotion.Interceptor.Benchmark" />
        <InternalsVisibleTo Include="Namotion.Interceptor.Tracking" />
        <InternalsVisibleTo Include="Namotion.Interceptor.Tracking.Tests" />
        <InternalsVisibleTo Include="Namotion.Interceptor.Connectors.Tests" />
    </ItemGroup>
```

This is decision 2 from "Decisions surfaced while planning". The grant is **not** what lets the probe read `_usedByContexts`: reflection with `BindingFlags.NonPublic` reaches a private field regardless. It is needed for Task 4's two-edges test, which calls the internal `TrySetParentContext`. These tests live here rather than in `Namotion.Interceptor.Tests` because that project has no reference to `Namotion.Interceptor.Tracking` and so cannot reach `WithContextInheritance()`.

- [ ] **Step 2: Write the reverse-entry probe**

Create `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/UsedByContextsProbe.cs`:

```csharp
using System.Reflection;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Reads <c>InterceptorSubjectContext._usedByContexts</c>, the reverse registration set that #207
/// reports growing without bound. Private with no accessor, so the leak is only observable by
/// reflection. Modelled on Namotion.Interceptor.Tests/Context/ContextStateReflection.cs: the lookup
/// raises with the field name, so a rename fails with the field to fix rather than a null reference.
/// </summary>
internal static class UsedByContextsProbe
{
    private static readonly FieldInfo UsedByContextsField = typeof(InterceptorSubjectContext)
        .GetField("_usedByContexts", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("InterceptorSubjectContext._usedByContexts was renamed, the leak tests need updating.");

    /// <summary>Returns how many contexts are registered as resolving through the given context.</summary>
    internal static int Count(IInterceptorSubjectContext context)
    {
        var set = UsedByContextsField.GetValue(context);
        if (set is null)
        {
            return 0;
        }

        // IReadOnlyCollection, not the non-generic ICollection: HashSet<T> does not implement the
        // latter, so casting to it throws InvalidCastException on every call.
        lock (set)
        {
            return ((IReadOnlyCollection<InterceptorSubjectContext>)set).Count;
        }
    }
}
```

- [ ] **Step 3: Write the #207 reproduction tests**

Create `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/AttachEdgeLeakTests.cs`:

```csharp
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Issue #207, both reproductions. On master the reverse registration set of the root context grows
/// by one per cycle, which is the 8,558 entries the issue measured. The two paths diverge before
/// they converge, so each gets its own test: the first has a constructor and parent context
/// mismatch, the second has none and leaks purely through multi-parent removal order.
/// </summary>
public class AttachEdgeLeakTests
{
    [Fact]
    public void WhenConstructorAttachedChildIsAddedAndRemovedRepeatedly_ThenTheRootContextDoesNotAccumulateEntries()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var parent = new Person(rootContext) { FirstName = "Parent" };
        var baseline = UsedByContextsProbe.Count(rootContext);

        // Act
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var child = new Person(rootContext) { FirstName = "Child" };
            parent.Children = [child];
            parent.Children = [];
        }

        // Assert
        Assert.Equal(baseline, UsedByContextsProbe.Count(rootContext));
    }

    [Fact]
    public void WhenSharedChildIsRemovedFromItsParentsInOrder_ThenTheFirstParentContextDoesNotAccumulateEntries()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var parent1 = new Person(context) { FirstName = "P1" };
        var parent2 = new Person(context) { FirstName = "P2" };
        var parent1Context = ((IInterceptorSubject)parent1).Context;
        var baseline = UsedByContextsProbe.Count(parent1Context);

        // Act
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var child = new Person { FirstName = "Child" };
            parent1.Children = [child];
            parent2.Children = [child];
            parent1.Children = [];
            parent2.Children = [];
        }

        // Assert
        Assert.Equal(baseline, UsedByContextsProbe.Count(parent1Context));
    }

    [Fact]
    public void WhenConstructorAttachedChildIsFullyDetached_ThenItStopsResolvingInterceptors()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parent = new Person(rootContext) { FirstName = "Parent" };
        var child = new Person(rootContext) { FirstName = "Child" };

        // Act
        parent.Children = [child];
        parent.Children = [];

        // Assert
        Assert.Empty(((IInterceptorSubject)child).Context.GetServices<IWriteInterceptor>());
    }
}
```

- [ ] **Step 4: Run the #207 reproductions and confirm they fail on master's production code**

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~AttachEdgeLeakTests"
```

Expected: **FAIL**, 3 of 3.
- The first two fail with the count having grown by 3 over the baseline.
- The third fails because the constructor-attached child keeps its constructor edge and still resolves 4 write interceptors, which is #207's leak shape.

If any passes, stop and investigate before continuing: either the issue is already fixed or the reproduction does not express it.

- [ ] **Step 5: Write the root-attach contract reproductions**

Create `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RootAttachContractTests.cs`:

```csharp
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Issues #402 defects 3, 4 and 5, the two-graph half-attach from spec section 2, and re-attach
/// during detach. Each must fail against unmodified master for the reason named in its comment.
/// Defect 2 (concurrent detach running the interceptors twice) needs the new API and lives in
/// Task 4.
/// </summary>
public class RootAttachContractTests
{
    [Fact]
    public void WhenAttachResolveThrows_ThenNoEdgeIsPublished()
    {
        // #402 defect 4: master publishes the edge and resolves after, so a failing resolve leaves
        // the edge registered with no attach callback having run.
        //
        // Only a PURE DELEGATION cycle raises. A context with two fallbacks has no delegation
        // target at all, so the ordinary service walk tolerates the loop through its visited set
        // and returns normally. Both loop contexts therefore carry no service and exactly one
        // fallback each.

        // Arrange
        var loopA = InterceptorSubjectContext.Create();
        var loopB = InterceptorSubjectContext.Create();
        loopA.AddFallbackContext(loopB);
        loopB.AddFallbackContext(loopA);

        var subject = new Person { FirstName = "Subject" };
        var baseline = UsedByContextsProbe.Count(loopA);

        // Act
        try
        {
            ((IInterceptorSubject)subject).Context.AddFallbackContext(loopA);
        }
        catch (InvalidOperationException)
        {
            // Expected once the resolve precedes the publish.
        }

        // Assert
        Assert.Equal(baseline, UsedByContextsProbe.Count(loopA));
    }

    [Fact]
    public void WhenChainTurnedCyclicAfterAttach_ThenDetachStillRemovesTheEdge()
    {
        // #402 defects 3 and 5: master re-resolves at detach, so a chain that has since turned
        // cyclic raises before the edge is removed, and no other route can then remove it.
        //
        // The cycle has to be built by REWIRING an existing pure-delegation chain, because a
        // context cannot be given a second fallback without ceasing to be a pure delegator.

        // Arrange
        var graphContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var midContext = InterceptorSubjectContext.Create();
        midContext.AddFallbackContext(graphContext);

        var attachContext = InterceptorSubjectContext.Create();
        attachContext.AddFallbackContext(midContext);

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).Context.AddFallbackContext(attachContext);

        var attachedCount = UsedByContextsProbe.Count(attachContext);

        // attachContext -> midContext -> attachContext, both pure delegators, so resolving raises.
        midContext.RemoveFallbackContext(graphContext);
        midContext.AddFallbackContext(attachContext);

        // Act: the detach raises either way, because the descent resolves handlers through the
        // subject's own now-cyclic chain. What this pins is that the edge comes out regardless.
        Assert.ThrowsAny<InvalidOperationException>(
            () => ((IInterceptorSubject)subject).Context.RemoveFallbackContext(attachContext));

        // Assert
        Assert.Equal(attachedCount - 1, UsedByContextsProbe.Count(attachContext));
    }

    [Fact]
    public void WhenSubjectOwnedByOneGraphIsAttachedToAnother_ThenItThrowsAndPublishesNothing()
    {
        // Spec section 2: on master both registries index the subject and only graph A resolves,
        // so graph B holds a subject it can enumerate and never hears from.

        // Arrange
        var contextA = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var contextB = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var parentA = new Person(contextA) { FirstName = "ParentA" };
        var shared = new Person { FirstName = "Shared" };
        parentA.Mother = shared;

        var parentB = new Person(contextB) { FirstName = "ParentB" };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => parentB.Mother = shared);

        var registryB = contextB.TryGetService<ISubjectRegistry>()!;
        Assert.DoesNotContain(shared, registryB.KnownSubjects.Keys);
    }

    [Fact]
    public void WhenRootAttachedSubjectIsReferencedFromAnotherGraph_ThenItThrowsAndPublishesNothing()
    {
        // Spec section 9 lists TWO shapes for change 5 and this is the second: root in A, then
        // child in B. It is the shape that catches a root attach which records the attach context
        // without claiming ownership, because the parent-to-parent shape above claims ownership on
        // the property path and passes either way.

        // Arrange
        var contextA = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var contextB = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).Context.AddFallbackContext(contextA);

        var parentB = new Person(contextB) { FirstName = "ParentB" };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => parentB.Mother = subject);

        var registryB = contextB.TryGetService<ISubjectRegistry>()!;
        Assert.DoesNotContain(subject, registryB.KnownSubjects.Keys);
        Assert.Equal(0, subject.GetReferenceCount());
    }


    [Fact]
    public void WhenHandlerReAttachesSubjectDuringItsOwnDetach_ThenItThrows()
    {
        // Behaviour change 17: under the redesign the same action would form an unrecoverable
        // parent-only cycle, so it fails fast. The handler swallows the throw so the outer detach
        // still completes, which is what lets the assertions run.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var parent = new Person(context) { FirstName = "Parent" };
        var reAttachTarget = new Person(context) { FirstName = "Target" };
        var caught = null as Exception;

        context.WithService(() => new ReAttachingHandler(reAttachTarget, exception => caught = exception));

        var child = new Person { FirstName = "Child" };
        parent.Mother = child;

        // Act
        parent.Mother = null;

        // Assert
        Assert.IsType<InvalidOperationException>(caught);
        Assert.Equal(0, child.GetReferenceCount());
    }

    private class ReAttachingHandler(Person target, Action<Exception> onThrow) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsPropertyReferenceRemoved || change.ReferenceCount != 0)
            {
                return;
            }

            try
            {
                target.Father = (Person)change.Subject;
            }
            catch (Exception exception)
            {
                onThrow(exception);
            }
        }
    }
}
```

- [ ] **Step 6: Run the contract reproductions and confirm they fail on master's production code**

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~RootAttachContractTests"
```

Expected: **FAIL**, 5 of 5, and the project must still **compile**. Every test here is written against `master`'s API on purpose, so that commit 2 builds and fails only on assertions. Task 3 step 11d migrates them to the new API along with every other subject-facing call site.

Each must fail for its own reason. Confirm each individually rather than trusting the count:
- `WhenAttachResolveThrows_...` fails because master publishes the edge before resolving, so `loopA`'s reverse-entry count grew by one.
- `WhenChainTurnedCyclicAfterAttach_...` fails because master's `RemoveFallbackContext` re-resolves and raises before removing, so the count did not drop.
- `WhenSubjectOwnedByOneGraphIsAttachedToAnother_...` fails because no exception is thrown; registry B does contain the subject.
- `WhenRootAttachedSubjectIsReferencedFromAnotherGraph_...` fails for the same reason on the other shape: no exception, and the subject picks up a second graph's reference.
- `WhenHandlerReAttachesSubjectDuringItsOwnDetach_...` fails because `caught` is null; master accepts the re-attach.

If any fails with an exception type other than the assertion failure, the arrange is wrong, not the production code. Record all four observations in the commit message.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor/Namotion.Interceptor.csproj \
        src/Namotion.Interceptor.Tracking.Tests/Lifecycle/UsedByContextsProbe.cs \
        src/Namotion.Interceptor.Tracking.Tests/Lifecycle/AttachEdgeLeakTests.cs \
        src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RootAttachContractTests.cs
git commit -m "Test: reproduce #207 and #402 defects 3, 4 and 5 against the current design

Red by design: these fail against master's production code, each for its issue's
stated reason, and the next commit turns them green. Every test is written against
master's API so this commit still builds; the migration to AttachToContext and
DetachFromContext happens with every other call site in the next commit.

Grants Namotion.Interceptor.Tracking.Tests access to core internals, which the
two-edges-to-one-target test needs to reach the parent link setter. The core test
project has no Tracking reference, so it cannot host these."
git push
```

---

## Task 3: The production change

**This commit cannot be split.** Spec section 10 records the five orderings that were tried and the window each leaves. Steps 1 through 11 below all land in one commit; only step 12 runs the full gate. Intermediate steps compile where noted and do not otherwise.

**Files:**
- Modify: `src/Namotion.Interceptor/InterceptorSubjectContext.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Create: `src/Namotion.Interceptor/SubjectAttachmentExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptorExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/ContextInheritanceHandler.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`
- Modify: 8 production call sites and the subject-facing test call sites
- Modify: 19 `.verified.txt` snapshots

**Interfaces:**

Produced, public (core, `namespace Namotion.Interceptor`, class `SubjectAttachmentExtensions`):
```csharp
public static void AttachToContext(this IInterceptorSubject subject, IInterceptorSubjectContext context);
public static void DetachFromContext(this IInterceptorSubject subject, IInterceptorSubjectContext context);
public static bool IsAttached(this IInterceptorSubject subject);
public static IInterceptorSubjectContext? TryGetAttachContext(this IInterceptorSubject subject);
```

Produced, protected (core, `InterceptorSubjectContext`):
```csharp
protected virtual void OnAddingFallbackContext(IInterceptorSubjectContext context);
protected virtual void OnRemovingFallbackContext(IInterceptorSubjectContext context);
```

Produced, internal (core), consumed by `Namotion.Interceptor.Tracking`:
```csharp
// InterceptorSubjectContext
internal bool TrySetParentContext(IInterceptorSubjectContext parent);
internal bool TryClearParentContext();
internal bool HasParentContext { get; }
internal bool RemoveAttachEdge(IInterceptorSubjectContext context);

// InterceptorExecutor
internal bool TryRecordAttachContext(IInterceptorSubjectContext context, ImmutableArray<ILifecycleInterceptor> interceptors);
internal bool TryClearAttachContext(IInterceptorSubjectContext context, out ImmutableArray<ILifecycleInterceptor> interceptors);
internal void ClearAttachContext(IInterceptorSubjectContext context);
internal void ReleaseAttachEdge();
internal void ClaimOwnership(ILifecycleInterceptor owner);
internal void ReleaseOwnership(ILifecycleInterceptor owner);
internal IInterceptorSubjectContext? AttachContext { get; }
internal bool IsAttachedCore { get; }
internal int ReferenceCount { get; }
internal int IncrementReferenceCount();
internal int DecrementReferenceCount();

// SubjectAttachmentExtensions
internal static InterceptorExecutor GetExecutor(this IInterceptorSubject subject);
```

- [ ] **Step 1: Add the parent link to `ContextState`**

In `src/Namotion.Interceptor/InterceptorSubjectContext.cs`, replace four things inside the nested `ContextState` class, **by member and not by line range**: the `Services` / `FallbackContexts` / `DelegationTarget` field block (`:945-950`), the constructor (`:968-973`), `IsEmpty` (`:975`) and `WithoutCaches` (`:1000-1003`).

Do **not** replace the whole span `:945-1003`. It also contains the five cache and terminal fields and the three members `ResolvedTerminal`, `SetResolvedTerminalIfAbsent` and `MethodInvocationFunction`, which the snippets below do not restate and which `GetServiceCache`, `ResolveDelegationTarget` and the compiled-chain accessors all depend on.

```csharp
        internal readonly ImmutableArray<object> Services;
        internal readonly ImmutableArray<InterceptorSubjectContext> FallbackContexts;

        // The inherited context of a subject held by a parent property. A separate slot rather than
        // an entry in FallbackContexts because it is owned by the lifecycle system and must not be
        // reachable from RemoveFallbackContext. Resolution visits it last, so explicit composition
        // beats inheritance.
        internal readonly InterceptorSubjectContext? Parent;

        internal readonly InterceptorSubjectContext? DelegationTarget;
```

```csharp
        internal ContextState(
            ImmutableArray<object> services,
            ImmutableArray<InterceptorSubjectContext> fallbackContexts,
            InterceptorSubjectContext? parent)
        {
            Services = services;
            FallbackContexts = fallbackContexts;
            Parent = parent;

            // A context with no own service and exactly one outgoing edge resolves everything
            // through it, whichever kind that edge is. The parent-only case is the dominant
            // topology after this change, so it has to qualify or every child pays a full walk.
            DelegationTarget = services.IsEmpty
                ? fallbackContexts.Length == 1 && parent is null ? fallbackContexts[0]
                : fallbackContexts.IsEmpty && parent is not null ? parent
                : null
                : null;
        }

        // Parent counts: without it a parent-only state would be "empty" and resolve nothing,
        // and today that only works by accident because such a state has a DelegationTarget.
        internal bool IsEmpty => Services.IsEmpty && FallbackContexts.IsEmpty && Parent is null;
```

```csharp
        internal ContextState WithoutCaches()
        {
            return new ContextState(Services, FallbackContexts, Parent);
        }
```

- [ ] **Step 2: Teach the service walk and the initial state about the parent**

Still in `InterceptorSubjectContext.cs`:

Update the field initialiser at `:71`:

```csharp
    private ContextState _state = new(ImmutableArray<object>.Empty, ImmutableArray<InterceptorSubjectContext>.Empty, null);
```

Update `CollectServices`'s inner fallback loop to walk the parent after the fallbacks. The replaced region starts at `var entered = false;` (`:626`) and ends at the loop's closing brace (`:640`); starting at `:627` instead leaves a duplicate `entered` local behind.

```csharp
            var entered = false;
            var edgeCount = fallbackContexts.Length + (frame.State.Parent is not null ? 1 : 0);
            while (frame.NextFallbackIndex < edgeCount)
            {
                var edgeIndex = frame.NextFallbackIndex++;
                var nextContext = edgeIndex < fallbackContexts.Length
                    ? fallbackContexts[edgeIndex]
                    : frame.State.Parent!;

                if (!TryEnterContext(nextContext, Volatile.Read(ref nextContext._state), visited, out var nextState))
                {
                    continue;
                }

                // The advanced cursor has to survive the push, the frame is a struct.
                frames[frameIndex] = frame;
                PushFrame(frames, collected, type, nextState);
                entered = true;
                break;
            }
```

`TryEnterContext` and `ResolveDelegationChain` need no change: both follow `DelegationTarget`, which now covers the parent-only case.

- [ ] **Step 3: Add the reverse-entry helpers and make unregistration conditional**

Still in `InterceptorSubjectContext.cs`, add these private members next to `GetOrCreateUsedByContexts` (around `:810`):

```csharp
    /// <summary>
    /// R4: register into the target BEFORE publishing, so its using set is always a superset of the
    /// true using set. A missing entry leaves a compiled chain above permanently stale. An extra
    /// entry costs a spurious invalidation and lets the invalidation walk arrive out of chain order,
    /// which is why no walk may trust what a context further down recorded.
    /// </summary>
    private void RegisterUsedBy(InterceptorSubjectContext target)
    {
        var usedByContexts = target.GetOrCreateUsedByContexts();
        lock (usedByContexts)
        {
            usedByContexts.Add(this);
        }
    }

    /// <summary>
    /// Drops the reverse entry only when no remaining edge of this context targets
    /// <paramref name="target"/>. Two edge kinds make two edges to one target possible, and
    /// unregistering unconditionally would unregister the sole reverse entry while the other edge
    /// still resolves through it, so invalidation would never reach this context again and its
    /// compiled chain would silently keep an interceptor set the graph no longer has. That is
    /// #400's defect 6, unreachable on master where one edge kind plus dedup rules it out.
    /// </summary>
    private void UnregisterUsedByIfUnreferenced(ContextState state, InterceptorSubjectContext target)
    {
        if (state.FallbackContexts.Contains(target) || ReferenceEquals(state.Parent, target))
        {
            return;
        }

        var usedByContexts = Volatile.Read(ref target._usedByContexts);
        if (usedByContexts is null)
        {
            return;
        }

        lock (usedByContexts)
        {
            usedByContexts.Remove(this);
        }
    }
```

- [ ] **Step 4: Add the guard hooks and rewrite the two fallback mutators**

Still in `InterceptorSubjectContext.cs`. First make the lock reachable from the executor, at `:74`:

```csharp
    // Serializes mutators; never held on a query path. Reachable from InterceptorExecutor so that a
    // guard and the publish it protects are one critical section.
    private protected readonly object _mutationLock = new();
```

Replace `AddFallbackContext` (`:118-146`) and `RemoveFallbackContext` (`:154-184`) with:

```csharp
    /// <summary>
    /// Called inside the mutation critical section before a fallback edge is added, so a subclass
    /// can reject the call while holding the same lock that publishes the edge. Reading the state,
    /// releasing the lock and then calling the base would be check-then-act across a lock boundary.
    /// </summary>
    protected virtual void OnAddingFallbackContext(IInterceptorSubjectContext context)
    {
    }

    /// <summary>
    /// Called inside the mutation critical section before a fallback edge is removed. See
    /// <see cref="OnAddingFallbackContext"/>.
    /// </summary>
    protected virtual void OnRemovingFallbackContext(IInterceptorSubjectContext context)
    {
    }

    public virtual bool AddFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;

        lock (_mutationLock)
        {
            OnAddingFallbackContext(context);

            var state = Volatile.Read(ref _state);
            if (state.FallbackContexts.Contains(contextImpl))
            {
                return false;
            }

            RegisterUsedBy(contextImpl);
            PublishState(new ContextState(state.Services, state.FallbackContexts.Add(contextImpl), state.Parent));
        }

        InvalidateUsingContexts();
        return true;
    }

    public virtual bool RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        return RemoveFallbackContextCore(context, runGuard: true);
    }

    /// <summary>
    /// Removes an edge the library owns, bypassing the guard that rejects a consumer removing the
    /// attach edge. The public guard cannot distinguish the library's own cleanup from a consumer's
    /// call, and either answer would be wrong for the other.
    /// </summary>
    internal bool RemoveAttachEdge(IInterceptorSubjectContext context)
    {
        return RemoveFallbackContextCore(context, runGuard: false);
    }

    private bool RemoveFallbackContextCore(IInterceptorSubjectContext context, bool runGuard)
    {
        var contextImpl = (InterceptorSubjectContext)context;

        lock (_mutationLock)
        {
            if (runGuard)
            {
                OnRemovingFallbackContext(context);
            }

            var state = Volatile.Read(ref _state);
            var index = state.FallbackContexts.IndexOf(contextImpl);
            if (index < 0)
            {
                return false;
            }

            var newState = new ContextState(state.Services, state.FallbackContexts.RemoveAt(index), state.Parent);
            PublishState(newState);

            // R4: unregister only AFTER publishing so the using set stays a superset for the whole
            // transition, and only when no remaining edge targets the same context.
            UnregisterUsedByIfUnreferenced(newState, contextImpl);
        }

        InvalidateUsingContexts();
        return true;
    }
```

Update the two remaining `new ContextState(...)` calls in `TryAddService` (`:205`) and `AddService` (`:217`) to pass `state.Parent` as the third argument.

- [ ] **Step 5: Add the parent-link setters**

Still in `InterceptorSubjectContext.cs`, after `RemoveFallbackContextCore`:

```csharp
    /// <summary>Whether this context currently inherits from a parent subject's context.</summary>
    internal bool HasParentContext => Volatile.Read(ref _state).Parent is not null;

    /// <summary>
    /// Publishes the inherited parent context. The single write site is
    /// <c>ContextInheritanceHandler</c> at reference count one, and the cycle argument in
    /// docs/superpowers/specs/2026-08-04-context-inheritance-design.md section 4 depends on it
    /// staying single and on three guards holding together: the attach edge surviving while the
    /// subject is referenced, RemoveFallbackContext rejecting the attach edge, and
    /// DetachFromContext rejecting a non-zero reference count. Relax any one and a root can become
    /// a pure delegator while holding a link, at which point this becomes cycle-capable.
    /// </summary>
    internal bool TrySetParentContext(IInterceptorSubjectContext parent)
    {
        var parentImpl = (InterceptorSubjectContext)parent;

        lock (_mutationLock)
        {
            var state = Volatile.Read(ref _state);
            if (state.Parent is not null)
            {
                return false;
            }

            RegisterUsedBy(parentImpl);
            PublishState(new ContextState(state.Services, state.FallbackContexts, parentImpl));
        }

        InvalidateUsingContexts();
        return true;
    }

    internal bool TryClearParentContext()
    {
        lock (_mutationLock)
        {
            var state = Volatile.Read(ref _state);
            var parent = state.Parent;
            if (parent is null)
            {
                return false;
            }

            var newState = new ContextState(state.Services, state.FallbackContexts, null);
            PublishState(newState);
            UnregisterUsedByIfUnreferenced(newState, parent);
        }

        InvalidateUsingContexts();
        return true;
    }
```

**Checkpoint:** `dotnet build src/Namotion.Interceptor/Namotion.Interceptor.csproj` must succeed. The rest of the solution will not build until step 9.

- [ ] **Step 6: Rewrite `InterceptorExecutor`**

Replace `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`'s two method overrides (`:58-89`) with the fields, the guard hooks and the record methods. The file's head (usings, class declaration, `_subject`, constructor, the four intercepted-access methods at `:14-56`) is unchanged except for adding `using System.Collections.Immutable;`.

```csharp
    // Per-subject lifecycle position, all written under the base's _mutationLock. On the executor
    // rather than in subject.Data because each Data entry costs a ConcurrentDictionary node of
    // roughly 50 to 60 bytes per attached subject, and because the guard that reads _attachContext
    // lives in AddFallbackContext, a method on the context, so a subject-side record would mean a
    // cross-object lookup on a path that is otherwise a field read. Not on the base class because
    // both guards are executor-only semantics: on a plain context, adding a lifecycle-bearing
    // context is exactly how services are composed and must not throw.
    private IInterceptorSubjectContext? _attachContext;
    private ILifecycleInterceptor? _owner;
    private ImmutableArray<ILifecycleInterceptor> _attachInterceptors = ImmutableArray<ILifecycleInterceptor>.Empty;
    private int _referenceCount;

    internal IInterceptorSubjectContext? AttachContext => Volatile.Read(ref _attachContext);

    /// <summary>
    /// Neither half is sufficient alone: a property-attached child has no record but is owned, and
    /// a subject root-attached through a core-only custom ILifecycleInterceptor has a record but no
    /// owner, because only Tracking's LifecycleInterceptor claims ownership.
    /// </summary>
    internal bool IsAttachedCore => Volatile.Read(ref _owner) is not null || Volatile.Read(ref _attachContext) is not null;

    /// <summary>
    /// A snapshot, as the public GetReferenceCount() has always been. Volatile because a plain
    /// field does not supply the ordering the ConcurrentDictionary this replaced supplied for free,
    /// and DetachFromContext's guard reads it, where a stale zero would admit exactly the detach the
    /// guard exists to reject.
    /// </summary>
    internal int ReferenceCount => Volatile.Read(ref _referenceCount);

    protected override void OnAddingFallbackContext(IInterceptorSubjectContext context)
    {
        // The predicate is "not the recorded attach context", not "no record": testing only for a
        // non-null record would accept AttachToContext(A) followed by AddFallbackContext(B) where B
        // carries a different lifecycle interceptor, and the subject would then resolve graph B's
        // interceptors while being absent from B's ledger and registry.
        if (ReferenceEquals(context, Volatile.Read(ref _attachContext)))
        {
            return;
        }

        if (!context.GetServices<ILifecycleInterceptor>().IsEmpty)
        {
            throw new InvalidOperationException(
                $"The context being added to subject '{_subject.GetType().FullName}' takes part in a lifecycle graph, " +
                "so adding it as a plain fallback context would leave the subject resolving that graph's interceptors " +
                $"while absent from its registry. Call {nameof(SubjectAttachmentExtensions.AttachToContext)} instead, " +
                "which publishes the edge and runs the attach callbacks together.");
        }
    }

    protected override void OnRemovingFallbackContext(IInterceptorSubjectContext context)
    {
        if (ReferenceEquals(context, Volatile.Read(ref _attachContext)))
        {
            throw new InvalidOperationException(
                $"The context being removed from subject '{_subject.GetType().FullName}' is the context it was attached " +
                $"through, so removing it here would strand the subject in its lifecycle graph. Call " +
                $"{nameof(SubjectAttachmentExtensions.DetachFromContext)} instead, which runs the detach callbacks and " +
                "then removes the edge.");
        }
    }

    /// <summary>
    /// Records the attach before the edge is published, so the guard above sees a record naming this
    /// context by the time AttachToContext's own AddFallbackContext arrives. Returns false when the
    /// record already names this context, so a repeated attach is a no-op rather than a second pass.
    /// </summary>
    internal bool TryRecordAttachContext(IInterceptorSubjectContext context, ImmutableArray<ILifecycleInterceptor> interceptors)
    {
        lock (_mutationLock)
        {
            if (ReferenceEquals(_attachContext, context))
            {
                return false;
            }

            if (_attachContext is not null)
            {
                throw new InvalidOperationException(
                    $"Subject '{_subject.GetType().FullName}' is already attached through a different context. Detach it " +
                    $"with {nameof(SubjectAttachmentExtensions.DetachFromContext)} before attaching it elsewhere.");
            }

            // A check rather than a claim, so it races a concurrent claim. It is what makes the
            // deterministic misuse case publish nothing: without it, root-attaching a
            // property-owned subject into a second graph would set the record and the edge and only
            // then be rejected.
            if (_owner is not null && !interceptors.Contains(_owner))
            {
                throw new InvalidOperationException(
                    $"Subject '{_subject.GetType().FullName}' already belongs to another lifecycle graph. A subject belongs " +
                    "to at most one graph; remove it from its current graph before attaching it to this one.");
            }

            _attachContext = context;
            _attachInterceptors = interceptors;
            return true;
        }
    }

    /// <summary>
    /// Clears the record and returns the interceptor set the attach resolved, from inside the same
    /// critical section that picks the winner. Two concurrent detaches both take the lock; one finds
    /// the record and proceeds, the other finds null and returns having called nothing. Reading the
    /// set before this call would be check-then-act across a lock boundary and could enumerate a
    /// default ImmutableArray.
    /// </summary>
    internal bool TryClearAttachContext(IInterceptorSubjectContext context, out ImmutableArray<ILifecycleInterceptor> interceptors)
    {
        lock (_mutationLock)
        {
            if (_attachContext is null)
            {
                interceptors = ImmutableArray<ILifecycleInterceptor>.Empty;
                return false;
            }

            if (!ReferenceEquals(_attachContext, context))
            {
                throw new InvalidOperationException(
                    $"Subject '{_subject.GetType().FullName}' was not attached through the given context, so detaching it " +
                    "from that context would do nothing. Pass the context it was attached through, which " +
                    $"{nameof(SubjectAttachmentExtensions.TryGetAttachContext)} returns.");
            }

            interceptors = _attachInterceptors;
            _attachContext = null;
            _attachInterceptors = ImmutableArray<ILifecycleInterceptor>.Empty;
            return true;
        }
    }

    /// <summary>Rolls back a failed attach: clears the record and removes the edge it published.</summary>
    internal void ClearAttachContext(IInterceptorSubjectContext context)
    {
        lock (_mutationLock)
        {
            if (!ReferenceEquals(_attachContext, context))
            {
                return;
            }

            _attachContext = null;
            _attachInterceptors = ImmutableArray<ILifecycleInterceptor>.Empty;
        }

        RemoveAttachEdge(context);
    }

    /// <summary>
    /// Releases whatever attach edge the subject holds, silently. Called when the subject leaves the
    /// graph by the property route, where the descent has already detached it, so its own graph
    /// loses nothing. A second interceptor co-registered on the attach context does lose the
    /// notification master's executor override gave it.
    /// </summary>
    internal void ReleaseAttachEdge()
    {
        IInterceptorSubjectContext? context;

        lock (_mutationLock)
        {
            context = _attachContext;
            if (context is null)
            {
                return;
            }

            _attachContext = null;
            _attachInterceptors = ImmutableArray<ILifecycleInterceptor>.Empty;
        }

        RemoveAttachEdge(context);
    }

    /// <summary>
    /// The check and the claim in one critical section. Two graphs hold two different
    /// <c>_attachedSubjects</c> monitors but contend for this same lock, so no interleaving of their
    /// monitors can beat it.
    /// </summary>
    internal void ClaimOwnership(ILifecycleInterceptor owner)
    {
        lock (_mutationLock)
        {
            if (_owner is null)
            {
                _owner = owner;
                return;
            }

            if (!ReferenceEquals(_owner, owner))
            {
                throw new InvalidOperationException(
                    $"Subject '{_subject.GetType().FullName}' already belongs to another lifecycle graph. A subject belongs " +
                    "to at most one graph; remove it from its current graph before referencing it from this one.");
            }
        }
    }

    /// <summary>
    /// Conditional on the caller being the owner, so a detach in one graph can never clear a claim
    /// another graph holds.
    /// </summary>
    internal void ReleaseOwnership(ILifecycleInterceptor owner)
    {
        lock (_mutationLock)
        {
            if (ReferenceEquals(_owner, owner))
            {
                _owner = null;
            }
        }
    }

    internal int IncrementReferenceCount()
    {
        lock (_mutationLock)
        {
            var count = _referenceCount + 1;
            Volatile.Write(ref _referenceCount, count);
            return count;
        }
    }

    internal int DecrementReferenceCount()
    {
        lock (_mutationLock)
        {
            var count = _referenceCount > 0 ? _referenceCount - 1 : 0;
            Volatile.Write(ref _referenceCount, count);
            return count;
        }
    }
```

- [ ] **Step 7: Write the public attach and detach extensions**

Create `src/Namotion.Interceptor/SubjectAttachmentExtensions.cs`:

```csharp
using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor;

/// <summary>
/// How a root subject joins and leaves a lifecycle graph. These live in core because the generated
/// constructor emits a call to <see cref="AttachToContext"/> without a Tracking reference, and
/// because a core-only consumer with its own <see cref="ILifecycleInterceptor"/> must be able to
/// undo what it attached: after this change <see cref="IInterceptorSubjectContext.RemoveFallbackContext"/>
/// rejects the attach edge, so there would otherwise be no core API to remove it.
/// </summary>
public static class SubjectAttachmentExtensions
{
    /// <summary>
    /// Attaches the subject and its whole subtree to the lifecycle graph the given context takes
    /// part in, and adds that context to the subject's resolution chain.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The subject is already attached through a different context, or already belongs to another
    /// lifecycle graph. A subject belongs to at most one graph.
    /// </exception>
    public static void AttachToContext(this IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        // Resolved before the edge is published and recorded with the attach. Resolving first means
        // a failing resolve leaves no edge behind. Recording it means the detach never re-resolves,
        // so a chain that has since turned cyclic cannot block the edge coming out, and attach and
        // detach pair exactly instead of each seeing whatever resolves at its own time.
        var interceptors = context.GetServices<ILifecycleInterceptor>();

        var executor = subject.GetExecutor();

        // No interceptor means there is no graph to join, so there is nothing to record: no
        // library-owned edge for RemoveFallbackContext to refuse, no interceptor to notify on
        // detach, and nothing that makes the subject attached. Recording it anyway would mark the
        // subject as belonging to a graph that does not exist, and that mark would then refuse
        // every later attempt to join a real one. So this call is exactly what it truthfully is,
        // plain composition. Its inverse is RemoveFallbackContext, not DetachFromContext.
        if (interceptors.IsEmpty)
        {
            executor.AddFallbackContext(context);
            return;
        }

        if (!executor.TryRecordAttachContext(context, interceptors))
        {
            return;
        }

        try
        {
            executor.AddFallbackContext(context);

            for (var index = 0; index < interceptors.Length; index++)
            {
                interceptors[index].AttachSubjectToContext(subject);
            }
        }
        catch
        {
            // Rolls back this context's own state so a retry is possible. What it cannot roll back
            // is anything the lifecycle system already did before throwing: seeded reconciliation
            // entries and attached children stay. That residue is #384 and is out of scope.
            executor.ClearAttachContext(context);
            throw;
        }
    }

    /// <summary>
    /// Detaches the subject and its subtree from the lifecycle graph it was attached to through the
    /// given context, and removes that context from its resolution chain.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The subject is still referenced from parent properties, or was attached through a different
    /// context.
    /// </exception>
    public static void DetachFromContext(this IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        var executor = subject.GetExecutor();

        // Pre-flight, before anything is cleared, so a rejection leaves the subject exactly as it
        // was. Detaching a still-referenced subject would remove its lifecycle ledger entry while
        // reading the reference count rather than decrementing it, stranding both the count and the
        // ownership, and the parent's later removal would then no-op.
        var referenceCount = executor.ReferenceCount;
        if (referenceCount != 0)
        {
            throw new InvalidOperationException(
                $"Subject '{subject.GetType().FullName}' is still referenced from {referenceCount} parent " +
                "property/properties, so it cannot be detached as a root. Remove those references first; the " +
                "subject then leaves the graph on its own.");
        }

        if (!executor.TryClearAttachContext(context, out var interceptors))
        {
            return;
        }

        try
        {
            for (var index = 0; index < interceptors.Length; index++)
            {
                interceptors[index].DetachSubjectFromContext(subject);
            }
        }
        finally
        {
            // Runs even when a detach interceptor throws, so the edge cannot outlive the detach.
            // The record is already clear, so the subject can be re-attached afterwards.
            executor.RemoveAttachEdge(context);
        }
    }

    /// <summary>
    /// Whether the subject takes part in a lifecycle graph, either as a root it was attached to or
    /// as a subject held by a parent property. A subject holding nothing but an explicit fallback
    /// context reports false.
    /// </summary>
    public static bool IsAttached(this IInterceptorSubject subject)
    {
        return subject.GetExecutor().IsAttachedCore;
    }

    /// <summary>
    /// The context the subject was root-attached through, which is the context
    /// <see cref="DetachFromContext"/> accepts. Null for a subject that was never root-attached,
    /// including an attached child: <see cref="IsAttached"/> answers whether the subject is in a
    /// graph, this answers which context would take it out.
    /// </summary>
    public static IInterceptorSubjectContext? TryGetAttachContext(this IInterceptorSubject subject)
    {
        return subject.GetExecutor().AttachContext;
    }

    internal static InterceptorExecutor GetExecutor(this IInterceptorSubject subject)
    {
        return subject.Context as InterceptorExecutor
            ?? throw new InvalidOperationException(
                $"Subject '{subject.GetType().FullName}' does not use an {nameof(InterceptorExecutor)} as its context, so " +
                "there is nowhere to record its position in the lifecycle graph. Subjects generated from " +
                "[InterceptorSubject] and DynamicSubject always do; a hand-written IInterceptorSubject must return an " +
                $"{nameof(InterceptorExecutor)} from its Context property.");
    }
}
```

**Checkpoint:** `dotnet build src/Namotion.Interceptor/Namotion.Interceptor.csproj` must succeed.

- [ ] **Step 8: Move the reference count onto the executor**

Replace the three reference-count members in `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptorExtensions.cs` (`:5-43`, including the `ReferenceCountKey` constant, which goes away):

```csharp
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public static class LifecycleInterceptorExtensions
{
    /// <summary>
    /// Gets the lifecycle interceptor from the context, if configured.
    /// </summary>
    public static LifecycleInterceptor? TryGetLifecycleInterceptor(this IInterceptorSubjectContext context)
    {
        return context.TryGetService<LifecycleInterceptor>();
    }

    /// <summary>
    /// Gets the current reference count (number of parent references) for the subject.
    /// Returns 0 if subject is not attached or lifecycle tracking is not enabled.
    /// </summary>
    public static int GetReferenceCount(this IInterceptorSubject subject)
    {
        // Still a snapshot, and still 0 for a subject whose context cannot carry the count, which is
        // what this method has always returned for an unattached subject.
        return subject.Context is InterceptorExecutor executor ? executor.ReferenceCount : 0;
    }

    /// <summary>
    /// Increments the reference count and returns the new value.
    /// </summary>
    internal static int IncrementReferenceCount(this IInterceptorSubject subject)
    {
        return subject.GetExecutor().IncrementReferenceCount();
    }

    /// <summary>
    /// Decrements the reference count and returns the new value.
    /// </summary>
    internal static int DecrementReferenceCount(this IInterceptorSubject subject)
    {
        return subject.GetExecutor().DecrementReferenceCount();
    }
```

Leave `AttachSubjectProperty` and `DetachSubjectProperty` (`:45-73`) unchanged.

- [ ] **Step 9: Rewrite `ContextInheritanceHandler`**

Replace `src/Namotion.Interceptor.Tracking/Lifecycle/ContextInheritanceHandler.cs` entirely:

```csharp
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

#pragma warning disable CS0659

/// <summary>
/// Owns context inheritance: publishes a subject's parent link when it first enters the graph, and
/// drives the descent into the next level of the object graph on the way in and on the way out.
///
/// Both were previously side effects of this handler calling the public fallback API, which is what
/// made AddFallbackContext mean three different things depending on what the added context carried.
/// The link is published through an internal setter that runs no callbacks; the descent is an
/// explicit ILifecycleInterceptor call.
/// </summary>
public class ContextInheritanceHandler : ILifecycleHandler
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (!change.Property.HasValue)
        {
            return;
        }

        var subject = change.Subject;
        var parentContext = change.Property.Value.Subject.Context;

        if (change.IsPropertyReferenceAdded)
        {
            if (change.ReferenceCount == 1)
            {
                // The single write site for the parent link. Any second one needs its own cycle
                // argument; see the design document's section 4. The assertion is defence in depth
                // on the same invariant the re-attach check in LifecycleInterceptor enforces, and is
                // an assertion rather than a silent branch so that it cannot become unreachable and
                // therefore untestable.
                Debug.Assert(!subject.GetExecutor().HasParentContext, "The subject already holds a parent link at its first reference.");

                // Self-context: a.Mother = a reaches here with the parent being the subject itself,
                // which would self-delegate and make every access on it throw.
                // Attach context: the connector sites attach an item through its parent's context
                // and then assign it into a property of that same parent, where a link would be a
                // second edge to a context the attach edge already names.
                if (!ReferenceEquals(parentContext, subject.Context) &&
                    !ReferenceEquals(parentContext, subject.TryGetAttachContext()))
                {
                    subject.GetExecutor().TrySetParentContext(parentContext);
                }
            }

            // IsContextAttach, not the reference count: gating the descent on count == 1 would
            // re-run the seeding pass over an already-attached subtree, overwriting its
            // reconciliation baseline from the backing store.
            if (change.IsContextAttach)
            {
                Descend(parentContext, subject, attach: true);
            }

            return;
        }

        if (change is { IsPropertyReferenceRemoved: true, ReferenceCount: 0 })
        {
            Descend(parentContext, subject, attach: false);
        }
    }

    private static void Descend(IInterceptorSubjectContext parentContext, IInterceptorSubject subject, bool attach)
    {
        var interceptors = parentContext.GetServices<ILifecycleInterceptor>();
        for (var index = 0; index < interceptors.Length; index++)
        {
            if (attach)
            {
                interceptors[index].AttachSubjectToContext(subject);
            }
            else
            {
                interceptors[index].DetachSubjectFromContext(subject);
            }
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is ContextInheritanceHandler;
    }
}
```

- [ ] **Step 10: Update `LifecycleInterceptor`**

Four edits in `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`.

**10a.** Rename the two private root helpers so they do not read like the new public extensions. At `:49` change `AttachToContext(subject, subject.Context)` to `AttachRootSubject(subject, subject.Context)`; at `:73` change `DetachFromContext(subject, subject.Context)` to `DetachRootSubject(subject, subject.Context)`; at `:86` rename the declaration to `AttachRootSubject`; at `:170` rename the declaration to `DetachRootSubject`.

**10b.** Add the re-attach check and the ownership claim at the top of `AttachRootSubject`, before `_attachedSubjects.TryAdd`:

```csharp
    private void AttachRootSubject(IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        ThrowIfDetachIsUnwinding(subject);

        // Both attach entry points claim ownership. This one is the only code that runs for a
        // childless root, and without it such a root carries an attach record with no owner, so a
        // second graph's property attach finds _owner null and claims it: the subject then holds an
        // attach edge into one graph and a parent link into another and resolves both. That is the
        // half-attached state behaviour change 5 exists to make unreachable.
        subject.GetExecutor().ClaimOwnership(this);

        var isFirstAttach = _attachedSubjects.TryAdd(subject, default);
```

The claim is idempotent for the same owner, so the duplicate-attach early return below is unaffected, and it precedes `TryAdd` so a cross-graph rejection mutates nothing.

**10b-2.** Release ownership at the end of `DetachRootSubject`, after `InvokeRemovedLifecycleHandlers`:

```csharp
        SubjectDetaching?.Invoke(change);
        InvokeRemovedLifecycleHandlers(subject, context, change);

        // Paired with the claim in AttachRootSubject. Without it a detached root stays owned
        // forever: IsAttached() keeps reporting true and any later attach to a different graph is
        // rejected by an owner that no longer means anything. It sits after the handlers for the
        // same reason the property path's release does, and it is unreachable for a subject the
        // descent already removed from the ledger, because the guard at the top of this method
        // returns first.
        subject.GetExecutor().ReleaseOwnership(this);
```

**10c.** Add the re-attach check and the ownership claim at the top of `AttachToProperty`, before `CollectionsMarshal.GetValueRefOrAddDefault`:

```csharp
    private void AttachToProperty(IInterceptorSubject subject, IInterceptorSubjectContext context,
        PropertyReference property, object? index)
    {
        ThrowIfDetachIsUnwinding(subject);

        // Before any mutation, so a cross-graph rejection leaves this subject's bookkeeping clean.
        // What it cannot undo is the property write itself, which WriteProperty already committed
        // through next(), nor earlier items of the same batch. That partial batch is #384's shape.
        subject.GetExecutor().ClaimOwnership(this);

        ref var set = ref CollectionsMarshal.GetValueRefOrAddDefault(_attachedSubjects, subject, out var existed);
```

Add the helper as a private method next to `InvokeAddedLifecycleHandlers`:

```csharp
    /// <summary>
    /// Within one graph, absent from the ledger while holding a parent link is exactly and only a
    /// detach still unwinding: DetachFromProperty removes the entry before the handlers run and
    /// clears the link afterwards. Re-attaching there would set a link on a subject that is
    /// currently a link source with no attach edge, which the cycle argument assumes impossible,
    /// and the resulting parent-only cycle is unrecoverable because the detach path itself would
    /// then throw and the link is internal.
    ///
    /// The ledger is per-interceptor, so across graphs the same condition also matches a live child
    /// of another graph. That case is rejected one line later by ClaimOwnership, which is the
    /// accurate diagnosis, so the message names both causes rather than asserting the wrong one.
    /// </summary>
    private void ThrowIfDetachIsUnwinding(IInterceptorSubject subject)
    {
        if (!_attachedSubjects.ContainsKey(subject) && subject.GetExecutor().HasParentContext)
        {
            throw new InvalidOperationException(
                $"Subject '{subject.GetType().FullName}' cannot be attached here: it is either being detached right now, " +
                "in which case it cannot be re-attached from inside a lifecycle callback, or it is a live child of another " +
                "lifecycle graph, in which case it must leave that graph first.");
        }
    }
```

**10d.** Wrap the detach notification in `DetachFromProperty` so the release happens in a `finally`. The replaced region starts at `var count = subject.DecrementReferenceCount();` (`:242`) and ends after `InvokeRemovedLifecycleHandlers` (`:258`); the snippet restates the `count` and `change` declarations, so replacing only `:253-258` duplicates both.

```csharp
        var count = subject.DecrementReferenceCount();
        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            Property = property,
            Index = index,
            ReferenceCount = count,
            IsPropertyReferenceRemoved = true,
            IsContextDetach = isLastDetach
        };

        try
        {
            if (isLastDetach)
            {
                SubjectDetaching?.Invoke(change);
            }

            InvokeRemovedLifecycleHandlers(subject, context, change);
        }
        finally
        {
            // After the handlers, never before. The descent resolves the next level's handlers
            // through the child's own context, and a property-attached subject has no other edge,
            // so releasing first would make grandchildren get bookkeeping but no handler
            // invocation, and would lose this subject's own per-property deregistration too.
            // It also closes the window in which the subject is unowned while its graph is still
            // mid-detach, during which another graph could claim it.
            if (count == 0)
            {
                var executor = subject.GetExecutor();
                executor.TryClearParentContext();
                executor.ReleaseAttachEdge();
                executor.ReleaseOwnership(this);
            }
        }
```

Leave the explicit child recursion at `:260-268` exactly where it is.

- [ ] **Step 11: Migrate the call sites and the generator**

**11a.** Recompute the split. Spec section 6 warns that three reviews produced three different counts, so measure rather than trust:

```bash
grep -rn --include="*.cs" -E "\.(Add|Remove)FallbackContext\(" src/ | wc -l
grep -rn --include="*.cs" -E "(subject|item|newItem|proxy|person|motor|mother1?|car|simulation|Root|this)\)?\.Context\.(Add|Remove)FallbackContext\(" src/
```

A call is **subject-facing** when the receiver is a subject's context (`x.Context`, `((IInterceptorSubject)x).Context`) and the argument is a graph context. It **stays** when both sides are plain contexts built by `InterceptorSubjectContext.Create()`. Record the final count in the commit message.

**11b.** Migrate these production sites, replacing `X.Context.AddFallbackContext(Y)` with `X.AttachToContext(Y)`:

| File | Line | Replacement |
|---|---|---|
| `src/HomeBlaze/HomeBlaze.Services/RootManager.cs` | 85 | `Root.AttachToContext(_context);` |
| `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectLoader.cs` | 280 | `subjectToLoad.AttachToContext(subject.Context);` |
| `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs` | 145 | `newItem.AttachToContext(parent.Context);` |
| `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs` | 229 | `newItem.AttachToContext(parent.Context);` |
| `src/Namotion.Interceptor.Dynamic/DynamicSubject.cs` | 15 | `((IInterceptorSubject)this).AttachToContext(context);` |
| `src/Namotion.Interceptor.Benchmark/DynamicSubjectBenchmark.cs` | 34 | `motor.AttachToContext(_context);` |
| `src/Namotion.Interceptor.Benchmark/DynamicSubjectBenchmark.cs` | 51 | `subject.AttachToContext(_iterationContext!);` |

Leave `src/Namotion.Interceptor.Benchmark/ContextDelegationDepthBenchmark.cs:37` alone: it builds a plain context-to-context chain.

**11c.** Change the generator emit line, `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs:246`:

```csharp
            builder.AppendLine("            ((IInterceptorSubject)this).AttachToContext(context);");
```

**11d.** Migrate the subject-facing test call sites. `AddFallbackContext` becomes `AttachToContext`, `RemoveFallbackContext` becomes `DetachFromContext`, in:
- `src/Namotion.Interceptor.Tests/InterceptorTests.cs:101`
- `src/Namotion.Interceptor.Tracking.Tests/LifecycleInterceptorTests.cs:50,102,124,148,175,197`
- `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleEventsTests.cs:340,448`
- `src/Namotion.Interceptor.Tracking.Tests/Parent/ParentAccessDuringLifecycleTests.cs:225`
- `src/Namotion.Interceptor.Dynamic.Tests/DynamicSubjectTests.cs:55,78,89`
- `src/Namotion.Interceptor.Hosting.Tests/HostedServiceHandlerTests.cs:102`
- `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/AttachOrderCharacterizationTests.cs` (Task 1, the root attach in `WhenRootIsAttachedWithChildren_ThenTheRootsOwnAttachFiresLast`)
- `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/InheritedContextResolutionTests.cs` (Task 1, the seeding pattern in `WhenSubjectIsSeededLikeAConnectorItem_...`)

**Do not migrate** the plain-context composition sites: `FallbackContextInvalidationTests.cs:22,29`, `PerPropertySubscriptionLifecycleTests.cs:115,169,455`, `WritePipelineOrderTests.cs:64`, and everything in `src/Namotion.Interceptor.Tests/Context/`.

- [ ] **Step 12: Build, then run the full gate**

```bash
dotnet build src/Namotion.Interceptor.slnx
```

Expected: succeeds. Fix compile errors before proceeding; a `CS0117` on `AttachToContext` means a file is missing `using Namotion.Interceptor;`.

```bash
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected outcomes, all three of which are gates:
1. **Task 1's characterization tests still pass, unchanged.** A failure here means the change moved an order it was required to preserve. Stop and fix production code, never the test.
2. **Task 2's reproduction tests now pass**, all 7.
3. **The eight ordering oracles in `Namotion.Interceptor.Tracking.Tests` must not move.** Verify explicitly:

```bash
git status --short 'src/Namotion.Interceptor.Tracking.Tests/*.received.txt'
```

Expected: empty. Any `.received.txt` under `Namotion.Interceptor.Tracking.Tests` other than `VerifyChecksTests.PublicApi` is a signal to stop, not a snapshot to accept.

- [ ] **Step 13: Accept the snapshots that are expected to move**

Three groups, and only these three:

```bash
# 16 generator snapshots: the emitted constructor line changed.
for received in src/Namotion.Interceptor.Generator.Tests/**/*.received.txt; do
  mv "$received" "${received%.received.txt}.verified.txt"
done

# The core public API: four new extensions, two new protected hooks.
mv src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.received.txt \
   src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt

# InterceptorTests: its root detach now runs through DetachFromContext.
mv src/Namotion.Interceptor.Tests/InterceptorTests.WhenAddingAndRemovingContext_ThenInterceptorsAreCalledInTheRightOrder.received.txt \
   src/Namotion.Interceptor.Tests/InterceptorTests.WhenAddingAndRemovingContext_ThenInterceptorsAreCalledInTheRightOrder.verified.txt
```

Then re-run and confirm green:

```bash
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: PASS.

**Review the core PublicApi diff by eye before committing.** `git diff src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt` must show exactly: the four `SubjectAttachmentExtensions` methods, the two `protected virtual` hooks on `InterceptorSubjectContext`, and the removal of `InterceptorExecutor`'s two overrides. Anything else means something leaked to public that should be internal.

The Tracking public API snapshot must **not** move: `DetachFromContext` is a core extension and `LifecycleInterceptor`'s interface list is unchanged.

- [ ] **Step 14: Run the integration suites**

The three migrated connector paths are covered only by integration-tagged tests, which the default command excludes, so the branch can be green while the OPC UA client silently browses nothing. Confirm port 4840 is free first.

```bash
lsof -nP -iTCP:4840 -sTCP:LISTEN
dotnet test src/Namotion.Interceptor.OpcUa.Tests
dotnet test src/Namotion.Interceptor.Connectors.Tests
```

Expected: `lsof` prints nothing, both suites PASS. If `lsof` prints a process, stop the local Demo.Host before running.

- [ ] **Step 15: Commit**

```bash
git add -A src/
git commit -m "Split AddFallbackContext into composition, root entry and subtree descent

AddFallbackContext becomes the dependency-injection primitive its name claims and
stops attaching. Root entry moves to AttachToContext and DetachFromContext in core.
A child's inherited context becomes an internal parent link on ContextState,
published by ContextInheritanceHandler through a setter that runs no callbacks.

InterceptorExecutor gains the attach context, the owning graph, the interceptor set
the attach resolved and the reference count moved off subject.Data. Its two method
overrides become protected virtual guard hooks the base calls from inside its own
critical section, so a guard and the publish it protects are one critical section.

Traversal order is unchanged: the handler keeps its type, registration position and
ordering attributes, and the eight ordering oracles do not move.

Closes #207 on both paths. Closes #402 defects 2, 3, 4 and 5.
Design: docs/superpowers/specs/2026-08-04-context-inheritance-design.md"
git push
```

- [ ] **Step 16: Run the benchmark gate**

```bash
pwsh scripts/benchmark.ps1 -Filter "*RegistryBenchmark*"
```

Run with multiple launches: PR #412 recorded a single launch on a busy machine inverting its own result. Expected direction: an improvement on attach and detach, because each drops a `ConcurrentDictionary.AddOrUpdate` with a `(string?, string)` tuple key, against one added uncontended `_mutationLock` acquisition per attach and per detach.

Record the numbers. If attach or detach regresses by more than a few percent, the likely cause is the two separate lock acquisitions per attach described in decision 3, and the fix is to profile before changing anything.

---

## Task 4: Reproductions needing the new API, and the gap tests

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RootAttachContractTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/KnownGapTests.cs`

**Interfaces:**
- Consumes: `AttachToContext`, `DetachFromContext`, `IsAttached`, `TryGetAttachContext` from Task 3; `UsedByContextsProbe.Count` from Task 2.
- Produces: nothing.

- [ ] **Step 1: Add the exactly-once detach reproduction**

Append to `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RootAttachContractTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task WhenTwoThreadsDetachTheSameRoot_ThenTheInterceptorsRunExactlyOnce()
    {
        // #402 defect 2. Nothing in the ILifecycleInterceptor contract requires a consumer's detach
        // to be idempotent, so running it twice is a defect even though Tracking's own happens to
        // tolerate it. TryClearAttachContext picks exactly one winner under the mutation lock.

        // Arrange
        var detachCount = 0;
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new CountingLifecycleInterceptor(() => Interlocked.Increment(ref detachCount)), _ => false);

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(context);

        using var start = new ManualResetEventSlim(false);

        // Act
        var racers = Enumerable
            .Range(0, 2)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.Wait();
                    ((IInterceptorSubject)subject).DetachFromContext(context);
                },
                TaskCreationOptions.LongRunning))
            .ToArray();

        start.Set();
        await Task.WhenAll(racers);

        // Assert
        Assert.Equal(1, detachCount);
        Assert.False(((IInterceptorSubject)subject).IsAttached());
    }

    [Fact]
    public void WhenDetachInterceptorThrows_ThenTheEdgeIsStillRemovedAndAReattachWorks()
    {
        // Behaviour change 6.

        // Arrange
        var shouldThrow = true;
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new CountingLifecycleInterceptor(() =>
            {
                if (shouldThrow)
                {
                    throw new InvalidOperationException("detach handler failed");
                }
            }), _ => false);

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(context);
        var attachedCount = UsedByContextsProbe.Count(context);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => ((IInterceptorSubject)subject).DetachFromContext(context));

        // The edge assertion is the one that matters: the record is cleared before the interceptor
        // loop, so IsAttached() reports false even when the finally is deleted, and a re-attach
        // succeeds because AddFallbackContext dedups the leftover edge. Without this the mutant
        // that deletes the finally survives.
        Assert.Equal(attachedCount - 1, UsedByContextsProbe.Count(context));
        Assert.False(((IInterceptorSubject)subject).IsAttached());

        shouldThrow = false;
        ((IInterceptorSubject)subject).AttachToContext(context);
        Assert.True(((IInterceptorSubject)subject).IsAttached());
    }

    [Fact]
    public void WhenSubjectIsStillReferenced_ThenDetachFromContextThrowsAndLeavesTheCountIntact()
    {
        // Behaviour change 8.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };
        ((IInterceptorSubject)child).AttachToContext(context);
        parent.Mother = child;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => ((IInterceptorSubject)child).DetachFromContext(context));

        Assert.Equal(1, child.GetReferenceCount());
        Assert.True(((IInterceptorSubject)child).IsAttached());
    }

    [Fact]
    public void WhenTwoEdgesTargetOneContext_ThenRemovingOneKeepsInvalidationReachingTheOther()
    {
        // Behaviour change 9, unreachable on master where one edge kind plus dedup rules it out.

        // Built on plain contexts through the internal setter, because the guard on
        // AddFallbackContext makes this shape unreachable from consumer code: adding a
        // lifecycle-bearing parent context to a child that has no attach record now throws. Two
        // edges to one target can therefore only arise inside the library, which is exactly why
        // the unregistration has to be conditional rather than why a consumer would hit it.

        // Arrange
        var target = InterceptorSubjectContext.Create();
        var user = InterceptorSubjectContext.Create();

        user.AddFallbackContext(target);
        Assert.True(user.TrySetParentContext(target));

        // Act: remove one of the two edges pointing at the same target.
        user.RemoveFallbackContext(target);

        // Assert: the surviving parent link must still receive invalidation, so a service added to
        // the target after the removal has to reach the user.
        target.AddService(new MarkerService());
        Assert.Single(user.GetServices<MarkerService>());
    }

    [Fact]
    public void WhenPreWiredChildIsAttachedUnderANewParent_ThenItsGrandchildIsDiscovered()
    {
        // Behaviour change 11: on master the descent is gated on AddFallbackContext's return value,
        // so pre-wiring a child's context suppresses discovery of everything below it.

        // The parent must NOT be attached yet. Pre-wiring to an already-attached parent attaches
        // the whole subtree at pre-wire time, so the later assignment exercises nothing and the
        // mutant that gates the descent on the reference count survives.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var parent = new Person { FirstName = "Parent" };
        var grandchild = new Person { FirstName = "Grandchild" };
        var child = new Person { FirstName = "Child", Mother = grandchild };

        // Legal: the parent carries no lifecycle interceptor yet, so the guard does not fire.
        ((IInterceptorSubject)child).Context.AddFallbackContext(((IInterceptorSubject)parent).Context);
        parent.Father = child;

        // Act
        ((IInterceptorSubject)parent).AttachToContext(context);

        // Assert
        Assert.NotNull(((IInterceptorSubject)grandchild).TryGetRegisteredSubject());
        Assert.Equal(1, grandchild.GetReferenceCount());
        Assert.NotEmpty(((IInterceptorSubject)grandchild).Context.GetServices<IWriteInterceptor>());
    }

    [Fact]
    public void WhenConstructorAttachedChildIsPlacedUnderAParent_ThenItInheritsTheParentsSubtreeServices()
    {
        // Behaviour change 12, the attach-side twin of change 10.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person(context) { FirstName = "Child" };

        ((IInterceptorSubject)parent).Context.AddService(new MarkerService());

        // Act
        parent.Mother = child;

        // Assert
        Assert.Single(((IInterceptorSubject)child).Context.GetServices<MarkerService>());
    }

    [Fact]
    public void WhenReferenceCountsChange_ThenSubjectDataCarriesNoEntryForThem()
    {
        // Behaviour change 13.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var parent1 = new Person(context) { FirstName = "P1" };
        var parent2 = new Person(context) { FirstName = "P2" };
        var shared = new Person { FirstName = "Shared" };

        // Act
        parent1.Mother = shared;
        var afterFirst = shared.GetReferenceCount();

        parent2.Mother = shared;
        var afterSecond = shared.GetReferenceCount();

        parent1.Mother = null;
        var afterFirstRemoval = shared.GetReferenceCount();

        parent2.Mother = null;

        // Assert
        Assert.Equal(1, afterFirst);
        Assert.Equal(2, afterSecond);
        Assert.Equal(1, afterFirstRemoval);
        Assert.Equal(0, shared.GetReferenceCount());
        Assert.DoesNotContain(((IInterceptorSubject)shared).Data.Keys, key => key.key.Contains("ReferenceCount"));
    }

    [Fact]
    public void WhenSubjectUsesAPlainContext_ThenNoAttachContextIsRecorded()
    {
        // Decision 4. A context carrying no lifecycle interceptor is not a graph, so the
        // constructor's AttachToContext degenerates to plain composition and records nothing.
        // Spec section 7 already says such a subject reports IsAttached() false; recording would
        // have contradicted that.

        // Arrange
        var plainContext = InterceptorSubjectContext.Create();

        // Act
        var subject = new Person(plainContext) { FirstName = "Subject" };

        // Assert
        Assert.Null(((IInterceptorSubject)subject).TryGetAttachContext());
        Assert.False(((IInterceptorSubject)subject).IsAttached());
        Assert.Equal(0, subject.GetReferenceCount());
    }

    [Fact]
    public void WhenPlainContextSubjectJoinsAGraphLater_ThenItAttachesInOneStep()
    {
        // Decision 4's payoff, and the regression this guards: recording the plain context would
        // make this throw "already attached through a different context", and the only escape
        // would be a DetachFromContext nobody would guess at.

        // Arrange
        var plainContext = InterceptorSubjectContext.Create();
        var subject = new Person(plainContext) { FirstName = "Subject" };

        var trackingContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        // Act
        ((IInterceptorSubject)subject).AttachToContext(trackingContext);

        // Assert
        Assert.True(((IInterceptorSubject)subject).IsAttached());
        Assert.Same(trackingContext, ((IInterceptorSubject)subject).TryGetAttachContext());
        Assert.NotNull(((IInterceptorSubject)subject).Context.TryGetLifecycleInterceptor());
    }

    [Fact]
    public void WhenAnInterceptorIsRegisteredAfterTheAttach_ThenItReceivesNoUnpairedDetach()
    {
        // Behaviour change 16: the detach notifies exactly the set the attach resolved.

        // Arrange
        var attachContext = InterceptorSubjectContext.Create();
        var lateDetaches = 0;
        var earlyDetaches = 0;

        attachContext.WithService(
            () => new CountingLifecycleInterceptor(() => Interlocked.Increment(ref earlyDetaches)),
            _ => false);

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(attachContext);

        attachContext.WithService(
            () => new CountingLifecycleInterceptor(() => Interlocked.Increment(ref lateDetaches)),
            _ => false);

        // Act
        ((IInterceptorSubject)subject).DetachFromContext(attachContext);

        // Assert
        Assert.Equal(1, earlyDetaches);
        Assert.Equal(0, lateDetaches);
    }

    [Fact]
    public void WhenRemoveFallbackContextTargetsTheAttachEdge_ThenItThrows()
    {
        // Behaviour change 2's second half. The guard hook has no other coverage: every migrated
        // test now goes through DetachFromContext and never exercises the rejection.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(context);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => ((IInterceptorSubject)subject).Context.RemoveFallbackContext(context));

        Assert.True(((IInterceptorSubject)subject).IsAttached());
        Assert.Same(context, ((IInterceptorSubject)subject).TryGetAttachContext());
    }

    [Fact]
    public void WhenAnInterceptorsContextLeavesTheChain_ThenItStillReceivesTheDetachItWasOwed()
    {
        // Behaviour change 16's second half. Re-resolving at detach would give this interceptor
        // nothing, leaking whatever per-subject state it took at attach.

        // Arrange
        var detaches = 0;
        var departingContext = InterceptorSubjectContext
            .Create()
            .WithService(() => new CountingLifecycleInterceptor(() => Interlocked.Increment(ref detaches)), _ => false);

        var attachContext = InterceptorSubjectContext.Create();
        attachContext.AddFallbackContext(departingContext);

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(attachContext);

        // The interceptor's context leaves the chain after the attach recorded it.
        attachContext.RemoveFallbackContext(departingContext);
        Assert.Empty(attachContext.GetServices<ILifecycleInterceptor>());

        // Act
        ((IInterceptorSubject)subject).DetachFromContext(attachContext);

        // Assert
        Assert.Equal(1, detaches);
    }

    [Fact]
    public void WhenPropertyOwnedSubjectIsRootAttachedIntoAnotherGraph_ThenNoRecordAndNoEdgeArePublished()
    {
        // The directed test spec section 9 asks for, and the only thing that kills the mutant which
        // deletes the ownership read in TryRecordAttachContext. The cross-graph reproduction goes
        // through the property path and is rejected by ClaimOwnership instead, so it passes with
        // that check deleted.

        // Arrange
        var contextA = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var contextB = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var parentA = new Person(contextA) { FirstName = "ParentA" };
        var owned = new Person { FirstName = "Owned" };
        parentA.Mother = owned;

        var baseline = UsedByContextsProbe.Count(contextB);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => ((IInterceptorSubject)owned).AttachToContext(contextB));

        Assert.Null(((IInterceptorSubject)owned).TryGetAttachContext());
        Assert.Equal(baseline, UsedByContextsProbe.Count(contextB));
    }

    [Fact]
    public void WhenConnectorItemIsAssignedUnderItsAttachParent_ThenItKeepsTheAttachEdgeAndGetsNoLink()
    {
        // Kills the mutant that sets the link and releases the attach edge instead of skipping the
        // link. Nothing else observes the record after a connector-shaped assignment.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var parent = new Person(context) { FirstName = "Parent" };
        var parentContext = ((IInterceptorSubject)parent).Context;

        var item = new Person { FirstName = "Item" };
        ((IInterceptorSubject)item).AttachToContext(parentContext);

        // Act
        parent.Mother = item;

        // Assert: the record still names the attach context, so the edge was not traded for a link.
        Assert.Same(parentContext, ((IInterceptorSubject)item).TryGetAttachContext());
        Assert.Equal(1, item.GetReferenceCount());
        Assert.NotNull(((IInterceptorSubject)item).TryGetRegisteredSubject());
    }

    [Fact]
    public void WhenHandlerRootAttachesTheSubjectDuringItsOwnDetach_ThenItThrows()
    {
        // Behaviour change 17 through the OTHER attach entry point. The property-path variant is
        // covered in commit 2; this one reaches ThrowIfDetachIsUnwinding via AttachRootSubject.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        Exception? caught = null;
        var parent = new Person(context) { FirstName = "Parent" };
        context.WithService(() => new RootReAttachingHandler(exception => caught = exception));

        var child = new Person { FirstName = "Child" };
        parent.Mother = child;

        // Act
        parent.Mother = null;

        // Assert
        Assert.IsType<InvalidOperationException>(caught);
        Assert.Equal(0, child.GetReferenceCount());
    }

    [Fact]
    public void WhenSubjectReferencesItself_ThenNoSelfLinkIsPublished()
    {
        // Kills the mutant that deletes the self-context guard. A self-link makes the context
        // delegate to itself, so every intercepted access on the subject raises afterwards.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var subject = new Person(context) { FirstName = "Subject" };

        // Act
        subject.Mother = subject;

        // Assert: still readable and writable, which a self-delegating context would not be.
        subject.LastName = "written after the self reference";
        Assert.Equal("written after the self reference", subject.LastName);
        Assert.Equal(1, subject.GetReferenceCount());
    }

    [Fact]
    public void WhenASecondLifecycleBearingContextIsAdded_ThenItThrowsEvenThoughARecordExists()
    {
        // Kills the mutant that makes the AddFallbackContext guard test only for a non-null record
        // rather than for record identity. Without it the subject resolves graph B's interceptors
        // while being absent from B's ledger and registry.

        // Arrange
        var contextA = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var contextB = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(contextA);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => ((IInterceptorSubject)subject).Context.AddFallbackContext(contextB));

        Assert.Same(contextA, ((IInterceptorSubject)subject).TryGetAttachContext());
    }

    private class RootReAttachingHandler(Action<Exception> onThrow) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsPropertyReferenceRemoved || change.ReferenceCount != 0)
            {
                return;
            }

            try
            {
                change.Subject.AttachToContext(change.Subject.Context);
            }
            catch (Exception exception)
            {
                onThrow(exception);
            }
        }
    }

    private class MarkerService;

    private class CountingLifecycleInterceptor(Action onDetach) : ILifecycleInterceptor
    {
        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            onDetach();
        }
    }
```

- [ ] **Step 2: Run the new-API reproductions**

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~RootAttachContractTests"
```

Expected: PASS, every test in the class.

If `WhenTwoEdgesTargetOneContext_...` fails, the conditional unregistration in step 3 of Task 3 is wrong: check that `UnregisterUsedByIfUnreferenced` reads the **new** state, not the state captured before the publish.

If `WhenPlainContextSubjectJoinsAGraphLater_...` throws "already attached through a different context", the `interceptors.IsEmpty` early return in Task 3 step 7 is missing or placed after `TryRecordAttachContext`. That is decision 4 and it must be fixed there, not worked around in the test.

- [ ] **Step 3: Write the gap tests**

These pin the **current documented outcome** of everything this change does not fix, so a later change that worsens one fails visibly rather than silently. They are not aspirational: a failure means either someone improved the behaviour and the test should be updated deliberately, or someone regressed it. Say so in the class comment.

Create `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/KnownGapTests.cs`:

```csharp
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The known limits of the context inheritance redesign, each pinned at its current documented
/// outcome so that a later change which worsens one fails visibly instead of silently.
///
/// A failure here is not automatically a bug. It means either someone improved the behaviour, in
/// which case update the test and the design document's section 11 deliberately, or someone
/// regressed it. Every case below is recorded in
/// docs/superpowers/specs/2026-08-04-context-inheritance-design.md sections 5, 9 and 11.
/// </summary>
public class KnownGapTests
{
    [Fact]
    public async Task WhenAttachRacesDetachOnTheSameRoot_ThenNoInvariantOtherThanEdgeAgreementHolds()
    {
        // #402 defect 1, explicitly NOT closed. Both operations are two steps and are not atomic
        // against each other: a detach can clear the record, an attach can then record and publish,
        // and the detach's second step then removes the edge, leaving a record with no edge.
        //
        // So this test deliberately does NOT assert that the record and the edge agree. That
        // agreement is exactly what defect 1 breaks, and asserting it would be asserting a fix we
        // did not make. What it pins is the weaker set that must survive: the reference count stays
        // zero, nothing but InvalidOperationException escapes, and the subject is still usable.

        for (var round = 0; round < 50; round++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithContextInheritance();

            var subject = new Person { FirstName = "Subject" };
            ((IInterceptorSubject)subject).AttachToContext(context);

            using var start = new ManualResetEventSlim(false);

            // Act
            var racers = new[]
            {
                Task.Factory.StartNew(() =>
                {
                    start.Wait();
                    try { ((IInterceptorSubject)subject).AttachToContext(context); }
                    catch (InvalidOperationException) { }
                }, TaskCreationOptions.LongRunning),
                Task.Factory.StartNew(() =>
                {
                    start.Wait();
                    try { ((IInterceptorSubject)subject).DetachFromContext(context); }
                    catch (InvalidOperationException) { }
                }, TaskCreationOptions.LongRunning)
            };

            start.Set();
            await Task.WhenAll(racers);

            // Assert
            Assert.Equal(0, subject.GetReferenceCount());

            subject.LastName = "written after the race";
            Assert.Equal("written after the race", subject.LastName);
        }
    }

    [Fact]
    public void WhenAddingTheAttachContextDuringTheDetachWindow_ThenItThrows()
    {
        // #411, whose silent form is gone. DetachFromContext clears the record before the
        // interceptor loop, so the re-add is no longer naming the recorded attach context and the
        // guard rejects it. The issue stays open because the caller still cannot complete the add.

        // Arrange
        using var insideDetach = new ManualResetEventSlim(false);
        using var addAttempted = new ManualResetEventSlim(false);
        Exception? caught = null;

        var subject = new Person { FirstName = "Subject" };
        var context = InterceptorSubjectContext.Create();
        context.WithService(
            () => new RendezvousLifecycleInterceptor(() =>
            {
                insideDetach.Set();
                addAttempted.Wait(TimeSpan.FromSeconds(5));
            }),
            _ => false);

        ((IInterceptorSubject)subject).AttachToContext(context);

        var detach = Task.Factory.StartNew(
            () => ((IInterceptorSubject)subject).DetachFromContext(context),
            TaskCreationOptions.LongRunning);

        // Act
        insideDetach.Wait(TimeSpan.FromSeconds(5));
        try
        {
            ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
        }
        catch (Exception exception)
        {
            caught = exception;
        }

        addAttempted.Set();
        detach.Wait(TimeSpan.FromSeconds(5));

        // Assert
        Assert.IsType<InvalidOperationException>(caught);
    }

    [Fact]
    public void WhenLinkedParentLeavesWhileAnotherHoldsTheSubject_ThenTheSubjectGoesDark()
    {
        // #410 symptom 2, first shape, not closed. The subject stays attached and referenced while
        // resolving nothing at all, which is more severe than the issue predicts.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parent1 = new Person(context) { FirstName = "P1" };
        var parent2 = new Person(context) { FirstName = "P2" };
        var shared = new Person { FirstName = "Shared" };

        parent1.Mother = shared;
        parent2.Mother = shared;

        // Act: the parent the link points at leaves the graph while parent2 still holds the subject.
        ((IInterceptorSubject)parent1).DetachFromContext(context);

        // Assert: the gap. Change this only deliberately.
        Assert.Equal(1, shared.GetReferenceCount());
        Assert.Empty(((IInterceptorSubject)shared).Context.GetServices<IWriteInterceptor>());
    }

    [Fact]
    public void WhenConnectorItemsAttachParentLeaves_ThenTheItemGoesDark()
    {
        // #410 symptom 2, second shape. The item's only edge is its attach edge, because the link
        // gate deliberately skips a context the attach edge already names.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parent = new Person(context) { FirstName = "Parent" };
        var holder = new Person(context) { FirstName = "Holder" };

        var item = new Person { FirstName = "Item" };
        ((IInterceptorSubject)item).AttachToContext(((IInterceptorSubject)parent).Context);

        // The FIRST reference must be the attach parent, so the link gate skips: it fires only at
        // reference count 1, and there it sees a context the attach edge already names. Referencing
        // the item from the holder first would set a link to the holder instead and the item would
        // survive the parent leaving, which is not the shape #410 describes.
        parent.Mother = item;
        holder.Mother = item;

        // Act
        ((IInterceptorSubject)parent).DetachFromContext(context);

        // Assert: the gap. The item is still referenced and still attached, and its only edge is an
        // attach edge into a context that has itself left the graph.
        Assert.Equal(1, item.GetReferenceCount());
        Assert.Empty(((IInterceptorSubject)item).Context.GetServices<IWriteInterceptor>());
    }

    [Fact]
    public void WhenCrossGraphRejectionHappensMidBatch_ThenEarlierItemsStayAttached()
    {
        // #384's shape: WriteProperty commits through next() before taking the lock, so the backing
        // store keeps the value and earlier items of the batch are already attached.

        // Arrange
        var contextA = InterceptorSubjectContext.Create().WithContextInheritance();
        var contextB = InterceptorSubjectContext.Create().WithContextInheritance();

        var ownerA = new Person(contextA) { FirstName = "OwnerA" };
        var owned = new Person { FirstName = "Owned" };
        ownerA.Mother = owned;

        var parentB = new Person(contextB) { FirstName = "ParentB" };
        var free = new Person { FirstName = "Free" };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => parentB.Children = [free, owned]);

        // The gap: the write is committed and the earlier item is attached.
        Assert.Equal(2, parentB.Children.Length);
        Assert.Equal(1, free.GetReferenceCount());
        Assert.Equal(0, owned.GetReferenceCount());
    }

    [Fact]
    public void WhenAttachHandlerThrowsPartWay_ThenTheLifecycleResidueRemains()
    {
        // The rollback in AttachToContext's catch clears this context's own state only. Anything the
        // lifecycle system already did stays, which is #384's rollback problem.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        context.WithService(() => new ThrowingAttachHandler());

        var child = new Person { FirstName = "Child" };
        var root = new Person { FirstName = "Root", Mother = child };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => ((IInterceptorSubject)root).AttachToContext(context));

        // The gap: the root's own record and edge are rolled back, the child's attach is not.
        Assert.Null(((IInterceptorSubject)root).TryGetAttachContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenFallbackCycleExists_ThenASubjectInheritsItsOwnDescendantsSubtreeService()
    {
        // Predates this design and is not fixed by it. Contradicts what ContextSubtreeServiceTests
        // documents about subtree scoping.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var a = new Person(context) { FirstName = "A" };
        var b = new Person { FirstName = "B" };

        a.Mother = b;
        b.Father = a;

        // Act
        ((IInterceptorSubject)b).Context.AddService(new SubtreeMarker());

        // Assert: the gap. A resolves a service registered on its own descendant.
        Assert.Single(((IInterceptorSubject)a).Context.GetServices<SubtreeMarker>());
    }

    [Fact]
    public void WhenTwoRootContextsShareOneTrackingContext_ThenTheCrossGraphRejectionDoesNotApply()
    {
        // The owner is an ILifecycleInterceptor reference, so two root contexts sharing one tracking
        // context count as one graph while having two registries. The two-graph finding from spec
        // section 2 is not closed in that configuration.

        // Arrange. The fallback must be wired BEFORE WithRegistry: WithService skips only when the
        // service type already RESOLVES through the chain, so registering first would give each
        // root its own LifecycleInterceptor, two genuinely separate graphs, and the cross-graph
        // rejection would fire correctly rather than demonstrating the gap.
        var trackingContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var rootA = InterceptorSubjectContext.Create();
        rootA.AddFallbackContext(trackingContext);
        rootA.WithRegistry();

        var rootB = InterceptorSubjectContext.Create();
        rootB.AddFallbackContext(trackingContext);
        rootB.WithRegistry();

        // One lifecycle interceptor, two registries.
        Assert.Same(rootA.TryGetLifecycleInterceptor(), rootB.TryGetLifecycleInterceptor());
        Assert.NotSame(rootA.TryGetService<ISubjectRegistry>(), rootB.TryGetService<ISubjectRegistry>());

        var parentA = new Person(rootA) { FirstName = "ParentA" };
        var parentB = new Person(rootB) { FirstName = "ParentB" };
        var shared = new Person { FirstName = "Shared" };

        // Act: no throw, because both graphs resolve the same lifecycle interceptor.
        parentA.Mother = shared;
        parentB.Mother = shared;

        // Assert: the gap. Both registries index it, one resolution wins.
        Assert.Equal(2, shared.GetReferenceCount());
        Assert.Contains(shared, rootA.TryGetService<ISubjectRegistry>()!.KnownSubjects.Keys);
        Assert.Contains(shared, rootB.TryGetService<ISubjectRegistry>()!.KnownSubjects.Keys);
    }

    private class SubtreeMarker;

    private class ThrowingAttachHandler : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change is { IsContextAttach: true, Property: null })
            {
                throw new InvalidOperationException("attach handler failed");
            }
        }
    }

    private class RendezvousLifecycleInterceptor(Action onDetach) : ILifecycleInterceptor
    {
        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            onDetach();
        }
    }
}
```

- [ ] **Step 4: Run the gap tests**

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~KnownGapTests"
```

Expected: PASS, 8 tests.

**A failure here is information, not necessarily a defect.** Each test asserts an outcome the design predicts. If one fails, the design's prediction about that gap is wrong, which is a spec correction and must be reported before being papered over. Do not adjust an assertion to match observed behaviour without recording why in the commit message.

`WhenDetachFromContextRacesAPropertyAttach` from spec section 9 is deliberately absent: it needs a rendezvous inside `LifecycleInterceptor`'s monitor, which no public seam reaches. Record that as a spec correction in Task 5 rather than writing a test that does not test the race.

- [ ] **Step 5: Extend the concurrency fuzz model with the parent link**

The fuzz harness models fallback edges as the only mutable topology. Add the parent link as a third edge kind under the same lock and the same R4 discipline.

In `src/Namotion.Interceptor.Tests/Context/ContextConcurrencyFuzzTests.cs`, the model needs:
- The oracle's single-threaded walk to visit `Parent` after `FallbackContexts`, matching `CollectServices`.
- A worker operation that sets and clears a parent link on an executor-backed context, via the internal setters.
- The expected-resolution computation to include parent-reachable services.

Read the file first: the topology builder, the worker operation switch and the oracle are the three places to touch. Keep the round, worker and operation counts unchanged.

- [ ] **Step 6: Run the fuzz tests**

```bash
dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~ContextConcurrencyFuzzTests"
```

Expected: PASS, 8 seeds. A quiescent-consistency mismatch means a cache survived a topology change, which for the parent link means either `RegisterUsedBy` runs after the publish or `UnregisterUsedByIfUnreferenced` drops an entry an edge still needs.

- [ ] **Step 7: Run the full gate and commit**

```bash
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: PASS, no snapshot movement.

```bash
git add -A src/
git commit -m "Test: pin exactly-once detach, the remaining behaviour changes and the known gaps

Reproductions that need the new API, plus gap tests holding the documented outcome
of everything this design does not fix, so a later change that worsens one fails
visibly. Extends the context fuzz model with the parent link as a third edge kind."
git push
```

---

## Task 5: Documentation

**Files:**
- Modify: `docs/interceptor.md`, `docs/dynamic.md`, `docs/generator.md`, `docs/tracking.md`
- Modify: `docs/design/tracking-lifecycle.md`
- Modify: `docs/superpowers/specs/2026-08-04-context-inheritance-design.md`

**Interfaces:** none.

Remember: **no em dashes**, in any of these files.

- [ ] **Step 1: Update `docs/interceptor.md`**

At `:64`, the claim "This is used internally by `WithContextInheritance()` to automatically assign the parent's context to child subjects" stops being true. Replace with:

```markdown
Context inheritance no longer uses this API. `WithContextInheritance()` publishes an internal parent
link on the child's context instead, which resolves after any fallback context registered here, so
explicit composition beats inheritance. The link is owned by the lifecycle system and cannot be added
or removed through the fallback API.
```

Leave the code example at `:52-60` unchanged: it composes two plain contexts and still works exactly as written.

Add a resolution-order line to the list at `:66-69`:

```markdown
**Resolution order:**
1. Services registered directly on the context
2. Services from fallback contexts (recursively), in registration order
3. Services from the inherited parent context, if the subject is an attached child
4. Results are deduplicated and ordered
```

- [ ] **Step 2: Update `docs/dynamic.md` and `docs/generator.md`**

`docs/dynamic.md:55`, `:102`, `:123` and `docs/generator.md:48` teach `AddFallbackContext` as the attach mechanism. Replace each with the new call:

```csharp
subject.AttachToContext(context);
```

and for the generated constructor in `docs/generator.md:48`:

```csharp
    ((IInterceptorSubject)this).AttachToContext(context);
```

- [ ] **Step 3: Add the attachment section to `docs/tracking.md`**

Add a section after the reference-counting section (currently `:418-435`):

```markdown
### Joining and Leaving a Graph

A subject enters a lifecycle graph in one of two ways, and there are three kinds of edge on a
subject's context:

| Kind | Created by | Released by | Owner |
|---|---|---|---|
| Attach edge | `AttachToContext` | `DetachFromContext`, or the subject's last property detach | the library |
| Parent link | the lifecycle system, when a subject gains its first parent reference | the subject's last property detach | the library |
| Explicit fallback | `AddFallbackContext` | the caller | you |

```csharp
subject.AttachToContext(context);     // joins the graph, attaches the whole subtree
subject.DetachFromContext(context);   // leaves it, detaching the subtree

subject.IsAttached();                 // is this subject in a graph at all
subject.TryGetAttachContext();        // which context DetachFromContext would accept, or null
```

`AttachToContext` adopts an edge that is already there: calling `AddFallbackContext(X)` and then
`AttachToContext(X)` leaves one edge, which the detach then removes. That is the one case where an
explicit fallback becomes library-owned.

**One graph per subject.** A subject belongs to at most one lifecycle graph, and may be referenced
from any number of parents inside it. This is the model Entity Framework uses for tracked entities.
Attaching a subject that another graph already owns throws rather than half-attaching it.

**What throws:**

| Condition | Result |
|---|---|
| Attaching a subject another graph owns | `InvalidOperationException`; earlier items of the same batch stay attached |
| `AddFallbackContext` with a lifecycle-bearing context that is not the recorded attach context | `InvalidOperationException` naming `AttachToContext` |
| `RemoveFallbackContext` aimed at the attach edge | `InvalidOperationException` naming `DetachFromContext` |
| `DetachFromContext` while the subject is still referenced from a parent property | `InvalidOperationException`; remove the references first |
| Re-attaching a subject from a lifecycle callback while its own detach is unwinding | `InvalidOperationException` |

`AttachToContext` and `DetachFromContext` are not atomic against each other. Calling them
concurrently on the same subject is not supported; roots are normally attached at startup and
detached at shutdown.
```

- [ ] **Step 4: Correct the cycle workaround in `docs/tracking.md`**

At `:487-489` the workaround list recommends `DetachSubjectFromContext(subject)`. That is the
low-level descent operation and bypasses the record and edge cleanup. Replace item 1 with:

```markdown
1. Call `subject.DetachFromContext(context)` on the root that holds the cycle
```

- [ ] **Step 5: Update `docs/design/tracking-lifecycle.md`**

Five additions, per spec section 10:

1. The parent link: its ownership, the `count == 1` gate, both guards on that gate (self-context and attach-context), and the cycle argument's dependence on three guards holding together.
2. Replace the "Removed on detach" table of three `_lastProcessedValues` locations with the invariant it should have stated: an entry lives exactly as long as its property is attached.
3. The lock ordering section (`:168-177`) keeps its existing `_attachedSubjects -> SubjectRegistry._knownSubjects` pair and gains `_attachedSubjects -> _mutationLock -> _usedByContexts`, with `_mutationLock -> user code -> _attachedSubjects` named as the edge that closes the cycle. That is #404, pre-existing and not made worse here.
4. The resolved-position ordering dependency from spec section 2: a handler's observed order depends on its resolved service position, not its registration position, and `SubjectRegistry` carries no ordering attribute so where it lands is purely its registration index. No issue records this and this design preserves it.
5. The global versus per-graph reference count distinction, and how one graph per subject collapses it: `ReferenceCount` was global while `IsContextAttach` was per-graph, and that mismatch produced #207.

- [ ] **Step 6: Fold the planning decisions and corrections back into the spec**

In `docs/superpowers/specs/2026-08-04-context-inheritance-design.md`:

1. Section 8: add **behaviour change 18**, that the lifecycle path now requires an `InterceptorExecutor` for any subject it attaches, where a hand-written subject with a plain context worked before. Update the "complete list" count from seventeen to eighteen in both the section heading and section 8's opening line, and add its evidence row in section 9.
2. Section 9: correct the evidence row for change 3, which says the `_usedByContexts` probe lives in `Namotion.Interceptor.Tests`. It lives in `Namotion.Interceptor.Tracking.Tests` behind a new `InternalsVisibleTo` grant, because the core test project has no Tracking reference.
3. Section 4 and section 10's benchmark row: correct the claim that the owner claim and the count increment share one lock acquisition. They cannot, because `set.Add`'s early return sits between them.
4. Section 9's gap test list: record that the `DetachFromContext` racing a property attach case has **no test**, because the rendezvous it needs is inside `LifecycleInterceptor`'s monitor and no public seam reaches it. Leaving it unpinned is the honest outcome; a test that cannot hit the window would only look like coverage.
5. Section 8's **behaviour change 15 appears to be unobservable and should be struck or restated.** It claims a second interceptor co-registered on the attach context loses the detach notification that `master`'s executor override gave it when the attach edge is released on the property route. Trace it and the difference disappears: the handler's descent at reference count zero notifies everything resolved from the *parent's* context, and in every reachable shape that set is a superset of the attach context's, because the parent's context resolves the attach context. The one shape where they could differ, an attach context not reachable from the parent's context, is the cross-graph case that behaviour change 5 now rejects. On `master` the #207 shape never releases the attach edge at all, so the "where master notifies" clause describes a path that does not run. Verify this during implementation before striking it; if a shape does exist, it needs a test and the change stays. Either way section 9's evidence row for 15 must stop claiming a test that cannot be written.

6. Section 5's `AttachToContext` listing and section 9's "plain contexts" evidence row: **`AttachToContext` skips the record entirely when the context resolves no `ILifecycleInterceptor`**, degenerating to plain composition. Without it the generated constructor marks a plain-context subject as attached to a graph that does not exist, and that mark refuses every later attempt to join a real one. Record the consequence too: `DetachFromContext` is not the inverse of `AttachToContext` for such a subject, `RemoveFallbackContext` is. Add the same line to `docs/tracking.md`'s edge-kinds table under step 3, so the asymmetry is a documented rule rather than a surprise.

- [ ] **Step 7: Verify no em dashes were introduced**

```bash
grep -rn "—" docs/*.md docs/design/*.md docs/superpowers/specs/2026-08-04-context-inheritance-design.md
```

Expected: no output. Any hit is a violation of the project's documentation rule; restructure the sentence rather than substituting a different dash.

- [ ] **Step 8: Commit**

```bash
git add docs/
git commit -m "Docs: document attach and detach, the three edge kinds and the one-graph rule

Consumer docs stop teaching AddFallbackContext as the attach mechanism and gain the
attach API, the exception table and the one-graph-per-subject rule. The design doc
gains the parent link, the corrected lock ordering, the resolved-position ordering
dependency and the reference count distinction.

Folds three corrections back into the spec: the executor requirement now extends to
the whole lifecycle path, the leak probe lives in the Tracking test project, and the
owner claim and count increment do not share a lock acquisition."
git push
```

---

## Task 6: Branch head verification

**Files:** none modified. This task produces measurements for the pull request.

- [ ] **Step 1: Run the full suite including integration tests**

Confirm port 4840 is free first.

```bash
lsof -nP -iTCP:4840 -sTCP:LISTEN
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.slnx
```

Expected: PASS, no snapshot movement beyond the 19 accepted in Task 3.

- [ ] **Step 2: Verify the ordering oracles never moved across the whole branch**

```bash
git diff master --stat -- 'src/Namotion.Interceptor.Tracking.Tests/LifecycleInterceptorTests.*.verified.txt' \
                          'src/Namotion.Interceptor.Tracking.Tests/Change/*.verified.txt' \
                          'src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt'
```

Expected: empty. These nine files are the design's own gate. If any moved, the traversal order changed, which spec section 8 explicitly says is not on the list of accepted behaviour changes. Escalate rather than accepting.

- [ ] **Step 3: Run the benchmarks against master**

```bash
pwsh scripts/benchmark.ps1 -Filter "*RegistryBenchmark*"
pwsh scripts/benchmark.ps1 -Filter "*ContextDelegationDepthBenchmark*"
```

Multiple launches each. Record the numbers for the pull request description.

- [ ] **Step 4: Run the mutant checks**

For each mutant in spec section 9, apply it, confirm the named test fails, and revert. This is the discipline that says the tests test what we think they do. Work through them one at a time; do not batch.

Every pairing below was checked against what the named test actually asserts. Six of the spec's original pairings did not kill their mutant and have been repointed or split; the two that nothing kills are marked as such rather than given a test that would only look like coverage.

| Mutant | Must fail |
|---|---|
| Restore `IsContextAttach` to the link gate | `WhenConstructorAttachedChildIsPlacedUnderAParent_...` |
| Publish the link from `LifecycleInterceptor` instead of the handler | `AttachOrderCharacterizationTests.WhenHandlerRunsBeforeContextInheritance_...`. **Not** `RecursiveAttachTests`: the internal setter runs no descent, so under `WithLifecycle()` alone the grandchild still does not attach and that test passes either way |
| Gate the descent on `count == 1` instead of `IsContextAttach` | `WhenRootAttachedSubjectGainsItsFirstParent_ThenTheSubtreeDescentDoesNotRunAgain`. **Not** `WhenPreWiredChildIsAttachedUnderANewParent_...`: in that trace `IsContextAttach` and `count == 1` are equal, so it survives. The two diverge only for a subject already attached as a root that then gains its first parent |
| Release the link before the handlers instead of in the `finally` | `AttachOrderCharacterizationTests.WhenThreeLevelGraphIsAttached_...` |
| Delete the owner check in `ClaimOwnership` | `WhenSubjectOwnedByOneGraphIsAttachedToAnother_...` |
| Delete the ownership claim in `AttachRootSubject` | `WhenRootAttachedSubjectIsReferencedFromAnotherGraph_...`. This is the hole the review found, so the mutant is the plan's own first draft |
| Delete the ownership release in `DetachRootSubject` | `WhenDetachedRootIsAttachedToAnotherGraph_ThenOwnershipWasReleased`. **Not** `WhenTwoThreadsDetachTheSameRoot_...`, which uses a bare hand-written interceptor that never claims ownership, so deleting the release changes nothing for it |
| Delete the ownership read in `TryRecordAttachContext` | `WhenPropertyOwnedSubjectIsRootAttachedIntoAnotherGraph_...`, and it is the **only** defence, not the first of two. Measured: with the read deleted the mutant produces no exception at all, because once the edge is published the subject resolves both graphs and `ClaimOwnership`'s membership predicate then accepts |
| Delete the `finally` around the detach edge removal | `WhenDetachInterceptorThrows_...`, and only because of its edge assertion: the record is cleared before the interceptor loop, so `IsAttached()` is false and a re-attach succeeds even with the `finally` gone |
| Delete the self-context guard | `WhenSubjectReferencesItself_ThenNoSelfLinkIsPublished` |
| Delete the rollback in `AttachToContext`'s `catch` | `WhenAttachHandlerThrowsPartWay_...` |
| Put the `Debug.Assert` outside the `count == 1` branch | `AttachOrderCharacterizationTests.WhenSubjectHasTwoParents_...`, in a Debug build |
| Put the link publication outside the `count == 1` branch | `WhenConnectorItemIsAssignedUnderItsAttachParent_...`. The two-parents test does **not** kill this half: `TrySetParentContext` returns false when a link exists, so a second parent is a no-op |
| Set the link and release the attach edge instead of skipping the link | `WhenConnectorItemIsAssignedUnderItsAttachParent_...` |
| Make the reverse-entry unregistration unconditional | `WhenTwoEdgesTargetOneContext_...` |
| Route the detach cleanup through public `RemoveFallbackContext` | **Nothing kills this, and it is structural rather than a coverage gap.** Measured: every one of the three call sites clears the attach record before removing the edge, so the public guard can never fire and the internal bypass is currently unobservable. It stays because a future call site that removes before clearing would need it, but nothing today distinguishes the two |
| Drop the reference-count guard on `DetachFromContext` | `WhenSubjectIsStillReferenced_...` |
| Drop the last-property-detach release of the attach edge | both `AttachEdgeLeakTests` reproductions |
| Make the lifecycle-bearing guard test `_owner == null` | every root attach; the whole suite |
| Make the guard test only that the record is non-null | `WhenASecondLifecycleBearingContextIsAdded_ThenItThrowsEvenThoughARecordExists` |
| Delete the re-attach-during-detach throw | `WhenHandlerReAttachesSubjectDuringItsOwnDetach_...` and `WhenHandlerRootAttachesTheSubjectDuringItsOwnDetach_...`, one per entry point |
| Re-resolve the interceptors at detach | `WhenAnInterceptorIsRegisteredAfterTheAttach_...` and `WhenAnInterceptorsContextLeavesTheChain_...`. **Not** `WhenChainTurnedCyclicAfterAttach_...`, which asserts the edge comes out and does not distinguish which set was used |
| Skip the `interceptors.IsEmpty` early return in `AttachToContext` | `WhenPlainContextSubjectJoinsAGraphLater_...` (decision 4) |
| Read the reference count without `Volatile.Read` | **Nothing kills this.** It is a memory-visibility weakening with no deterministic observation. Report it as uncaught |
| Release `_owner` while not holding `_attachedSubjects`, or make the release unconditional | **Nothing kills either.** A non-owner reaching `ReleaseOwnership` requires the subject to be in a second graph's ledger, which the ownership claim already prevents, so the mutant is unreachable rather than merely hard to observe. Report both as uncaught, with that reasoning |

Three mutants have no killer and that is the honest outcome: two are unreachable given the guards above them, and one is a visibility weakening. Report them as uncaught rather than adding a test that passes for unrelated reasons.

- [ ] **Step 5: Re-review the four planning decisions against the implemented code**

These were decided from the spec alone, before any of it compiled or ran. Each is now checkable against real behaviour, and this is the last point before the pull request where changing one is cheap. Do not skip because the suite is green: three of the four are green either way.

| Decision | What to look at now that it runs |
|---|---|
| **4, the highest priority of the four** | `AttachToContext` skips the record when no interceptor resolves, so `DetachFromContext(plainContext)` returns false silently while `RemoveFallbackContext` removes that edge. Attach and detach are therefore not literal inverses for a plain context. Confirm the two pinning tests in `RootAttachContractTests` still express the intent, and decide deliberately whether the asymmetry should be documented in `docs/tracking.md` as a rule or removed by making `DetachFromContext` fall back to removing an unrecorded edge. This is the one part of decision 4 a reader could reasonably want the other way. |
| 1 | The lifecycle path now requires an `InterceptorExecutor` for every subject it attaches. Confirm the thrown message actually names the requirement usefully, and that `GetReferenceCount()` returning 0 for a non-executor did not turn any real failure into a silent zero. |
| 2 | The `InternalsVisibleTo` grant for `Namotion.Interceptor.Tracking.Tests`. If the two-edges test ended up not needing `TrySetParentContext`, drop the grant rather than leaving a widened internals surface behind. |
| 3 | Two lock acquisitions per property attach instead of one. Check this against the benchmark numbers from step 3; if attach regressed, this is the first thing to look at. |

Record the outcome of each in the report below, including "unchanged, and why" where nothing moves.

- [ ] **Step 6: Report**

The plan ends here. Updating GitHub issues (#402, #207, #410, #210, #411, #384, #412) and merging PR #419 are human-gated per spec section 10 and are **not** performed. Report:

- the recomputed subject-facing call site count against the spec's estimate of 28 across 16 files
- the benchmark numbers, both suites, against `master`
- which mutants died, which needed a new test, and which could not be caught
- any gap test whose asserted outcome differed from the design's prediction
- the spec corrections folded in during Task 5
- **the outcome of step 5's re-review of all four planning decisions**, decision 4's attach/detach asymmetry first

---

## Self-review

**Spec coverage.** Section 4's architecture is Task 3 steps 1 through 10. Section 5's sequences are steps 4 through 10, with the guard predicate at step 6 and the detach `finally` at step 10d. Section 6's migration is step 11. Section 7's aliasing rule is step 3, its exception table is Task 5 step 3. Section 8's eighteen behaviour changes each have an evidence row covered by Task 2 or Task 4. Section 9's three test categories are Tasks 1, 2 and 4; its mutant list is Task 6 step 4; its oracle inventory is Task 3 step 13 and Task 6 step 2. Section 10's staging is the five commits, its integration gate is Task 3 step 14 and Task 6 step 1, its benchmark gates are Task 3 step 16 and Task 6 step 3, its documentation list is Task 5.

Four spec items are deliberately not implemented as written, and each is called out rather than silently dropped: the `DetachFromContext`-racing-a-property-attach gap test, which no public seam can reach; the shared lock acquisition for the owner claim and the count increment, which `set.Add`'s early return makes impossible; behaviour change 15, which appears unobservable in any reachable shape; and three mutants that nothing kills, two of them because the guards above them make the mutant unreachable.

Every one of those is a claim the spec makes that the plan could not honour. They are listed in Task 5 step 6 and Task 6 step 4 so the spec ends up agreeing with what was actually built, rather than the plan quietly diverging from it.

**Type consistency.** `GetExecutor` returns `InterceptorExecutor` and is used identically in `SubjectAttachmentExtensions`, `LifecycleInterceptorExtensions`, `LifecycleInterceptor` and `ContextInheritanceHandler`. `TryClearAttachContext` has the same `out ImmutableArray<ILifecycleInterceptor>` signature at its definition in Task 3 step 6 and its call in step 7. `RemoveAttachEdge` is declared on `InterceptorSubjectContext` in step 4 and called from the executor in step 6 and from `SubjectAttachmentExtensions` in step 7, on an `InterceptorExecutor` receiver in both cases, which inherits it. `ReferenceCount` is a property everywhere. `HasParentContext` is a property, read in `ContextInheritanceHandler`'s assertion and in `ThrowIfDetachIsUnwinding`. `UsedByContextsProbe.Count` takes `IInterceptorSubjectContext` at its definition in Task 2 and is called with both a raw context and a `subject.Context` in Tasks 2 and 4.

**Placeholders.** None. Every code step carries its code; every command step carries its expected output; the two steps that say "read the file first" (Task 4 step 5, Task 5 step 5) name the exact locations to touch.
