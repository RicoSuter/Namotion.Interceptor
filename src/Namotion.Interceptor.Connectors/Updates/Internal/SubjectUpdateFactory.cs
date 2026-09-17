using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Performance;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Factory for creating <see cref="SubjectUpdate"/> instances.
/// Uses a flat structure where all subjects are stored in a single dictionary
/// and referenced by string IDs, eliminating cycle detection complexity.
/// </summary>
internal static class SubjectUpdateFactory
{
    private static readonly ObjectPool<SubjectUpdateBuilder> BuilderPool = new(() => new SubjectUpdateBuilder());

    /// <summary>
    /// Creates a complete update with all properties for the given subject.
    /// </summary>
    public static SubjectUpdate CreateCompleteUpdate(
        IInterceptorSubject subject,
        ISubjectUpdateProcessor[] processors)
    {
        var builder = BuilderPool.Rent();
        try
        {
            builder.Initialize(subject, processors);
            ProcessSubjectComplete(subject, builder);
            return builder.Build(subject);
        }
        finally
        {
            builder.Clear();
            BuilderPool.Return(builder);
        }
    }

    /// <summary>
    /// Creates a partial update from property changes.
    /// </summary>
    public static SubjectUpdate CreatePartialUpdateFromChanges(
        IInterceptorSubject rootSubject,
        ReadOnlySpan<SubjectPropertyChange> propertyChanges,
        ISubjectUpdateProcessor[] processors)
    {
        var builder = BuilderPool.Rent();
        try
        {
            builder.Initialize(rootSubject, processors);
            propertyChanges = builder.MergeChanges(propertyChanges);

            // Subject-holding changes run first, so every assigned or inserted subject is complete before
            // value and attribute changes write their captured values and timestamps over it. Arrival
            // order cannot be relied on: merging can place a child's change before its parent's assignment.
            var deferredChanges = builder.DeferredChanges;
            for (var i = 0; i < propertyChanges.Length; i++)
            {
                var property = propertyChanges[i].Property.TryGetRegisteredProperty();
                if (property is null)
                    continue;

                if (property.CanContainSubjects)
                    ProcessPropertyChange(propertyChanges[i], property, rootSubject, builder);
                else
                    deferredChanges.Add((i, property));
            }

            foreach (var (index, property) in deferredChanges)
            {
                ProcessPropertyChange(propertyChanges[index], property, rootSubject, builder);
            }

            return builder.Build(rootSubject);
        }
        finally
        {
            builder.Clear();
            BuilderPool.Return(builder);
        }
    }

    internal static void ProcessSubjectComplete(
        IInterceptorSubject subject,
        SubjectUpdateBuilder builder)
    {
        var subjectId = builder.GetOrCreateId(subject);

        if (!builder.ProcessedSubjects.Add(subject))
            return;

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            builder.HasUnregisteredSubjects = true;
            return;
        }

        var properties = builder.GetOrCreateProperties(subjectId);

