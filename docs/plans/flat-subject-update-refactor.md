# Flat SubjectUpdate Refactor - Implementation Plan

## Goal

Refactor `SubjectUpdate` from a complex hierarchical tree structure to a simple flat dictionary structure. This eliminates cycle detection complexity, fixes the dangling reference bug, and makes the code significantly easier to understand and maintain.

**Public APIs remain unchanged** - only internal implementation changes.

---

## New JSON Format

### Before (Hierarchical - Complex)
```json
{
  "Id": 1,
  "Properties": {
    "name": { "Kind": "Value", "Value": "Root" },
    "child": {
      "Kind": "Item",
      "Item": {
        "Properties": {
          "name": { "Kind": "Value", "Value": "Child" },
          "parent": {
            "Kind": "Item",
            "Item": { "Reference": 1 }
          }
        }
      }
    }
  }
}
```

### After (Flat - Simple)
```json
{
  "root": "1",
  "subjects": {
    "1": {
      "name": { "Kind": "Value", "Value": "Root" },
      "child": { "Kind": "Item", "Id": "2" }
    },
    "2": {
      "name": { "Kind": "Value", "Value": "Child" },
      "parent": { "Kind": "Item", "Id": "1" }
    }
  }
}
```

### Benefits
- **No cycles possible** - references are just string IDs
- **No dangling references** - every ID exists in dictionary
- **O(1) lookup** - simple dictionary access
- **Each subject appears once** - no duplication
- **Simpler code** - no complex cycle detection logic

---

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| ID type | `string` | Works naturally with JSON, TypeScript, no conversions |
| Null items | Omit `Id` property | `{ "Kind": "Item" }` = null, saves bytes |
| Path completeness | All subjects reachable from root | Apply logic can navigate the tree |
| Complete vs Partial | Same structure | Partial just has fewer subjects/properties |
| Extension data on subjects | Dropped for now | Can add `$` prefix later if needed |

---

## Implementation Tasks

### Phase 1: Update Data Models

#### Task 1.1: Update SubjectUpdate.cs

**File:** `src/Namotion.Interceptor.Connectors/Updates/SubjectUpdate.cs`

**Current:** Has `Id`, `Reference`, `Properties` for hierarchical tree.

**New:** Has `Root`, `Subjects` for flat dictionary.

```csharp
using System.Text.Json.Serialization;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents an update containing one or more subject property updates.
/// Uses a flat structure where all subjects are stored in a dictionary
/// and referenced by string IDs.
/// </summary>
public class SubjectUpdate
{
    /// <summary>
    /// The ID of the root subject in the <see cref="Subjects"/> dictionary.
    /// </summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>
    /// Dictionary of all subjects keyed by their string ID.
    /// Each subject is a dictionary of property name to property update.
    /// </summary>
    public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; set; } = new();

    /// <summary>
    /// Creates a complete update with all objects and properties for the given subject as root.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreateCompleteUpdate(
        IInterceptorSubject subject,
        ReadOnlySpan<ISubjectUpdateProcessor> processors)
        => SubjectUpdateFactory.CreateComplete(subject, processors);

    /// <summary>
    /// Creates a partial update from the given property changes.
    /// Only directly or indirectly needed objects and properties are added.
    /// </summary>
    /// <param name="subject">The root subject.</param>
    /// <param name="propertyChanges">The changes to look up within the object graph.</param>
    /// <param name="processors">The update processors to filter and transform updates.</param>
    /// <returns>The update.</returns>
    public static SubjectUpdate CreatePartialUpdateFromChanges(
        IInterceptorSubject subject,
        ReadOnlySpan<SubjectPropertyChange> propertyChanges,
        ReadOnlySpan<ISubjectUpdateProcessor> processors)
        => SubjectUpdateFactory.CreatePartialFromChanges(subject, propertyChanges, processors);
}
```

**Verification:**
```bash
# Should compile without errors
dotnet build src/Namotion.Interceptor.Connectors/Namotion.Interceptor.Connectors.csproj
```

---

#### Task 1.2: Update SubjectPropertyUpdate.cs

**File:** `src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyUpdate.cs`

**Changes:**
- Remove `Item` property (was `SubjectUpdate?`)
- Add `Id` property (`string?`) for Item kind references

```csharp
using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents a property update within a subject.
/// </summary>
public class SubjectPropertyUpdate
{
    /// <summary>
    /// The kind of property update.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SubjectPropertyUpdateKind Kind { get; set; }

    /// <summary>
    /// The value for Value kind properties.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Value { get; set; }

    /// <summary>
    /// The timestamp of when the value was changed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>
    /// The subject ID for Item kind properties.
    /// Null means the item reference is null.
    /// Omitted entirely when null (no "Id": null in JSON).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    /// <summary>
    /// Structural operations (Remove, Insert, Move) for Collection kind.
    /// Applied in order BEFORE sparse property updates.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SubjectCollectionOperation>? Operations { get; set; }

    /// <summary>
    /// Sparse property updates by final index/key for Collection kind.
    /// Applied AFTER structural operations.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SubjectPropertyCollectionUpdate>? Collection { get; set; }

    /// <summary>
    /// Total count of collection after all operations.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Count { get; set; }

    /// <summary>
    /// Attribute updates for this property.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, SubjectPropertyUpdate>? Attributes { get; set; }

    /// <summary>
    /// Extension data for custom properties added by processors.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? ExtensionData { get; set; }
}
```

