# Stable Subject IDs for Bidirectional Structural Mutations

## Problem

The WebSocket connector uses index-based diff operations (Insert at index N, Remove at index M) for collections. These operations are computed against the sender's local state. When multiple participants make concurrent structural mutations, the receiver's state may differ, causing:

- Inserts at wrong positions
- Duplicate items from echo-back
- Cascading divergence that never converges

The root cause: subjects have no stable identity across replicas. Each `SubjectUpdate` message assigns ephemeral sequential IDs ("1", "2", "3") that are discarded after the message is applied. There is no way to recognize that a subject on one participant is the same logical entity as a subject on another.

## Design Goals

- Enable bidirectional structural mutations (collections, dictionaries, object refs)
- Minimal changes to existing code — no rewrite of the update pipeline
- Keep the wire format shape (same JSON envelope) — this is a **breaking protocol change** for collection operations, but the overall message structure (`root`, `subjects` dictionary) stays the same
- No opinionated conflict resolution policies baked into the library
- Simple protocol with a single ID type — easy to implement in other languages

## Solution Overview

Four changes:

1. **Stable subject IDs** — Each subject gets a lazy-generated base62-encoded GUID (22 chars) stored in its `Data` dictionary. Used everywhere as the single ID type.
2. **ID-based collection operations** — Collection Insert/Remove/Move reference subjects by stable ID instead of positional index
3. **Dictionaries minimal changes** — Already key-based, only change is reusing existing subjects by stable ID lookup on apply
4. **Simplified partial updates** — No path building from changed subject to root. Only subjects with actual changes are included. `root` optional for partial updates.

**Single ID type**: Base62-encoded GUIDs are used as `Subjects` dictionary keys, in collection operations, in item references — everywhere. No separate ephemeral IDs. One concept, simple for cross-language implementations.

## 1. Stable Subject IDs

### Storage

IDs stored in the existing `IInterceptorSubject.Data` dictionary under key `(null, "Namotion.Interceptor.SubjectId")`. Generated lazily on first access via `Guid.NewGuid()`, encoded as 22-char base62 string (pure alphanumeric, e.g., `6sFX7L0iPt4AvgD7BYfWbm`).

**Zero overhead when not using connectors**: IDs are only generated when `SubjectUpdateBuilder` builds an update message. If you only use Registry, Tracking, Validation, or Lifecycle without connectors, no IDs are created and no reverse index entries exist.

### API (in `Namotion.Interceptor.Registry` project)

Two extension methods:

- `GetOrAddSubjectId()` — Returns existing ID or atomically generates a new base62-encoded GUID via `ConcurrentDictionary.GetOrAdd` with a factory delegate. Auto-registers in the registry's reverse index if available.
- `SetSubjectId(string id)` — Assigns a known ID from an incoming update. Auto-registers in the reverse index. Only called on freshly created subjects or subjects already having the same ID — overwriting a different existing ID should never occur.

```csharp
public static string GetOrAddSubjectId(this IInterceptorSubject subject)
{
    return (string)subject.Data.GetOrAdd(
        (null, SubjectIdKey),
        static (_, s) =>
        {
            var id = GenerateSubjectId();
            s.Context.TryGetService<ISubjectRegistry>()?.RegisterStableId(id, s);
            return id;
        },
        subject)!;
}

public static void SetSubjectId(this IInterceptorSubject subject, string id)
{
    subject.Data[(null, SubjectIdKey)] = id;
    subject.Context.TryGetService<ISubjectRegistry>()?.RegisterStableId(id, subject);
}

private static string GenerateSubjectId()
{
    const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    var value = new BigInteger(Guid.NewGuid().ToByteArray(), isUnsigned: true);
    var result = new char[22];
    for (var i = 21; i >= 0; i--)
    {
        value = BigInteger.DivRem(value, 62, out var remainder);
        result[i] = chars[(int)remainder];
    }
    return new string(result);
}
```

