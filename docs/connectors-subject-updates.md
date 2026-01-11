# Subject Updates Specification

This document describes the `SubjectUpdate` format used for synchronizing interceptor subject state between server and clients (e.g., via WebSocket, REST API, or other transport mechanisms).

## Overview

Subject updates enable efficient synchronization of object graphs by transmitting only the changed data. The format supports:

- **Value properties**: Simple scalar values (strings, numbers, booleans, etc.)
- **Item properties**: Single nested subject references
- **Collection properties**: Arrays/lists and dictionaries of subjects
- **Circular references**: Object graphs with cycles are handled via reference IDs

## JSON Format

### SubjectUpdate

The root structure representing an update to a subject:

```json
{
  "id": "unique-subject-id",
  "reference": "ref-to-existing-subject",
  "properties": {
    "propertyName": { /* SubjectPropertyUpdate */ }
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | `string?` | Unique identifier for this subject (used for circular reference resolution) |
| `reference` | `string?` | Reference to an already-defined subject by its `id` (for cycles) |
| `properties` | `object` | Map of property names to their updates |

### SubjectPropertyUpdate

Represents an update to a single property:

```json
{
  "kind": "Value" | "Item" | "Collection",
  "value": <any>,
  "timestamp": "2024-01-10T12:00:00Z",
  "item": { /* SubjectUpdate */ },
  "operations": [ /* CollectionOperation[] */ ],
  "collection": [ /* SubjectPropertyCollectionUpdate[] */ ],
  "count": 5,
  "attributes": { /* property attributes */ }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `kind` | `string` | Update type: `"Value"`, `"Item"`, or `"Collection"` |
| `value` | `any` | The property value (for `kind: "Value"`) |
| `timestamp` | `string?` | ISO 8601 timestamp of when the change occurred |
| `item` | `SubjectUpdate?` | Nested subject (for `kind: "Item"`) |
| `operations` | `array?` | Structural operations for collections (see below) |
| `collection` | `array?` | Sparse property updates by final index/key |
| `count` | `number?` | Total collection size after operations |
| `attributes` | `object?` | Property-level attributes (metadata) |

## Collection Updates

Collection updates use a **two-phase approach** that separates structural changes from property updates. This design minimizes payload size and preserves object identity during reordering.

### Phase 1: Structural Operations

The `operations` array contains ordered structural changes to apply first:

```json
{
  "operations": [
    { "action": "Remove", "index": 0 },
    { "action": "Insert", "index": 1, "item": { /* new subject data */ } },
    { "action": "Move", "fromIndex": 2, "index": 0 }
  ]
}
```

#### CollectionOperation

| Field | Type | Description |
|-------|------|-------------|
| `action` | `string` | `"Remove"`, `"Insert"`, or `"Move"` |
| `index` | `number\|string` | Target index (arrays) or key (dictionaries) |
| `fromIndex` | `number?` | Source index for `Move` operations (arrays only) |
| `item` | `SubjectUpdate?` | New subject data for `Insert` operations only |

#### Operation Types

| Action | Arrays | Dictionaries | Description |
|--------|--------|--------------|-------------|
| `Remove` | Yes | Yes | Remove item at index/key |
| `Insert` | Yes | Yes | Insert new item at index/key |
| `Move` | Yes | No | Move item from one position to another |

**Important**: `Move` operations do NOT include item data - they only specify indices. This keeps payloads small when reordering large objects.

### Phase 2: Property Updates

The `collection` array contains sparse property updates applied AFTER structural operations:

```json
{
  "collection": [
    { "index": 0, "item": { "properties": { "name": { "kind": "Value", "value": "Updated" } } } },
    { "index": 2, "item": { "properties": { "status": { "kind": "Value", "value": "active" } } } }
  ]
}
```

#### SubjectPropertyCollectionUpdate

| Field | Type | Description |
|-------|------|-------------|
| `index` | `number\|string` | Final index (after operations) or dictionary key |
| `item` | `SubjectUpdate?` | Property updates for the item at this position |

**Key insight**: The `index` refers to the FINAL position after all structural operations are applied, not the original position.

### Complete vs Partial Updates

#### Partial Updates (Changes Only)

When synchronizing incremental changes, both `operations` and `collection` may be present:

```json
{
  "kind": "Collection",
  "operations": [
    { "action": "Remove", "index": 1 }
  ],
  "collection": [
    { "index": 0, "item": { "properties": { "name": { "kind": "Value", "value": "Changed" } } } }
  ],
  "count": 2
}
```

#### Complete Updates (Full State)

For initial state (e.g., WebSocket welcome message), `operations` is omitted and `collection` contains all items:

```json
{
  "kind": "Collection",
  "collection": [
    { "index": 0, "item": { "id": "item-1", "properties": { /* full item data */ } } },
    { "index": 1, "item": { "id": "item-2", "properties": { /* full item data */ } } }
  ],
  "count": 2
}
```

When applying: if `operations` is empty/null but `collection` contains items at indices that don't exist locally, create new items at those positions.

## Examples

### Example 1: Simple Property Change

```json
{
  "properties": {
    "firstName": {
      "kind": "Value",
      "value": "John",
      "timestamp": "2024-01-10T12:00:00Z"
    }
  }
}
```

### Example 2: Nested Subject Update

```json
{
  "properties": {
    "address": {
      "kind": "Item",
      "item": {
        "properties": {
          "city": { "kind": "Value", "value": "New York" }
        }
      }
    }
  }
}
```

### Example 3: Array - Item Inserted

Before: `[A, B]`
After: `[A, X, B]`

```json
{
  "properties": {
    "items": {
      "kind": "Collection",
      "operations": [
        { "action": "Insert", "index": 1, "item": { "properties": { "name": { "kind": "Value", "value": "X" } } } }
      ],
      "count": 3
    }
  }
}
```

### Example 4: Array - Item Removed

Before: `[A, B, C]`
After: `[A, C]`

```json
{
  "properties": {
    "items": {
      "kind": "Collection",
      "operations": [
        { "action": "Remove", "index": 1 }
      ],
      "count": 2
    }
  }
}
```

### Example 5: Array - Items Reordered

Before: `[A, B, C]`
After: `[C, A, B]`

```json
{
  "properties": {
    "items": {
      "kind": "Collection",
      "operations": [
        { "action": "Move", "fromIndex": 2, "index": 0 }
      ],
      "count": 3
    }
  }
}
```

Note: Only one `Move` operation needed - moving C to index 0 automatically shifts A and B.

### Example 6: Array - Reorder with Property Change

Before: `[A, B, C]` where B.name = "Bob"
After: `[C, A, B]` where B.name = "Bobby"

```json
{
  "properties": {
    "items": {
      "kind": "Collection",
      "operations": [
        { "action": "Move", "fromIndex": 2, "index": 0 }
      ],
      "collection": [
        { "index": 2, "item": { "properties": { "name": { "kind": "Value", "value": "Bobby" } } } }
      ],
      "count": 3
    }
  }
}
```

Note: The property update uses index `2` (B's FINAL position after the move).

### Example 7: Dictionary - Key Added

Before: `{ "a": A }`
After: `{ "a": A, "b": B }`

```json
{
  "properties": {
    "lookup": {
      "kind": "Collection",
      "operations": [
        { "action": "Insert", "index": "b", "item": { "properties": { "name": { "kind": "Value", "value": "B" } } } }
      ],
      "count": 2
    }
  }
}
```

### Example 8: Dictionary - Property Update

Before: `{ "a": A, "b": B }` where A.name = "Alpha"
After: `{ "a": A, "b": B }` where A.name = "Alpha Updated"

```json
{
  "properties": {
    "lookup": {
      "kind": "Collection",
      "collection": [
        { "index": "a", "item": { "properties": { "name": { "kind": "Value", "value": "Alpha Updated" } } } }
      ],
      "count": 2
    }
  }
}
```

### Example 9: Circular Reference

Object graph: `Root -> Child -> Root` (cycle)

```json
{
  "id": "root-1",
  "properties": {
    "name": { "kind": "Value", "value": "Root" },
    "child": {
      "kind": "Item",
      "item": {
        "id": "child-1",
        "properties": {
          "name": { "kind": "Value", "value": "Child" },
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

The `reference` field points back to the already-defined subject, avoiding infinite recursion.

## Apply Logic

### C# Server-Side

```csharp
// Apply update from external source (e.g., WebSocket message from client)
subject.ApplySubjectUpdateFromSource(update, source, DefaultSubjectFactory.Instance);

// Apply update without source tracking
subject.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);
```

### TypeScript Client-Side

The client applies updates in two phases:

```typescript
function applyCollectionUpdate(existing: any[], update: SubjectPropertyUpdate): any[] {
    const items = [...existing];

    // Phase 1: Structural operations
    if (update.operations?.length) {
        for (const op of update.operations) {
            switch (op.action) {
                case 'Remove':
                    items.splice(op.index as number, 1);
                    break;
                case 'Insert':
                    const newItem = createFromUpdate(op.item);
                    items.splice(op.index as number, 0, newItem);
                    break;
                case 'Move':
                    const [moved] = items.splice(op.fromIndex!, 1);
                    items.splice(op.index as number, 0, moved);
                    break;
            }
        }
    }

    // Phase 2: Property updates (by final index)
    if (update.collection?.length) {
        for (const upd of update.collection) {
            const index = upd.index as number;
            if (index < items.length) {
                applyPropertyUpdates(items[index], upd.item);
            } else {
                // Complete update: create missing items
                items[index] = createFromUpdate(upd.item);
            }
        }
    }

    return items;
}
```

## Creating Updates

### Complete Update (Full State)

Use for initial synchronization or when client requests full state:

```csharp
var update = SubjectUpdate.CreateCompleteUpdate(rootSubject, processors);
```

### Partial Update (Changes Only)

Use for incremental synchronization based on tracked property changes:

```csharp
var changes = /* collected SubjectPropertyChange[] */;
var update = SubjectUpdate.CreatePartialUpdateFromChanges(rootSubject, changes, processors);
```

## Design Rationale

### Why Separate Operations from Collection Updates?

1. **Smaller payloads**: Move operations contain only indices, not full object data
2. **Identity preservation**: Moved objects maintain their identity on the client
3. **Clear semantics**: Structural changes vs property changes are distinct concerns
4. **Efficient apply**: Two-phase processing is straightforward to implement

### Why Final Index for Property Updates?

Property updates reference items by their position AFTER structural operations. This:
- Simplifies client apply logic (no index translation needed)
- Avoids ambiguity when combining moves with updates
- Matches how the server sees the final state

### Why No Move for Dictionaries?

Dictionaries are unordered key-value stores. The concept of "moving" doesn't apply - keys are either present or absent.

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
    index: number | string;
    fromIndex?: number;
    item?: SubjectUpdate | null;
}

export interface SubjectPropertyCollectionUpdate {
    index: number | string;
    item?: SubjectUpdate | null;
}
```

## Versioning & Compatibility

- Clients that don't understand `operations` will ignore it (backward compatible)
- Complete updates (no `operations`, full `collection`) work with older clients
- The `count` field enables clients to detect truncation or verify final size
