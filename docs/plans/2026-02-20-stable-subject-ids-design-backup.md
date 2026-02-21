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
- Keep the wire format backward-compatible (same JSON structure)
- No opinionated conflict resolution policies baked into the library
- Simple protocol with a single ID type — easy to implement in other languages (e.g., TypeScript)

## Solution Overview

Three changes:

1. **Stable subject IDs** — Each subject gets a lazy-generated base62-encoded GUID (22 chars) stored in its `Data` dictionary. Used everywhere as the single ID type.
2. **ID-based collection operations** — Collection Insert/Remove reference subjects by stable ID instead of positional index
3. **Dictionaries unchanged** — Already key-based, no positional conflicts

**Single ID type**: Base62-encoded GUIDs are used as `Subjects` dictionary keys, in collection operations, in item references — everywhere. No separate ephemeral IDs. One concept, simple for cross-language implementations.

**Payload impact**: ~25% increase in message size compared to short sequential IDs. Acceptable trade-off for protocol simplicity. Can be mitigated with WebSocket `permessage-deflate` compression if needed.

## 1. Stable Subject IDs

### Storage

IDs stored in the existing `IInterceptorSubject.Data` dictionary under key `(null, "Namotion.Interceptor.SubjectId")`. Generated lazily on first access via `Guid.NewGuid()`, encoded as 22-char base62 string (pure alphanumeric, e.g., `6sFX7L0iPt4AvgD7BYfWbm`).

### API (in `Namotion.Interceptor.Registry` project)

Two extension methods:

- `GetOrAddSubjectId()` — Returns existing ID or atomically generates a new base62-encoded GUID via `ConcurrentDictionary.GetOrAdd` with a factory delegate. Auto-registers in the registry's reverse index if available.
- `SetSubjectId(string id)` — Assigns a known ID from an incoming update. Auto-registers in the reverse index.

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

`SubjectUpdateBuilder.GetOrCreateId()` changes from `(++_nextId).ToString()` to `subject.GetOrAddSubjectId()`. The `Subjects` dictionary keys become base62 GUIDs. This is **not a breaking change** in structure — the keys are already strings, and receivers treat them as opaque references.

## 2. ID-Based Collection Operations

### New Fields on `SubjectCollectionOperation`

- `subjectId` (string) — The stable ID of the item being inserted or removed
- `afterId` (string, nullable) — For Insert: the stable ID of the predecessor item. Null means insert at head.

The existing `Index` field remains for dictionary operations (where the key is the index).

### Wire Format Examples

Value update:
```json
{
  "root": "6sFX7L0iPt4AvgD7BYfWbm",
  "subjects": {
    "6sFX7L0iPt4AvgD7BYfWbm": {
      "StringValue": { "kind": "Value", "value": "hello", "timestamp": "..." }
    }
  }
}
```

Structural update:
```json
{
  "root": "6sFX7L0iPt4AvgD7BYfWbm",
  "subjects": {
    "6sFX7L0iPt4AvgD7BYfWbm": {
      "Collection": {
        "kind": "Collection",
        "operations": [
          { "action": "Remove", "subjectId": "3kPQ9mNwRx2YvgE8CZhXbn" },
          { "action": "Insert", "afterId": "8uHZ9N2kRv6CxiG0EbjWdp", "subjectId": "7tGY8M1jQu5BwhF9DaiVco", "id": "7tGY8M1jQu5BwhF9DaiVco" }
        ],
        "items": [{ "id": "7tGY8M1jQu5BwhF9DaiVco", "index": 0 }],
        "count": 22
      }
    },
    "7tGY8M1jQu5BwhF9DaiVco": { "StringValue": { ... }, "IntValue": { ... } }
  }
}
```

The `id` field in Insert and in `items` references the same stable ID as the `Subjects` dictionary key. Single ID type throughout.

### CollectionDiffBuilder Changes

`GetCollectionChanges()` changes from index-based to ID-based output:

- **Remove**: Emit `subjectId` of each removed item via `GetOrAddSubjectId()`
- **Insert**: Emit `subjectId` of new item + `afterId` of its predecessor in the new collection
- **Move**: Expressed as Remove + Insert of the same subject (no new instances created, just repositioned)

Internal diff computation still uses `_oldIndexMap` / `_newIndexMap` by object reference. Only the output operations change to reference stable IDs.

### SubjectItemsUpdateApplier Changes

`ApplyCollectionUpdate()` changes from index-based to ID-based:

- **Remove**: Find item by `subjectId` in the working list, detach from collection. The subject instance stays alive — it is not destroyed.
- **Insert**: Look up predecessor by `afterId`. Insert new item after it. Use `registry.TryGetSubjectByStableId(subjectId)` to reuse an existing local instance. If not found, create via `SubjectFactory` and call `SetSubjectId()`.
- **Missing predecessor**: If `afterId` references an item not in the current collection, append to end. The server linearizes all operations and is the source of truth for ordering — clients converge via subsequent updates.

### Concurrent Insert Resolution

When multiple items are inserted after the same predecessor, order by lexicographic `subjectId` (lower ID closer to predecessor). This is deterministic across all replicas.

No tombstones. The server serializes all operations via `_applyUpdateLock` and broadcasts the authoritative result.

## 3. Dictionaries

No structural changes needed. Dictionary operations already use keys (not positional indices), so they don't have the concurrent mutation problem that collections have.

The only change: when applying an Insert operation that references a subject, use `TryGetSubjectByStableId()` to reuse an existing local instance instead of always creating a new one. This prevents duplicates from echo-back.

The server hub linearizes all concurrent operations. No additional conflict resolution policy is imposed.

## 4. Complete Updates (Welcome)

No structural changes needed. Complete updates already send full state with all items. With stable IDs as `Subjects` dictionary keys, the receiver can match existing local subjects by stable ID instead of creating fresh instances every time, improving reconnection behavior.

## 5. What Doesn't Change

- Value property updates (timestamps, convergence)
- Object reference handling
- Dictionary diff builder (already key-based)
- ChangeQueueProcessor (buffering, deduplication)
- Source filtering / echo-back prevention
- Wire format JSON structure (`SubjectUpdate.Subjects` is still `Dictionary<string, ...>`)

## Files to Modify

| File | Change |
|------|--------|
| `Namotion.Interceptor.Registry/SubjectRegistryExtensions.cs` (new or existing) | `GetOrAddSubjectId()`, `SetSubjectId()`, `GenerateSubjectId()` |
| `Namotion.Interceptor.Registry/SubjectRegistry.cs` | Reverse index + `RegisterStableId` + `TryGetSubjectByStableId` + cleanup |
| `Namotion.Interceptor.Registry/Abstractions/ISubjectRegistry.cs` | Add reverse index methods to interface |
| `Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateBuilder.cs` | Use `GetOrAddSubjectId()` instead of counter |
| `Namotion.Interceptor.Connectors/Updates/SubjectCollectionOperation.cs` | Add `subjectId`, `afterId` fields |
| `Namotion.Interceptor.Connectors/Updates/Internal/CollectionDiffBuilder.cs` | Emit ID-based Remove/Insert instead of index-based |
| `Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs` | Apply by stable ID lookup instead of index |
| `Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateFactory.cs` | Emit stable IDs in operations |
