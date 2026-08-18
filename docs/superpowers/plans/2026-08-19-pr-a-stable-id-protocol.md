# PR A: Stable-ID Protocol, Pipeline, and Batch Scope Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the stable-ID subject-update protocol (base62 subject IDs, ID-referenced structural items, `CompleteSubjectIds` guard), the ID-resolving serialize/apply pipeline, and the lifecycle batch scope on master, replacing the index-based protocol, with all consumers adapted and the WebSocket protocol version bumped.

**Architecture:** Take the reference branch's protocol model and factory files wholesale (master never touched them), port the `LifecycleInterceptor` batch scope (the applier depends on it to keep subjects attached while they move within one update), hand-merge the applier trio to preserve master's `ChangeOrigin` stamping and sent-value survival-evidence rule inside the branch's ID-resolving structure, and adapt the two consumers with shape-level code (ConnectorTester snapshots) plus the protocol version bump. The branch's `PendingApplyBuffer`, lazy outbound ID minting, per-root apply lock, and `Diag*` public counters are NOT ported (spec tenet 1); `ProcessSubjectFromMetadata` IS ported with its trigger documented.

**Tech Stack:** .NET 9, xUnit, Verify (snapshot tests), PublicApiGenerator, BenchmarkDotNet, Connector Tester (chaos harness).

**Reference branch:** all wholesale ports come from commit `6898d3f7` (`RicoSuter/feature/websocket-structural-mutations`, fetched locally). Spec: `docs/superpowers/specs/2026-08-18-websocket-structural-stack-design.md`.

## Global Constraints

- Priorities: correctness > performance > style (AGENTS.md).
- `TreatWarningsAsErrors` is on; XML docs are generated, so every cref must resolve.
- Test naming `When<Condition>_Then<ExpectedBehavior>`; explicit `// Arrange`, `// Act`, `// Assert` comments; no hardcoded waits.
- No em dashes in docs or PR descriptions. Markdown paragraphs on one line, never hard-wrapped.
- No AI attribution anywhere (commits, PRs, comments).
- Approved breaking changes (spec): delete `SubjectCollectionOperation`, `SubjectCollectionOperationType`, `SubjectPropertyUpdate.Operations`; reshape `SubjectPropertyItemUpdate` (`Index: required object` becomes `Id: required string`; old `Id: string?` becomes `Key: string?`); `SubjectUpdate.Root` becomes `string?`. Additionally discovered during planning and to be named in the PR description: `SubjectPropertyUpdate.Count` (`int?`) is also removed (its bounds-check role is obsolete under complete-state items), and `SubjectUpdate.CompleteSubjectIds` (`HashSet<string>?`) is added (additive).
- Intermediate commits on this branch may leave sibling projects temporarily uncompilable; the PR is squash-merged, and the final state must build the full solution warnings-clean. Never end a task without the build state the task's verify step names.
- Working branch: `feature/websocket-structural-stack` (already contains the spec commits). All work happens in the current worktree.

## File Structure

| File | Action | Source |
|---|---|---|
| `src/Namotion.Interceptor.Connectors/Updates/SubjectUpdate.cs` | replace | branch wholesale |
| `src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyUpdate.cs` | replace | branch wholesale |
| `src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyItemUpdate.cs` | replace | branch wholesale |
| `src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyUpdateKind.cs` | replace | branch wholesale |
| `src/Namotion.Interceptor.Connectors/Updates/SubjectCollectionOperation.cs` | delete | |
| `src/Namotion.Interceptor.Connectors/Updates/SubjectCollectionOperationType.cs` | delete | |
| `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateBuilder.cs` | replace | branch wholesale |
| `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateFactory.cs` | replace + edit | branch, fallback block removed |
| `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateFactory.cs` | replace | branch wholesale |
| `src/Namotion.Interceptor.Connectors/Updates/Internal/CollectionDiffBuilder.cs` | delete | |
| `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplyContext.cs` | replace | hand-merged (full code in Task 3) |
| `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs` | replace | hand-merged (full code in Task 3) |
| `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs` | replace + edit | branch, writes routed through context |
| `src/Namotion.Interceptor.Connectors/Updates/SubjectUpdateExtensions.cs` | keep master | already correct signature |
| `src/Namotion.Interceptor.Connectors/Updates/SubjectUpdateDiagnostics.cs` | create | tripwire counters |
| `src/Namotion.Interceptor.WebSocket/Protocol/WebSocketProtocol.cs` | edit | version 1 to 2 |
| `src/Namotion.Interceptor.WebSocket/Server/WebSocketServerDiagnostics.cs` | edit | tripwire gauges |
| `src/Namotion.Interceptor.ConnectorTester/Snapshot/SnapshotComparer.cs`, `SnapshotIdMap.cs`, `SnapshotDiffer.cs` | edit | branch delta |
| `src/Namotion.Interceptor.ConnectorTester/appsettings.websocket-structural.json` | create | new chaos profile |
| `src/Namotion.Interceptor.Connectors.Tests/Updates/*` and `ModuleInitializer.cs` | port + adapt | branch, see Task 4 |
| `docs/connectors-subject-updates.md` | rewrite | branch draft, adjusted |

Additional files for the batch scope (Task 2): `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs` (port branch delta), `src/Namotion.Interceptor.Tracking/Lifecycle/ContextInheritanceHandler.cs` (one-line condition change), `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/BatchScopeTests.cs` (port from branch), plus the Tracking PublicApi snapshot if `CreateBatchScope` is public.

NOT ported, ever (spec): `Updates/Internal/PendingApplyBuffer.cs`, the `ProcessPropertyChange` unregistered-subject fallback ("lazy minting"), `GetApplyLock`/per-root apply lock, `Diag*` public counters on `SubjectUpdateExtensions`.

Not touched at all: `Namotion.Interceptor.Registry` (the stable-ID infrastructure `GetOrAddSubjectId`, `SetSubjectId`, `TryGetSubjectId`, `ISubjectIdRegistry.TryGetSubjectById`, and detach cleanup already exist on master), `ISubjectFactory`/`SubjectFactoryExtensions` (both needed overloads exist on master), `Namotion.Interceptor.AspNetCore` (read-only consumer, compiles unchanged), WebSocket serializer/client/handler code (no direct `Index`/`Operations` references; the shape rides through `System.Text.Json`).

---

### Task 1: Port the protocol model and factories

**Files:**
- Replace: the four model files, `SubjectUpdateBuilder.cs`, `SubjectUpdateFactory.cs`, `SubjectItemsUpdateFactory.cs` (paths above)
- Delete: `SubjectCollectionOperation.cs`, `SubjectCollectionOperationType.cs`, `CollectionDiffBuilder.cs`

