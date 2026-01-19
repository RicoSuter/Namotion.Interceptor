# Subject Updates

Subject updates enable efficient synchronization of object graphs between server and clients. Instead of sending full object state on every change, only the changed properties are transmitted.

## Flat Structure

Subject updates use a **flat dictionary structure** where all subjects are stored in a single dictionary and referenced by string IDs. This design:

- Eliminates circular reference issues during serialization
- Enables O(1) subject lookup
- Makes debugging easier (all subjects visible at top level)
- Keeps each subject's data in exactly one place

### JSON Format Overview

```json
{
  "root": "1",
  "subjects": {
    "1": {
      "name": { "kind": "Value", "value": "Parent" },
      "child": { "kind": "Item", "id": "2" }
    },
    "2": {
      "name": { "kind": "Value", "value": "Child" },
      "parent": { "kind": "Item", "id": "1" }
    }
  }
}
```

- `root` - The ID of the root subject
- `subjects` - Dictionary of all subjects, keyed by string ID
- Each subject contains property name → property update mappings
- References to other subjects use `id` (not nested objects)

## Creating Updates

### Complete Update (Full State)

Use for initial synchronization when a client connects:

```csharp
var update = SubjectUpdate.CreateCompleteUpdate(rootSubject, processors);
var json = JsonSerializer.Serialize(update);
```

### Partial Update (Changes Only)

Use for incremental synchronization based on tracked property changes:

```csharp
// Collect changes from the tracking system
var changes = /* SubjectPropertyChange[] from change tracking */;

// Create update containing only changed properties
var update = SubjectUpdate.CreatePartialUpdateFromChanges(rootSubject, changes, processors);
var json = JsonSerializer.Serialize(update);
```

## Applying Updates

### Server-Side (C#)

```csharp
// Apply update from external source (e.g., WebSocket message from client)
// The WithSource scope enables echo prevention in change tracking
using (SubjectChangeContext.WithSource(source))
{
    subject.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);
}

// Apply update without source tracking
subject.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);
```

## Property Update Kinds

| Kind | Description |
|------|-------------|
| `Value` | Scalar value (string, number, boolean, etc.) |
| `Item` | Single nested subject (referenced by ID) |
| `Collection` | Array or dictionary of subjects |

### Value Property

```json
{
  "kind": "Value",
  "value": "John",
  "timestamp": "2024-01-10T12:00:00Z"
}
```

### Item Property

References another subject by ID:

```json
{
  "kind": "Item",
  "id": "2"
}
```

A null reference omits the `id` field:

```json
{
  "kind": "Item"
}
```

### Collection Property

```json
{
  "kind": "Collection",
  "operations": [ ... ],
  "collection": [ ... ],
  "count": 5
}
```

## How Collection Updates Work

Collections (arrays and dictionaries) use a **two-phase approach** that separates structural changes from property updates. This design:

- Minimizes payload size (move operations contain only indices, not full objects)
- Preserves object identity during reordering
- Enables efficient sparse updates (only changed items are transmitted)

### Phase 1: Structural Operations

Apply structural changes in two sub-phases:

**Sub-phase 1a: Remove and Insert operations** are applied sequentially in the order they appear:
- `Remove` operations are sent in **descending index order** so each remove doesn't affect subsequent removes
- `Insert` operations reference the final target position

**Sub-phase 1b: Move operations** are applied atomically using snapshot semantics:
- All moves reference the state **after** removes/inserts have been applied
- Multiple moves are applied simultaneously (each move reads from the snapshot)
- Move `fromIndex` accounts for prior removes (intermediate index, not original)

| Operation | Index semantics |
|-----------|-----------------|
| `Remove` | Original index, descending order |
| `Insert` | Final target index |
| `Move` | `fromIndex`: intermediate (after removes), `index`: final target |

**Example: Remove + Move**

Transform `[A, B, C]` → `[C, B]` (remove A, swap remaining):

```json
{
  "operations": [
    { "action": "Remove", "index": 0 },
    { "action": "Move", "fromIndex": 1, "index": 0 }
  ]
}
```

After `Remove(0)`: `[B, C]` (indices 0, 1)
After `Move(from=1, to=0)`: `[C, B]`

Note: The move's `fromIndex` is 1 (C's position after the remove), not 2 (C's original position).

### Phase 2: Property Updates

Then, apply sparse property updates. The index/key references the **final position after structural operations**.

### Example: Remove + Property Change

Before: `[A, B, C]` where C.name = "Charlie"
After: `[A, C]` where C.name = "Charles"

```json
{
  "kind": "Collection",
  "operations": [
    { "action": "Remove", "index": 1 }
  ],
  "collection": [
    { "index": 1, "id": "3" }
  ],
  "count": 2
}
```

The subject with ID "3" (C) has its property update in the `subjects` dictionary:

```json
{
  "subjects": {
    "3": {
      "name": { "kind": "Value", "value": "Charles" }
    }
  }
}
```

Note: The property update uses index `1` (C's final position after B was removed).

### Example: Reorder Without Data

Before: `[A, B, C]`
After: `[C, A, B]`

```json
{
  "kind": "Collection",
  "operations": [
    { "action": "Move", "fromIndex": 2, "index": 0 }
  ],
  "count": 3
}
```

Move operations contain only indices - no item data is transmitted, keeping payloads small even for large objects.

### Example: Insert New Item

```json
{
  "kind": "Collection",
  "operations": [
    { "action": "Insert", "index": 1, "id": "5" }
  ],
  "count": 3
}
```

The new item's data is in the `subjects` dictionary:

```json
{
  "subjects": {
    "5": {
      "name": { "kind": "Value", "value": "New Item" }
    }
  }
}
```

### Complete vs Partial Collection Updates

**Partial updates** (incremental changes) have `operations` for structural changes:

```json
{
  "kind": "Collection",
  "operations": [ { "action": "Remove", "index": 1 } ],
  "collection": [ { "index": 0, "id": "2" } ],
  "count": 2
}
```

**Complete updates** (initial sync) have no `operations` - just all items in `collection`:

```json
{
  "kind": "Collection",
  "collection": [
    { "index": 0, "id": "1" },
    { "index": 1, "id": "2" }
  ],
  "count": 2
}
```

### Applying Collection Updates

When applying sparse property updates from the `collection` array, the `index` must be valid according to the declared `count`:

| Condition | Behavior |
|-----------|----------|
| `index < count` | Valid - update or create item at that position |
| `index >= count` | **Error** - throws `InvalidOperationException` |
| `count` not specified | Index validated against current collection size |

**Important:** The `count` field declares the final expected size of the collection. Any `index` in the `collection` array must satisfy `index < count`. An index >= count indicates a malformed update (bug in the sender) and will throw an exception.

For complete updates (no `operations`), items at indices that don't exist locally are created sequentially. For partial updates, indices reference the final position after structural operations have been applied.

## Circular References

Circular references are handled naturally by the flat structure. Each subject appears exactly once in the `subjects` dictionary, and references use string IDs:

```json
{
  "root": "1",
  "subjects": {
    "1": {
      "name": { "kind": "Value", "value": "Parent" },
      "child": { "kind": "Item", "id": "2" }
    },
    "2": {
      "name": { "kind": "Value", "value": "Child" },
      "parent": { "kind": "Item", "id": "1" }
    }
  }
}
```

No special `reference` field is needed - the `id` field always points to a subject in the dictionary.

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