---

#### Task 1.3: Update SubjectCollectionOperation.cs

**File:** `src/Namotion.Interceptor.Connectors/Updates/SubjectCollectionOperation.cs`

**Changes:**
- Remove `Item` property (was `SubjectUpdate?`)
- Add `Id` property (`string?`) for Insert operations

```csharp
using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents a structural operation on a collection.
/// </summary>
public class SubjectCollectionOperation
{
    /// <summary>
    /// The type of operation.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SubjectCollectionOperationType Action { get; set; }

    /// <summary>
    /// Target index (int for arrays) or key (string for dictionaries).
    /// </summary>
    public object Index { get; set; } = null!;

    /// <summary>
    /// Source index for Move operations (arrays only).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FromIndex { get; set; }

    /// <summary>
    /// The subject ID for Insert operations.
    /// References a subject in the Subjects dictionary.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }
}
```

---

#### Task 1.4: Update SubjectPropertyCollectionUpdate.cs

**File:** `src/Namotion.Interceptor.Connectors/Updates/SubjectPropertyCollectionUpdate.cs`

**Changes:**
- Remove `Item` property (was `SubjectUpdate?`)
- Add `Id` property (`string?`)

```csharp
using System.Text.Json.Serialization;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Represents a sparse property update for a collection item at a specific index/key.
/// </summary>
public class SubjectPropertyCollectionUpdate
{
    /// <summary>
    /// The target index (int for arrays) or key (string for dictionaries).
    /// This is the FINAL index after structural operations are applied.
    /// </summary>
    public object Index { get; set; } = null!;

    /// <summary>
    /// The subject ID for this collection item.
    /// References a subject in the Subjects dictionary.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }
}
```

---

### Phase 2: Rewrite Factory (Create Logic)

#### Task 2.1: Rewrite SubjectUpdateFactory.cs

**File:** `src/Namotion.Interceptor.Connectors/Updates/SubjectUpdateFactory.cs`

This is a complete rewrite. The new logic is much simpler:

1. **ID Assignment:** Each subject gets a unique string ID when first encountered
2. **Flat Collection:** All subjects go into one dictionary
3. **No Cycle Detection Needed:** References are just IDs, not object references