**Interfaces:**
- Produces: `SubjectUpdate { string? Root; Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects; HashSet<string>? CompleteSubjectIds; }`; `SubjectPropertyItemUpdate { required string Id; string? Key; }`; `SubjectPropertyUpdate` without `Operations` and without `Count`; internal `SubjectUpdateBuilder.GetOrCreateIdWithStatus`, `MarkSubjectComplete`, `SubjectsWithPartialChanges`; internal `SubjectItemsUpdateFactory.BuildCollectionComplete/BuildDictionaryComplete/BuildCollectionUpdate/BuildDictionaryUpdate`; internal counters `SubjectUpdateFactory.MetadataFallbackSerializationCount`, `SubjectUpdateFactory.DroppedUnregisteredChangeCount`.
- Consumed by: Task 3 (appliers), Task 4 (tests), Task 6 (ConnectorTester).

- [ ] **Step 1: Copy the wholesale files from the reference commit**

```bash
git show 6898d3f7:src/Namotion.Interceptor.Connectors/Updates/SubjectUpdate.cs > src/Namotion.Interceptor.Connectors/Updates/SubjectUpdate.cs
git show 6898d3f7:src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyUpdate.cs > src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyUpdate.cs
git show 6898d3f7:src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyItemUpdate.cs > src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyItemUpdate.cs
git show 6898d3f7:src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyUpdateKind.cs > src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyUpdateKind.cs
git show 6898d3f7:src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateBuilder.cs > src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateBuilder.cs
git show 6898d3f7:src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateFactory.cs > src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateFactory.cs
git show 6898d3f7:src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateFactory.cs > src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateFactory.cs
git rm src/Namotion.Interceptor.Connectors/Updates/SubjectCollectionOperation.cs
git rm src/Namotion.Interceptor.Connectors/Updates/SubjectCollectionOperationType.cs
git rm src/Namotion.Interceptor.Connectors/Updates/Internal/CollectionDiffBuilder.cs
```

- [ ] **Step 2: Remove the lazy-minting fallback from `SubjectUpdateFactory.ProcessPropertyChange`**

In the just-copied `SubjectUpdateFactory.cs`, find the block inside `ProcessPropertyChange` that begins with `if (registeredProperty is null)` and ends with the matching `return;`. It currently serializes the change from metadata (the "fallback" path). Replace the entire `if (registeredProperty is null) { ... return; }` block with:

```csharp
        if (registeredProperty is null)
        {
            // The subject is momentarily unregistered (a concurrent structural mutation detached it).
            // Drop the change: the structural update that re-attaches the subject serializes its
            // complete state (see ProcessSubjectFromMetadata), so the value converges through that
            // path. The counter is the production tripwire for this drop.
            Interlocked.Increment(ref DroppedUnregisteredChangeCount);
            return;
        }
```

- [ ] **Step 3: Rename and reduce the factory counters**

At the top of `SubjectUpdateFactory`, replace the two counter fields with:

```csharp
    /// <summary>Tripwire: complete-state serializations of momentarily unregistered subjects (metadata path).</summary>
    internal static long MetadataFallbackSerializationCount;

    /// <summary>Tripwire: outbound changes dropped because their subject was momentarily unregistered.</summary>
    internal static long DroppedUnregisteredChangeCount;
```

In `ProcessSubjectComplete`, immediately before the `ProcessSubjectFromMetadata(subject, subjectId, builder);` call, add:

```csharp
            Interlocked.Increment(ref MetadataFallbackSerializationCount);
```

Remove the now-unused `using System.Collections;` only if the compiler flags it (the metadata path still uses `IDictionary`). Fix any other unused-using warnings the copy introduced; warnings are errors.

- [ ] **Step 4: Verify the model and factories compile in isolation**

The appliers still reference the old shapes, so a full project build fails at this point; that is expected. Confirm only that the edited factory parses by building and reading the errors: every remaining error must be in `SubjectUpdateApplier.cs`, `SubjectItemsUpdateApplier.cs`, or `SubjectUpdateApplyContext.cs`.

Run: `dotnet build src/Namotion.Interceptor.Connectors/Namotion.Interceptor.Connectors.csproj 2>&1 | grep -E "error" | grep -v "Applier|ApplyContext" | head`
Expected: no output (all errors confined to the applier trio).

- [ ] **Step 5: Commit**

```bash
git add -A src/Namotion.Interceptor.Connectors
git commit -m "Port stable-ID protocol model and factories from reference branch"
```

---

### Task 2: Port the lifecycle batch scope

The batch scope keeps a subject attached and registered while it moves between structural properties within a single update, deferring last-detach processing to scope end. The merged applier (Task 3) wraps its whole apply body in it, and the structural-churn chaos gate (Task 9) depends on it: the branch added the scope precisely because ID-based keep/move applies exposed the transient-detach race under structural churn.

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs` (apply branch delta; master moved only +3/-2 here since the merge-base, so the 3-way apply is near-clean)
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/ContextInheritanceHandler.cs:23`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/BatchScopeTests.cs` (from branch)
- Modify: `src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt` (accept: `CreateBatchScope` is public)

**Interfaces:**
- Produces: `public IDisposable LifecycleInterceptor.CreateBatchScope(IInterceptorSubjectContext rootContext)`; `TryGetLifecycleInterceptor` already exists on master (`LifecycleInterceptorExtensions.cs:11`).
- Consumed by: Task 3's merged applier.

- [ ] **Step 1: Apply the branch's LifecycleInterceptor delta onto master's file**

```bash
git diff 36dcd520 6898d3f7 -- src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs > /tmp/batchscope.patch
git apply --3way /tmp/batchscope.patch
```

If the 3-way apply conflicts (master's +3/-2 overlap), resolve by keeping master's lines and adding the branch's scope machinery around them; the branch's additions are the `BatchScope` nested class, `CreateBatchScope`, `EndBatchScope`, the deferred-detach bookkeeping they manage, and any `IsContextDetach` stamping changes. Read the surrounding master code before resolving: master reordered the registry ahead of the context-inheritance descent (#427) and restricted context inheritance (#407) after the branch forked, so any conflict here is a semantic checkpoint, not a textual one. If a resolution would change WHEN a detach event fires relative to registry removal, stop and surface it rather than guessing.

- [ ] **Step 2: Apply the ContextInheritanceHandler condition change**

At `ContextInheritanceHandler.cs:23`, change:

```csharp
            else if (change is { ReferenceCount: 0, IsPropertyReferenceRemoved: true })
```

to:

```csharp
            else if (change is { IsContextDetach: true, IsPropertyReferenceRemoved: true })