### Registry Reverse Index

`SubjectRegistry` gains:

- `Dictionary<string, IInterceptorSubject>` reverse index (private)
- `RegisterStableId(string id, IInterceptorSubject subject)` — adds to index
- `TryGetSubjectByStableId(string id, out IInterceptorSubject subject)` — lookups
- Cleanup in the existing `IsContextDetach` lifecycle handler — removes from index

Only subjects that have had their ID accessed or set are in the index. No automatic ID assignment on registration.

### Wire Format

`SubjectUpdateBuilder.GetOrCreateId()` changes from `(++_nextId).ToString()` to `subject.GetOrAddSubjectId()`. The `Subjects` dictionary keys become base62 GUIDs. The keys are already strings and receivers treat them as opaque references, so this specific change is transparent.

## 2. ID-Based Collection Operations

### Operation Semantics

Three operation types with clear, unambiguous meaning:

| Operation | Meaning | Registry action |
|-----------|---------|-----------------|
| **Remove** | Subject removed from collection | Decrement ref count; remove from registry when count reaches 0 |
| **Insert** | New subject added to collection | Increment ref count (add to registry if new) |
| **Move** | Subject repositioned within or across collections | No ref count change |

### Fields on `SubjectCollectionOperation`

- `id` (string) — The stable ID of the target subject (required for all collection operations). Also the key in the `Subjects` dictionary for Insert operations that include the subject's properties.
- `afterId` (string, nullable) — For Insert and Move: the stable ID of the predecessor item. Null means place at head.

The existing `Index` field is renamed to `key` (optional, string) — only used for dictionary operations to specify the dictionary key. Collection operations don't use it. The old `fromIndex` field is removed (no longer needed with ID-based operations).

### Items Array (`SubjectPropertyItemUpdate`)

The `items` array lists subjects whose properties are included in this message.

- **Collections**: Each item has only `id` (the stable ID, also the Subjects dictionary key). The positional `index` field is dropped — it's redundant since items are identified by stable ID. For complete updates, the array ORDER defines collection ordering.
- **Dictionaries**: Each item has `id` + `key` (the dictionary key). The `index` field is renamed to `key`.

### CollectionDiffBuilder Changes

`GetCollectionChanges()` changes from index-based to ID-based output:

- **Remove**: Emit `id` (stable ID) of each removed item via `GetOrAddSubjectId()`
- **Insert**: Emit `id` of new item + `afterId` of its predecessor in the new collection
- **Move**: Emit `id` of moved item + `afterId` of its new predecessor. The diff builder detects items present in both old and new collections at different positions.

Internal diff computation still uses `_oldIndexMap` / `_newIndexMap` by object reference. Only the output operations change to reference stable IDs.

### SubjectItemsUpdateApplier Changes

`ApplyCollectionUpdate()` changes from index-based to ID-based:

- **Remove**: Find item by `id` in the working list, detach from collection.
- **Insert**: Look up predecessor by `afterId`. Insert new item after it. Use `registry.TryGetSubjectByStableId(id)` to reuse an existing local instance. If not found, create via `SubjectFactory` and call `SetSubjectId()`.
- **Move**: Find item by `id` in the working list, remove from current position, insert after `afterId`. Same instance, just repositioned.
- **Missing predecessor**: If `afterId` references an item not in the current collection, append to end. The server linearizes all operations and is the source of truth for ordering — clients converge via subsequent updates.

### Concurrent Insert Resolution

When multiple items are inserted after the same predecessor, order by lexicographic `id` (lower ID closer to predecessor). This is deterministic across all replicas.

No tombstones. The server serializes all operations via `_applyUpdateLock` and broadcasts the authoritative result.

## 3. Dictionaries

No structural changes needed. Dictionary operations already use keys (not positional indices), so they don't have the concurrent mutation problem that collections have.

The only change: when applying an Insert operation that references a subject, use `TryGetSubjectByStableId()` to reuse an existing local instance instead of always creating a new one. This prevents duplicates from echo-back.