```csharp
using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Factory for creating SubjectUpdate instances.
/// </summary>
public static class SubjectUpdateFactory
{
    /// <summary>
    /// Creates a complete update with all properties for the given subject.
    /// </summary>
    public static SubjectUpdate CreateComplete(
        IInterceptorSubject subject,
        ReadOnlySpan<ISubjectUpdateProcessor> processors)
    {
        var context = new UpdateContext(processors);
        var rootId = context.GetOrCreateId(subject);

        // Process root and all reachable subjects
        ProcessSubjectComplete(subject, context);

        // Apply transformations
        context.ApplyTransformations();

        return new SubjectUpdate
        {
            Root = rootId,
            Subjects = context.Subjects
        };
    }

    /// <summary>
    /// Creates a partial update from property changes.
    /// </summary>
    public static SubjectUpdate CreatePartialFromChanges(
        IInterceptorSubject rootSubject,
        ReadOnlySpan<SubjectPropertyChange> propertyChanges,
        ReadOnlySpan<ISubjectUpdateProcessor> processors)
    {
        var context = new UpdateContext(processors);
        var rootId = context.GetOrCreateId(rootSubject);

        // Process each property change
        for (var i = 0; i < propertyChanges.Length; i++)
        {
            var change = propertyChanges[i];
            ProcessPropertyChange(change, rootSubject, context);
        }

        // Apply transformations
        context.ApplyTransformations();

        return new SubjectUpdate
        {
            Root = rootId,
            Subjects = context.Subjects
        };
    }

    private static void ProcessSubjectComplete(
        IInterceptorSubject subject,
        UpdateContext context)
    {
        var subjectId = context.GetOrCreateId(subject);

        // Skip if already fully processed
        if (context.ProcessedSubjects.Contains(subject))
            return;
        context.ProcessedSubjects.Add(subject);

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
            return;

        var properties = context.GetOrCreateProperties(subjectId);

        foreach (var property in registeredSubject.Properties)
        {
            if (!property.HasGetter || property.IsAttribute)
                continue;

            if (!IsPropertyIncluded(property, context.Processors))
                continue;

            var propertyUpdate = CreatePropertyUpdate(property, context);
            properties[property.Name] = propertyUpdate;

            context.TrackPropertyUpdate(propertyUpdate, property, properties);
        }
    }

    private static void ProcessPropertyChange(
        SubjectPropertyChange change,
        IInterceptorSubject rootSubject,
        UpdateContext context)
    {
        var changedSubject = change.Property.Subject;
        var registeredProperty = change.Property.TryGetRegisteredProperty();

        if (registeredProperty is null)
            return;

        if (!IsPropertyIncluded(registeredProperty, context.Processors))
            return;

        var subjectId = context.GetOrCreateId(changedSubject);
        var properties = context.GetOrCreateProperties(subjectId);

        if (registeredProperty.IsAttribute)
        {
            ProcessAttributeChange(registeredProperty, change, properties, context);
        }
        else
        {
            var propertyUpdate = CreatePropertyUpdateFromChange(registeredProperty, change, context);
            properties[registeredProperty.Name] = propertyUpdate;
            context.TrackPropertyUpdate(propertyUpdate, registeredProperty, properties);
        }

        // Build path from changed subject to root
        BuildPathToRoot(changedSubject, rootSubject, context);
    }

    private static SubjectPropertyUpdate CreatePropertyUpdate(
        RegisteredSubjectProperty property,
        UpdateContext context)
    {
        var value = property.GetValue();
        var timestamp = property.Reference.TryGetWriteTimestamp();

        var update = new SubjectPropertyUpdate { Timestamp = timestamp };

        if (property.IsSubjectDictionary)
        {
            ApplyDictionaryComplete(update, value as IDictionary, context);
        }
        else if (property.IsSubjectCollection)
        {
            ApplyCollectionComplete(update, value as IEnumerable<IInterceptorSubject>, context);
        }
        else if (property.IsSubjectReference)
        {
            ApplyItemReference(update, value as IInterceptorSubject, context);
        }
        else
        {
            update.Kind = SubjectPropertyUpdateKind.Value;
            update.Value = value;
        }

        // Process attributes
        update.Attributes = CreateAttributeUpdates(property, context);

        return update;
    }

    private static SubjectPropertyUpdate CreatePropertyUpdateFromChange(
        RegisteredSubjectProperty property,
        SubjectPropertyChange change,
        UpdateContext context)
    {
        var update = new SubjectPropertyUpdate { Timestamp = change.ChangedTimestamp };

        if (property.IsSubjectDictionary)
        {
            ApplyDictionaryDiff(update, change.GetOldValue<IDictionary?>(),
                change.GetNewValue<IDictionary?>(), context);
        }
        else if (property.IsSubjectCollection)
        {
            ApplyCollectionDiff(update,
                change.GetOldValue<IEnumerable<IInterceptorSubject>?>(),
                change.GetNewValue<IEnumerable<IInterceptorSubject>?>(), context);
        }
        else if (property.IsSubjectReference)
        {
            ApplyItemReference(update, change.GetNewValue<IInterceptorSubject?>(), context);
        }
        else
        {
            update.Kind = SubjectPropertyUpdateKind.Value;
            update.Value = change.GetNewValue<object?>();
        }

        return update;
    }

    private static void ApplyItemReference(
        SubjectPropertyUpdate update,
        IInterceptorSubject? item,
        UpdateContext context)
    {
        update.Kind = SubjectPropertyUpdateKind.Item;

        if (item is not null)
        {
            update.Id = context.GetOrCreateId(item);
            ProcessSubjectComplete(item, context);
        }
        // If item is null, Id stays null (omitted in JSON)
    }

    private static void ApplyCollectionComplete(
        SubjectPropertyUpdate update,
        IEnumerable<IInterceptorSubject>? collection,
        UpdateContext context)
    {
        update.Kind = SubjectPropertyUpdateKind.Collection;

        if (collection is null)
            return;

        var items = collection.ToList();
        update.Count = items.Count;
        update.Collection = new List<SubjectPropertyCollectionUpdate>(items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var itemId = context.GetOrCreateId(item);
            ProcessSubjectComplete(item, context);

            update.Collection.Add(new SubjectPropertyCollectionUpdate
            {
                Index = i,
                Id = itemId
            });
        }
    }

    private static void ApplyCollectionDiff(
        SubjectPropertyUpdate update,
        IEnumerable<IInterceptorSubject>? oldCollection,
        IEnumerable<IInterceptorSubject>? newCollection,
        UpdateContext context)
    {
        update.Kind = SubjectPropertyUpdateKind.Collection;

        if (newCollection is null)
            return;

        var oldItems = oldCollection?.ToList() ?? new List<IInterceptorSubject>();
        var newItems = newCollection.ToList();
        update.Count = newItems.Count;

        // Build old index map
        var oldIndexMap = new Dictionary<IInterceptorSubject, int>();
        for (var i = 0; i < oldItems.Count; i++)
            oldIndexMap[oldItems[i]] = i;

        var processedOld = new HashSet<IInterceptorSubject>();
        List<SubjectCollectionOperation>? operations = null;
        List<SubjectPropertyCollectionUpdate>? updates = null;

        for (var newIndex = 0; newIndex < newItems.Count; newIndex++)
        {
            var item = newItems[newIndex];
            var itemId = context.GetOrCreateId(item);

            if (oldIndexMap.TryGetValue(item, out var oldIndex))
            {
                processedOld.Add(item);

                // Item existed - check if moved
                if (oldIndex != newIndex)
                {
                    operations ??= new List<SubjectCollectionOperation>();
                    operations.Add(new SubjectCollectionOperation
                    {
                        Action = SubjectCollectionOperationType.Move,
                        FromIndex = oldIndex,
                        Index = newIndex
                    });
                }

                // Check if item has property updates
                if (context.SubjectHasUpdates(item))
                {
                    updates ??= new List<SubjectPropertyCollectionUpdate>();
                    updates.Add(new SubjectPropertyCollectionUpdate
                    {
                        Index = newIndex,
                        Id = itemId
                    });
                }
            }
            else
            {
                // New item - Insert
                ProcessSubjectComplete(item, context);

                operations ??= new List<SubjectCollectionOperation>();
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Insert,
                    Index = newIndex,
                    Id = itemId
                });
            }
        }

        // Find removed items
        foreach (var oldItem in oldItems)
        {
            if (!processedOld.Contains(oldItem))
            {
                var oldIndex = oldIndexMap[oldItem];
                operations ??= new List<SubjectCollectionOperation>();
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Remove,
                    Index = oldIndex
                });
            }
        }

        update.Operations = operations;
        update.Collection = updates;
    }

    private static void ApplyDictionaryComplete(
        SubjectPropertyUpdate update,
        IDictionary? dictionary,
        UpdateContext context)
    {
        update.Kind = SubjectPropertyUpdateKind.Collection;

        if (dictionary is null)
            return;

        update.Count = dictionary.Count;
        update.Collection = new List<SubjectPropertyCollectionUpdate>(dictionary.Count);

        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Value is IInterceptorSubject item)
            {
                var itemId = context.GetOrCreateId(item);
                ProcessSubjectComplete(item, context);

                update.Collection.Add(new SubjectPropertyCollectionUpdate
                {
                    Index = entry.Key,
                    Id = itemId
                });
            }
        }
    }

    private static void ApplyDictionaryDiff(
        SubjectPropertyUpdate update,
        IDictionary? oldDict,
        IDictionary? newDict,
        UpdateContext context)
    {
        update.Kind = SubjectPropertyUpdateKind.Collection;

        if (newDict is null)
            return;

        update.Count = newDict.Count;

        var oldKeys = new HashSet<object>(
            oldDict?.Keys.Cast<object>() ?? Enumerable.Empty<object>());

        List<SubjectCollectionOperation>? operations = null;
        List<SubjectPropertyCollectionUpdate>? updates = null;

        foreach (DictionaryEntry entry in newDict)
        {
            var key = entry.Key;
            var item = entry.Value as IInterceptorSubject;

            if (item is null)
                continue;

            var itemId = context.GetOrCreateId(item);

            if (oldKeys.Contains(key))
            {
                oldKeys.Remove(key);

                // Existing key - check for property updates
                if (context.SubjectHasUpdates(item))
                {
                    updates ??= new List<SubjectPropertyCollectionUpdate>();
                    updates.Add(new SubjectPropertyCollectionUpdate
                    {
                        Index = key,
                        Id = itemId
                    });
                }
            }
            else
            {
                // New key - Insert
                ProcessSubjectComplete(item, context);

                operations ??= new List<SubjectCollectionOperation>();
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Insert,
                    Index = key,
                    Id = itemId
                });
            }
        }

        // Removed keys
        foreach (var removedKey in oldKeys)
        {
            operations ??= new List<SubjectCollectionOperation>();
            operations.Add(new SubjectCollectionOperation
            {
                Action = SubjectCollectionOperationType.Remove,
                Index = removedKey
            });
        }

        update.Operations = operations;
        update.Collection = updates;
    }

    private static void BuildPathToRoot(
        IInterceptorSubject subject,
        IInterceptorSubject rootSubject,
        UpdateContext context)
    {
        var current = subject.TryGetRegisteredSubject();

        while (current is not null && current.Subject != rootSubject)
        {
            if (current.Parents.Length == 0)
                break;

            var parentInfo = current.Parents[0];
            var parentProperty = parentInfo.Property;
            var parentSubject = parentProperty.Parent;

            var parentId = context.GetOrCreateId(parentSubject.Subject);
            var parentProperties = context.GetOrCreateProperties(parentId);

            // Create property update pointing to current subject
            var propertyUpdate = new SubjectPropertyUpdate();

            if (parentInfo.Index is not null)
            {
                // Collection item
                propertyUpdate.Kind = SubjectPropertyUpdateKind.Collection;
                propertyUpdate.Collection = new List<SubjectPropertyCollectionUpdate>
                {
                    new SubjectPropertyCollectionUpdate
                    {
                        Index = parentInfo.Index,
                        Id = context.GetOrCreateId(current.Subject)
                    }
                };
            }
            else
            {
                // Single item reference
                propertyUpdate.Kind = SubjectPropertyUpdateKind.Item;
                propertyUpdate.Id = context.GetOrCreateId(current.Subject);
            }

            // Only add if not already present
            if (!parentProperties.ContainsKey(parentProperty.Name))
            {
                parentProperties[parentProperty.Name] = propertyUpdate;
            }

            current = parentSubject;
        }
    }

    private static Dictionary<string, SubjectPropertyUpdate>? CreateAttributeUpdates(
        RegisteredSubjectProperty property,
        UpdateContext context)
    {
        Dictionary<string, SubjectPropertyUpdate>? attributes = null;

        foreach (var attribute in property.Attributes)
        {
            if (!attribute.HasGetter)
                continue;

            attributes ??= new Dictionary<string, SubjectPropertyUpdate>();

            var attrUpdate = new SubjectPropertyUpdate
            {
                Kind = SubjectPropertyUpdateKind.Value,
                Value = attribute.GetValue(),
                Timestamp = attribute.Reference.TryGetWriteTimestamp()
            };

            attributes[attribute.AttributeMetadata.AttributeName] = attrUpdate;
            context.TrackPropertyUpdate(attrUpdate, attribute, attributes);
        }

        return attributes;
    }

    private static void ProcessAttributeChange(
        RegisteredSubjectProperty attributeProperty,
        SubjectPropertyChange change,
        Dictionary<string, SubjectPropertyUpdate> subjectProperties,
        UpdateContext context)
    {
        // Find or create the root property update
        var rootProperty = attributeProperty;
        while (rootProperty.IsAttribute)
        {
            rootProperty = rootProperty.GetAttributedProperty();
        }

        if (!subjectProperties.TryGetValue(rootProperty.Name, out var rootUpdate))
        {
            rootUpdate = new SubjectPropertyUpdate();
            subjectProperties[rootProperty.Name] = rootUpdate;
        }

        // Navigate/create attribute chain
        var currentUpdate = rootUpdate;
        var attrChain = new List<RegisteredSubjectProperty>();
        var prop = attributeProperty;
        while (prop.IsAttribute)
        {
            attrChain.Insert(0, prop);
            prop = prop.GetAttributedProperty();
        }

        foreach (var attrProp in attrChain)
        {
            currentUpdate.Attributes ??= new Dictionary<string, SubjectPropertyUpdate>();
            var attrName = attrProp.AttributeMetadata.AttributeName;

            if (!currentUpdate.Attributes.TryGetValue(attrName, out var attrUpdate))
            {
                attrUpdate = new SubjectPropertyUpdate();
                currentUpdate.Attributes[attrName] = attrUpdate;
            }

            currentUpdate = attrUpdate;
        }

        // Apply the value
        currentUpdate.Kind = SubjectPropertyUpdateKind.Value;
        currentUpdate.Value = change.GetNewValue<object?>();
        currentUpdate.Timestamp = change.ChangedTimestamp;

        context.TrackPropertyUpdate(currentUpdate, attributeProperty,
            currentUpdate == rootUpdate ? subjectProperties : rootUpdate.Attributes!);
    }

    private static bool IsPropertyIncluded(
        RegisteredSubjectProperty property,
        ReadOnlySpan<ISubjectUpdateProcessor> processors)
    {
        for (var i = 0; i < processors.Length; i++)
        {
            if (!processors[i].IsIncluded(property))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Context for building a SubjectUpdate. Tracks IDs, subjects, and transformations.
    /// </summary>
    private sealed class UpdateContext
    {
        private int _nextId;
        private readonly Dictionary<IInterceptorSubject, string> _subjectToId = new();
        private readonly Dictionary<SubjectPropertyUpdate, (RegisteredSubjectProperty Property, IDictionary<string, SubjectPropertyUpdate> Parent)> _propertyUpdates = new();

        public ReadOnlySpan<ISubjectUpdateProcessor> Processors { get; }
        public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; } = new();
        public HashSet<IInterceptorSubject> ProcessedSubjects { get; } = new();

        public UpdateContext(ReadOnlySpan<ISubjectUpdateProcessor> processors)
        {
            // Copy processors to array for later use in transformations
            Processors = processors;
            _processors = processors.ToArray();
        }

        private readonly ISubjectUpdateProcessor[] _processors;

        public string GetOrCreateId(IInterceptorSubject subject)
        {
            if (!_subjectToId.TryGetValue(subject, out var id))
            {
                id = (++_nextId).ToString();
                _subjectToId[subject] = id;
            }
            return id;
        }

        public Dictionary<string, SubjectPropertyUpdate> GetOrCreateProperties(string subjectId)
        {
            if (!Subjects.TryGetValue(subjectId, out var properties))
            {
                properties = new Dictionary<string, SubjectPropertyUpdate>();
                Subjects[subjectId] = properties;
            }
            return properties;
        }

        public bool SubjectHasUpdates(IInterceptorSubject subject)
        {
            if (_subjectToId.TryGetValue(subject, out var id))
            {
                return Subjects.TryGetValue(id, out var props) && props.Count > 0;
            }
            return false;
        }

        public void TrackPropertyUpdate(
            SubjectPropertyUpdate update,
            RegisteredSubjectProperty property,
            IDictionary<string, SubjectPropertyUpdate> parent)
        {
            if (_processors.Length > 0)
            {
                _propertyUpdates[update] = (property, parent);
            }
        }

        public void ApplyTransformations()
        {
            if (_processors.Length == 0)
                return;

            foreach (var (update, info) in _propertyUpdates)
            {
                for (var i = 0; i < _processors.Length; i++)
                {
                    var transformed = _processors[i].TransformSubjectPropertyUpdate(info.Property, update);
                    if (transformed != update)
                    {
                        info.Parent[info.Property.Name] = transformed;
                    }
                }
            }
        }
    }
}
```