```

Rationale to preserve in a short comment above the line: under a batch scope, `ReferenceCount` can be transiently 0 while last-detach processing is deferred; keying fallback-context removal off `IsContextDetach` (which the scope stamps only when the detach actually lands) keeps a subject's inherited context alive while it moves between structural properties within one update. `IsContextDetach` already exists on master's `SubjectLifecycleChange` (line 30).

- [ ] **Step 3: Port BatchScopeTests**

```bash
git show 6898d3f7:src/Namotion.Interceptor.Tracking.Tests/Lifecycle/BatchScopeTests.cs > src/Namotion.Interceptor.Tracking.Tests/Lifecycle/BatchScopeTests.cs
```

Adapt only what fails to compile against master's current lifecycle types; assertions stay.

- [ ] **Step 4: Run the Tracking suite**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj`
Expected: clean pass including every existing lifecycle, registry-ordering, and context-inheritance test unmodified, plus the ported `BatchScopeTests`. Accept the PublicApi snapshot if its only delta is `CreateBatchScope`. Any existing lifecycle test that the port breaks is a semantic regression against #427/#407: fix the port, never the test.

Also run the registry suite, which pins the #427 ordering: `dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj`
Expected: clean pass, unmodified.

- [ ] **Step 5: Commit**

```bash
git add -A src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests
git commit -m "Port lifecycle batch scope for move-within-update apply"
```

---

### Task 3: Hand-merge the applier trio

This is the merge of two divergent rewrites. Master's side contributes `ChangeOrigin` routing (every graph write during apply must carry the update's origin so echo suppression works) and the sent-value survival-evidence rule with its convert-once reference-equality subtlety. The branch's side contributes ID resolution, `CompleteSubjectIds` gating, pre-resolution, deferred retry, and the new-subject populate-before-attach ordering. The public `ApplySubjectUpdate` signature stays exactly master's (including `Action<RegisteredSubjectProperty, SubjectPropertyUpdate>?` for the transform) so no unapproved API break occurs.

**Files:**
- Replace: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplyContext.cs` (full content below)
- Replace: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs` (full content below)
- Replace + edit: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs` (branch file, four writes rerouted)
- Keep unchanged: `src/Namotion.Interceptor.Connectors/Updates/SubjectUpdateExtensions.cs` (master's version already has the origin parameter and no lock)
- Create: `src/Namotion.Interceptor.Connectors/Updates/SubjectUpdateDiagnostics.cs`

**Interfaces:**
- Consumes: Task 1's model and factories; master's `SetValueFromOrigin(PropertyReference, ChangeOrigin, DateTimeOffset?, DateTimeOffset?, object?, object?)` extension; `ISubjectIdRegistry.TryGetSubjectById(string, out IInterceptorSubject)`; `subject.SetSubjectId(string)`, `subject.TryGetSubjectId()`; `ISubjectFactory.CreateSubject(Type, IServiceProvider?)`, `CreateSubjectCollection(Type, IEnumerable<IInterceptorSubject?>)`, `CreateSubjectDictionary(Type, IDictionary<object, IInterceptorSubject>)`; internal extension `CreateCollectionSubject(this ISubjectFactory, Type, object?, IServiceProvider?)`.
- Produces: internal `SubjectUpdateApplier.ApplyUpdate(IInterceptorSubject, SubjectUpdate, ISubjectFactory, ChangeOrigin, Action<RegisteredSubjectProperty, SubjectPropertyUpdate>?)`; internal counters `DroppedInboundSubjectUpdateCount`, `UnknownInboundPropertyCount`; public `SubjectUpdateDiagnostics` static class.

- [ ] **Step 1: Write the merged `SubjectUpdateApplyContext.cs`**

Replace the file's entire content with:

```csharp
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Context for applying a SubjectUpdate. Tracks processed subjects to prevent cycles.
/// Designed to be pooled and reused.
/// </summary>
internal sealed class SubjectUpdateApplyContext
{
    private readonly HashSet<string> _processedSubjectIds = [];
    private readonly Dictionary<string, IInterceptorSubject> _preResolvedSubjects = [];

    public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; private set; } = null!;
    public ISubjectFactory SubjectFactory { get; private set; } = null!;
    public ChangeOrigin Origin { get; private set; }
    public Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? TransformValueBeforeApply { get; private set; }

    /// <summary>
    /// The subject ID registry from the root subject's context. Stored here so that newly created
    /// subjects (whose contexts may not yet have services resolved via fallback) don't need to
    /// look up the registry themselves.
    /// </summary>
    public ISubjectIdRegistry SubjectIdRegistry { get; private set; } = null!;

    private HashSet<string>? _completeSubjectIds;

    /// <summary>
    /// Returns true if the subject ID has complete state in this update.
    /// null means all subjects are complete (e.g., a full initial-state update).
    /// </summary>
    public bool IsSubjectComplete(string subjectId)
        => _completeSubjectIds is null || _completeSubjectIds.Contains(subjectId);

    public void Initialize(
        IInterceptorSubjectContext rootContext,
        Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects,
        HashSet<string>? completeSubjectIds,
        ISubjectFactory subjectFactory,
        ChangeOrigin origin,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply)
    {
        Subjects = subjects;
        _completeSubjectIds = completeSubjectIds;
        SubjectFactory = subjectFactory;
        Origin = origin;
        TransformValueBeforeApply = transformValueBeforeApply;
        SubjectIdRegistry = rootContext.GetService<ISubjectIdRegistry>();
    }

    /// <summary>
    /// Pre-resolves all subject IDs to their instances using the live registry.
    /// Must be called before structural changes are applied, so that subjects
    /// concurrently detached by another mutation can still be found afterwards.
    /// </summary>
    public void PreResolveSubjects(IEnumerable<string> subjectIds)
    {
        foreach (var subjectId in subjectIds)
        {
            if (SubjectIdRegistry.TryGetSubjectById(subjectId, out var subject))
            {
                _preResolvedSubjects[subjectId] = subject;
            }
        }
    }

    /// <summary>
    /// Tries to resolve a subject by ID. Checks the pre-resolved cache first (captured before
    /// structural changes), then falls back to the live registry (for subjects created during
    /// the apply, e.g., by structural processing).
    /// </summary>
    public bool TryResolveSubject(string subjectId, out IInterceptorSubject subject)
    {
        if (_preResolvedSubjects.TryGetValue(subjectId, out subject!))
        {
            return true;
        }

        return SubjectIdRegistry.TryGetSubjectById(subjectId, out subject!);
    }

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="property"/> under the update's origin,
    /// using the written value as the origin's sent-value evidence. See the overload taking a
    /// separate <c>sentValue</c> for the case where the applied value was locally transformed.
    /// </summary>
    public void SetPropertyValue(PropertyReference property, DateTimeOffset? changedTimestamp, object? value)
        => SetPropertyValue(property, changedTimestamp, value, value);

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="property"/> under the update's origin.
    /// Local origins keep the unarmed write path (Local is the default and needs no stamp); for
    /// FromSource and Confirmed origins the write goes through SetValueFromOrigin so the resulting
    /// change carries the source and echo suppression works. <paramref name="sentValue"/> is the
    /// value the source semantically sent, armed as the origin's survival evidence: when a
    /// transform corrects the applied value it differs from <paramref name="value"/>, so the
    /// origin demotes to Local and the correction is not echo-suppressed back to the source.
    /// In all cases <paramref name="changedTimestamp"/> is applied as the changed timestamp so
    /// the inbound timestamp is never replaced with capture-time now.
    /// </summary>
    public void SetPropertyValue(PropertyReference property, DateTimeOffset? changedTimestamp, object? value, object? sentValue)
    {
        if (Origin.Kind == ChangeOriginKind.Local)
        {
            using (SubjectChangeContext.WithChangedTimestamp(changedTimestamp))
            {
                property.Metadata.SetValue?.Invoke(property.Subject, value);
            }
        }
        else
        {
            property.SetValueFromOrigin(Origin, changedTimestamp, null, value, sentValue);
        }
    }

    public bool TryMarkAsProcessed(string subjectId)
        => _processedSubjectIds.Add(subjectId);

    /// <summary>
    /// Clears the context for reuse. Call before returning to pool.
    /// </summary>
    public void Clear()
    {
        _processedSubjectIds.Clear();
        _preResolvedSubjects.Clear();
        _completeSubjectIds = null;
        Subjects = null!;
        SubjectFactory = null!;
        Origin = default;
        TransformValueBeforeApply = null;
        SubjectIdRegistry = null!;
    }
}
```

Note: the branch's `SubjectRegistry` property is intentionally not carried (nothing in the merged applier uses it), and `PreResolveSubjects` loses its redundant `idRegistry` parameter. If `SetValueFromOrigin`'s namespace differs (it lives in `Namotion.Interceptor.Tracking.Change` as `SubjectChangeContextExtensions`), adjust the usings until the build resolves it; do not change the call.

- [ ] **Step 2: Write the merged `SubjectUpdateApplier.cs`**

Replace the file's entire content with:

```csharp
using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Performance;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Applies SubjectUpdate instances to subjects, resolving subjects by stable ID.
/// </summary>
internal static class SubjectUpdateApplier
{
    private static readonly ObjectPool<SubjectUpdateApplyContext> ContextPool = new(() => new SubjectUpdateApplyContext());