The server hub linearizes all concurrent operations. No additional conflict resolution policy is imposed.

## 4. Update Types

Four update types, distinguished by `root` presence and `operations` presence:

### Complete Update (Welcome)

Sent on initial connection or reconnection. Full graph snapshot. `root` identifies the graph entry point.

```json
{
  "root": "R",
  "subjects": {
    "R": {
      "Collection": {
        "kind": "Collection",
        "items": [{ "id": "A" }, { "id": "B" }, { "id": "C" }],
        "count": 3
      },
      "Items": {
        "kind": "Dictionary",
        "items": [{ "key": "k1", "id": "D" }, { "key": "k2", "id": "E" }],
        "count": 2
      },
      "ObjectRef": { "kind": "Object", "id": "F" }
    },
    "A": { "StringValue": { "kind": "Value", "value": "a", "timestamp": "..." } },
    "B": { ... }, "C": { ... }, "D": { ... }, "E": { ... }, "F": { ... }
  }
}
```

Consumer action: rebuild the stable ID registry from all subjects in the update, discarding any entries not present. The receiver can match existing local subjects by stable ID instead of creating fresh instances, improving reconnection behavior.

### Partial Value Update

Only subjects with changed value properties. No `root`, no structural operations, no intermediates.

```json
{
  "subjects": {
    "A": { "StringValue": { "kind": "Value", "value": "updated", "timestamp": "..." } }
  }
}
```

With stable IDs, the builder no longer needs to walk from changed subject up to root (`BuildPathToRoot` is removed). Only subjects with actual changes are included — no intermediate parent/grandparent subjects.

### Partial Structural Diff

Collection/dictionary operations on existing collections. `operations` present = diff mode. The `items` array includes only newly inserted subjects.

```json
{
  "subjects": {
    "R": {
      "Collection": {
        "kind": "Collection",
        "operations": [
          { "action": "Remove", "id": "B" },
          { "action": "Insert", "id": "G", "afterId": "A" },
          { "action": "Move", "id": "C", "afterId": "G" }
        ],
        "items": [{ "id": "G" }],
        "count": 3
      }
    },
    "G": { "StringValue": { "kind": "Value", "value": "new", "timestamp": "..." } }
  }
}
```

Consumer action: apply operations in order, update ref counts. Only the parent subject is included — no intermediates above it.

### Partial Complete Collection

A collection/dictionary assigned for the first time (was null) or fully replaced. No `operations` — the `items` array lists ALL items in order.

```json
{
  "subjects": {
    "R": {
      "Collection": {
        "kind": "Collection",
        "items": [{ "id": "X" }, { "id": "Y" }, { "id": "Z" }],
        "count": 3
      }
    },
    "X": { ... }, "Y": { ... }, "Z": { ... }
  }
}
```

Consumer action: replace the entire local collection. Decrement ref counts for old items, increment for new items.

### Distinguishing Diff vs Complete

**Rule**: `operations` present = diff-based (apply operations). `operations` absent = complete state (replace entire collection). This matches the current protocol behavior.

## 5. What Doesn't Change

- Value property updates (timestamps, convergence)
- Object reference wire format (consumers need to track ref counts for ObjectRef changes — see consumer guide)
- Dictionary diff builder logic (already key-based, only wire format field renames)
- ChangeQueueProcessor (buffering, deduplication)
- Source filtering / echo-back prevention

## 6. Consumer Implementation Guide

Any consumer (regardless of language) that applies updates from this protocol needs:

### Stable ID Registry with Reference Counting

Maintain a persistent map of `stableId → (local object, ref count)` across messages. The ref count tracks how many collections, dictionaries, and object references currently point to this subject. This map is needed for:

