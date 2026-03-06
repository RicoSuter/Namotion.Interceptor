# Subject Updates

Subject updates enable efficient synchronization of object graphs between participants. The protocol is **server-authoritative** — the server holds the canonical state, and clients apply updates as received without conflict resolution or logical clocks. Instead of sending full object state on every change, only the changed properties are transmitted. All subjects are identified by **stable subject IDs** (22-character base62-encoded GUIDs) that remain consistent across the lifetime of a subject, enabling correct convergence even under concurrent structural mutations.

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

1. If `root` is set and present in `subjects`, apply the root's properties and set the target's subject ID to `root` (first sync only — subject IDs are immutable once assigned; setting the same ID again is a no-op).
2. Process remaining entries in `subjects` by looking up each subject ID in the local registry's reverse index. Entries whose subject ID is not yet in the registry are skipped — they may be created later by a structural operation (e.g., collection insert) during this same apply pass.

## Property Update Kinds

| Kind | Description |
|------|-------------|
| `Value` | Scalar value (string, number, boolean, etc.) |
| `Object` | Single nested subject (referenced by stable ID) |
| `Collection` | Ordered list of subjects (array/list) |
| `Dictionary` | Key-based dictionary of subjects |

**Important**: The `kind` must always match the property's declared type, not its current value. A property typed as `IInterceptorSubject?` must always use `kind: "Object"` — even when the value is null (represented as `kind: "Object"` with `id` omitted). A null collection uses `kind: "Collection"` with `items` omitted, and a null dictionary uses `kind: "Dictionary"` with `items` omitted (see [Null Collections and Dictionaries](#null-collections-and-dictionaries)). Clients (e.g., TypeScript) rely on consistent `kind` values to create the correct data structures.

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

For ordered collections (arrays, lists). All items are referenced by stable subject ID. The `items` array defines the complete ordered state:

```json
{
  "kind": "Collection",
  "items": [
    { "id": "child1Id" },
    { "id": "child2Id" }
  ]
}
```

### Dictionary Property

For key-based dictionaries. Works like Collection but each item also has a `key` field:

```json
{
  "kind": "Dictionary",
  "items": [
    { "id": "item1Id", "key": "alpha" },
    { "id": "item2Id", "key": "beta" }
  ]
}
```

### Item References

The `items` array lists all items and defines the complete state. Each item is referenced by stable subject ID.

**Collection item** (ordering comes from array position):

```json
{ "id": "subjectId" }
```

**Dictionary item** (key identifies the entry):

```json
{ "id": "subjectId", "key": "myKey" }
```

## Complete vs Partial Updates

Both complete and partial updates use the same complete-state format for collections and dictionaries — the `items` array always defines the full ordered state.

The difference is in scope:

- **Complete update** — Contains all properties of all reachable subjects. Used for initial sync.
- **Partial update** — Contains only changed properties. For structural changes (insert, remove, reorder), the `items` array lists the full new ordering but only new items have their properties included in `subjects`. Existing items whose properties haven't changed are referenced by ID only.

### Collection Example (Initial Sync)

```json
{
  "kind": "Collection",
  "items": [
    { "id": "child1Id" },
    { "id": "child2Id" }
  ]
}
```

### Collection Example (Item Inserted)

Before: `[A, B]` → After: `[A, NewItem, B]`

```json
{
  "root": "rootId",
  "subjects": {
    "rootId": {
      "children": {
        "kind": "Collection",
        "items": [
          { "id": "childAId" },
          { "id": "newItemId" },
          { "id": "childBId" }
        ]
      }
    },
    "newItemId": {
      "name": { "kind": "Value", "value": "Charlie" }
    }
  }
}
```

Note: Only the new item (`newItemId`) has its properties in `subjects`. Existing items (`childAId`, `childBId`) are referenced by ID only since their properties haven't changed.

### Collection Example (Item Removed)

Before: `[A, B, C]` → After: `[A, C]`

```json
{
  "root": "rootId",
  "subjects": {
    "rootId": {
      "children": {
        "kind": "Collection",
        "items": [
          { "id": "childAId" },
          { "id": "childCId" }
        ]
      }
    }
  }
}
```

### Collection Example (Items Reordered)

Before: `[A, B, C]` → After: `[C, A, B]`

```json
{
  "root": "rootId",
  "subjects": {
    "rootId": {
      "children": {
        "kind": "Collection",
        "items": [
          { "id": "childCId" },
          { "id": "childAId" },
          { "id": "childBId" }
        ]
      }
    }
  }
}
```

## Apply Order

When applying a collection or dictionary update:

1. If `items` is present, it defines the **full ordered state** of the collection. Each item is looked up by ID (reused if found) or created via `ISubjectFactory`. The array position determines ordering. An empty `items` array (`[]`) clears the collection.
2. If `items` is absent (null/omitted), the structure is unchanged — only value properties on existing items are updated.
3. For each item, if its properties are present in `subjects`, they are applied.

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

When a collection or dictionary property is set to `null`, it keeps its declared `kind` but omits the `items` field:

```json
{
  "kind": "Collection"
}
```

```json
{
  "kind": "Dictionary"
}
```

An empty (non-null) collection uses an empty `items` array (`[]`).

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

- **ID → Object map** (`Map<string, object>`): Used to find local objects when the protocol references a subject by ID (partial updates, object references).
- **Object → ID**: Either store the subject ID directly on each local object (e.g., a `_subjectId` property) or maintain a separate `Map<object, string>`.

Both must be updated whenever objects are created, replaced, or removed.

**Important**: Subject IDs are immutable once assigned. A subject's ID must not change after the first assignment. Attempting to reassign a different ID is an error.

### When to Register Objects

Register the subject ID ↔ object mapping in these cases:

- **Root subject**: When applying a complete update, register the root object with `update.root` (on first sync; subsequent applies with the same ID are a no-op).
- **Object properties**: When applying a `kind: "Object"` property, register the created/reused object with the `id` from the property update.
- **Collection/Dictionary items**: When creating a new item from the `items` array, register it with the item's `id`.

### Applying Collection Updates

The `items` array defines the complete ordered state. For each item:

1. Look up `item.id` in the ID → Object map to find an existing local object. If found, reuse it.
2. If not found, create a new object and register it with `item.id`.
3. Apply the item's properties from `subjects[item.id]` if present.
4. Replace the local collection with the new ordered list.

### Applying Dictionary Updates

The `items` array defines the complete state. For each item:

1. Look up `item.id` in the ID → Object map. If found, reuse it.
2. If not found, create a new object and register it with `item.id`.
3. Apply the item's properties from `subjects[item.id]` if present.
4. Set the object at `dictionary[item.key]`.
5. Remove any dictionary entries whose keys are not mentioned in the `items` array.

### Echo Prevention and Reconnection

- **Echo prevention**: When applying received updates, mark changes with a source identifier so your own change listener does not re-send them.
- **Reconnection**: On reconnection, request a fresh complete update to resynchronize. Subject IDs are stable, so existing local objects can be matched by ID after reconnection.

## Limitations

- **Non-subject collections** (`List<int>`, `Dictionary<string, string>`) use value-replacement semantics (full replacement, no granular diffing). Only `IInterceptorSubject` collections support structural updates.
- **Conflict resolution** is last-applied-wins by message arrival order with eventual consistency via reconnection.
- **Dictionary keys** are normalized to strings during transport; non-string keys (int, enum) must be convertible via `Convert.ChangeType` or `Enum.Parse`.

## Transport

This update format is transport-agnostic. It is currently used by the [WebSocket connector](websocket.md) for real-time bidirectional synchronization.