        foreach (var property in registeredSubject.Properties)
        {
            if (!property.HasGetter || property.IsAttribute)
                continue;

            if (IsComputedSubjectProjection(property))
                continue;

            if (!IsPropertyIncluded(property, builder.Processors))
                continue;

            var propertyUpdate = CreatePropertyUpdate(property, builder);
            properties[property.Name] = propertyUpdate;

            builder.TrackPropertyUpdate(propertyUpdate, property, properties);
        }
    }

    /// <summary>
    /// A computed derived property that holds subjects is a projection: it owns nothing, so the
    /// subjects behind it are often outside the graph and contribute no payload, and an id that
    /// describes nothing is worse on the wire than saying nothing at all. Skipped at the source,
    /// in complete payloads and in changes alike, so no receiver has to reason about it.
    /// </summary>
    /// <remarks>
    /// The setter test asks whether the value is stored on this side, which is what separates a
    /// projection from a real edge, and not whether a receiver could write it. A stored [Derived]
    /// property carries an ordinary edge and is still published. Value typed derived properties
    /// are published too: a receiver may display them or rely on this side to compute them, and
    /// they carry no reference that can dangle.
    /// </remarks>
    private static bool IsComputedSubjectProjection(RegisteredSubjectProperty property)
        => property.CanContainSubjects && property.Reference.Metadata.IsDerived && !property.HasSetter;

    private static void ProcessPropertyChange(
        SubjectPropertyChange change,
        RegisteredSubjectProperty registeredProperty,
        IInterceptorSubject rootSubject,
        SubjectUpdateBuilder builder)
    {
        var changedSubject = change.Property.Subject;

        if (IsComputedSubjectProjection(registeredProperty) || !IsPropertyIncluded(registeredProperty, builder.Processors))
            return;

        var subjectId = builder.GetOrCreateId(changedSubject);
        var properties = builder.GetOrCreateProperties(subjectId);

        // A complete payload already states this subject's final structure, and the receiver may be
        // building the subject from it, so it has no baseline for an incremental step. Value and
        // attribute changes still apply on top, which is what carries their captured values.
        if (registeredProperty.CanContainSubjects &&
            builder.ProcessedSubjects.Contains(changedSubject) &&
            properties.ContainsKey(registeredProperty.Name))
        {
            return;
        }

        if (registeredProperty.IsAttribute)
        {
            ProcessAttributeChange(registeredProperty, change, properties, builder);
        }
        else
        {
            // Try to get existing update (may have been created by earlier attribute changes)
            if (!properties.TryGetValue(registeredProperty.Name, out var propertyUpdate))
            {
                propertyUpdate = new SubjectPropertyUpdate();
                properties[registeredProperty.Name] = propertyUpdate;
            }

            // Update the property value in place (preserves any existing attributes)
            ApplyPropertyChangeToUpdate(propertyUpdate, registeredProperty, change, builder);
            builder.TrackPropertyUpdate(propertyUpdate, registeredProperty, properties);
        }

        BuildPathToRoot(changedSubject, rootSubject, builder);
    }

    private static SubjectPropertyUpdate CreatePropertyUpdate(
        RegisteredSubjectProperty property,
        SubjectUpdateBuilder builder)
    {
        var value = property.GetValue();
        var timestamp = property.Reference.TryGetWriteTimestamp();

        var update = new SubjectPropertyUpdate { Timestamp = timestamp };

        if (property.IsSubjectDictionary)
        {
            if (value is null)
            {
                update.Kind = SubjectPropertyUpdateKind.Value;
                update.Value = null;
            }
            else
            {
                SubjectItemsUpdateFactory.BuildDictionaryComplete(update, value, builder);
            }
        }
        else if (property.IsSubjectCollection)
        {
            if (value is null)
            {
                update.Kind = SubjectPropertyUpdateKind.Value;
                update.Value = null;
            }
            else
            {
                SubjectItemsUpdateFactory.BuildCollectionComplete(update, value, builder);
            }
        }
        else if (property.IsSubjectReference)
        {
            BuildObjectReference(update, value as IInterceptorSubject, property, isAssignment: false, builder);
        }
        else
        {
            update.Kind = SubjectPropertyUpdateKind.Value;
            update.Value = value;
        }

        update.Attributes = CreateAttributeUpdates(property, builder);

        return update;
    }

    /// <summary>
    /// Applies a property change to an existing update in place.
    /// This preserves any existing attributes on the update.
    /// </summary>
    private static void ApplyPropertyChangeToUpdate(
        SubjectPropertyUpdate update,
        RegisteredSubjectProperty property,
        SubjectPropertyChange change,
        SubjectUpdateBuilder builder)
    {
        update.Timestamp = change.ChangedTimestamp;

        if (property.IsSubjectDictionary)
        {
            var newValue = change.GetNewValue<object?>();
            if (newValue is null)
            {
                update.Kind = SubjectPropertyUpdateKind.Value;
                update.Value = null;
            }
            else
            {
                SubjectItemsUpdateFactory.BuildDictionaryDiff(update, change.GetOldValue<object?>(),
                    newValue, builder);
            }
        }
        else if (property.IsSubjectCollection)
        {
            var newValue = change.GetNewValue<object?>();
            if (newValue is null)
            {
                update.Kind = SubjectPropertyUpdateKind.Value;
                update.Value = null;
            }
            else
            {
                SubjectItemsUpdateFactory.BuildCollectionDiff(update, change.GetOldValue<object?>(),
                    newValue, builder);
            }
        }
        else if (property.IsSubjectReference)
        {
            BuildObjectReference(update, change.GetNewValue<IInterceptorSubject?>(), property, isAssignment: true, builder);
        }
        else
        {
            update.Kind = SubjectPropertyUpdateKind.Value;
            update.Value = change.GetNewValue<object?>();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BuildObjectReference(
        SubjectPropertyUpdate update,
        IInterceptorSubject? item,
        RegisteredSubjectProperty property,
        bool isAssignment,
        SubjectUpdateBuilder builder)
    {
        update.Kind = SubjectPropertyUpdateKind.Object;
        update.Id = null;

        if (item is null)
            return;

        var (id, isNew) = builder.GetOrCreateIdWithStatus(item);
        update.Id = id;

        // Ids are minted per message, so an existing id only means something earlier in this message
        // referenced the subject, not that the receiver has it. An assigned subject is new at this
        // position and needs its complete payload, unless it is the root or the owner. A subject reached
        // while completing another one is expanded only through its first parent, so a back or cross
        // reference to a subject the tree already holds stays an id rather than pulling in that subtree.
        if (isAssignment
                ? !ReferenceEquals(item, builder.RootSubject) && !ReferenceEquals(item, property.Parent.Subject)
                : isNew || IsReachedThroughFirstParent(item, property, builder))
        {
            ProcessSubjectComplete(item, builder);
        }
    }

    private static bool IsReachedThroughFirstParent(
        IInterceptorSubject item,
        RegisteredSubjectProperty property,
        SubjectUpdateBuilder builder)
    {
        if (ReferenceEquals(item, builder.RootSubject) || builder.ProcessedSubjects.Contains(item))
            return false;

        var registeredSubject = item.TryGetRegisteredSubject();
        if (registeredSubject is null)
            return false;

        var parents = registeredSubject.Parents;
        return parents.Length > 0 && ReferenceEquals(parents[0].Property, property);
    }

    /// <summary>
    /// Builds the path from a changed subject up to the root subject by adding
    /// property references for each parent in the hierarchy.
    /// Only traverses the first parent (canonical registration path) in DAG structures.
    /// </summary>
    private static void BuildPathToRoot(
        IInterceptorSubject subject,
        IInterceptorSubject rootSubject,
        SubjectUpdateBuilder builder)
    {
        builder.PathVisited.Clear();
        var current = subject.TryGetRegisteredSubject();

        while (current is not null && current.Subject != rootSubject)
        {
            // A completed subject is already referenced by whatever completed it, so its path exists. Walking
            // on would add a sparse item beside the insert operation that introduced it.
            if (builder.ProcessedSubjects.Contains(current.Subject) || !builder.PathVisited.Add(current.Subject))
                break;

            if (current.Parents.Length == 0)
                break;

            var parentInfo = current.Parents[0];
            var parentProperty = parentInfo.Property;
            var parentSubject = parentProperty.Parent;

            var parentId = builder.GetOrCreateId(parentSubject.Subject);
            var parentProperties = builder.GetOrCreateProperties(parentId);
            var childId = builder.GetOrCreateId(current.Subject);

            if (parentInfo.Index is not null)
            {
                var kind = parentProperty.IsSubjectDictionary
                    ? SubjectPropertyUpdateKind.Dictionary
                    : SubjectPropertyUpdateKind.Collection;
            
                AddCollectionOrDictionaryItemToParent(parentProperties, parentProperty.Name, parentInfo.Index, childId, kind);
            }
            else
            {
                AddSingleReferenceToParent(parentProperties, parentProperty.Name, childId);
            }

            current = parentSubject;
        }
    }

    /// <summary>
    /// Adds a collection or dictionary item reference to the parent's property update.
    /// Appends to an existing update or creates a new one with the specified kind.
    /// </summary>
    private static void AddCollectionOrDictionaryItemToParent(
        Dictionary<string, SubjectPropertyUpdate> parentProperties,
        string propertyName,
        object index,
        string childId,
        SubjectPropertyUpdateKind kind)
    {
        if (parentProperties.TryGetValue(propertyName, out var existingUpdate))
        {
            existingUpdate.Items ??= [];

            // Skip if this subject is already referenced in Items (multiple property
            // changes on the same child each trigger BuildPathToRoot)
            for (var i = 0; i < existingUpdate.Items.Count; i++)
            {
                if (existingUpdate.Items[i].Id == childId)
                    return;
            }

            existingUpdate.Items.Add(new SubjectPropertyItemUpdate
            {
                Index = index,
                Id = childId
            });
        }
        else
        {
            parentProperties[propertyName] = new SubjectPropertyUpdate
            {
                Kind = kind,
                Items = [new SubjectPropertyItemUpdate { Index = index, Id = childId }]
            };
        }
    }

    /// <summary>
    /// Adds a single item reference to the parent's property update.
    /// Skips if the property already exists (avoids overwriting).
    /// </summary>
    private static void AddSingleReferenceToParent(
        Dictionary<string, SubjectPropertyUpdate> parentProperties,
        string propertyName,
        string childId)
    {
        if (!parentProperties.ContainsKey(propertyName))
        {
            parentProperties[propertyName] = new SubjectPropertyUpdate
            {
                Kind = SubjectPropertyUpdateKind.Object,
                Id = childId
            };
        }
    }

    private static Dictionary<string, SubjectPropertyUpdate>? CreateAttributeUpdates(
        RegisteredSubjectProperty property,
        SubjectUpdateBuilder builder)
    {
        Dictionary<string, SubjectPropertyUpdate>? attributes = null;

        foreach (var attribute in property.Attributes)
        {
            if (!attribute.HasGetter)
                continue;

            if (!IsPropertyIncluded(attribute, builder.Processors))
                continue;

            attributes ??= new Dictionary<string, SubjectPropertyUpdate>();

            // Reuse the same property update logic for attributes
            var attributeUpdate = CreatePropertyUpdate(attribute, builder);
            attributes[attribute.AttributeMetadata.AttributeName] = attributeUpdate;
            builder.TrackPropertyUpdate(attributeUpdate, attribute, attributes);
        }

        return attributes;
    }

    private static void ProcessAttributeChange(
        RegisteredSubjectProperty attributeProperty,
        SubjectPropertyChange change,
        Dictionary<string, SubjectPropertyUpdate> subjectProperties,
        SubjectUpdateBuilder builder)
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

        // Navigate/create an attribute chain (excluding the last one which we'll create from change)
        var currentUpdate = rootUpdate;
        var attributeChain = new List<RegisteredSubjectProperty>();
        var currentProperty = attributeProperty;
        while (currentProperty.IsAttribute)
        {
            attributeChain.Add(currentProperty);
            currentProperty = currentProperty.GetAttributedProperty();
        }
        attributeChain.Reverse();

        // Navigate to parent of target attribute
        for (var i = 0; i < attributeChain.Count - 1; i++)
        {
            var chainedAttribute = attributeChain[i];
            currentUpdate.Attributes ??= new Dictionary<string, SubjectPropertyUpdate>();
            var attributeName = chainedAttribute.AttributeMetadata.AttributeName;

            if (!currentUpdate.Attributes.TryGetValue(attributeName, out var nestedAttributeUpdate))
            {
                nestedAttributeUpdate = new SubjectPropertyUpdate();
                currentUpdate.Attributes[attributeName] = nestedAttributeUpdate;
            }

            currentUpdate = nestedAttributeUpdate;
        }

        // Get or create the final attribute update
        var finalAttribute = attributeChain[^1];
        currentUpdate.Attributes ??= new Dictionary<string, SubjectPropertyUpdate>();
        var finalAttributeName = finalAttribute.AttributeMetadata.AttributeName;

        if (!currentUpdate.Attributes.TryGetValue(finalAttributeName, out var attributeUpdate))
        {
            attributeUpdate = new SubjectPropertyUpdate();
            currentUpdate.Attributes[finalAttributeName] = attributeUpdate;
        }

        // Apply the change in place (preserves any existing nested attributes)
        ApplyPropertyChangeToUpdate(attributeUpdate, attributeProperty, change, builder);
        builder.TrackPropertyUpdate(attributeUpdate, attributeProperty, currentUpdate.Attributes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPropertyIncluded(
        RegisteredSubjectProperty property,
        ISubjectUpdateProcessor[] processors)
    {
        for (var i = 0; i < processors.Length; i++)
        {
            if (!processors[i].IsIncluded(property))
                return false;
        }
        return true;
    }
}