- **Move operations**: Find the local object being repositioned to reuse it
- **Remove operations**: Find the local object to detach by its `id`
- **Insert with existing subject**: Reuse existing local objects to preserve local state (e.g., UI bindings)
- **Reconnection**: Match incoming subjects to existing local objects by stable ID
- **Memory management**: When ref count reaches 0, the subject is no longer part of the graph and can be removed from the map to prevent memory leaks

### Applying Collection Updates

**If `operations` is present** — apply diff operations in order:

1. **Remove**: Look up the item by `id` in the local collection. Remove from the collection. Decrement the ref count in the stable ID registry. If ref count reaches 0, remove the entry from the registry.
2. **Insert**:
   - Find the predecessor item by `afterId` in the local collection
   - If `afterId` is null, insert at the head
   - If `afterId` is not found, append to end
   - Check the stable ID registry for an existing local object with this `id`. If found, reuse it. If not, create a new object and register it with ref count 1.
   - Increment the ref count.
   - Insert after the predecessor
3. **Move**: Find the item by `id`, remove from current position, insert after `afterId`. Same object instance, just repositioned. No ref count change.
4. **Concurrent inserts**: If multiple items share the same `afterId`, order by lexicographic `id` (lower ID closer to predecessor)

**If `operations` is absent** — complete replacement:

Replace the entire local collection with the items listed in the `items` array (array order = collection order). Decrement ref counts for all old items, increment for all new items. Reuse existing local objects by stable ID where possible.

### Applying Dictionary Operations

No changes from current behavior. Dictionary operations use keys (not indices), so they work correctly as-is. On Insert, increment the ref count (or register with ref count 1 if new). On Remove, decrement the ref count; remove from registry when it reaches 0.

### Applying Object Reference Changes

When an Object kind property changes (the `id` field on `SubjectPropertyUpdate`):
- If the old target is not null: decrement its ref count. Remove from registry when it reaches 0.
- If the new target is not null: increment its ref count (or register with ref count 1 if new).
- Setting to null: only the decrement applies.

### Cleanup

Reference counting handles cleanup automatically — when a subject's ref count reaches 0, it is removed from the stable ID registry. No explicit "detached" signal is needed.

On receiving a complete update (Welcome), rebuild the stable ID registry from the subjects in the update — this implicitly prunes any stale entries and resets ref counts.

**C# consumers** already have equivalent cleanup via the `IsContextDetach` lifecycle handler in `SubjectRegistry`, which fires when a subject loses its last parent reference.

## Files to Modify

| File | Change |
|------|--------|
| `Namotion.Interceptor.Registry/SubjectRegistryExtensions.cs` (new or existing) | `GetOrAddSubjectId()`, `SetSubjectId()`, `GenerateSubjectId()` |
| `Namotion.Interceptor.Registry/SubjectRegistry.cs` | Reverse index + `RegisterStableId` + `TryGetSubjectByStableId` + cleanup |
| `Namotion.Interceptor.Registry/Abstractions/ISubjectRegistry.cs` | Add reverse index methods to interface |
| `Namotion.Interceptor.Connectors/Updates/SubjectUpdate.cs` | Make `Root` optional (nullable) for partial updates |
| `Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateBuilder.cs` | Use `GetOrAddSubjectId()` instead of counter |
| `Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateFactory.cs` | Remove `BuildPathToRoot`, simplify partial update creation |
| `Namotion.Interceptor.Connectors/Updates/SubjectCollectionOperation.cs` | Add `afterId` field, rename `Index` to `Key` (optional), remove `FromIndex`. Existing `id` becomes stable ID. |
| `Namotion.Interceptor.Connectors/Updates/SubjectPropertyItemUpdate.cs` | Drop `Index` for collections (use `id` only), rename to `Key` for dictionaries |
| `Namotion.Interceptor.Connectors/Updates/Internal/CollectionDiffBuilder.cs` | Emit ID-based Remove/Insert/Move instead of index-based |
| `Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs` | Apply by stable ID lookup instead of index |
| `Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateFactory.cs` | Emit stable IDs in operations |
