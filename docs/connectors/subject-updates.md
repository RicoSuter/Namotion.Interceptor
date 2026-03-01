# Subject Updates

Subject updates enable efficient synchronization of object graphs between participants. Instead of sending full object state on every change, only the changed properties are transmitted. All subjects are identified by **stable subject IDs** (22-character base62-encoded GUIDs) that remain consistent across the lifetime of a subject, enabling correct convergence even under concurrent structural mutations.

## Flat Structure

Subject updates use a **flat dictionary structure** where all subjects are stored in a single dictionary and referenced by stable string IDs. This design:

- Eliminates circular reference issues during serialization
- Enables O(1) subject lookup by stable ID
- Makes debugging easier (all subjects visible at top level)
- Keeps each subject's data in exactly one place

### JSON Format

```json
{
  "root": "4mK9xRtL7nPqWzYbC3hJ0A",
  "subjects": {
    "4mK9xRtL7nPqWzYbC3hJ0A": {
      "name": { "kind": "Value", "value": "Parent" },
      "child": { "kind": "Object", "id": "7fG2sVdN8kMwXyTaE5iQ1B" }
    },
    "7fG2sVdN8kMwXyTaE5iQ1B": {
      "name": { "kind": "Value", "value": "Child" },
      "parent": { "kind": "Object", "id": "4mK9xRtL7nPqWzYbC3hJ0A" }
    }
  }
}
```

- `root` — The stable ID of the root subject. Null when the update only contains changes to non-root subjects (partial update without root).
- `subjects` — Dictionary of all subjects, keyed by stable subject ID.
- Each subject contains property name → property update mappings.
- References to other subjects use `id` pointing to another key in `subjects`.

### Subject IDs

