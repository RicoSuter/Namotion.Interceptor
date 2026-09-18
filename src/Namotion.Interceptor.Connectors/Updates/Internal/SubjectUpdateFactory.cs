using System.Diagnostics.CodeAnalysis;
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
                {
                    if (property.IsSubjectReference && IsReassignment(propertyChanges[i]))
                        builder.ReplacedReferences.Add(property);

                    ProcessPropertyChange(propertyChanges[i], property, canContainSubjects: true, builder);
                }
                else
                {
                    deferredChanges.Add((i, property));
                }
            }

            foreach (var (index, property) in deferredChanges)
            {
                ProcessPropertyChange(propertyChanges[index], property, canContainSubjects: false, builder);
            }

            // A reassigned reference can reach the update through a complete payload built for its subject,
            // which knows nothing of the change, so the mode is stated once everything is built, and before
            // Build lets a processor rename the keys the entries are found by.
            foreach (var property in builder.ReplacedReferences)
            {
                if (builder.TryGetIdWithUpdates(property.Parent.Subject) is { } subjectId &&
                    TryGetPropertyUpdate(builder.Subjects[subjectId], property) is { Kind: SubjectPropertyUpdateKind.Object, Id: not null } update)
                {
                    update.Mode = SubjectPropertyUpdateMode.Replaced;
                }
            }

            return builder.Build(rootSubject);
        }
        finally
        {
            builder.Clear();
            BuilderPool.Return(builder);
        }
    }

    /// <summary>
    /// Writes the complete payload of <paramref name="subject"/> unless this build already did, and returns its id.
    /// </summary>
    internal static string ProcessSubjectComplete(
        IInterceptorSubject subject,
        SubjectUpdateBuilder builder)
    {
        var subjectId = builder.GetOrCreateId(subject);

        if (!builder.ProcessedSubjects.Add(subject))
            return subjectId;

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            builder.HasUnregisteredSubjects = true;
            return subjectId;
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

        return subjectId;
    }

    /// <summary>
    /// A derived property that holds subjects and has no setter is a computed projection: it owns nothing,
    /// so neither complete payloads nor changes publish it, and the applier ignores an update naming it.
    /// The setter tells whether the value is stored on this side, so a [Derived] property with a setter is
    /// an ordinary edge and still published.
    /// </summary>
    internal static bool IsComputedSubjectProjection(RegisteredSubjectProperty property)
        => property.CanContainSubjects && property.Reference.Metadata.IsDerived && !property.HasSetter;

    /// <summary>
    /// Whether a change to a subject reference ends on a different subject than it started with, compared by
    /// reference because a subject may override Equals. A clear is not one: it already states no subject.
    /// </summary>
    private static bool IsReassignment(SubjectPropertyChange change)
        => change.GetNewValue<IInterceptorSubject?>() is { } newSubject &&
           !ReferenceEquals(newSubject, change.GetOldValue<IInterceptorSubject?>());

    private static void ProcessPropertyChange(
        SubjectPropertyChange change,
        RegisteredSubjectProperty registeredProperty,
        bool canContainSubjects,
        SubjectUpdateBuilder builder)
    {
        var changedSubject = change.Property.Subject;

        if (IsComputedSubjectProjection(registeredProperty) || !IsPropertyIncluded(registeredProperty, builder.Processors))
            return;

        // Before an id is minted or an entry created, because stopping the path walk at the excluded edge
        // would leave the changed subject's entry in the update: unreachable, but still transmitted.
        if (builder.Processors.Length > 0 && !IsPathToRootIncluded(changedSubject, builder))
            return;

        var subjectId = builder.GetOrCreateId(changedSubject);
        var properties = builder.GetOrCreateProperties(subjectId);

        // A complete payload already states this subject's final structure, and the receiver may be
        // building the subject from it, so it has no baseline for an incremental step. Value and
        // attribute changes still apply on top, which is what carries their captured values. The path is
        // still stated, because the payload may hang off a reference other than the first parent.
        if (canContainSubjects &&
            builder.ProcessedSubjects.Contains(changedSubject) &&
            TryGetPropertyUpdate(properties, registeredProperty) is not null)
        {
            BuildPathToRoot(changedSubject, builder);
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

        BuildPathToRoot(changedSubject, builder);
    }

    /// <summary>
    /// The update this build holds for <paramref name="property"/>, found for an attribute in the
    /// attributes of the update of the property it is attached to.
    /// </summary>
    private static SubjectPropertyUpdate? TryGetPropertyUpdate(
        Dictionary<string, SubjectPropertyUpdate> properties,
        RegisteredSubjectProperty property)
    {
        if (!property.IsAttribute)
            return properties.GetValueOrDefault(property.Name);

        return TryGetPropertyUpdate(properties, property.GetAttributedProperty())?.Attributes is { } attributes
            ? attributes.GetValueOrDefault(property.AttributeMetadata.AttributeName)
            : null;
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
        update.Mode = SubjectPropertyUpdateMode.Incremental;

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
        => !ReferenceEquals(item, builder.RootSubject) &&
           !builder.ProcessedSubjects.Contains(item) &&
           item.TryGetRegisteredSubject()?.Parents is { Length: > 0 } parents &&
           ReferenceEquals(parents[0].Property, property);

    /// <summary>
    /// Whether every ancestor edge <see cref="BuildPathToRoot"/> walks survives the processors.
    /// A processor that excludes a property hides everything behind it, so a change reached only through
    /// an excluded edge is dropped rather than shipped with the excluded property name that names it.
    /// </summary>
    private static bool IsPathToRootIncluded(
        IInterceptorSubject subject,
        SubjectUpdateBuilder builder)
    {
        builder.PathVisited.Clear();
        var current = subject.TryGetRegisteredSubject();

        while (TryGetNextPathParent(current, builder, out var parent))
        {
            if (!IsPropertyIncluded(parent.Property, builder.Processors))
                return false;

            current = parent.Property.Parent;
        }

        return true;
    }

    /// <summary>
    /// Gets the first parent of <paramref name="current"/> on the walk towards the root, or returns <c>false</c>
    /// where the walk stops. Both path walks step through here, so the processor check covers exactly the
    /// edges the build states.
    /// </summary>
    private static bool TryGetNextPathParent(
        [NotNullWhen(true)] RegisteredSubject? current,
        SubjectUpdateBuilder builder,
        out SubjectPropertyParent parent)
    {
        parent = default;

        // The walk passes completed subjects: whatever completed one need not be reachable itself, such as an
        // ancestor completed because a back reference to it was assigned.
        if (current is null ||
            current.Subject == builder.RootSubject ||
            !builder.PathVisited.Add(current.Subject))
        {
            return false;
        }

        var parents = current.Parents;
        if (parents.Length == 0)
            return false;

        parent = parents[0];
        return true;
    }

    /// <summary>
    /// Builds the path from a changed subject up to the root subject by adding
    /// property references for each parent in the hierarchy.
    /// Only traverses the first parent (canonical registration path) in DAG structures.
    /// </summary>
    private static void BuildPathToRoot(
        IInterceptorSubject subject,
        SubjectUpdateBuilder builder)
    {
        builder.PathVisited.Clear();
        var current = subject.TryGetRegisteredSubject();

        while (TryGetNextPathParent(current, builder, out var parent))
        {
            var parentProperty = parent.Property;
            var parentSubject = parentProperty.Parent;

            var parentId = builder.GetOrCreateId(parentSubject.Subject);
            var parentProperties = builder.GetOrCreateProperties(parentId);
            var childId = builder.GetOrCreateId(current.Subject);

            if (parent.Index is not null)
            {
                var kind = parentProperty.IsSubjectDictionary
                    ? SubjectPropertyUpdateKind.Dictionary
                    : SubjectPropertyUpdateKind.Collection;
            
                AddCollectionOrDictionaryItemToParent(parentProperties, parentProperty.Name, parent.Index, childId, kind);
            }
            else
            {
                AddSingleReferenceToParent(parentProperties, parentProperty.Name, childId);
            }

            current = parentSubject;
        }
    }

    /// <summary>
    /// Adds a collection or dictionary item reference to the parent's property update, unless the update
    /// already references the child through an item or an Insert operation.
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
            if (ReferencesItem(existingUpdate, childId))
                return;

            (existingUpdate.Items ??= []).Add(new SubjectPropertyItemUpdate
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
    /// Whether <paramref name="update"/> already references the child: several changes on one child each
    /// state its edge, and a child the update inserts arrives through its Insert operation.
    /// </summary>
    private static bool ReferencesItem(SubjectPropertyUpdate update, string childId)
    {
        if (update.Items is { } items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Id == childId)
                    return true;
            }
        }

        if (update.Operations is { } operations)
        {
            for (var i = 0; i < operations.Count; i++)
            {
                if (operations[i].Action == SubjectCollectionOperationType.Insert && operations[i].Id == childId)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Adds a single item reference to the parent's property update.
    /// Skips if the property already has an update, which may be a complete payload or an assignment.
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

            if (IsComputedSubjectProjection(attribute))
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
