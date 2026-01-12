using System.Collections;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Connectors.Updates.Internal;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Performance;
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
    private static readonly ObjectPool<UpdateContext> ContextPool = new(() => new UpdateContext());
    private static readonly ObjectPool<CollectionChangeBuilder> ChangeBuilderPool = new(() => new CollectionChangeBuilder());

    /// <summary>
    /// Creates a complete update with all properties for the given subject.
    /// </summary>
    public static SubjectUpdate CreateComplete(
        IInterceptorSubject subject,
        ReadOnlySpan<ISubjectUpdateProcessor> processors)
    {
        var context = ContextPool.Rent();
        try
        {
            context.Initialize(processors);
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
        finally
        {
            context.Clear();
            ContextPool.Return(context);
        }
    }

    /// <summary>
    /// Creates a partial update from property changes.
    /// </summary>
    public static SubjectUpdate CreatePartialFromChanges(
        IInterceptorSubject rootSubject,
        ReadOnlySpan<SubjectPropertyChange> propertyChanges,
        ReadOnlySpan<ISubjectUpdateProcessor> processors)
    {
        var context = ContextPool.Rent();
        try
        {
            context.Initialize(processors);
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
        finally
        {
            context.Clear();
            ContextPool.Return(context);
        }
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
            BuildDictionaryComplete(update, value as IDictionary, context);
        }
        else if (property.IsSubjectCollection)
        {
            BuildCollectionComplete(update, value as IEnumerable<IInterceptorSubject>, context);
        }
        else if (property.IsSubjectReference)
        {
            BuildItemReference(update, value as IInterceptorSubject, context);
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
            BuildDictionaryDiff(update, change.GetOldValue<IDictionary?>(),
                change.GetNewValue<IDictionary?>(), context);
        }
        else if (property.IsSubjectCollection)
        {
            BuildCollectionDiff(update,
                change.GetOldValue<IEnumerable<IInterceptorSubject>?>(),
                change.GetNewValue<IEnumerable<IInterceptorSubject>?>(), context);
        }
        else if (property.IsSubjectReference)
        {
            BuildItemReference(update, change.GetNewValue<IInterceptorSubject?>(), context);
        }
        else
        {
            update.Kind = SubjectPropertyUpdateKind.Value;
            update.Value = change.GetNewValue<object?>();
        }

        return update;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BuildItemReference(
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

    private static void BuildCollectionComplete(
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

    private static void BuildCollectionDiff(
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

        var changeBuilder = ChangeBuilderPool.Rent();
        try
        {
            changeBuilder.BuildCollectionChanges(
                oldItems, newItems,
                out var operations,
                out var newItemsToProcess,
                out var reorderedItems);

            // Add Insert operations for new items
            foreach (var (index, item) in newItemsToProcess)
            {
                var itemId = context.GetOrCreateId(item);
                ProcessSubjectComplete(item, context);

                operations ??= new();
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Insert,
                    Index = index,
                    Id = itemId
                });
            }

            // Add Move operations for reordered items
            foreach (var (oldIndex, newIndex, item) in reorderedItems)
            {
                operations ??= new();
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Move,
                    FromIndex = oldIndex,
                    Index = newIndex
                });
            }

            // Generate sparse updates for common items with property changes
            List<SubjectPropertyCollectionUpdate>? updates = null;
            foreach (var item in changeBuilder.GetCommonItems())
            {
                if (context.SubjectHasUpdates(item))
                {
                    var itemId = context.GetOrCreateId(item);
                    var newIndex = changeBuilder.GetNewIndex(item);
                    updates ??= new();
                    updates.Add(new SubjectPropertyCollectionUpdate
                    {
                        Index = newIndex,
                        Id = itemId
                    });
                }
            }

            update.Operations = operations;
            update.Collection = updates;
        }
        finally
        {
            changeBuilder.Clear();
            ChangeBuilderPool.Return(changeBuilder);
        }
    }

    private static void BuildDictionaryComplete(
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

    private static void BuildDictionaryDiff(
        SubjectPropertyUpdate update,
        IDictionary? oldDict,
        IDictionary? newDict,
        UpdateContext context)
    {
        update.Kind = SubjectPropertyUpdateKind.Collection;

        if (newDict is null)
            return;

        update.Count = newDict.Count;

        var changeBuilder = ChangeBuilderPool.Rent();
        try
        {
            changeBuilder.BuildDictionaryChanges(
                oldDict, newDict,
                out var operations,
                out var newItemsToProcess,
                out var removedKeys);

            // Add Insert operations for new items
            foreach (var (key, item) in newItemsToProcess)
            {
                var itemId = context.GetOrCreateId(item);
                ProcessSubjectComplete(item, context);

                operations ??= new();
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Insert,
                    Index = key,
                    Id = itemId
                });
            }

            // Add Remove operations
            foreach (var removedKey in removedKeys)
            {
                operations ??= new();
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Remove,
                    Index = removedKey
                });
            }

            // Check for property updates on existing items
            List<SubjectPropertyCollectionUpdate>? updates = null;
            foreach (DictionaryEntry entry in newDict)
            {
                var key = entry.Key;
                var item = entry.Value as IInterceptorSubject;
                if (item is null)
                    continue;

                // Skip if this is a new item (already handled above)
                var isNewItem = newItemsToProcess.Any(n => Equals(n.key, key));
                if (isNewItem)
                    continue;

                if (context.SubjectHasUpdates(item))
                {
                    var itemId = context.GetOrCreateId(item);
                    updates ??= new();
                    updates.Add(new SubjectPropertyCollectionUpdate
                    {
                        Index = key,
                        Id = itemId
                    });
                }
            }

            update.Operations = operations;
            update.Collection = updates;
        }
        finally
        {
            changeBuilder.Clear();
            ChangeBuilderPool.Return(changeBuilder);
        }
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

}
