# Deferred Subject Removal

## Problem

When a subject moves between structural properties within the same update (e.g., DictA → DictB), the `SubjectUpdateApplier` processes properties sequentially. If the source property (DictA) is processed first, the subject is fully detached — removed from both `_knownSubjects` and `_subjectIdToSubject`, with parent/child cleanup executed. When the target property (DictB) is processed, the subject is re-attached from scratch.

During the gap between detach and re-attach:

1. **`_subjectIdToSubject`** is missing the subject → the applier's own `TryGetSubjectById` fails for other properties referencing the same subject within the same update.
2. **`_knownSubjects`** is missing the subject → `TryGetRegisteredSubject()` returns null → the CQP filter (running on a background thread) drops value changes for the subject → permanent divergence.
3. **Parent/child links** are cleaned up → `RegisteredSubject.Parents` and `RegisteredSubjectProperty.Children` are inconsistent until re-attachment completes.

Subject swaps (DictA: X↔Y, DictB: Y↔X) make this unsolvable by processing order — no safe order exists.

## Solution: SuppressRemoval

Add `IDisposable SuppressRemoval()` to `SubjectRegistry`. During suppression, context-detach cleanup is deferred — subjects stay in `_knownSubjects` and `_subjectIdToSubject`, and parent/child cleanup is skipped. On resume, only subjects that are genuinely orphaned (not re-attached by any thread) are cleaned up.

### Thread Model

The suppression counter is `[ThreadStatic]` — each thread independently controls whether its lifecycle-triggered detaches are deferred. This ensures:

- **Thread A** (applier, with scope): ContextDetach is deferred. Subject stays visible in both maps.
- **Thread B** (no scope, concurrent mutation): ContextDetach executes immediately. Not affected by Thread A's scope.

The deferred detaches set is also `[ThreadStatic]` — each thread tracks only its own deferred subjects.

### Resume Logic

On scope dispose (counter reaches zero), for each deferred subject, the resume logic checks **actual state** rather than bookkeeping:

```
lock (_knownSubjects):
    for each subject in s_deferredDetaches:
        if _knownSubjects.TryGetValue(subject, out registered):
            if registered.Parents.Length == 0:
                // Genuinely orphaned — execute full cleanup
                execute parent/child cleanup
                _knownSubjects.Remove(subject)
                _subjectIdToSubject.Remove(subjectId)
            // else: has parents → re-attached by some thread → skip
        // else: not in _knownSubjects → already removed by another thread → skip
    clear s_deferredDetaches
```

Checking actual parent state (rather than tracking re-attachments) makes the design stateless and correct regardless of what other threads did during the suppression window.

### Why This Is Correct

**PropertyReferenceRemoved/Added always run immediately** on all threads, inside `lock(_knownSubjects)`. This means `RegisteredSubject.Parents` is always up-to-date at the moment of resume.

| Scenario | Resume behavior |
|----------|-----------------|
| Move (DictA → DictB) | Subject has parents (DictB added one) → skip removal |
| Genuine removal | Subject has no parents → execute full cleanup |
| Cross-thread reattach | Thread B added parents during window → skip removal |
| Cross-thread removal | Thread B already removed from `_knownSubjects` → skip (not found) |
| Same subject deferred by two threads | First resume cleans up or skips; second finds nothing → skip |

### What Is Deferred

On ContextDetach when `s_suppressRemovalCount > 0`:

- `_knownSubjects.Remove(subject)` — deferred
- `_subjectIdToSubject.Remove(subjectId)` — deferred
- Parent/child cleanup loop (iterating detaching subject's properties, removing parent refs from children, clearing children lists) — deferred

### What Still Runs Immediately

- `PropertyReferenceRemoved` — per-link parent/child removal (always correct, always immediate)
- `PropertyReferenceAdded` — per-link parent/child addition (always correct, always immediate)
- `ContextAttach` — subject registration (if subject is already in `_knownSubjects` due to deferred removal, reuses existing `RegisteredSubject`)

### Call Site

`SubjectUpdateApplier.ApplyUpdate` wraps the entire structural processing in the scope:

```csharp
var registry = subject.Context.TryGetService<ISubjectRegistry>() as SubjectRegistry;
using (registry?.SuppressRemoval())
{
    // All structural processing (root path + remaining subjects)
}
```

By the time `ApplyUpdate` returns, the scope is disposed, deferred cleanup has run, and registry state is fully consistent with model state.

### Nested Scopes

The counter supports nesting. First dispose decrements to N-1 (no processing). Last dispose decrements to 0 (processes all deferred detaches). This allows composing scoped operations without premature cleanup.

### Performance Impact

- **Per-write overhead during suppression**: Zero. The only check is `s_suppressRemovalCount > 0` (ThreadStatic read, no lock needed — already inside `lock(_knownSubjects)` in `HandleLifecycleChange`).
- **Resume overhead**: One iteration over `s_deferredDetaches` inside `lock(_knownSubjects)`. For typical update sizes (10-100 subjects), negligible.
- **No suppression active**: Zero overhead. The `s_suppressRemovalCount` check is a single comparison against zero.

## Files To Modify

| File | Change |
|------|--------|
| `SubjectRegistry.cs` | Add ThreadStatic fields, modify `HandleLifecycleChange` detach path, add `SuppressRemoval()`/resume logic |
| `SubjectUpdateApplier.cs` | Wrap apply in `using (registry?.SuppressRemoval())` |
| `SubjectIdTests.cs` | Unit tests for suppression/resume |
| `StableIdApplyTests.cs` | Integration test for subject move during apply |

## Documentation Updates

| File | Change |
|------|--------|
| `docs/registry.md` | Add "Deferred Subject Removal" section explaining `SuppressRemoval()` API |
| `docs/connectors-subject-updates.md` | Add section explaining deferred removal during structural apply |
| `docs/plans/fixes.md` | Close "Open Problem" and add as Fix 17 |
