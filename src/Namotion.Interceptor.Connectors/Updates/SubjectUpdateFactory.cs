using System.Collections;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Factory for creating <see cref="SubjectUpdate"/> instances.
/// Uses a flat structure where all subjects are stored in a single dictionary
/// and referenced by string IDs, eliminating cycle detection complexity.
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

        ProcessSubjectComplete(subject, context);
        context.ApplyTransformations();

        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = context.Subjects
        };

        // Apply subject-level transformations (e.g., camelCase property names)
        for (var i = 0; i < processors.Length; i++)
        {
            update = processors[i].TransformSubjectUpdate(subject, update);
        }

        return update;
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

        for (var i = 0; i < propertyChanges.Length; i++)
        {
            ProcessPropertyChange(propertyChanges[i], rootSubject, context);
        }

        context.ApplyTransformations();

        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = context.Subjects
        };

        // Apply subject-level transformations (e.g., camelCase property names)
        for (var i = 0; i < processors.Length; i++)
        {
            update = processors[i].TransformSubjectUpdate(rootSubject, update);
        }

        return update;
    }

    private static void ProcessSubjectComplete(
        IInterceptorSubject subject,
        UpdateContext context)
    {
        var subjectId = context.GetOrCreateId(subject);

        if (!context.ProcessedSubjects.Add(subject))
            return;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        // Pre-compute index maps for O(1) lookups instead of O(n) IndexOf calls
        var oldIndexMap = new Dictionary<IInterceptorSubject, int>(oldItems.Count);
        for (var i = 0; i < oldItems.Count; i++)
            oldIndexMap[oldItems[i]] = i;

        var newIndexMap = new Dictionary<IInterceptorSubject, int>(newItems.Count);
        for (var i = 0; i < newItems.Count; i++)
            newIndexMap[newItems[i]] = i;

        List<SubjectCollectionOperation>? operations = null;
        List<SubjectPropertyCollectionUpdate>? updates = null;

        // Generate Remove operations (process from highest index first to preserve indices)
        for (var i = oldItems.Count - 1; i >= 0; i--)
        {
            var item = oldItems[i];
            if (!newIndexMap.ContainsKey(item))
            {
                operations ??= [];
                operations.Insert(0, new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Remove,
                    Index = i
                });
            }
        }

        // Generate Insert operations for new items
        for (var i = 0; i < newItems.Count; i++)
        {
            var item = newItems[i];
            if (!oldIndexMap.ContainsKey(item))
            {
                var itemId = context.GetOrCreateId(item);
                ProcessSubjectComplete(item, context);

                operations ??= [];
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Insert,
                    Index = i,
                    Id = itemId
                });
            }
        }

        // Check if common items were reordered - use index comparison
        // Build lists of common items in their respective orders
        var oldCommonOrder = new List<IInterceptorSubject>();
        var newCommonOrder = new List<IInterceptorSubject>();

        for (var i = 0; i < oldItems.Count; i++)
        {
            if (newIndexMap.ContainsKey(oldItems[i]))
                oldCommonOrder.Add(oldItems[i]);
        }

        for (var i = 0; i < newItems.Count; i++)
        {
            if (oldIndexMap.ContainsKey(newItems[i]))
                newCommonOrder.Add(newItems[i]);
        }

        // Check if relative order changed
        var hasReorder = oldCommonOrder.Count > 0 && !oldCommonOrder.SequenceEqual(newCommonOrder);
        if (hasReorder)
        {
            // Build a map of old common order position
            var oldCommonIndexMap = new Dictionary<IInterceptorSubject, int>(oldCommonOrder.Count);
            for (var i = 0; i < oldCommonOrder.Count; i++)
                oldCommonIndexMap[oldCommonOrder[i]] = i;

            for (var i = 0; i < newCommonOrder.Count; i++)
            {
                var item = newCommonOrder[i];
                var oldCommonIndex = oldCommonIndexMap[item];

                if (oldCommonIndex != i)
                {
                    operations ??= [];
                    operations.Add(new SubjectCollectionOperation
                    {
                        Action = SubjectCollectionOperationType.Move,
                        FromIndex = oldIndexMap[item],
                        Index = newIndexMap[item]
                    });
                }
            }
        }

        // Generate sparse updates for common items with property changes
        for (var i = 0; i < newCommonOrder.Count; i++)
        {
            var item = newCommonOrder[i];
            if (context.SubjectHasUpdates(item))
            {
                var itemId = context.GetOrCreateId(item);
                updates ??= [];
                updates.Add(new SubjectPropertyCollectionUpdate
                {
                    Index = newIndexMap[item],
                    Id = itemId
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

            if (parentInfo.Index is not null)
            {
                // Collection item - need to append to existing collection update or create new
                if (parentProperties.TryGetValue(parentProperty.Name, out var existingUpdate))
                {
                    // Append this item to the existing collection update
                    existingUpdate.Collection ??= [];
                    existingUpdate.Collection.Add(new SubjectPropertyCollectionUpdate
                    {
                        Index = parentInfo.Index,
                        Id = context.GetOrCreateId(current.Subject)
                    });
                }
                else
                {
                    // Create new collection update
                    var propertyUpdate = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Collection =
                        [
                            new SubjectPropertyCollectionUpdate
                            {
                                Index = parentInfo.Index,
                                Id = context.GetOrCreateId(current.Subject)
                            }
                        ]
                    };
                    parentProperties[parentProperty.Name] = propertyUpdate;
                }
            }
            else
            {
                // Single item reference - skip if already has this property
                if (!parentProperties.ContainsKey(parentProperty.Name))
                {
                    var propertyUpdate = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Item,
                        Id = context.GetOrCreateId(current.Subject)
                    };
                    parentProperties[parentProperty.Name] = propertyUpdate;
                }
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
        // Find the root property
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        private readonly ISubjectUpdateProcessor[] _processors;

        public ReadOnlySpan<ISubjectUpdateProcessor> Processors => _processors;
        public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; } = new();
        public HashSet<IInterceptorSubject> ProcessedSubjects { get; } = new();

        public UpdateContext(ReadOnlySpan<ISubjectUpdateProcessor> processors)
        {
            _processors = processors.ToArray();
        }

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