---

### Phase 3: Rewrite Apply Logic

#### Task 3.1: Rewrite SubjectUpdateExtensions.cs

**File:** `src/Namotion.Interceptor.Connectors/Updates/SubjectUpdateExtensions.cs`

The apply logic is now much simpler - just look up subjects in the dictionary.

```csharp
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Extension methods for applying SubjectUpdate to subjects.
/// </summary>
public static class SubjectUpdateExtensions
{
    /// <summary>
    /// Applies update from an external source with source tracking.
    /// </summary>
    public static void ApplySubjectUpdateFromSource(
        this IInterceptorSubject subject,
        SubjectUpdate update,
        object source,
        ISubjectFactory? subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        var receivedTimestamp = DateTimeOffset.UtcNow;

        ApplySubjectUpdate(subject, update, subjectFactory, (property, propertyUpdate) =>
        {
            transformValueBeforeApply?.Invoke(property, propertyUpdate);
            var value = ConvertValue(propertyUpdate.Value, property.Type);
            property.SetValueFromSource(source, propertyUpdate.Timestamp, receivedTimestamp, value);
        });
    }

    /// <summary>
    /// Applies update to a subject.
    /// </summary>
    public static void ApplySubjectUpdate(
        this IInterceptorSubject subject,
        SubjectUpdate update,
        ISubjectFactory? subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        if (string.IsNullOrEmpty(update.Root))
            return;

        if (!update.Subjects.TryGetValue(update.Root, out var rootProperties))
            return;

        var context = new ApplyContext(update.Subjects, subjectFactory ?? DefaultSubjectFactory.Instance,
            (property, propertyUpdate) =>
            {
                transformValueBeforeApply?.Invoke(property, propertyUpdate);
                var value = ConvertValue(propertyUpdate.Value, property.Type);
                property.SetValue(value);
            });

        ApplyProperties(subject, rootProperties, context);
    }

    private static void ApplyProperties(
        IInterceptorSubject subject,
        Dictionary<string, SubjectPropertyUpdate> properties,
        ApplyContext context)
    {
        var registry = subject.Context.GetService<ISubjectRegistry>();

        foreach (var (propertyName, propertyUpdate) in properties)
        {
            // Apply attributes first
            if (propertyUpdate.Attributes is not null)
            {
                foreach (var (attrName, attrUpdate) in propertyUpdate.Attributes)
                {
                    var registeredAttr = subject.TryGetRegisteredSubject()?
                        .TryGetPropertyAttribute(propertyName, attrName);

                    if (registeredAttr is not null)
                    {
                        ApplyPropertyUpdate(subject, registeredAttr.Name, attrUpdate, context, registry);
                    }
                }
            }

            ApplyPropertyUpdate(subject, propertyName, propertyUpdate, context, registry);
        }
    }

    private static void ApplyPropertyUpdate(
        IInterceptorSubject subject,
        string propertyName,
        SubjectPropertyUpdate propertyUpdate,
        ApplyContext context,
        ISubjectRegistry? registry)
    {
        var registeredProperty = subject.TryGetRegisteredProperty(propertyName, registry);
        if (registeredProperty is null)
            return;

        switch (propertyUpdate.Kind)
        {
            case SubjectPropertyUpdateKind.Value:
                using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                {
                    context.ApplyValue(registeredProperty, propertyUpdate);
                }
                break;

            case SubjectPropertyUpdateKind.Item:
                ApplyItemUpdate(subject, registeredProperty, propertyUpdate, context);
                break;

            case SubjectPropertyUpdateKind.Collection:
                if (registeredProperty.IsSubjectDictionary)
                    ApplyDictionaryUpdate(subject, registeredProperty, propertyUpdate, context);
                else
                    ApplyCollectionUpdate(subject, registeredProperty, propertyUpdate, context);
                break;
        }
    }

    private static void ApplyItemUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        ApplyContext context)
    {
        if (propertyUpdate.Id is not null &&
            context.Subjects.TryGetValue(propertyUpdate.Id, out var itemProperties))
        {
            var existingItem = property.GetValue() as IInterceptorSubject;

            if (existingItem is not null)
            {
                // Update existing
                ApplyProperties(existingItem, itemProperties, context);
            }
            else
            {
                // Create new
                var newItem = context.SubjectFactory.CreateSubject(property);
                if (newItem is not null)
                {
                    newItem.Context.AddFallbackContext(parent.Context);
                    ApplyProperties(newItem, itemProperties, context);

                    using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                    {
                        property.SetValue(newItem);
                    }
                }
            }
        }
        else
        {
            // Set to null
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                property.SetValue(null);
            }
        }
    }

    private static void ApplyCollectionUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        ApplyContext context)
    {
        var existingItems = (property.GetValue() as IEnumerable<IInterceptorSubject>)?
            .ToList() ?? new List<IInterceptorSubject>();
        var workingItems = new List<IInterceptorSubject>(existingItems);
        var structureChanged = false;

        // Phase 1: Structural operations
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            foreach (var op in propertyUpdate.Operations)
            {
                var index = Convert.ToInt32(op.Index);

                switch (op.Action)
                {
                    case SubjectCollectionOperationType.Remove:
                        if (index >= 0 && index < workingItems.Count)
                        {
                            workingItems.RemoveAt(index);
                            structureChanged = true;
                        }
                        break;

                    case SubjectCollectionOperationType.Insert:
                        if (op.Id is not null && context.Subjects.TryGetValue(op.Id, out var itemProps))
                        {
                            var newItem = context.SubjectFactory.CreateCollectionSubject(property, index);
                            newItem.Context.AddFallbackContext(parent.Context);
                            ApplyProperties(newItem, itemProps, context);

                            if (index >= workingItems.Count)
                                workingItems.Add(newItem);
                            else
                                workingItems.Insert(index, newItem);
                            structureChanged = true;
                        }
                        break;

                    case SubjectCollectionOperationType.Move:
                        if (op.FromIndex.HasValue)
                        {
                            var from = op.FromIndex.Value;
                            if (from >= 0 && from < workingItems.Count && index >= 0)
                            {
                                var item = workingItems[from];
                                workingItems.RemoveAt(from);
                                if (index >= workingItems.Count)
                                    workingItems.Add(item);
                                else
                                    workingItems.Insert(index, item);
                                structureChanged = true;
                            }
                        }
                        break;
                }
            }
        }

        // Phase 2: Sparse property updates
        if (propertyUpdate.Collection is { Count: > 0 })
        {
            foreach (var collUpdate in propertyUpdate.Collection)
            {
                var index = Convert.ToInt32(collUpdate.Index);

                if (collUpdate.Id is not null &&
                    context.Subjects.TryGetValue(collUpdate.Id, out var itemProps))
                {
                    if (index >= 0 && index < workingItems.Count)
                    {
                        // Update existing
                        ApplyProperties(workingItems[index], itemProps, context);
                    }
                    else if (index >= 0)
                    {
                        // Create new (complete update case)
                        var newItem = context.SubjectFactory.CreateCollectionSubject(property, index);
                        newItem.Context.AddFallbackContext(parent.Context);
                        ApplyProperties(newItem, itemProps, context);

                        while (workingItems.Count < index)
                            workingItems.Add(null!);

                        if (index >= workingItems.Count)
                            workingItems.Add(newItem);
                        else
                            workingItems[index] = newItem;
                        structureChanged = true;
                    }
                }
            }
        }

        if (structureChanged)
        {
            var collection = context.SubjectFactory.CreateSubjectCollection(property.Type, workingItems);
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                property.SetValue(collection);
            }
        }
    }

    private static void ApplyDictionaryUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        ApplyContext context)
    {
        var existingDict = property.GetValue() as System.Collections.IDictionary;
        var workingDict = new Dictionary<object, IInterceptorSubject>();
        var structureChanged = false;

        // Copy existing
        if (existingDict is not null)
        {
            foreach (System.Collections.DictionaryEntry entry in existingDict)
            {
                if (entry.Value is IInterceptorSubject item)
                    workingDict[entry.Key] = item;
            }
        }

        // Phase 1: Structural operations
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            foreach (var op in propertyUpdate.Operations)
            {
                var key = ConvertDictKey(op.Index);

                switch (op.Action)
                {
                    case SubjectCollectionOperationType.Remove:
                        if (workingDict.Remove(key))
                            structureChanged = true;
                        break;

                    case SubjectCollectionOperationType.Insert:
                        if (op.Id is not null && context.Subjects.TryGetValue(op.Id, out var itemProps))
                        {
                            var newItem = context.SubjectFactory.CreateCollectionSubject(property, key);
                            newItem.Context.AddFallbackContext(parent.Context);
                            ApplyProperties(newItem, itemProps, context);
                            workingDict[key] = newItem;
                            structureChanged = true;
                        }
                        break;
                }
            }
        }

        // Phase 2: Sparse property updates
        if (propertyUpdate.Collection is { Count: > 0 })
        {
            foreach (var collUpdate in propertyUpdate.Collection)
            {
                var key = ConvertDictKey(collUpdate.Index);

                if (collUpdate.Id is not null &&
                    context.Subjects.TryGetValue(collUpdate.Id, out var itemProps))
                {
                    if (workingDict.TryGetValue(key, out var existing))
                    {
                        // Update existing
                        ApplyProperties(existing, itemProps, context);
                    }
                    else
                    {
                        // Create new (complete update case)
                        var newItem = context.SubjectFactory.CreateCollectionSubject(property, key);
                        newItem.Context.AddFallbackContext(parent.Context);
                        ApplyProperties(newItem, itemProps, context);
                        workingDict[key] = newItem;
                        structureChanged = true;
                    }
                }
            }
        }

        if (structureChanged)
        {
            var dict = context.SubjectFactory.CreateSubjectDictionary(property.Type, workingDict);
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                property.SetValue(dict);
            }
        }
    }

    private static object ConvertValue(object? value, Type targetType)
    {
        if (value is null)
            return null!;

        if (value is System.Text.Json.JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => jsonElement.GetString()!,
                System.Text.Json.JsonValueKind.Number when targetType == typeof(int) => jsonElement.GetInt32(),
                System.Text.Json.JsonValueKind.Number when targetType == typeof(long) => jsonElement.GetInt64(),
                System.Text.Json.JsonValueKind.Number when targetType == typeof(double) => jsonElement.GetDouble(),
                System.Text.Json.JsonValueKind.Number when targetType == typeof(decimal) => jsonElement.GetDecimal(),
                System.Text.Json.JsonValueKind.Number => jsonElement.GetDouble(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => value
            };
        }

        return value;
    }

    private static object ConvertDictKey(object key)
    {
        if (key is System.Text.Json.JsonElement jsonElement)
            return jsonElement.GetString() ?? jsonElement.ToString();
        return key;
    }

    private sealed class ApplyContext
    {
        public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; }
        public ISubjectFactory SubjectFactory { get; }
        public Action<RegisteredSubjectProperty, SubjectPropertyUpdate> ApplyValue { get; }

        public ApplyContext(
            Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects,
            ISubjectFactory subjectFactory,
            Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValue)
        {
            Subjects = subjects;
            SubjectFactory = subjectFactory;
            ApplyValue = applyValue;
        }
    }
}
```

