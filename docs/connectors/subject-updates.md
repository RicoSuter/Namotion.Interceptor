# Subject Updates

Subject updates enable efficient synchronization of object graphs between participants. The protocol is **server-authoritative**: the server holds the canonical state, and clients apply updates as received without conflict resolution or logical clocks. Instead of sending full object state on every change, only the changed properties are transmitted. All subjects are identified by **stable subject IDs** (22-character base62-encoded GUIDs) that remain consistent across the lifetime of a subject, enabling correct convergence even under concurrent structural mutations.

## Sync Flow

1. **Client connects** to the server (e.g., via WebSocket).
2. **Server sends a complete update** containing the full state of the object graph.
3. Both sides send **partial updates** as properties change, containing only the changed properties.

The protocol is bidirectional: both sides can send updates. However, the server is authoritative: if there is a conflict, the server's state wins. See the [WebSocket connector](websocket.md) for transport-specific details.

## JSON Schema

Subject updates use a **flat dictionary structure** where all subjects are stored in a single dictionary and referenced by stable string IDs. This design eliminates circular reference issues, enables O(1) subject lookup, and keeps each subject's data in exactly one place.

```
SubjectUpdate {
  root: string?                  // sender's root subject ID (mapping hint)
  subjects: Map<string, Map<string, PropertyUpdate>>  // subjectId → propertyName → update
}

PropertyUpdate {
  kind: "Value" | "Object" | "Collection" | "Dictionary"
  value: any?                    // Value only
  id: string?                    // Object only
  items: ItemRef[]?              // Collection/Dictionary only
  timestamp: string?             // ISO 8601, optional, all kinds
  attributes: Map<string, PropertyUpdate>?  // optional, all kinds
}

ItemRef {
  id: string                     // subject ID of the item
  key: string?                   // Dictionary only
}
```

### Fields

- `root`: The sender's subject ID for the root subject. This is a **mapping hint** that tells the receiver which entry in `subjects` corresponds to the local root subject. The root's ID may differ between sender and receiver because each side generates its own IDs independently. Null when the root subject is not part of this update.
- `subjects`: All subjects in the update, keyed by the sender's subject IDs. Each subject contains property name to property update mappings.

### Subject IDs