All subjects are identified by stable string IDs. See the [Subject IDs section in the Registry documentation](../registry.md#subject-ids) for details on ID generation, assignment, lookup, and lifecycle management.

## Creating Updates

### Complete Update (Full State)

Use for initial synchronization:

```csharp
var update = SubjectUpdate.CreateCompleteUpdate(rootSubject, processors);
var json = JsonSerializer.Serialize(update);
```

### Partial Update (Changes Only)

Use for incremental synchronization based on tracked property changes:

```csharp
var changes = /* SubjectPropertyChange[] from change tracking */;
var update = SubjectUpdate.CreatePartialUpdateFromChanges(rootSubject, changes, processors);
var json = JsonSerializer.Serialize(update);
```

## Applying Updates

```csharp
// Apply update with source tracking (enables echo prevention)
using (SubjectChangeContext.WithSource(source))
{
    subject.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);
}
```

The applier processes updates in two steps:

1. If `root` is set and present in `subjects`, apply the root's properties and set the target's subject ID to `root`.
2. Process remaining entries in `subjects` by looking up each subject ID in the local registry's reverse index.

## Property Update Kinds

| Kind | Description |
|------|-------------|
| `Value` | Scalar value (string, number, boolean, etc.) |
| `Object` | Single nested subject (referenced by stable ID) |
| `Collection` | Ordered list of subjects (array/list) |
| `Dictionary` | Key-based dictionary of subjects |

### Value Property

```json
{
  "kind": "Value",
  "value": "John",
  "timestamp": "2024-01-10T12:00:00Z"
}
```

The `timestamp` field is optional and records when the value was last changed.

### Object Property

References another subject by stable ID:

```json
{
  "kind": "Object",
  "id": "7fG2sVdN8kMwXyTaE5iQ1B"
}
```

A null reference omits the `id` field:

```json
{
  "kind": "Object"
}
```

When applying, the applier checks if the existing object has the same subject ID (keeps it), otherwise looks up the ID in the registry (reuses if found) or creates a new subject via `ISubjectFactory`.

### Collection Property

For ordered collections (arrays, lists). All items are referenced by stable subject ID:

```json
{
  "kind": "Collection",
  "operations": [ ... ],
  "items": [ ... ],
  "count": 5
}
```

### Dictionary Property

For key-based dictionaries. Works like Collection but each item also has a `key` field. Does not support Move operations:

```json
{
  "kind": "Dictionary",
  "operations": [ ... ],
  "items": [ ... ],
  "count": 5
}
```

## Structural Operations

All structural operations reference subjects by **stable ID**, not by positional index. This ensures correct behavior under concurrent modifications.

### Operation Types

| Operation | Fields | Description |
|-----------|--------|-------------|
| `Insert` | `id`, `afterId`, `key`? | Insert a new subject. `afterId` = predecessor ID (null = head). `key` for dictionaries. |
| `Remove` | `id`, `key`? | Remove the subject with the given ID. `key` for dictionaries. |
| `Move` | `id`, `afterId` | Move an existing subject to after `afterId` (null = head). Collections only. |

### Operation JSON Examples

**Insert after a specific item:**

```json
{ "action": "Insert", "id": "newSubjectId", "afterId": "predecessorId" }
```

**Insert at head (first position):**

```json
{ "action": "Insert", "id": "newSubjectId", "afterId": null }
```

**Remove by stable ID:**

```json
{ "action": "Remove", "id": "subjectToRemoveId" }
```

**Move to after a specific item:**

```json
{ "action": "Move", "id": "subjectToMoveId", "afterId": "predecessorId" }
```

**Move to head:**

```json
{ "action": "Move", "id": "subjectToMoveId", "afterId": null }
```

**Dictionary insert with key:**

```json
{ "action": "Insert", "id": "newSubjectId", "key": "myKey" }
```

### Item References

In a complete update (no `operations`), the `items` array lists all items and defines the full ordering. In a partial update (with `operations`), it lists only items whose properties have changed.

**Collection item** (ordering comes from array position):

```json
{ "id": "subjectId" }
```

**Dictionary item** (key identifies the entry):

```json
{ "id": "subjectId", "key": "myKey" }
```

## Complete vs Partial Updates

### Complete Collection (Initial Sync)

All items listed in `items`, no `operations`. The `items` array defines the full ordering:

```json
{
  "kind": "Collection",
  "items": [
    { "id": "child1Id" },
    { "id": "child2Id" }
  ],
  "count": 2
}
```

### Partial Collection (Incremental)

Contains structural changes via `operations` and/or property updates for existing items via `items`. When `operations` is present, the `items` array contains only items whose properties have changed — it does not define ordering or structure (that is handled by the operations):

```json
{
  "kind": "Collection",
  "operations": [
    { "action": "Remove", "id": "removedChildId" }
  ],
  "items": [
    { "id": "changedChildId" }
  ],
  "count": 2
}
```

### Complete Dictionary

```json
{
  "kind": "Dictionary",
  "items": [
    { "id": "item1Id", "key": "alpha" },
    { "id": "item2Id", "key": "beta" }
  ],
  "count": 2
}
```

## Apply Order

When applying a collection or dictionary update, the behavior depends on whether `operations` is present:

**Partial update** (has `operations`):

1. **Structural operations** are applied first, in order. Each operation uses stable IDs to find subjects in the local registry.
2. **Sparse item updates** (`items`) are applied after operations. Each item's subject is looked up by ID and its property updates from `subjects` are applied. The `items` array only contains items whose properties changed — it does not affect ordering.

**Complete update** (no `operations`):

1. The `items` array defines the **full ordered state** of the collection. Each item is looked up by ID (reused if found) or created. The array position determines ordering.
2. The collection is **trimmed to `count`** if it exceeds the declared size, removing excess items from the end.

In both cases:
- Operations referencing an unknown ID (e.g., `Remove` of a subject not in the local collection) are silently skipped.
- **Insert is idempotent**: if an item with the same ID already exists in the collection, the Insert operation is skipped. This provides natural echo protection when the same update is applied multiple times.

## Examples

### Insert + Property Update

Before: `[A, B]` → After: `[A, NewItem, B]` where NewItem.name = "Charlie"

```json
{
  "root": "rootId",
  "subjects": {
    "rootId": {
      "children": {
        "kind": "Collection",
        "operations": [
          { "action": "Insert", "id": "newItemId", "afterId": "childAId" }
        ],
        "count": 3
      }
    },
    "newItemId": {
      "name": { "kind": "Value", "value": "Charlie" }
    }
  }
}
```

### Remove + Move

Before: `[A, B, C]` → After: `[C, B]`

```json
{
  "root": "rootId",
  "subjects": {
    "rootId": {
      "children": {
        "kind": "Collection",
        "operations": [
          { "action": "Remove", "id": "childAId" },
          { "action": "Move", "id": "childCId" }
        ],
        "count": 2
      }
    }
  }
}
```

Operations are applied sequentially: first A is removed, then C is moved to head (no `afterId` = head position).

## Circular References

Circular references are handled naturally by the flat structure. Each subject appears exactly once in `subjects`, and references use stable IDs:

```json
{
  "root": "parentId",
  "subjects": {
    "parentId": {
      "name": { "kind": "Value", "value": "Parent" },
      "child": { "kind": "Object", "id": "childId" }
    },
    "childId": {
      "name": { "kind": "Value", "value": "Child" },
      "parent": { "kind": "Object", "id": "parentId" }
    }
  }
}
```

## Null Collections and Dictionaries

When a collection or dictionary property is set to `null`, it is represented as `Kind=Value, Value=null`:

```json
{
  "kind": "Value",
  "value": null
}
```

An empty (non-null) collection uses `count: 0` with no items.

## Attributes

Properties can have attributes (metadata) that are updated alongside values:

```json
{
  "kind": "Value",
  "value": 25.5,
  "timestamp": "2024-01-10T12:00:00Z",
  "attributes": {
    "unit": { "kind": "Value", "value": "celsius" },
    "quality": { "kind": "Value", "value": "good" }
  }
}
```

## Implementing a Custom Client

### Subject ID ↔ Object Mapping

The client must be able to look up local objects by subject ID and vice versa:

- **ID → Object map** (`Map<string, object>`): Used to find local objects when the protocol references a subject by ID (partial updates, collection operations, object references).
- **Object → ID**: Either store the subject ID directly on each local object (e.g., a `_subjectId` property) or maintain a separate `Map<object, string>`. This is needed so that ID-based Remove/Move operations can find the correct item in a local collection by scanning items and comparing their stored IDs.

Both must be updated whenever objects are created, replaced, or removed.

### When to Register Objects

Register the subject ID ↔ object mapping in these cases:

- **Root subject**: When applying a complete update, register the root object with `update.root`.
- **Object properties**: When applying a `kind: "Object"` property, register the created/reused object with the `id` from the property update.
- **Collection/Dictionary items**: When creating a new item via Insert operation or from the `items` array, register it with the item's `id`.
- **Cleanup**: When a Remove operation removes an item, unregister it from both maps.

### Applying Collection Operations by ID

All collection operations use stable IDs instead of positional indices. To apply them, the client must scan the local collection to find items by their subject ID using the Object → ID map:

- **Remove**: Find the item in the local array whose subject ID matches `operation.id`, then splice it out.
- **Insert**: If an item with `operation.id` already exists in the collection, skip (idempotent). Otherwise, look up `operation.afterId` in the local array to find the predecessor's position. Insert the new item immediately after it. If `afterId` is null, insert at position 0 (head). If `afterId` is not found, append to end. Register the new object with `operation.id`.
- **Move**: Find the item with `operation.id` in the local array, remove it, then find `operation.afterId` to determine the insertion position (null = head). Re-insert the item there.

### Applying Dictionary Operations by Key

Dictionary operations use `operation.key` (not `operation.id`) to identify the dictionary entry:

- **Remove**: Delete the entry at `operation.key`.
- **Insert**: Create a new object, apply its properties from `subjects[operation.id]`, and set it at `dictionary[operation.key]`. Register the new object with `operation.id`.

### Complete Update Items

In a complete collection update (no `operations`), the `items` array defines the **full ordered list** of items. The array position determines collection ordering — there is no `index` field. For each item:

1. Look up `item.id` in the ID → Object map to find an existing local object. If found, reuse it.
2. If not found, create a new object and register it with `item.id`.
3. Apply the item's properties from `subjects[item.id]`.

For dictionaries, each item also has a `key` field that determines the dictionary key.

### Echo Prevention and Reconnection

- **Echo prevention**: When applying received updates, mark changes with a source identifier so your own change listener does not re-send them.
- **Reconnection**: On reconnection, request a fresh complete update to resynchronize. Subject IDs are stable, so existing local objects can be matched by ID after reconnection.

## Limitations

- **No "clear collection" operation** — clearing N items emits N individual Remove operations.
- **Non-subject collections** (`List<int>`, `Dictionary<string, string>`) use value-replacement semantics (full replacement, no granular diffing). Only `IInterceptorSubject` collections support structural diffs.
- **Conflict resolution** is last-applied-wins by message arrival order with eventual consistency via reconnection.
- **Dictionary keys** are normalized to strings during transport; non-string keys (int, enum) must be convertible via `Convert.ChangeType` or `Enum.Parse`.
- **Move operations** are only supported for Collection kind, not Dictionary kind.

## Transport

This update format is transport-agnostic. It is currently used by the [WebSocket connector](websocket.md) for real-time bidirectional synchronization.