---

### Phase 4: Delete Obsolete Files

#### Task 4.1: Delete old logic files

These files are no longer needed - their logic is now integrated into the simplified factory and extensions:

```bash
# Delete these files (logic is now in SubjectUpdateFactory.cs and SubjectUpdateExtensions.cs)
rm src/Namotion.Interceptor.Connectors/Updates/Items/SubjectItemUpdateLogic.cs
rm src/Namotion.Interceptor.Connectors/Updates/Collections/SubjectCollectionUpdateLogic.cs
rm src/Namotion.Interceptor.Connectors/Updates/Collections/SubjectDictionaryUpdateLogic.cs
rm src/Namotion.Interceptor.Connectors/Updates/Values/SubjectValueUpdateLogic.cs
rm src/Namotion.Interceptor.Connectors/Updates/Performance/SubjectUpdatePools.cs
```

**Note:** If any of these contain utility methods used elsewhere, extract those first.

---

### Phase 5: Update Tests

#### Task 5.1: Update verified snapshots

All `.verified.txt` files need to be updated to reflect the new JSON format.

**Example - Old format:**
```json
{
  "Id": 1,
  "Properties": {
    "name": { "Kind": "Value", "Value": "Root" }
  }
}
```

**New format:**
```json
{
  "root": "1",
  "subjects": {
    "1": {
      "name": { "Kind": "Value", "Value": "Root" }
    }
  }
}
```