    /// <summary>Tripwire: inbound subject updates dropped because their subject stayed unresolvable.</summary>
    internal static long DroppedInboundSubjectUpdateCount;

    /// <summary>Tripwire: inbound properties skipped because the subject does not declare them.</summary>
    internal static long UnknownInboundPropertyCount;

    public static void ApplyUpdate(
        IInterceptorSubject subject,
        SubjectUpdate update,
        ISubjectFactory subjectFactory,
        ChangeOrigin origin,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        var context = ContextPool.Rent();
        try
        {
            context.Initialize(subject.Context, update.Subjects, update.CompleteSubjectIds, subjectFactory, origin, transformValueBeforeApply);
            context.PreResolveSubjects(update.Subjects.Keys);

            // Batch scope: defer last-detach processing so subjects moving between structural
            // properties within this update stay attached and registered throughout.
            // PreResolveSubjects above handles the concurrent-mutation race (different thread);
            // the scope handles the apply-path move race (same thread).
            var lifecycle = subject.Context.TryGetLifecycleInterceptor();
            using (lifecycle?.CreateBatchScope(subject.Context))
            {
                if (update.Root is not null && update.Subjects.TryGetValue(update.Root, out var rootProperties))
                {
                    // The Root field identifies which subject ID in the update corresponds to the
                    // local root subject. The root's ID may differ between sender and receiver;
                    // Root is a mapping hint, not an identity assignment.
                    context.TryMarkAsProcessed(update.Root);
                    ApplyPropertyUpdates(subject, rootProperties, context);
                }

                // Process remaining subjects by ID lookup. Partial updates can contain changes to
                // subjects not reachable from the root's changed properties. Subjects not found on
                // the first pass are retried after all known subjects are processed: structural
                // processing in the first pass may create them.
                List<(string SubjectId, Dictionary<string, SubjectPropertyUpdate> Properties)>? deferred = null;
                foreach (var (subjectId, properties) in update.Subjects)
                {
                    if (context.TryResolveSubject(subjectId, out var targetSubject))
                    {
                        if (context.TryMarkAsProcessed(subjectId))
                        {
                            ApplyPropertyUpdates(targetSubject, properties, context);
                        }
                    }
                    else
                    {
                        deferred ??= [];
                        deferred.Add((subjectId, properties));
                    }
                }

                if (deferred is not null)
                {
                    foreach (var (subjectId, properties) in deferred)
                    {
                        if (context.SubjectIdRegistry.TryGetSubjectById(subjectId, out var targetSubject) &&
                            context.TryMarkAsProcessed(subjectId))
                        {
                            ApplyPropertyUpdates(targetSubject, properties, context);
                        }
                        else
                        {
                            // The subject was not created by structural processing and is not in the
                            // registry: drop the update. The next update carrying the subject's
                            // complete state converges it. The counter is the production tripwire.
                            Interlocked.Increment(ref DroppedInboundSubjectUpdateCount);
                            System.Diagnostics.Trace.TraceInformation(
                                $"SubjectUpdateApplier: Dropped update for unresolvable subject {subjectId} " +
                                $"({properties.Count} properties: {string.Join(", ", properties.Keys)}).");
                        }
                    }
                }
            }
        }
        finally
        {
            context.Clear();
            ContextPool.Return(context);
        }
    }

    internal static void ApplyPropertyUpdates(
        IInterceptorSubject subject,
        Dictionary<string, SubjectPropertyUpdate> properties,
        SubjectUpdateApplyContext context)
    {
        foreach (var (propertyName, propertyUpdate) in properties)
        {
            // Apply attributes first
            if (propertyUpdate.Attributes is not null)
            {
                foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
                {
                    var registeredAttribute = subject
                        .TryGetRegisteredSubject()?
                        .TryGetPropertyAttribute(propertyName, attributeName);

                    if (registeredAttribute is not null)
                    {
                        ApplyPropertyUpdate(subject, new PropertyReference(subject, registeredAttribute.Name), attributeUpdate, context);
                    }
                }
            }

            ApplyPropertyUpdate(subject, new PropertyReference(subject, propertyName), propertyUpdate, context);
        }
    }

