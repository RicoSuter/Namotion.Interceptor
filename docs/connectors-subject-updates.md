# Subject Updates

Subject updates enable efficient synchronization of object graphs between server and clients. Instead of sending full object state on every change, only the changed properties are transmitted.

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
// The 'source' parameter enables echo prevention in change tracking
subject.ApplySubjectUpdateFromSource(update, source, DefaultSubjectFactory.Instance);

// Apply update without source tracking
subject.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);
```

### Client-Side (TypeScript)

See the TypeScript types at the end of this document. The apply logic follows the same two-phase approach described below.

## How Collection Updates Work

Collections (arrays and dictionaries) use a **two-phase approach** that separates structural changes from property updates. This design:

- Minimizes payload size (move operations contain only indices, not full objects)
- Preserves object identity during reordering
- Enables efficient sparse updates (only changed items are transmitted)

### Phase 1: Structural Operations

First, apply structural changes in order:

| Operation | Description |
|-----------|-------------|
| `Remove` | Remove item at index (arrays) or key (dictionaries) |
| `Insert` | Insert new item at index/key with full item data |
| `Move` | Move item from one index to another (arrays only, no item data) |

### Phase 2: Property Updates

Then, apply sparse property updates. The index/key references the **final position after structural operations**.

### Example: Remove + Property Change

Before: `[A, B, C]` where C.name = "Charlie"
After: `[A, C]` where C.name = "Charles"

```json
{
  "operations": [
    { "action": "Remove", "index": 1 }
  ],
  "collection": [
    { "index": 1, "item": { "properties": { "name": { "value": "Charles" } } } }
  ]
}
```

Note: The property update uses index `1` (C's final position after B was removed).

### Example: Reorder Without Data

Before: `[A, B, C]`
After: `[C, A, B]`

```json
{
  "operations": [
    { "action": "Move", "fromIndex": 2, "index": 0 }
  ]
}
```

Move operations contain only indices - no item data is transmitted, keeping payloads small even for large objects.

### Complete vs Partial Collection Updates

**Partial updates** (incremental changes) have `operations` for structural changes:

```json
{
  "kind": "Collection",
  "operations": [ { "action": "Remove", "index": 1 } ],
  "collection": [ { "index": 0, "item": { ... } } ],
  "count": 2
}
```

**Complete updates** (initial sync) have no `operations` - just all items in `collection`:

```json
{
  "kind": "Collection",
  "collection": [
    { "index": 0, "item": { ... } },
    { "index": 1, "item": { ... } }
  ],
  "count": 2
}
```

When applying: if `operations` is empty but `collection` contains items at indices that don't exist locally, create new items at those positions.

## Circular References

Object graphs with cycles are handled via reference IDs:

```json
{
  "id": "root-1",
  "properties": {
    "child": {
      "kind": "Item",
      "item": {
        "id": "child-1",
        "properties": {
          "parent": {
            "kind": "Item",
            "item": { "reference": "root-1" }
          }
        }
      }
    }
  }
}
```

The `reference` field points to an already-defined subject by its `id`, avoiding infinite recursion.

## JSON Format Overview

### Property Update Kinds

| Kind | Description |
|------|-------------|
| `Value` | Scalar value (string, number, boolean, etc.) |
| `Item` | Single nested subject |
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

```json
{
  "kind": "Item",
  "item": {
    "properties": { ... }
  }
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

## TypeScript Types

```typescript
export type SubjectPropertyUpdateKind = "None" | "Value" | "Item" | "Collection";
export type CollectionOperationType = "Remove" | "Insert" | "Move";

export interface SubjectUpdate {
    id?: string | null;
    reference?: string | null;
    properties?: { [key: string]: SubjectPropertyUpdate };
}

export interface SubjectPropertyUpdate {
    kind?: SubjectPropertyUpdateKind;
    value?: any | null;
    timestamp?: string | null;
    item?: SubjectUpdate | null;
    operations?: CollectionOperation[] | null;
    collection?: SubjectPropertyCollectionUpdate[] | null;
    count?: number | null;
    attributes?: { [key: string]: SubjectPropertyUpdate } | null;
}

export interface CollectionOperation {
    action: CollectionOperationType;
    index: number | string;  // number for arrays, string for dictionaries
    fromIndex?: number;      // Move only
    item?: SubjectUpdate;    // Insert only
}

export interface SubjectPropertyCollectionUpdate {
    index: number | string;
    item?: SubjectUpdate;
}
```
