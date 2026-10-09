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
      "child": { "kind": "Object", "id": "2" }
    },
    "2": {
      "name": { "kind": "Value", "value": "Child" },
      "parent": { "kind": "Object", "id": "1" }
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

Use for incremental synchronization based on tracked property changes. Repeated changes to a property are merged before constructing the update, preserving the earliest old value and latest new value and timestamp. Committed changes are ordered by revision; if any change to that property has no revision, arrival order is used. Callers do not need to merge the batch first:

```csharp
// Collect changes from the tracking system
var changes = /* SubjectPropertyChange[] from change tracking */;

// Create update containing only changed properties
var update = SubjectUpdate.CreatePartialUpdateFromChanges(rootSubject, changes, processors);
var json = JsonSerializer.Serialize(update);
```

A subject that a change assigns to a reference, or inserts into a collection or dictionary, arrives with its complete property set and attributes. So does every subject the update mentions for the first time while that payload is built, such as the children of a newly attached subtree. Each subject is completed at most once per update. The items of a collection or dictionary sent in full, and inserted items, are always completed, while an object reference reached while building a payload completes a subject the update already mentions only when that reference is the subject's first parent, which is what keeps a back or cross reference from pulling in a subtree the update states elsewhere. This holds in any arrival order: changes to properties that hold subjects are processed before value and attribute changes, which then apply their captured values and timestamps on top. A reference to the root and a reference to the subject that owns the changed property are sent as an ID only.

An update also states the path from its root to every changed subject along first parents, even when that subject's complete payload hangs off another reference, such as a subject assigned to a second reference, or an ancestor completed because a back reference to it was assigned. Such a subject carries its payload once, under one ID, and which of its references comes first in the update depends on arrival order. A receiver therefore reaches the same state in any order only when it applies a shared ID's payload to every instance it reaches through those references. The path cannot be stated when first parents form a loop, which happens when a subject's first remaining parent is a reference from inside its own subtree, such as a child's back reference after the subject moved to another parent; such a change does not arrive, as on earlier versions.

Once a subject carries a complete payload in an update, a further change to one of its subject-holding properties or attributes in the same batch does not turn that property back into an incremental diff. The complete payload already states the final structure, and a receiver building the subject from it has no baseline the diff could apply to. Such a change still states the subject's path from the root. Changes to values, including value attributes, still apply on top, which is how their captured values and timestamps survive.

### Filtering with Processors

`ISubjectUpdateProcessor` controls which properties and attributes appear in updates. The `IsIncluded` method is called for each property **and each attribute** during update creation:

```csharp
public class MyProcessor : ISubjectUpdateProcessor
{
    public bool IsIncluded(RegisteredSubjectProperty property)
    {
        // Filter properties and attributes from the update
        return !property.ReflectionAttributes.OfType<MyIgnoreAttribute>().Any();
    }
}
```

Note: Dynamically added attributes (via `AddAttribute`) also expose their provided `Attribute[]` through `ReflectionAttributes`, so the same filtering logic works for both regular properties and dynamic attributes.

### Two-Layer Filtering for Connectors

Connectors that use `ChangeQueueProcessor` for real-time updates have two filtering layers:

1. **`ChangeQueueProcessor.propertyFilter`**: Pre-filters which property changes enter the queue. This prevents unnecessary buffering and partial update creation for properties that will never be sent.

2. **`ISubjectUpdateProcessor.IsIncluded`**: Filters properties and attributes during update creation (both `CreateCompleteUpdate` and `CreatePartialUpdateFromChanges`).

Both layers should apply the same filtering logic. The `propertyFilter` is an optimization for the change path; `IsIncluded` is the authoritative filter that also covers complete updates (initial sync), where no `ChangeQueueProcessor` is involved.

Exclusion covers everything reached through an excluded property on the path an update states: a change is dropped entirely, carrying neither its own entry nor any path entry, when the path from its subject up to the update root crosses a property `IsIncluded` rejects. That path follows each subject's first parent only, so a change to a subject whose first parent is excluded is dropped even when an included property also references the subject. Excluding the property that holds a subtree does not hide a subject of that subtree which an included property also references, as a second parent or a cross reference: complete updates, and partial updates that assign or insert it, publish it with its values through that reference. A permissions-style processor that must hide a subtree also has to exclude every included property that can reference into it.

Connectors with an `IPathProvider` can delegate to `pathProvider.IsPropertyIncluded` in both layers to keep filtering consistent:

```csharp
// In ISubjectUpdateProcessor
public bool IsIncluded(RegisteredSubjectProperty property)
    => pathProvider.IsPropertyIncluded(property);

// In ChangeQueueProcessor setup
propertyFilter: propertyReference =>
    propertyReference.TryGetRegisteredProperty() is { } property &&
    pathProvider.IsPropertyIncluded(property)
```

## Applying Updates

### Server-Side (C#)

```csharp
// Apply update from external source (e.g., WebSocket message from client).
// A FromSource origin stamps the applied changes so echo suppression skips
// that source's own outbound path.
subject.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.FromSource(source));

// Apply update as a local change (no source tracking).
subject.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);
```

### Replaced References and Shared Instances

An `Object` entry marked `mode: Replaced` never applies its payload to the subject the receiver currently holds at that reference: the source reassigned the reference, so the held subject is a different one there. The applier resolves the ID to the subject it already bound for that ID in this update, when one fits the declared type, and otherwise creates a new subject, exactly as for an empty reference. An unmarked `Object` entry, such as a path entry, applies its payload to the subject held there.

Within one update, one subject instance on the receiver takes the payload of at most one ID. Two IDs name two source subjects, so a position (a reference, a collection item or a dictionary entry) holding an instance that already took another ID's payload is treated as empty: it receives the subject bound for its ID, or a new one. This is what lets a complete update split a subject the receiver still shares between two positions after the source replaced it at one of them. A reference or container without a setter cannot take the new subject, which is reported as described below.

### Properties the Receiver Cannot Write

Whether a property has a setter is a fact about the receiving model, not the producer's, and a producer may legitimately publish one for a receiver to display. A property without a setter is therefore an expected shape rather than a failure: its own value is silently not written, and nothing is reported. Such a value is dropped before it is converted, so the `transformValueBeforeApply` callback of `ApplySubjectUpdate` does not run for it either.

Only that property's value is dropped. A reference or container the receiver already holds still carries the update through to its subtree, so a nested value behind a `get`-only or `init`-only reference arrives, and a sparse item update reaches an existing item of a read-only collection or dictionary. What such a property cannot receive is a new membership: the rebuilt container is not written, a reference holding `null` is left alone rather than being given a subject it has nowhere to store, and a reference keeps the subject it holds when the update replaces it with another. An update clearing such a reference (`id` absent) is ignored like a dropped value, so the reference keeps its subject. Attributes of a property without a setter apply as usual.

The two drops are reported differently. A dropped structural payload, meaning an unwritable reference that stays empty or keeps a subject the update replaced, a skipped container rebuild or an item that is not created, is logged as one warning per update that names each affected property once, qualified by the type of the subject holding it: the producer described a child the receiving model cannot hold, and nothing converges later. A dropped value is not logged, because a producer publishing a value-typed derived property that the receiving model computes itself would make that warning fire on nearly every message.

### When a Property Fails to Apply

A property that throws is skipped and the rest of the update still applies, including the properties of nested subjects, for every update kind. Applying is not all-or-nothing.

An update naming a subject it carries no payload for is one such failure. A non-null object, inserted item or sparse item ID missing from `subjects` fails that property without assigning a replacement object or collection, so the property keeps its current value while its siblings still apply. This holds for an insert into a container the receiver cannot write as well.

The call still reports failure once every property has been attempted. A single failure is rethrown as itself, keeping its original type and stack, so a caller catching a specific exception type is unaffected. Several failures are wrapped in an `AggregateException` whose message names the properties.

Two limits follow from applying in place rather than staging:

- A collection or dictionary item whose own property fails is still inserted, carrying a default for that property, so the parent references a new item that is only partly populated.
- A failure raised by the collection or dictionary machinery itself, such as the out-of-range index described in [Applying Collection Updates](#applying-collection-updates), is contained at that property but abandons the remaining items of it.

What a caller does with the failure is its own decision; see [Inbound Update Error Handling](connectors.md#inbound-update-error-handling) for what the built-in connector infrastructure does.

## Property Update Kinds

| Kind | Description |
|------|-------------|
| `Value` | Scalar value (string, number, boolean, etc.) |
| `Object` | Single nested subject (referenced by ID) |
| `Collection` | Index-based array or list of subjects |
| `Dictionary` | Key-based dictionary of subjects |

### Value Property

```json
{
  "kind": "Value",
  "value": "John",
  "timestamp": "2024-01-10T12:00:00Z"
}
```

### Object Property

References another subject by ID:

```json
{
  "kind": "Object",
  "id": "2"
}
```

A null reference omits the `id` field:

```json
{
  "kind": "Object"
}
```

A reference the source reassigned to another subject is marked `Replaced`, see [Property Update Modes](#property-update-modes):

```json
{
  "kind": "Object",
  "mode": "Replaced",
  "id": "3"
}
```

### Collection Property

For index-based collections (arrays, lists):

```json
{
  "kind": "Collection",
  "operations": [ ... ],
  "items": [ ... ],
  "count": 5
}
```

### Dictionary Property

For key-based dictionaries. Works the same as Collection but uses keys instead of positional indices, and does not support Move operations:

```json
{
  "kind": "Dictionary",
  "operations": [ ... ],
  "items": [ ... ],
  "count": 5
}
```

## Property Update Modes

The optional `mode` field is a second dimension beside `kind`: it states how an entry relates to what the receiver already holds.

| Mode | Kind | Description |
|------|------|-------------|
| `Incremental` | Any | The entry applies on top of what the receiver holds: a path entry, sparse items, or a diff with operations. The default, which is never written. |
| `Complete` | `Collection`, `Dictionary` | `items` state the whole membership, see [Reconciling Complete Membership](#reconciling-complete-membership). |
| `Replaced` | `Object` | The reference now holds a different subject than before, see [Replaced References and Shared Instances](#replaced-references-and-shared-instances). |

`Incremental` is omitted from JSON, so an entry that is neither marked `Complete` nor `Replaced` serializes exactly as it did before `mode` existed. A mode that does not apply to the entry's kind is treated as `Incremental`, never as a failure.

The producer marks `Complete` on every complete collection or dictionary payload, in complete updates and in the complete payload of a subject a partial update assigns or inserts, including an empty container (`count: 0`). It never marks a diff built from a change or a path entry, even when the diff has no operations. It marks `Replaced` on the `Object` entry of every reference whose change in the batch ends on a different, non-null subject than it started with, compared by reference, whether that entry was written for the change or as part of a complete payload of the subject holding the reference. A subject assigned back to where it started, a clear (`id` absent), a path entry and every reference in a complete update stay unmarked.

An applier that ignores `mode` keeps the behavior it had before. Another applier, such as a TypeScript one, gains the fixes this field carries by adopting two rules: reconcile membership only for entries marked `Complete`, and never apply the payload of an entry marked `Replaced` to the subject currently held at that reference.

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
- All `Remove` operations precede all `Insert` operations, for dictionaries and for collections alike, so replacing the value at an existing key removes the old entry before the replacement is inserted

**Sub-phase 1b: Move operations** are applied atomically using snapshot semantics:
- All moves reference the state **after** removes/inserts have been applied
- Multiple moves are applied simultaneously (each move reads from the snapshot)
- Move `fromIndex` accounts for prior removes (intermediate index, not original)
- Moves arrive as a **complete permutation**: the producer emits one Move for every retained item whose position changed, so every reordered slot is written from the snapshot. An applier must not read a single Move as a shift or a rotation of the items around it; one Move on its own overwrites its target and leaves a duplicate behind.

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
    { "action": "Move", "fromIndex": 1, "index": 0 },
    { "action": "Move", "fromIndex": 0, "index": 1 }
  ]
}
```

After `Remove(0)`: `[B, C]` (indices 0, 1), which is the snapshot both moves read from.

After both moves: `[C, B]`.

Note: C's `fromIndex` is 1 (its position after the remove), not 2 (its original position). B moves too, even though only C changed place relative to the original collection: the swap is stated as both halves so the snapshot writes cover every slot.

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
  "items": [
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
    { "action": "Move", "fromIndex": 2, "index": 0 },
    { "action": "Move", "fromIndex": 0, "index": 1 },
    { "action": "Move", "fromIndex": 1, "index": 2 }
  ],
  "count": 3
}
```

Every retained item is named, because each one sits at a new index: the rotation is stated as three snapshot writes rather than as one item moving past the others.

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
  "items": [ { "index": 0, "id": "2" } ],
  "count": 2
}
```

**Complete updates** (initial sync) have no `operations`, just all items in `items`, and are marked `Complete`:

```json
{
  "kind": "Collection",
  "mode": "Complete",
  "items": [
    { "index": 0, "id": "1" },
    { "index": 1, "id": "2" }
  ],
  "count": 2
}
```

### Applying Collection Updates

When applying sparse property updates from the `items` array, the `index` must be valid according to the declared `count`:

| Condition | Behavior |
|-----------|----------|
| `index < count` | Valid - update or create item at that position |
| `index >= count` | **Error** - throws `InvalidOperationException` |
| `count` not specified | Index validated against current collection size |

**Important:** The `count` field declares the final expected size of the collection. Any `index` in the `items` array must satisfy `index < count`. An index >= count indicates a malformed update (bug in the sender) and will throw an exception.

### Reconciling Complete Membership

A collection or dictionary entry marked `mode: Complete` states the whole membership, in a complete update or in the complete payload of a subject a partial update assigns or inserts. It reconciles only when its shape states a whole membership as well; a marked entry failing any of these checks applies incrementally, as an unmarked one does:

- `operations` is absent or empty.
- `count` is a nonnegative integer and equals the number of entries in `items`.
- Each item has a non-null `id` present in `subjects`. An empty subject property dictionary is a valid payload.
- Collection indices are distinct and cover every position from zero through `count - 1`.
- Dictionary keys are distinct after conversion to the declared key type.

An entry without `mode: Complete` never reconciles, whatever its shape. A diff whose membership did not change has no operations, and the path entries added for its changed children can name as many items as its `count` without saying anything about the members they do not name.

For a complete membership, remove local list entries beyond `count` or dictionary entries whose keys are not listed. Create missing members in the order `items` lists them: a list is first padded with nulls up to `count`, and each item is then written at its own index, so the order of `items` does not decide where a member lands. Retain existing child instances at matching positions or keys and apply only the child properties present in the payload. Complete membership does not imply complete child properties.

`count: 0` with absent or empty `items` and no operations, marked `Complete`, declares an empty collection or dictionary. Applying it creates an empty container even when the receiver currently holds null. A null value remains represented by `kind: Value` with a null value.

| Receiver before | Incoming membership | Receiver after |
|---|---|---|
| `[A, B, C]` | `mode: Complete`, `count: 1`, item A at index 0 | `[A]`, preserving A's unspecified properties |
| `{a: A, b: B}` | `mode: Complete`, `count: 1`, item A at key a | `{a: A}` |
| `[null]` | `mode: Complete`, `count: 1`, item A at index 0 | Create A at index 0 |
| `{a: A, stale: null}` | `mode: Complete`, `count: 1`, item A at key a | `{a: A}` |
| `null` or `[A]` | `mode: Complete`, `count: 0`, no items | `[]` |
| `[A, B, C]` | `count: 2`, items A and B at indices 0 and 1, no `mode` | Update A and B; retain C |
| `[A, B, C]` | An item update for A without `count` | Update A; retain B and C |

Entries that are not marked `Complete`, and marked entries whose shape does not state a whole membership, keep the structural-operation and sparse-item behavior. Missing payloads still report failures as described above; a duplicate position or converted key does not establish complete membership. Another applier, such as a TypeScript one, reconciles exactly the marked entries that pass these checks, and must not infer complete membership from the shape of an unmarked entry.

## Circular References

Circular references are handled naturally by the flat structure. Each subject instance appears exactly once in the `subjects` dictionary, and references use string IDs. Distinct instances retain distinct IDs even when their `Equals` implementation considers them equal:

```json
{
  "root": "1",
  "subjects": {
    "1": {
      "name": { "kind": "Value", "value": "Parent" },
      "child": { "kind": "Object", "id": "2" }
    },
    "2": {
      "name": { "kind": "Value", "value": "Child" },
      "parent": { "kind": "Object", "id": "1" }
    }
  }
}
```

No special `reference` field is needed - the `id` field always points to a subject in the dictionary. IDs are scoped to one update: the same ID within an update is the same subject, and IDs do not identify subjects across updates. A null object ID intentionally clears the reference. Remove operations need only the index or key, without subject payload.

Every subject referenced by the final update must have Registry metadata, including subjects returned by derived properties. A derived property that holds subjects and has no setter is a projection: it is not published, neither in complete nor in partial updates, and the applier ignores an update naming one, because the receiving model computes it itself and walking into a getter that creates a subject on every read would never end. A derived property with a setter stores an ordinary reference and is published. Intermediate references overwritten while building a batch do not require a payload. A subject that has left the graph, or a projection the graph never owned, is still valid to read, but the wire cannot carry it: update creation omits the referencing property instead of emitting a dangling ID and logs a warning naming it, qualified by the type of the subject that owns it. The receiver therefore keeps its own value for that property. Register the referenced subject or exclude the property with an `ISubjectUpdateProcessor` to silence the warning. A subject detached after its change was captured needs neither: the next update converges. Creation never throws for this, because the complete update is also the snapshot sent on every connector handshake.

## Null Collections and Dictionaries

When a collection or dictionary property is set to `null`, it is represented as `Kind=Value, Value=null`, the same as any other null property value:

```json
{
  "kind": "Value",
  "value": null
}
```

In partial updates, collection or dictionary entries without `count` are sparse path nodes: they describe the parent-to-child reference so the applier can navigate the tree. Only entries marked `mode: Complete` may instead establish [complete membership](#reconciling-complete-membership).

## Limitations

- **No "clear collection" operation**: clearing N items emits N individual Remove operations.
- **Non-subject collections** (`List<int>`, `Dictionary<string, string>`) use value-replacement semantics (full replacement, no granular diffing). Only `IInterceptorSubject` collections support structural diffs.
- **Conflict resolution** is last-applied-wins by message arrival order with eventual consistency via reconnection.
- **Shared subjects** have no identity across updates, so a subject referenced from two places arrives on the receiver as two instances unless both references are in the same update. Within one update the C# applier binds each ID to the first subject that receives its payload and resolves later references to that same subject, including references back to the root, but only where it creates the value: a property that already holds a different subject keeps its own instance and receives the payload without taking over the ID, unless that instance already took another ID's payload or the reference is marked `Replaced`. A property whose declared type the bound subject does not satisfy gets an instance already created for that ID that fits its type, or else a new one, so within one update each ID yields at most one instance per declared type. A reference inside a complete payload can also name a subject the update carries only a path entry for; when the applier meets that reference before the path, it creates the subject from the path entry alone, so that instance lacks everything the path does not state.
- **Reassigned references** are fixed where marked: a reference marked `Replaced` receives the new subject instead of having its payload written into the subject it held, so another reference sharing that instance on the receiver keeps it intact. A reference inside a complete payload is marked as well when the batch reassigned it; the references of a complete update are never marked and match by position, like complete membership below.
- **Complete membership matches by position or key**, not by subject identity, so when membership moves a different subject into a position the receiver applies the new payload onto the instance already sitting there, and that instance keeps every property the payload does not mention.
- **Dictionary keys** are carried in the item or operation's `index` field and converted to the declared key type before lookup.
- **Element types** are read from the declared collection or dictionary interfaces and must resolve uniquely. A legacy wrapper implementing only the non-generic `ICollection` or `IDictionary` falls back to its own generic arguments, read by position. A declaration naming several possible item types, of which more than one could hold a subject, and one naming none at all both throw `NotSupportedException`. This runs before any `ISubjectFactory` is consulted, so a factory cannot change which item subject is created for an incoming update or which key type a dictionary update is converted to.
- **Container types** are the factory's choice. The default factory creates arrays, `List<T>` and `Dictionary<TKey, TValue>`, so a property declared as a more specific container receives one of those instead; supply an `ISubjectFactory` to create the declared type.

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