    /// <summary>
    /// Applies a single property update using the subject's own property metadata
    /// (via <see cref="PropertyReference"/>). This does not depend on the registry:
    /// the subject always knows its own properties, even when momentarily unregistered
    /// or not yet attached (a newly created subject being populated before rooting).
    /// </summary>
    private static void ApplyPropertyUpdate(
        IInterceptorSubject subject,
        PropertyReference property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        if (!subject.Properties.ContainsKey(property.Name))
        {
            Interlocked.Increment(ref UnknownInboundPropertyCount);
            return;
        }

        switch (propertyUpdate.Kind)
        {
            case SubjectPropertyUpdateKind.Value:
                ApplyValueUpdate(subject, property, propertyUpdate, context);
                break;

            case SubjectPropertyUpdateKind.Object:
                ApplyObjectUpdate(subject, property, propertyUpdate, context);
                break;

            case SubjectPropertyUpdateKind.Collection:
                SubjectItemsUpdateApplier.ApplyCollectionUpdate(subject, property, propertyUpdate, context);
                break;

            case SubjectPropertyUpdateKind.Dictionary:
                SubjectItemsUpdateApplier.ApplyDictionaryUpdate(subject, property, propertyUpdate, context);
                break;
        }
    }

    private static void ApplyValueUpdate(
        IInterceptorSubject subject,
        PropertyReference property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        var registeredProperty = context.TransformValueBeforeApply is not null
            ? subject.TryGetRegisteredProperty(property.Name)
            : null;

        if (context.TransformValueBeforeApply is not null && registeredProperty is not null)
        {
            // Convert once BEFORE the transform runs; this converted instance is the value the
            // source semantically sent and doubles as the origin's survival evidence. If the
            // transform does not replace propertyUpdate.Value (reference unchanged), reuse that
            // same instance as the written value too: converting a JSON value twice yields two
            // reference-distinct instances for reference types (int[], DTOs), which fail the
            // reference-equality survival check and wrongly demote a genuine unchanged source
            // write to Local, defeating echo suppression. Only re-convert when the transform
            // substituted a new value, so a locally corrected value differs from the evidence
            // and the origin correctly demotes to Local.
            var rawValue = propertyUpdate.Value;
            var sentValue = ConvertValue(rawValue, property.Metadata.Type);
            context.TransformValueBeforeApply.Invoke(registeredProperty, propertyUpdate);
            var value = ReferenceEquals(propertyUpdate.Value, rawValue)
                ? sentValue
                : ConvertValue(propertyUpdate.Value, property.Metadata.Type);
            context.SetPropertyValue(property, propertyUpdate.Timestamp, value, sentValue);
        }
        else
        {
            var value = ConvertValue(propertyUpdate.Value, property.Metadata.Type);
            context.SetPropertyValue(property, propertyUpdate.Timestamp, value);
        }
    }

    private static void ApplyObjectUpdate(
        IInterceptorSubject parent,
        PropertyReference property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        if (propertyUpdate.Id is null)
        {
            context.SetPropertyValue(property, propertyUpdate.Timestamp, null);
            return;
        }

        // Resolve the target subject from the ID registry; do NOT read the backing store, to
        // avoid racing a concurrent structural mutation whose write landed before its
        // lifecycle processing.
        IInterceptorSubject targetItem;
        bool isNew;

        if (context.SubjectIdRegistry.TryGetSubjectById(propertyUpdate.Id, out var existing))
        {
            targetItem = existing;
            isNew = false;
        }
        else if (context.IsSubjectComplete(propertyUpdate.Id))
        {
            // The subject has complete state in this update, so creating it is safe.
            var serviceProvider = parent.Context.TryGetService<IServiceProvider>();
            targetItem = context.SubjectFactory.CreateSubject(property.Metadata.Type, serviceProvider);
            isNew = true;
        }
        else
        {
            // A reference to a subject that should exist but doesn't (a concurrent structural
            // mutation removed it). Skip: the next update carrying its complete state heals it.
            return;
        }

        if (isNew || targetItem.TryGetSubjectId() != propertyUpdate.Id)
        {
            targetItem.SetSubjectId(propertyUpdate.Id);
        }

        // For NEW subjects (no context, no interceptors yet): populate properties before the
        // SetValue below, so the subgraph is complete before it enters the graph and concurrent
        // readers of the backing store see fully populated instances.
        if (isNew)
        {
            if (context.Subjects.TryGetValue(propertyUpdate.Id, out var newItemProperties) &&
                context.TryMarkAsProcessed(propertyUpdate.Id))
            {
                ApplyPropertyUpdates(targetItem, newItemProperties, context);
            }
        }

        context.SetPropertyValue(property, propertyUpdate.Timestamp, targetItem);

        // For EXISTING subjects (context and interceptors live): apply properties after rooting.
        if (!isNew)
        {
            if (context.Subjects.TryGetValue(propertyUpdate.Id, out var itemProperties) &&
                context.TryMarkAsProcessed(propertyUpdate.Id))
            {
                ApplyPropertyUpdates(targetItem, itemProperties, context);
            }
        }
    }

    internal static object? ConvertValue(object? value, Type targetType)
    {
        return value switch
        {
            null => null,
            JsonElement jsonElement => jsonElement.Deserialize(targetType),
            _ => value
        };
    }
}
```

Merge provenance, for the reviewer: the structure (pre-resolve, batch scope, root-optional, deferred retry, `CompleteSubjectIds` gate, populate-before-attach) is the branch's; the origin parameter, `SetPropertyValue` routing on every graph write, and the whole `ApplyValueUpdate` transform block are master's, with `RegisteredSubjectProperty` replaced by `PropertyReference` where registry independence is required. Difference from BOTH parents: no `PendingApplyBuffer` (spec), drop-with-counter instead of buffering.

- [ ] **Step 3: Port and edit `SubjectItemsUpdateApplier.cs`**

```bash
git show 6898d3f7:src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs > src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs
```

Then reroute all four raw writes through the context so structural applies carry the origin. There are exactly four `using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp)) { metadata.SetValue?.Invoke(parent, X); }` blocks (X being `null` twice, `collection`, and `dictionary`). Replace each three-line block with the single call:

```csharp
        context.SetPropertyValue(property, propertyUpdate.Timestamp, X);
```

where X is respectively `null`, `null`, `collection`, `dictionary`. After this the file should have no remaining `SubjectChangeContext` or `metadata.SetValue` references in those four positions (the `metadata` local may become unused in one or both methods except for `metadata.Type`; keep it for `Type` access and delete it only if fully unused). Remove any using directives the compiler flags as unused.

- [ ] **Step 4: Create `SubjectUpdateDiagnostics.cs`**

```csharp
namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Process-wide tripwire counters for the subject-update pipeline. The pipeline drops rather than
/// buffers when a subject is momentarily unregistered (outbound) or unresolvable (inbound); these
/// counters make such drops observable in production, where no content-divergence detector exists.
/// A steadily rising counter under structural churn indicates a convergence gap worth investigating.
/// </summary>
public static class SubjectUpdateDiagnostics
{
    /// <summary>Outbound changes dropped because their subject was momentarily unregistered.</summary>
    public static long DroppedOutboundChanges => Volatile.Read(ref Internal.SubjectUpdateFactory.DroppedUnregisteredChangeCount);