All subjects are identified by stable string IDs (22-character base62-encoded GUIDs). See the [Subject IDs section in the Registry documentation](../registry.md#subject-ids) for details on ID generation, assignment, lookup, and lifecycle management.

Subject IDs are **immutable** once assigned. A subject's ID must not change after the first assignment. Attempting to reassign a different ID is an error.

### Property Names

Property names default to **UpperCamelCase** (PascalCase), matching the C# property names. Specific server-to-client implementations may apply transformations (e.g., to camelCase). This is up to the transport or connector configuration.

## Complete vs Partial Updates

Both complete and partial updates use the same JSON format and the same complete-state semantics for collections and dictionaries. The `items` array always defines the full ordered state.

The difference is in **scope**:

- **Complete update**: Contains all properties of all reachable subjects. Used for initial sync. Every subject in the graph appears in `subjects` with all its properties. `root` is always set.
- **Partial update**: Contains only changed properties. Unchanged subjects and properties are omitted entirely. `root` is set when the root subject has changes in this update, null otherwise. For structural changes (insert, remove, reorder), the `items` array lists the full new ordering but only new items have their properties included in `subjects`. Existing items whose properties haven't changed are referenced by ID only.

### Creating Updates

To create a **complete update**, traverse the entire object graph starting from the root subject. For each subject, generate a stable subject ID (if one doesn't exist yet), and include all of its properties in the `subjects` dictionary. Set `root` to the root subject's ID.

To create a **partial update**, collect the set of property changes since the last update. For each changed property, include its subject (by ID) and the changed property in the `subjects` dictionary. For structural changes (collection/dictionary), include the full `items` array with the new state. Include full properties for any newly created subjects. Set `root` to the root subject's ID if the root has changes, otherwise set it to null.

## Property Update Kinds

Each property update has a `kind` that determines how it is serialized and applied.

| Kind | Description |
|------|-------------|
| `Value` | Scalar value (string, number, boolean, etc.) |
| `Object` | Single nested subject (referenced by stable ID) |
| `Collection` | Ordered list of subjects (array/list) |
| `Dictionary` | Key-based dictionary of subjects |

**Important**: The `kind` must always match the **property's declared type**, not its current value. A property typed as `IInterceptorSubject?` must always use `kind: "Object"`, even when the value is null. Clients rely on consistent `kind` values to create the correct data structures.

### Value

Scalar replacement. The `value` field carries the JSON-serialized property value. This can be any JSON-compatible type (string, number, boolean, null, arrays, objects). Non-primitive types (e.g., enums, DateTimes, custom value objects) are serialized as their JSON representation. The `timestamp` field is optional and records when the value was last changed.

```json
{
  "kind": "Value",
  "value": "John",
  "timestamp": "2024-01-10T12:00:00Z"
}
```

### Object

References another subject by stable ID. A null reference omits the `id` field.

```json
{
  "kind": "Object",
  "id": "7fG2sVdN8kMwXyTaE5iQ1B"
}
```

```json
{
  "kind": "Object"
}
```

### Collection

Ordered list of subjects. The `items` array defines the **complete ordered state** where array position determines ordering.

```json
{
  "kind": "Collection",
  "items": [
    { "id": "child1Id" },
    { "id": "child2Id" }
  ]
}
```

### Dictionary

Key-based dictionary of subjects. Works like Collection but each item also has a `key` field.

```json
{
  "kind": "Dictionary",
  "items": [
    { "id": "item1Id", "key": "alpha" },
    { "id": "item2Id", "key": "beta" }
  ]
}
```

### Null Collections and Dictionaries

When a collection or dictionary property is `null`, it keeps its declared `kind` but omits `items`:

```json
{ "kind": "Collection" }
```

```json
{ "kind": "Dictionary" }
```

An empty (non-null) collection or dictionary uses an empty `items` array (`[]`).

### Attributes

Any property (regardless of `kind`) can carry metadata via the `attributes` field. Attributes are themselves property updates, so they follow the same schema and can be nested:

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

## Applying Updates

This section describes the algorithm for applying a received `SubjectUpdate` to a local object graph.

### Step 1: Root Mapping

If `root` is set and present in `subjects`, apply those properties to the local root subject. The `root` field is a mapping hint that identifies which entry in `subjects` corresponds to the applier's root, without assigning or changing the root's local subject ID.

If `root` is null, skip this step because the root subject has no changes in this update.

### Step 2: Remaining Subjects

Process remaining entries in `subjects` (those not already processed as root) by looking up each subject ID in the local ID-to-object map. If a subject ID is found, apply its properties to the local object. If not found, skip it. The subject is likely a newly introduced item (e.g., inserted into a collection) whose local object doesn't exist yet because its parent's structural property hasn't been applied yet. Skipping is safe because the parent's collection/dictionary/object apply step will create the item, register it by ID, and immediately look up its properties from `subjects` at that point.

### Applying Value Properties

Set the local property to the `value` from the update.

### Applying Object Properties

1. If `id` is null/omitted, set the local property to null.
2. If the existing local object already has the same subject ID as `id`, keep it (no replacement needed).
3. Otherwise, look up `id` in the local ID-to-object map. If found, reuse that object.
4. If not found, create a new object and register it with `id`.
5. If the subject's properties are present in `subjects[id]`, apply them.
6. Set the local property to the resolved object.

### Applying Collection Properties

1. If `items` is null/omitted, the collection is null. Set the local property to null.
2. If `items` is present, it defines the **full ordered state**:
   - For each item, look up `item.id` in the ID-to-object map. If found, reuse it. If not, create a new object and register it with `item.id`.
   - Apply the item's properties from `subjects[item.id]` if present.
   - Replace the local collection with the new ordered list.
3. An empty `items` array (`[]`) clears the collection.

### Applying Dictionary Properties

1. If `items` is null/omitted, the dictionary is null. Set the local property to null.
2. If `items` is present, it defines the **full state**:
   - For each item, look up `item.id` in the ID-to-object map. If found, reuse it. If not, create a new object and register it with `item.id`.
   - Apply the item's properties from `subjects[item.id]` if present.
   - Set the object at `dictionary[item.key]`.
   - Remove any dictionary entries whose keys are not in the `items` array.
3. An empty `items` array (`[]`) clears the dictionary.

### Subject Creation

Each participant is expected to know the type of each subject statically based on its position in the object hierarchy (e.g., "the `Child` property is of type `ChildSubject`"). The applier uses this knowledge to create the correct concrete type when a new subject ID is encountered. If dynamic type resolution is needed, it must be implemented on top of this protocol (e.g., via a special type discriminator property).

## Edge Cases

### Root ID Independence

The sender and receiver each generate their own subject IDs for the root. The sender includes its root ID in the `root` field so the receiver knows which `subjects` entry to apply to its local root. Non-root subjects (children, collection items, etc.) share IDs across sender/receiver because they are assigned during apply. Only the root is special: its identity is established by the `root` mapping hint, not by ID matching.

This works **symmetrically**: when the server sends an update, `root` contains the server's root ID, and the client maps it to its local root. When the client sends an update back, `root` contains the client's root ID, and the server maps it to its local root. Neither side needs to know or store the other's root ID beyond the current update.

### Circular References

Circular references are handled naturally by the flat structure. Each subject appears exactly once in `subjects`, and references use stable IDs:

```json
{
  "root": "parentId",
  "subjects": {
    "parentId": {
      "Name": { "kind": "Value", "value": "Parent" },
      "Child": { "kind": "Object", "id": "childId" }
    },
    "childId": {
      "Name": { "kind": "Value", "value": "Child" },
      "Parent": { "kind": "Object", "id": "parentId" }
    }
  }
}
```

### Non-Subject Collections

`List<int>`, `Dictionary<string, string>`, and other non-subject collections use value-replacement semantics (full replacement, no granular diffing). Only `IInterceptorSubject` collections support structural updates. These are serialized as `kind: "Value"` with the entire collection as the value.

### Dictionary Key Types

Dictionary keys are normalized to strings during transport. Non-string keys (int, enum) must be convertible via `Convert.ChangeType` or `Enum.Parse`.

## Implementing a Custom Client

### Subject ID to Object Mapping

The client must maintain bidirectional mappings between subject IDs and local objects:

- **ID to Object map** (`Map<string, object>`): Used to find local objects when the protocol references a subject by ID.
- **Object to ID**: Either store the subject ID directly on each local object (e.g., a `_subjectId` property) or maintain a separate `Map<object, string>`.

Both must be updated whenever objects are created, replaced, or removed.

### When to Register Objects

- **Root subject**: The root is identified by `update.root`, which is a mapping hint, not an ID to assign. The receiver should map its local root object to the sender's root ID (from `update.root`) so that subsequent references to that ID resolve correctly. The root's local ID may differ from the sender's ID.
- **Object properties**: When applying a `kind: "Object"` property, register the created/reused object with the `id` from the property update.
- **Collection/Dictionary items**: When creating a new item from the `items` array, register it with the item's `id`.

### Subject Lifecycle and Cleanup

The client's ID-to-object map grows as new subjects arrive but never shrinks unless the client actively cleans up. Since the protocol uses complete-state semantics for collections and dictionaries, a subject that disappears from all `items` arrays is no longer reachable and should be removed from the map.

**Complete updates** rebuild the map from scratch. Discard the entire ID-to-object map and populate it from the update. This implicitly drops all orphaned entries.

**Partial updates** require **reference counting** to detect when subjects become unreachable:

1. For each subject ID in the map, track how many structural properties (Object, Collection, Dictionary) currently reference it.
2. When applying a collection/dictionary update, decrement counts for IDs that were removed and increment counts for IDs that were added.
3. When applying an object property update, decrement the old ID (if any) and increment the new ID.
4. When a subject's reference count reaches zero, remove it from the ID-to-object map (and recursively decrement counts for any IDs that subject itself referenced).

**Without cleanup**, the map accumulates every subject ever seen, which is a memory leak proportional to churn (e.g., items repeatedly added and removed from collections).

### Echo Prevention

When applying received updates, mark changes with a source identifier so your own change listener does not re-send them.

### Reconnection and Server Restart

- **Reconnection**: Request a fresh complete update to resynchronize. Non-root subject IDs are stable within a server's lifetime, so existing local objects can be matched by ID after reconnection.
- **Server restart**: After a server restart, the server generates new subject IDs (including for the root). The client does not need special handling because the `root` mapping hint in the complete update identifies the new root entry, and all child subjects are re-created with their new IDs during apply. The client should clear its old ID-to-object maps when applying a complete update after reconnection.

### Conflict Resolution

Last-applied-wins by message arrival order with eventual consistency via reconnection.

## Examples

### Complete Update (Initial Sync)

```json
{
  "root": "rootId",
  "subjects": {
    "rootId": {
      "Name": { "kind": "Value", "value": "Root" },
      "Children": {
        "kind": "Collection",
        "items": [
          { "id": "child1Id" },
          { "id": "child2Id" }
        ]
      }
    },
    "child1Id": {
      "Name": { "kind": "Value", "value": "Alice" }
    },
    "child2Id": {
      "Name": { "kind": "Value", "value": "Bob" }
    }
  }
}
```

### Partial Update: Root Scalar Change

Only the changed property on the root subject is included.

```json
{
  "root": "rootId",
  "subjects": {
    "rootId": {
      "Name": { "kind": "Value", "value": "UpdatedRoot" }
    }
  }
}
```

### Partial Update: Non-Root Scalar Change

When only a non-root subject changes, `root` is null because the root has no changes. The changed subject appears in `subjects` by its ID.

```json
{
  "root": null,
  "subjects": {
    "child1Id": {
      "Name": { "kind": "Value", "value": "UpdatedAlice" }
    }
  }
}
```

### Partial Update: Item Inserted

Before: `[A, B]` → After: `[A, NewItem, B]`

```json
{
  "root": "rootId",
  "subjects": {
    "rootId": {
      "Children": {
        "kind": "Collection",
        "items": [
          { "id": "childAId" },
          { "id": "newItemId" },
          { "id": "childBId" }
        ]
      }
    },
    "newItemId": {
      "Name": { "kind": "Value", "value": "Charlie" }
    }
  }
}
```

Note: Only the new item (`newItemId`) has its properties in `subjects`. Existing items (`childAId`, `childBId`) are referenced by ID only since their properties haven't changed.

### Partial Update: Item Removed

Before: `[A, B, C]` → After: `[A, C]`

```json
{
  "root": "rootId",
  "subjects": {
    "rootId": {
      "Children": {
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

### Partial Update: Items Reordered

Before: `[A, B, C]` → After: `[C, A, B]`

```json
{
  "root": "rootId",
  "subjects": {
    "rootId": {
      "Children": {
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

## Transport

This update format is transport-agnostic. It is currently used by the [WebSocket connector](websocket.md) for real-time bidirectional synchronization.
