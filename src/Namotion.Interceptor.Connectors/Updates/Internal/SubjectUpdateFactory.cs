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
    private static readonly ObjectPool<SubjectUpdateBuilder> BuilderPool = new(() => new SubjectUpdateBuilder());

    /// <summary>
    /// Creates a complete update with all properties for the given subject.
    /// </summary>
    public static SubjectUpdate CreateCompleteUpdate(
        IInterceptorSubject subject,
        ReadOnlySpan<ISubjectUpdateProcessor> processors)
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
        ReadOnlySpan<ISubjectUpdateProcessor> processors)
    {
        var builder = BuilderPool.Rent();
        try
        {
            builder.Initialize(rootSubject, processors);

            for (var i = 0; i < propertyChanges.Length; i++)
            {
                var change = propertyChanges[i];

                // 1. Check inclusion (with attribute chain check)
                if (!IsChangeIncluded(change, builder))
                    continue;

                // 2. Build path FIRST (assigns IDs to ancestors) - with inclusion check
                if (!TryBuildPathToRoot(change.Property.Subject, rootSubject, builder))
                    continue; // No valid path

                // 3. Then process change (ancestors now have IDs)
                ProcessPropertyChange(change, builder);
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
            return;

        var properties = builder.GetOrCreateProperties(subjectId);

        foreach (var property in registeredSubject.Properties)
        {
            if (!property.HasGetter || property.IsAttribute)
                continue;

            if (!IsPropertyIncluded(property, builder.Processors))
                continue;

            var propertyUpdate = CreatePropertyUpdate(property, builder);
            properties[property.Name] = propertyUpdate;

            builder.TrackPropertyUpdate(propertyUpdate, property, properties);
        }
    }

    private static void ProcessPropertyChange(
        SubjectPropertyChange change,
        SubjectUpdateBuilder builder)
    {
        var changedSubject = change.Property.Subject;
        var registeredProperty = change.Property.TryGetRegisteredProperty()!; // Already validated in IsChangeIncluded

        var subjectId = builder.GetOrCreateId(changedSubject);
        var properties = builder.GetOrCreateProperties(subjectId);

        if (registeredProperty.IsAttribute)
        {
            ProcessAttributeChange(registeredProperty, change, properties, builder);
        }
        else
        {
            var propertyUpdate = CreatePropertyUpdateFromChange(registeredProperty, change, builder);
            properties[registeredProperty.Name] = propertyUpdate;
            builder.TrackPropertyUpdate(propertyUpdate, registeredProperty, properties);
        }

        // NOTE: BuildPathToRoot removed - handled before this call in CreatePartialUpdateFromChanges
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
            SubjectCollectionUpdateFactory.BuildDictionaryComplete(update, value as IDictionary, builder);
        }
        else if (property.IsSubjectCollection)
        {
            SubjectCollectionUpdateFactory.BuildCollectionComplete(update, value as IEnumerable<IInterceptorSubject>, builder);
        }
        else if (property.IsSubjectReference)
        {
            BuildItemReference(update, value as IInterceptorSubject, builder);
        }
        else
        {
            update.Kind = SubjectPropertyUpdateKind.Value;
            update.Value = value;
        }

        update.Attributes = CreateAttributeUpdates(property, builder);

        return update;
    }

    private static SubjectPropertyUpdate CreatePropertyUpdateFromChange(
        RegisteredSubjectProperty property,
        SubjectPropertyChange change,
        SubjectUpdateBuilder builder)
    {
        var update = new SubjectPropertyUpdate { Timestamp = change.ChangedTimestamp };

        if (property.IsSubjectDictionary)
        {
            SubjectCollectionUpdateFactory.BuildDictionaryDiff(update, change.GetOldValue<IDictionary?>(),
                change.GetNewValue<IDictionary?>(), builder);
        }
        else if (property.IsSubjectCollection)
        {
            SubjectCollectionUpdateFactory.BuildCollectionDiff(update,
                change.GetOldValue<IEnumerable<IInterceptorSubject>?>(),
                change.GetNewValue<IEnumerable<IInterceptorSubject>?>(), builder);
        }
        else if (property.IsSubjectReference)
        {
            BuildItemReference(update, change.GetNewValue<IInterceptorSubject?>(), builder);
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
        SubjectUpdateBuilder builder)
    {
        update.Kind = SubjectPropertyUpdateKind.Item;

        if (item is not null)
        {
            update.Id = builder.GetOrCreateId(item, out var isNew);

            // Only process the complete subject if it's newly encountered in this update.
            // If the subject already had an ID (from Initialize, being a changedSubject,
            // or BuildPathToRoot), it's either the root or an existing part of the tree
            // and we should only add a reference to it, not all its properties.
            // This prevents circular references from causing the entire tree to be included.
            if (isNew)
            {
                ProcessSubjectComplete(item, builder);
            }
        }
    }

    /// <summary>
    /// Builds the path from a changed subject up to the root subject.
    /// Returns false if no valid path exists (all paths have excluded segments).
    /// Tries all parents in DAG structures to find a valid path.
    /// </summary>
    private static bool TryBuildPathToRoot(
        IInterceptorSubject subject,
        IInterceptorSubject rootSubject,
        SubjectUpdateBuilder builder)
    {
        builder.PathVisited.Clear();
        return TryBuildPathToRootRecursive(subject, rootSubject, builder);
    }

    /// <summary>
    /// Recursive implementation of path building.
    /// Uses recursion to build references in root→subject order without allocations.
    /// </summary>
    private static bool TryBuildPathToRootRecursive(
        IInterceptorSubject subject,
        IInterceptorSubject rootSubject,
        SubjectUpdateBuilder builder)
    {
        if (subject == rootSubject)
            return true;

        // Cycle detection
        if (!builder.PathVisited.Add(subject))
            return false;

        var current = subject.TryGetRegisteredSubject();
        if (current is null || current.Parents.Length == 0)
            return false;

        // Try each parent to find a valid path (DAG support)
        foreach (var parentInfo in current.Parents)
        {
            var parentProperty = parentInfo.Property;

            // Check if this segment is included
            if (!IsPropertyIncluded(parentProperty, builder.Processors))
                continue;

            var parentSubject = parentProperty.Parent;

            // Recurse FIRST - this builds references from root down
            if (!TryBuildPathToRootRecursive(parentSubject.Subject, rootSubject, builder))
                continue;

            // THEN add this segment's reference (after parent path is built)
            var parentId = builder.GetOrCreateId(parentSubject.Subject);
            var parentProperties = builder.GetOrCreateProperties(parentId);
            var childId = builder.GetOrCreateId(subject);

            if (parentInfo.Index is not null)
            {
                AddCollectionItemToParent(parentProperties, parentProperty.Name, parentInfo.Index, childId);
            }
            else
            {
                AddSingleReferenceToParent(parentProperties, parentProperty.Name, childId);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Adds a collection item reference to the parent's property update.
    /// Appends to existing collection update or creates a new one.
    /// </summary>
    private static void AddCollectionItemToParent(
        Dictionary<string, SubjectPropertyUpdate> parentProperties,
        string propertyName,
        object index,
        string childId)
    {
        if (parentProperties.TryGetValue(propertyName, out var existingUpdate))
        {
            existingUpdate.Collection ??= [];
            existingUpdate.Collection.Add(new SubjectPropertyCollectionUpdate
            {
                Index = index,
                Id = childId
            });
        }
        else
        {
            parentProperties[propertyName] = new SubjectPropertyUpdate
            {
                Kind = SubjectPropertyUpdateKind.Collection,
                Collection = [new SubjectPropertyCollectionUpdate { Index = index, Id = childId }]
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
                Kind = SubjectPropertyUpdateKind.Item,
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

        // Create the final attribute update using the same logic as properties
        var finalAttribute = attributeChain[^1];
        currentUpdate.Attributes ??= new Dictionary<string, SubjectPropertyUpdate>();
        var finalAttributeName = finalAttribute.AttributeMetadata.AttributeName;
        var attributeUpdate = CreatePropertyUpdateFromChange(attributeProperty, change, builder);
        currentUpdate.Attributes[finalAttributeName] = attributeUpdate;

        builder.TrackPropertyUpdate(attributeUpdate, attributeProperty, currentUpdate.Attributes);
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
    /// Checks if a change should be included, validating the property
    /// and full attribute chain if applicable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsChangeIncluded(
        SubjectPropertyChange change,
        SubjectUpdateBuilder builder)
    {
        var registeredProperty = change.Property.TryGetRegisteredProperty();
        if (registeredProperty is null)
            return false;

        // Check the changed property itself
        if (!IsPropertyIncluded(registeredProperty, builder.Processors))
            return false;

        // For attributes: check entire chain up to root property
        if (registeredProperty.IsAttribute)
        {
            var current = registeredProperty.GetAttributedProperty();
            while (current is not null)
            {
                if (!IsPropertyIncluded(current, builder.Processors))
                    return false;

                if (!current.IsAttribute)
                    break;

                current = current.GetAttributedProperty();
            }
        }

        return true;
    }
}