    /// <summary>Complete-state serializations of momentarily unregistered subjects (metadata fallback path).</summary>
    public static long MetadataFallbackSerializations => Volatile.Read(ref Internal.SubjectUpdateFactory.MetadataFallbackSerializationCount);

    /// <summary>Inbound subject updates dropped because their subject stayed unresolvable.</summary>
    public static long DroppedInboundSubjectUpdates => Volatile.Read(ref Internal.SubjectUpdateApplier.DroppedInboundSubjectUpdateCount);

    /// <summary>Inbound properties skipped because the subject does not declare them.</summary>
    public static long UnknownInboundProperties => Volatile.Read(ref Internal.SubjectUpdateApplier.UnknownInboundPropertyCount);
}
```

- [ ] **Step 5: Build the Connectors project clean**

Run: `dotnet build src/Namotion.Interceptor.Connectors/Namotion.Interceptor.Connectors.csproj`
Expected: Build succeeded, 0 warnings. Iterate on compile errors without changing the semantics defined above (using directives, small signature mismatches against actual master helpers are fair game; behavioral edits are not).

- [ ] **Step 6: Commit**

```bash
git add -A src/Namotion.Interceptor.Connectors
git commit -m "Hand-merge ID-resolving appliers with origin stamping and add pipeline tripwire counters"
```

---

### Task 4: Port and adapt the Connectors test suite

**Files:**
- Create (from branch): `src/Namotion.Interceptor.Connectors.Tests/ModuleInitializer.cs`, `Updates/StableIdApplyTests.cs`, `Updates/StableIdCollectionTests.cs`, `Updates/ReconnectionConvergenceTests.cs`, `Updates/DetachedSubjectUpdateDropTests.cs`, and every `Updates/*.verified.txt` the branch adds or modifies
- Modify (from branch): `Updates/SubjectUpdateTests.cs`, `Updates/SubjectUpdateCycleTests.cs`, `Updates/SubjectUpdateCollectionTests.cs`, `Updates/SubjectUpdateDictionaryTests.cs`, `Updates/SubjectUpdateExtensionsTests.cs`
- Keep and adapt (master's, NOT the branch's deletion): `Updates/SubjectUpdateReadOnlyTypesTests.cs`
- Do NOT port: `Updates/PendingApplyBufferTests.cs`
- Modify: `VerifyChecksTests.PublicApi.verified.txt` (accept the received snapshot)

**Interfaces:**
- Consumes: everything Tasks 1-3 produced.
- Produces: the regression test `WhenStructuralChangeReferencesUnregisteredSubject_ThenCompleteStateIsSerializedFromMetadata` that later tasks and the spec's gate list refer to.

- [ ] **Step 1: Copy the branch's test files**

For each Create/Modify file listed above:

```bash
git show 6898d3f7:src/Namotion.Interceptor.Connectors.Tests/ModuleInitializer.cs > src/Namotion.Interceptor.Connectors.Tests/ModuleInitializer.cs
git show 6898d3f7:src/Namotion.Interceptor.Connectors.Tests/Updates/StableIdApplyTests.cs > src/Namotion.Interceptor.Connectors.Tests/Updates/StableIdApplyTests.cs
# ...repeat for every listed .cs file...
```

For the verified snapshots, list and copy in bulk:

```bash
git diff --name-only 36dcd520 6898d3f7 -- 'src/Namotion.Interceptor.Connectors.Tests/Updates/*.verified.txt' | while read f; do git show "6898d3f7:$f" > "$f" 2>/dev/null || git rm --ignore-unmatch "$f"; done
```

Set `DiffEngine_Disabled=true` in the environment for all test runs in this task (snapshot loops).

- [ ] **Step 2: Adapt the ported tests to this PR's decisions**

Three systematic adaptations; apply to every ported file:

1. `ApplySubjectUpdate` calls: the branch's tests call `subject.ApplySubjectUpdate(update, factory)` or with a `PropertyReference`-typed transform. Master's public signature is `ApplySubjectUpdate(update, factory, ChangeOrigin origin, Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transform = null)`. Insert `ChangeOrigin.Local` as the origin argument everywhere, and retype any transform lambdas to `RegisteredSubjectProperty`.
2. In `DetachedSubjectUpdateDropTests`, the test `WhenChangeForUnregisteredSubjectWithoutId_ThenIdIsMintedAndChangeSerialized` asserts the lazy-minting fallback this PR drops. Rewrite it to assert the drop policy: same arrangement, then assert the update does NOT contain the unregistered subject's change and that `SubjectUpdateDiagnostics.DroppedOutboundChanges` increased by 1 (capture the counter before the Act). Rename it `WhenChangeArrivesForUnregisteredSubject_ThenChangeIsDroppedAndCounted`.
3. In `ReconnectionConvergenceTests`, remove or rewrite any test that asserts pending-buffer recovery (references to buffered/recovered updates); keep every test that asserts convergence through complete-state re-sends. If a test cannot pass without the buffer, that is a finding to surface, not a test to delete silently: mark it with a comment and report it in the task's completion summary.

- [ ] **Step 3: Restore master's read-only-types coverage**

The branch deleted `SubjectUpdateReadOnlyTypesTests.cs`; this PR keeps it. Take master's file as-is (`git checkout RicoSuter/master -- src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateReadOnlyTypesTests.cs` if it was overwritten), then adapt only shape-level assertions (Items/Id/Key instead of Index; no Operations). If a test fails because the ID pipeline genuinely cannot round-trip a read-only collection type, do not delete or skip it: report the failure verbatim in the completion summary as an open question.

- [ ] **Step 4: Write the metadata-serialization regression test (the spec's PR A gate)**

Add to `src/Namotion.Interceptor.Connectors.Tests/Updates/DetachedSubjectUpdateDropTests.cs`:

```csharp
    [Fact]
    public void WhenStructuralChangeReferencesUnregisteredSubject_ThenCompleteStateIsSerializedFromMetadata()
    {
        // Arrange - a registered root whose structural change references a subject that has no
        // context and is not registered anywhere. Without the metadata path, the serializer
        // would emit a reference to an ID with no properties entry, which a receiver
        // materializes as a default-valued subject that can never converge.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context);
        var unregisteredChild = new Person { FirstName = "Detached" };

        var change = SubjectPropertyChange.Create(
            new PropertyReference(root, nameof(Person.Father)),
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            null,
            oldValue: (Person?)null,
            newValue: unregisteredChild);

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(root, [change], []);

        // Assert - the child's ID is referenced AND its complete state is present and marked complete
        var rootId = root.TryGetSubjectId();
        Assert.NotNull(rootId);
        var fatherUpdate = update.Subjects[rootId!][nameof(Person.Father)];
        Assert.Equal(SubjectPropertyUpdateKind.Object, fatherUpdate.Kind);
        Assert.NotNull(fatherUpdate.Id);
        Assert.True(update.Subjects.ContainsKey(fatherUpdate.Id!), "referenced subject must have a properties entry");
        Assert.Equal("Detached", update.Subjects[fatherUpdate.Id!][nameof(Person.FirstName)].Value);
        Assert.NotNull(update.CompleteSubjectIds);
        Assert.Contains(fatherUpdate.Id!, update.CompleteSubjectIds!);
    }
```

Adjust the `SubjectPropertyChange.Create` argument list to the actual signature at `src/Namotion.Interceptor.Tracking/Change/SubjectPropertyChange.cs:52` if parameter names or order differ; the test's assertions are the contract, the construction is plumbing.

- [ ] **Step 5: Run the suite, iterate to green, accept snapshots**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj`
Expected first run: failures from stale `.verified.txt` (including `VerifyChecksTests.PublicApi`) and possibly small adaptation misses. For snapshot failures whose `.received.txt` matches the intended new shape, replace the `.verified.txt` with the received output. For the PublicApi snapshot, review the received diff against the approved break list in Global Constraints before accepting; any API delta not on that list (beyond `CompleteSubjectIds` and the `Count` removal already named there) must be reported, not accepted.
Gate within this run: every test in `SubjectUpdateExtensionsTests` that exercises origins (`WhenApplyingFromSource_ThenSourceIsTracked` and siblings) must pass with UNMODIFIED assertions; these are master's origin-survival tests and they gate the hand-merge.

Run to confirm nothing outside the folder broke: `dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj` (full project, clean pass).

- [ ] **Step 6: Commit**

```bash
git add -A src/Namotion.Interceptor.Connectors.Tests
git commit -m "Port stable-ID test suites, keep read-only-types coverage, pin drop policy and metadata serialization"
```

---

### Task 5: WebSocket adaptation and protocol bump

**Files:**
- Modify: `src/Namotion.Interceptor.WebSocket/Protocol/WebSocketProtocol.cs:11`
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketServerDiagnostics.cs`
- Modify: whatever WebSocket tests reference the old shape (find them by building)
- Modify: `src/Namotion.Interceptor.WebSocket.Tests/VerifyChecksTests.PublicApi.verified.txt` if the received output changes

**Interfaces:**
- Consumes: `SubjectUpdateDiagnostics` from Task 3.

- [ ] **Step 1: Bump the protocol version**

In `WebSocketProtocol.cs` change `public const int Version = 1;` to `public const int Version = 2;` and extend the XML doc on the constant with one sentence: version 2 is the stable-ID update shape (`id`/`key` items, nullable `root`, `completeSubjectIds`); version 1 peers are rejected at the Welcome handshake.

- [ ] **Step 2: Surface the tripwire counters on the server diagnostics**

Add to `WebSocketServerDiagnostics` (after `CurrentSequence`):

```csharp
    /// <summary>
    /// Gets process-wide subject-update pipeline drop counters. Nonzero values that keep rising
    /// under structural churn indicate serialization or apply drops; see
    /// <see cref="Namotion.Interceptor.Connectors.Updates.SubjectUpdateDiagnostics"/>.
    /// </summary>
    public long DroppedOutboundChanges => Namotion.Interceptor.Connectors.Updates.SubjectUpdateDiagnostics.DroppedOutboundChanges;

    /// <inheritdoc cref="Namotion.Interceptor.Connectors.Updates.SubjectUpdateDiagnostics.DroppedInboundSubjectUpdates" />
    public long DroppedInboundSubjectUpdates => Namotion.Interceptor.Connectors.Updates.SubjectUpdateDiagnostics.DroppedInboundSubjectUpdates;
```

- [ ] **Step 3: Build and repair the WebSocket projects**

Run: `dotnet build src/Namotion.Interceptor.WebSocket/Namotion.Interceptor.WebSocket.csproj src/Namotion.Interceptor.WebSocket.Tests/Namotion.Interceptor.WebSocket.Tests.csproj 2>&1 | grep -E "error|Build succeeded" | head -20`
Production code is expected to compile without edits (no direct `Index`/`Operations` references exist). Test files that construct update shapes (`Serialization/SubjectUpdateFlowTests.cs`, `Protocol/PayloadTests.cs`, possibly `Server/WebSocketEchoSuppressionTests.cs`) will not; for each, first check whether the branch has an adapted version (`git show 6898d3f7:<path>`) and start from that, applying the same three adaptations as Task 4 Step 2. Otherwise adapt in place: `Index = i` becomes ordered `Items` entries with `Id`, dictionary items gain `Key`, `Operations` constructions become complete-state `Items` lists.

- [ ] **Step 4: Run WebSocket unit tests**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.WebSocket.Tests/Namotion.Interceptor.WebSocket.Tests.csproj --filter "Category!=Integration"`
Expected: clean pass; accept the PublicApi snapshot if its only delta is the two new diagnostics properties.

- [ ] **Step 5: Run WebSocket integration tests**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests/Namotion.Interceptor.WebSocket.Tests.csproj`
Expected: clean pass, including the transactional tests from #476 and all server-to-client structural sync tests, now on the ID shape.

- [ ] **Step 6: Commit**

```bash
git add -A src/Namotion.Interceptor.WebSocket src/Namotion.Interceptor.WebSocket.Tests
git commit -m "Bump WebSocket protocol to version 2 and surface pipeline drop tripwires"
```

---

### Task 6: ConnectorTester adaptation and structural chaos profile

**Files:**
- Modify: `src/Namotion.Interceptor.ConnectorTester/Snapshot/SnapshotComparer.cs`, `SnapshotIdMap.cs`, `SnapshotDiffer.cs`
- Create: `src/Namotion.Interceptor.ConnectorTester/appsettings.websocket-structural.json`

- [ ] **Step 1: Port the snapshot adaptation from the branch**

The branch's delta on these three files is small (+16/-13) and is exactly the Index-to-Id/Key adaptation. Apply it:

```bash
git diff RicoSuter/master 6898d3f7 -- src/Namotion.Interceptor.ConnectorTester/Snapshot/ > /tmp/snapshot.patch
git apply --3way /tmp/snapshot.patch || true
```

If the patch does not apply cleanly (master moved since the branch), do it by hand with the branch files as reference: sorting and comparison keys change from `item.Index?.ToString()` to `item.Key ?? item.Id`, and the `Operations = property.Operations` line disappears.

- [ ] **Step 2: Build and test ConnectorTester**

Run: `dotnet build src/Namotion.Interceptor.ConnectorTester/Namotion.Interceptor.ConnectorTester.csproj && dotnet test src/Namotion.Interceptor.ConnectorTester.Tests/Namotion.Interceptor.ConnectorTester.Tests.csproj`
Expected: clean build, clean pass. Fix remaining shape references the same way as Step 1.

- [ ] **Step 3: Add the server-side structural-churn profile (spec gate, transactions off)**

Create `src/Namotion.Interceptor.ConnectorTester/appsettings.websocket-structural.json`:

```json
{
  "ConnectorTester": {
    "Connector": "websocket",
    "CollectionCount": 500,
    "DictionaryCount": 100,
    "NumberOfBatches": 0,

    "MutatePhaseDuration": "00:05:00",
    "ConvergenceTimeout": "00:05:00",
    "MetricsReportingInterval": "00:01:00",
    "Server": {
      "Name": "server",
      "ValueMutationRate": 500,
      "StructuralMutationRate": 50
    },
    "Clients": [
      {
        "Name": "client",
        "ValueMutationRate": 0
      }
    ]
  }
}
```

Validate every key against `src/Namotion.Interceptor.ConnectorTester/Configuration/` (the configuration classes are authoritative; `StructuralMutationRate` is at `ParticipantConfiguration.cs:14`). If the tester requires a launch-profile or docs entry per profile (check how `appsettings.websocket-chaos.json` is referenced in `docs/connector-tester.md` and `Properties/launchSettings.json`), mirror that registration for the new profile.

- [ ] **Step 4: Full unit sweep**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: every project green. Fix any straggler the sweep finds (Benchmark project compile included via build).

- [ ] **Step 5: Commit**

```bash
git add -A src/Namotion.Interceptor.ConnectorTester src/Namotion.Interceptor.ConnectorTester.Tests
git commit -m "Adapt ConnectorTester snapshots to stable IDs and add server structural-churn profile"
```

---

### Task 7: Protocol documentation

**Files:**
- Rewrite: `docs/connectors-subject-updates.md` (branch's rewrite as draft)

- [ ] **Step 1: Take the branch's doc and adjust for this PR's deviations**

```bash
git show 6898d3f7:docs/connectors-subject-updates.md > docs/connectors-subject-updates.md
```

Then edit for the deltas between the branch and what PR A actually ships: remove every mention of the pending-apply buffer, recovery, lazy ID minting for changes, apply locks, and diagnostics counters that no longer exist; add a short section on the drop policy and `SubjectUpdateDiagnostics`; document `CompleteSubjectIds` and the metadata serialization path; note the removal of `Operations` and `Count` and the `Root` nullability in a "changes from the index-based protocol" list. Follow the markdown rules in Global Constraints (single-line paragraphs, no em dashes). Check for stale cross-references from other docs: `grep -rn "SubjectCollectionOperation\|Operations\b" docs/*.md` and fix hits.

- [ ] **Step 2: Commit**

```bash
git add docs/connectors-subject-updates.md docs
git commit -m "Document the stable-ID subject update protocol"
```

---

### Task 8: Benchmark gate (run by the user on a separate machine)

The user runs the benchmark on another machine; this task prepares the exact handoff and consumes the reported results. The PR stays draft until the numbers are back.

- [ ] **Step 1: Prepare the handoff**

Provide the user, in one message: the branch name to check out, the command `pwsh scripts/benchmark.ps1 -Filter "*SubjectUpdateBenchmark*" -LaunchCount 3` (they should read `docs/benchmarking.md` interpretation notes; regressions in allocations matter more than CPU per AGENTS.md priorities), and the request to also record serialized payload sizes: serialize one representative `CreateCompleteUpdate` of the benchmark model with `System.Text.Json` on master and on this branch and report both byte counts. 22-character base62 IDs replace small integers, so a size increase is expected and must be quantified, not hidden.

- [ ] **Step 2: Consume the results**

Record the reported result table verbatim in the PR description. A material regression is a user decision, not an implementer fix; do not tune code in response without a new task.

---

### Task 9: Final sweep, PR, and chaos handoff

- [ ] **Step 1: Final sweep**

Run: `dotnet build src/Namotion.Interceptor.slnx && dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"` then `dotnet test src/Namotion.Interceptor.WebSocket.Tests/Namotion.Interceptor.WebSocket.Tests.csproj`
Expected: all green, warnings-clean.

- [ ] **Step 2: Push and open the draft PR**

Push and open a draft PR titled "Stable-ID subject update protocol and ID-resolving pipeline". The description must include: the approved break list PLUS the two additions discovered in planning (`Count` removed, `CompleteSubjectIds` added); the release-notes paragraph for the AspNetCore JSON shape change (structural items serialize as `id`/`key` instead of `index`; `root` may be absent on partial updates); what was deliberately not ported and why (one line each: pending buffer, lazy minting, apply lock, digest); a "pending long-running verification" section listing the benchmark and the two Connector Tester runs as open gates the user runs on a separate machine. Do NOT mention closing #197 here; per the spec it closes only once all three stack PRs are implemented and reviewed. No AI attribution. Spec reference: `docs/superpowers/specs/2026-08-18-websocket-structural-stack-design.md`.

- [ ] **Step 3: Chaos handoff (run by the user on a separate machine)**

Hand the user, in one message alongside the Task 8 benchmark handoff: the branch name, and the two Connector Tester runs from `src/Namotion.Interceptor.ConnectorTester`:
1. `websocket-load` profile: the new-protocol baseline. Record convergence per cycle.
2. `websocket-structural` profile (new): server-side structural churn, transactions off. Record convergence and the four `SubjectUpdateDiagnostics` counters at the end; a rising `DroppedOutboundChanges`/`DroppedInboundSubjectUpdates` count with convergence still green is acceptable (drops that self-heal); convergence failures are blockers.

When the results come back, paste them into the PR description. The PR leaves draft only after both runs and the benchmark are reported green.

---

## Self-Review Notes

- Spec coverage: model+pipeline (Tasks 1, 3), lifecycle batch scope with `BatchScopeTests` and lifecycle re-validation (Task 2), metadata-serialization keep with named trigger and regression test (Tasks 1, 4), drop policy with tripwire counters and `WebSocketServerDiagnostics` exposure (Tasks 3, 5), consumer adaptation in the same PR (Tasks 5-6), protocol bump (Task 5), docs (Task 7), benchmark + payload sizes (Task 8), `websocket-load` baseline + structural-churn transactions-off gate (Task 9), origin-survival tests unmodified as merge gate (Task 4 Step 5), release-notes and versioning line (Task 9). Registry untouched (verified: infrastructure already on master). PR C/D items are out of scope by design.
- Deviations from spec discovered during planning, to surface to the user: `SubjectPropertyUpdate.Count` removal (6th break item), `CompleteSubjectIds` addition, branch deleted read-only-types coverage (this plan keeps and adapts it).
- Type consistency: `SubjectUpdateDiagnostics` property names match Task 3's counter fields; `ApplyUpdate` signature in Task 3 matches `SubjectUpdateExtensions` (master, unchanged); `CreateBatchScope` produced by Task 2 matches the call in Task 3's applier; the regression test name in Task 4 matches the gate reference.
