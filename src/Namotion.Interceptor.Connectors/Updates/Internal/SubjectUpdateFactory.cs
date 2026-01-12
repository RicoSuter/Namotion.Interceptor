using System.Collections;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Performance;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Factory for creating <see cref="SubjectUpdate"/> instances.
/// Uses a flat structure where all subjects are stored in a single dictionary
/// and referenced by string IDs, eliminating cycle detection complexity.
/// </summary>
internal static class SubjectUpdateFactory
{
    private static readonly ObjectPool<UpdateContext> ContextPool = new(() => new UpdateContext());

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

    internal static void ProcessSubjectComplete(
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
            CollectionUpdateBuilder.BuildDictionaryComplete(update, value as IDictionary, context);
        }
        else if (property.IsSubjectCollection)
        {
            CollectionUpdateBuilder.BuildCollectionComplete(update, value as IEnumerable<IInterceptorSubject>, context);
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
            CollectionUpdateBuilder.BuildDictionaryDiff(update, change.GetOldValue<IDictionary?>(),
                change.GetNewValue<IDictionary?>(), context);
        }
        else if (property.IsSubjectCollection)
        {
            CollectionUpdateBuilder.BuildCollectionDiff(update,
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

            // Only traverse the first parent - subjects may have multiple parents in a DAG,
            // but we follow the canonical registration path. This is intentional: each subject
            // was registered through one primary parent, and that's the path we use for updates.
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
