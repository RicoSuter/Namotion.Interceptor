---
name: fix-memory-leak
description: Use when detect-heap-growth confirms a real leak (constant rate over multiple windows), or you already have known constant-rate managed heap growth with suspect types — takes a full memory dump, traces retention with gcroot, identifies the retaining field/collection, writes a failing repro test, fixes the root cause, and verifies with a second dump.
---

# Fix Memory Leak

End-to-end: dump → find retention → identify root cause → failing repro test → fix → verify.

**Prerequisite:** Growth confirmed as constant (not decelerating) via `detect-heap-growth`. If the heap rate is still decelerating or unconfirmed, stop and run that skill first — fixing a non-leak wastes time and churns the code.

## Step 1: Collect full memory dump

```bash
pgrep -af "YourApp"
# Pick the PID with a diagnostic socket: ls /tmp/dotnet-diagnostic-{PID}-*
dotnet-dump collect -p $PID -o /tmp/memdump.dmp
```

Requires `dotnet-dump` (`dotnet tool install -g dotnet-dump`).

## Step 2: Count live instances of the suspect type

Use `-live` to filter out dead-but-uncollected objects. `dotnet-dump` unlike `dotnet-gcdump` does not force a GC, so the raw heap contains reachable and unreachable objects — without `-live`, counts are noisy and overstate the leak.

```bash
dotnet-dump analyze /tmp/memdump.dmp <<< "dumpheap -stat -live -type YourSuspectType
exit" 2>&1 | grep "YourSuspectType"
```

Compare against expected live count. Example: 25K `TestNode` instances when only 1.5K should be reachable → ~23K leaked.

## Step 3: Get addresses of leaked objects

```bash
dotnet-dump analyze /tmp/memdump.dmp <<< "dumpheap -mt {MethodTable} -live -short
exit" 2>&1 | grep "^7" | tail -5
```

MT from step 2's output.

## Step 4: Find GC root (retention chain)

```bash
dotnet-dump analyze /tmp/memdump.dmp <<< "gcroot {ADDRESS}
exit" 2>&1 | grep -v "^$" | grep -v "Loading" | grep -v "Ready"
```

First run caches GC roots (slow). Read the chain bottom-up:

- **Bottom**: the leaked object.
- **Top**: the GC root (static field, thread stack, handle table).
- **Middle**: the retention chain — find the collection or field that should have released the reference.

Common retainers:

| Retainer | Meaning |
|----------|---------|
| `HashSet<T>+Entry[]` | HashSet not removing entries on detach |
| `Dictionary<K,V>+Entry[]` | Dictionary not removing entries |
| `ConcurrentDictionary+Node` | ConcurrentDictionary accumulating |
| `List<T>` | List growing without trimming |
| Event handler | Publisher retains subscriber — missing `-=` before `+=` |

Namotion-specific retainers that have shown up before: `SourceOwnershipManager._properties`, `_usedByContexts`, `_fallbackContexts`.

## Step 5: Inspect the retaining collection

```bash
dotnet-dump analyze /tmp/memdump.dmp <<< "dumpobj {COLLECTION_ADDRESS}
exit" 2>&1 | grep "_count\|Count"
```

If `_count` is much higher than expected → confirms the leak source.

## Step 6: Identify root cause

From the retention chain, answer:

- **Which field** holds the reference (e.g. `SourceOwnershipManager._properties`)?
- **What cleanup** should release it (e.g. `OnSubjectDetaching` handler)?
- **Why didn't cleanup fire** (event not raised during batch scope, wrong thread, race condition, unsubscribe before subscribe)?

## Step 7: Write a failing repro test

**This is a bug-fix TDD step. The test MUST fail on the current (broken) code before you write any fix.** See `superpowers:test-driven-development` for the discipline. If your test passes immediately, either the test is wrong or the leak isn't what you think — go back to step 4.

**Red flags — stop and reconsider:**

- Test passes first run → test doesn't actually exercise the leak.
- "I'll write the fix first, then the test" → no, that defeats the repro.
- "Current behavior is technically correct" → if so, this isn't a leak; go back and confirm the retention chain.

Start simple and add complexity **only** until the leak reproduces:

```csharp
[Fact]
public void WhenSubjectsDetached_ThenCollectionDoesNotGrow()
{
    // Arrange
    var context = InterceptorSubjectContext.Create()
        .WithFullPropertyTracking()
        .WithRegistry();
    var root = new MySubject(context);
    var lifecycle = context.TryGetLifecycleInterceptor()!;
    var retainingCollection = new HashSet<SomeReference>();
    lifecycle.SubjectAttached += change => { /* mirror production subscription */ };

    // Warm up
    for (var i = 0; i < 10; i++) { /* attach/detach cycle */ }
    var initialCount = retainingCollection.Count;

    // Act: many attach/detach cycles
    for (var i = 0; i < 500; i++) { /* attach/detach cycle */ }

    // Assert: collection must not grow with cycles
    Assert.True(retainingCollection.Count <= initialCount + 10,
        $"Collection grew from {initialCount} to {retainingCollection.Count}");
}
```

**Escalation order** — add one complexity at a time, stop as soon as the test fails:

1. Basic attach/detach (no batch scope).
2. With registry (`.WithRegistry()`).
3. With applier (`ApplySubjectUpdate` — uses batch scope).
4. With concurrent mutations (separate thread).
5. With deep graph (parent → child → grandchild).
6. With multiple structural property types (ObjectRef + Collection + Dictionary).

Run the test. **Watch it fail.** Record the assertion message — that is your pre-fix baseline.

## Step 8: Fix, then verify both ways

Fix the root cause. Verification requires **both** signals — passing test alone is not enough:

1. **Unit test goes green.** The repro from step 7 now passes against the fix. Same test, unchanged — do not weaken the assertion.
2. **Live heap stabilizes.** Re-run `detect-heap-growth` on the running app. The rate must decelerate or be zero over ≥3 windows.

If the test passes but the heap still grows, you fixed a case, not the root cause. Return to step 4 with the dump from the latest run.

## Quick reference: SOS commands

| Command | Purpose |
|---------|---------|
| `dumpheap -stat -live -type Foo` | Count **live** instances of Foo |
| `dumpheap -mt {MT} -live -short` | List addresses of live instances |
| `dumpobj {addr}` | Inspect object fields |
| `gcroot {addr}` | Find GC root (retention chain) |
| `dumpheap -stat -live` | Full live heap statistics |

Omit `-live` only when deliberately investigating finalizer-queue or pending-collection behavior.

## Common .NET leak patterns

| Pattern | Symptom | Fix |
|---------|---------|-----|
| Event handler not unsubscribed | Publisher retains subscriber | `-=` before `+=`, or use WeakReference |
| `Dictionary.Clear()` retains capacity | Backing array grows, count stable | Recreate dictionary periodically |
| HashSet not removing entries | Count grows linearly | Ensure removal on detach/dispose |
| ConcurrentDictionary nodes | `VolatileNode` count grows | Verify `Remove` is called |
| ThreadStatic pools | Capacity grows, never shrinks | Cap pool size or trim periodically |
| Context fallback chain (Namotion) | `_usedByContexts` / `_fallbackContexts` grow | Ensure `RemoveFallbackContext` fires on detach |
