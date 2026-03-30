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
        ISubjectUpdateProcessor[] processors)
    {
        var builder = BuilderPool.Rent();
        try
        {
            builder.Initialize(subject, processors, isPartialUpdate: false);
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
            builder.Initialize(rootSubject, processors, isPartialUpdate: true);

            for (var i = 0; i < propertyChanges.Length; i++)
            {
                ProcessPropertyChange(propertyChanges[i], builder);
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

        builder.MarkSubjectComplete(subjectId);

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            // Subject is detached (concurrent mutation removed it from the graph).
            // Still, create an empty properties entry so the client can instantiate the
            // subject from its type metadata. Future updates will populate properties.
            builder.GetOrCreateProperties(subjectId);
            return;
        }

        var properties = builder.GetOrCreateProperties(subjectId);

        foreach (var property in registeredSubject.Properties)
        {
            if (!property.HasGetter || property.IsAttribute)
                continue;

            if (!IsPropertyIncluded(property, builder.Processors))
                continue;

            if (properties.TryGetValue(property.Name, out var existingUpdate))
            {
                // Property was already set by a value change earlier in this batch.
                // Preserve the change-based value but ensure attributes are populated,
                // since value changes don't include attributes.
                if (existingUpdate.Attributes is null)
                {
                    existingUpdate.Attributes = CreateAttributeUpdates(property, builder);
                }
            }
            else
            {
                var propertyUpdate = CreatePropertyUpdate(property, builder);
                properties[property.Name] = propertyUpdate;

                builder.TrackPropertyUpdate(propertyUpdate, property, properties);
            }
        }
    }

    private static void ProcessPropertyChange(
        SubjectPropertyChange change,
        SubjectUpdateBuilder builder)
    {
        var changedSubject = change.Property.Subject;
        var registeredProperty = change.Property.TryGetRegisteredProperty();

        if (registeredProperty is null)
        {
            // Subject is momentarily unregistered (concurrent structural mutation detached it).
            // For value properties, we can still include the change using the subject's own
            // property metadata. Dropping it would cause permanent value loss on clients
            // because no new change notification is generated when the subject is re-attached.
            // Structural properties (collections, dictionaries, object references) require full
            // registry metadata to serialize correctly, so those are still skipped.
            var droppedId = changedSubject.TryGetSubjectId();
            if (droppedId is not null &&
                changedSubject.Properties.TryGetValue(change.Property.Name, out var fallbackMetadata) &&
                !fallbackMetadata.Type.CanContainSubjects())
            {
                var fallbackId = builder.GetOrCreateId(changedSubject);
                var fallbackProps = builder.GetOrCreateProperties(fallbackId);

                if (!fallbackProps.TryGetValue(change.Property.Name, out var fallbackUpdate))
                {
                    fallbackUpdate = new SubjectPropertyUpdate();
                    fallbackProps[change.Property.Name] = fallbackUpdate;
                }

                fallbackUpdate.Kind = SubjectPropertyUpdateKind.Value;
                fallbackUpdate.Value = change.GetNewValue<object?>();
                fallbackUpdate.Timestamp = change.ChangedTimestamp;
            }

            return;
        }

        if (!IsPropertyIncluded(registeredProperty, builder.Processors))
            return;

        var (subjectId, isNewToBuilder) = builder.GetOrCreateIdWithStatus(changedSubject);
        if (isNewToBuilder)
        {
            // Track that this subject was first encountered via a value change, not via
            // a structural reference. If a structural change (ObjectRef/Collection/Dictionary)
            // later references this subject in the same batch, ProcessSubjectComplete must
            // still be called to populate the remaining properties.
            builder.SubjectsWithPartialChanges.Add(changedSubject);
        }

        var properties = builder.GetOrCreateProperties(subjectId);

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
            update.Kind = SubjectPropertyUpdateKind.Dictionary;
            if (value is not null)
            {
                SubjectItemsUpdateFactory.BuildDictionaryComplete(update, value as IDictionary, builder);
            }
        }
        else if (property.IsSubjectCollection)
        {
            update.Kind = SubjectPropertyUpdateKind.Collection;
            if (value is not null)
            {
                SubjectItemsUpdateFactory.BuildCollectionComplete(update, value as IEnumerable<IInterceptorSubject>, builder);
            }
        }
        else if (property.IsSubjectReference)
        {
            BuildObjectReference(update, value as IInterceptorSubject, builder);
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
            update.Kind = SubjectPropertyUpdateKind.Dictionary;

            var newValue = change.GetNewValue<IDictionary?>();
            if (newValue is not null)
            {
                SubjectItemsUpdateFactory.BuildDictionaryUpdate(update,
                    change.GetOldValue<IDictionary?>(), newValue, builder);
            }
        }
        else if (property.IsSubjectCollection)
        {
            update.Kind = SubjectPropertyUpdateKind.Collection;

            var newValue = change.GetNewValue<IEnumerable<IInterceptorSubject>?>();
            if (newValue is not null)
            {
                SubjectItemsUpdateFactory.BuildCollectionUpdate(update,
                    change.GetOldValue<IEnumerable<IInterceptorSubject>?>(),
                    newValue, builder);
            }
        }
        else if (property.IsSubjectReference)
        {
            BuildObjectReference(update, change.GetNewValue<IInterceptorSubject?>(), builder);
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
        SubjectUpdateBuilder builder)
    {
        update.Kind = SubjectPropertyUpdateKind.Object;

        if (item is not null)
        {
            var (id, isNew) = builder.GetOrCreateIdWithStatus(item);
            update.Id = id;

            // Process the complete subject if it's newly encountered OR if a value change
            // earlier in the same batch created its ID without populating complete properties.
            // ProcessSubjectComplete uses ProcessedSubjects.Add to prevent circular references
            // and ContainsKey to preserve explicitly changed properties.
            if (isNew || builder.SubjectsWithPartialChanges.Remove(item))
            {
                ProcessSubjectComplete(item, builder);
            }
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

        // Navigate/create an attribute chain (excluding the last one, which we'll create from change)
        var currentUpdate = rootUpdate;
        var attributeChain = new List<RegisteredSubjectProperty>();
        var currentProperty = attributeProperty;
        while (currentProperty.IsAttribute)
        {
            attributeChain.Add(currentProperty);
            currentProperty = currentProperty.GetAttributedProperty();
        }
        attributeChain.Reverse();

        // Navigate to the parent of the target attribute
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