**Verification:**
```bash
# Run tests and accept new snapshots
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectUpdate"

# Review and accept changes
# (Verify uses *.verified.txt files)
```

---

### Phase 6: TypeScript Updates

#### Task 6.1: Update TypeScript interfaces

**File:** `variables-http.service.ts` (auto-generated, update source schema)

```typescript
/** Represents an update containing subject property updates. */
export interface SubjectUpdate {
    /** The ID of the root subject. */
    root: string;
    /** Dictionary of all subjects keyed by string ID. */
    subjects: Record<string, Record<string, SubjectPropertyUpdate>>;
}

export interface SubjectPropertyUpdate {
    kind?: SubjectPropertyUpdateKind;
    value?: any | null;
    timestamp?: string | null;
    /** Subject ID for Item kind. Null/omitted means null reference. */
    id?: string | null;
    operations?: SubjectCollectionOperation[] | null;
    collection?: SubjectPropertyCollectionUpdate[] | null;
    count?: number | null;
    attributes?: Record<string, SubjectPropertyUpdate> | null;
    [key: string]: any;
}

export interface SubjectCollectionOperation {
    action?: SubjectCollectionOperationType;
    index?: any;
    fromIndex?: number | null;
    /** Subject ID for Insert operations. */
    id?: string | null;
}

export interface SubjectPropertyCollectionUpdate {
    index?: any;
    /** Subject ID referencing a subject in the subjects dictionary. */
    id?: string | null;
}
```

#### Task 6.2: Update variables.facade.ts

**Changes needed:**
1. Update `updatePropertyVariables` to accept flat structure
2. Add subject lookup helper
3. Update Item/Collection handlers to use ID references

```typescript
// Key changes in VariablesFacade:

private updatePropertyVariables(
    obj: VariableObject & Record<string, any>,
    update: SubjectUpdate,  // Now has root + subjects
    fullPath: string
) {
    if (!update.subjects || !update.root) {
        return;
    }

    const rootProperties = update.subjects[update.root];
    if (!rootProperties) {
        return;
    }

    this.applyProperties(obj, rootProperties, update.subjects, fullPath);
}

private applyProperties(
    obj: VariableObject & Record<string, any>,
    properties: Record<string, SubjectPropertyUpdate>,
    allSubjects: Record<string, Record<string, SubjectPropertyUpdate>>,
    fullPath: string
) {
    for (const propertyName in properties) {
        const propertyUpdate = properties[propertyName];
        // ... handle based on kind
        // For Item kind: look up allSubjects[propertyUpdate.id]
        // For Collection: look up allSubjects[operation.id] or allSubjects[collUpdate.id]
    }
}

private handleItem(
    propertyUpdate: SubjectPropertyUpdate,
    allSubjects: Record<string, Record<string, SubjectPropertyUpdate>>,
    // ... other params
) {
    if (propertyUpdate.id) {
        const itemProperties = allSubjects[propertyUpdate.id];
        if (itemProperties) {
            // Apply itemProperties to the item object
            this.applyProperties(item, itemProperties, allSubjects, fullPath);
        }
    } else {
        // null item
    }
}
```

---

## Verification Checklist

```bash
# 1. Build succeeds
dotnet build src/Namotion.Interceptor.slnx

# 2. All tests pass
dotnet test src/Namotion.Interceptor.slnx

# 3. JSON format is correct (check a few verified snapshots)
cat src/Namotion.Interceptor.Connectors.Tests/*.verified.txt | head -50

# 4. No cycle errors in tests
dotnet test --filter "FullyQualifiedName~Cycle"

# 5. TypeScript compiles (in frontend repo)
cd /path/to/frontend && npm run build
```

---

## Summary

| Aspect | Before | After |
|--------|--------|-------|
| Structure | Nested tree with `Properties` | Flat dictionary with `root` + `subjects` |
| Cycle handling | Complex currentPath/Id/Reference | None needed - IDs are just strings |
| Same subject multiple times | Possible (causes cycles) | Impossible (one entry per ID) |
| Code complexity | ~500 lines across 5 files | ~300 lines in 2 files |
| Apply logic | Navigate tree, resolve References | Simple dictionary lookup |
| Debugging | Hard (nested structure) | Easy (flat, all subjects visible) |

**Public API unchanged:**
- `SubjectUpdate.CreateCompleteUpdate(subject, processors)`
- `SubjectUpdate.CreatePartialUpdateFromChanges(subject, changes, processors)`
- `subject.ApplySubjectUpdate(update, factory)`
- `subject.ApplySubjectUpdateFromSource(update, source, factory)`
