using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Applies collection and dictionary updates from <see cref="SubjectUpdate"/> instances.
/// </summary>
internal static class SubjectItemsUpdateApplier
{
    /// <summary>
    /// Applies a collection update to a property using complete-state items.
    /// </summary>
    internal static void ApplyCollectionUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        if (propertyUpdate.Items is null)
        {
            // Null items mean the collection itself is null
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                property.SetValue(null);
            }
            return;
        }

        var idRegistry = parent.Context.GetService<ISubjectIdRegistry>();
      
        var newItems = new List<IInterceptorSubject>(propertyUpdate.Items.Count);
        foreach (var itemUpdate in propertyUpdate.Items)
        {
            var item = ResolveOrCreateSubject(
                parent, property, newItems.Count, itemUpdate.Id, idRegistry, context);

            newItems.Add(item);
        }

        var collection = context.SubjectFactory.CreateSubjectCollection(property.Type, newItems);
        using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
        {
            property.SetValue(collection);
        }
    }

    /// <summary>
    /// Applies a dictionary update to a property using complete-state items.
    /// </summary>
    internal static void ApplyDictionaryUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        if (propertyUpdate.Items is null)
        {
            // Null items mean the dictionary itself is null
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                property.SetValue(null);
            }
            return;
        }

        var workingDictionary = new Dictionary<object, IInterceptorSubject>();

        var existingDictionary = property.GetValue() as IDictionary;
        if (existingDictionary is not null)
        {
            foreach (DictionaryEntry entry in existingDictionary)
            {
                if (entry.Value is IInterceptorSubject item)
                    workingDictionary[entry.Key] = item;
            }
        }

        var idRegistry = parent.Context.GetService<ISubjectIdRegistry>();

        var targetKeyType = property.Type.GenericTypeArguments[0];
        var structureChanged = false;
        var updatedKeys = new HashSet<object>();

        foreach (var collectionUpdate in propertyUpdate.Items)
        {
            if (collectionUpdate.Key is null)
                continue;

            var key = DictionaryKeyConverter.Convert(collectionUpdate.Key, targetKeyType);
            updatedKeys.Add(key);

            if (workingDictionary.TryGetValue(key, out var existing))
            {
                var existingId = existing.TryGetSubjectId();
                if (existingId is not null && existingId != collectionUpdate.Id)
                {
                    // Different logical subject at the same key — replace it.
                    workingDictionary[key] = ResolveOrCreateSubject(
                        parent, property, key, collectionUpdate.Id, idRegistry, context);
 
                    structureChanged = true;
                }
                else
                {
                    // Same logical subject (or no ID yet) — converge ID and update in place.

                    // TODO: This could throw on conflict (same key, different subject already added)
                    existing.SetSubjectId(collectionUpdate.Id); 
                    ApplyPropertiesIfAvailable(existing, collectionUpdate.Id, context);
                }
            }
            else
            {
                workingDictionary[key] = ResolveOrCreateSubject(
                    parent, property, key, collectionUpdate.Id, idRegistry, context);

                structureChanged = true;
            }
        }

        // Remove dictionary entries not mentioned in items
        List<object>? keysToRemove = null;
        foreach (var key in workingDictionary.Keys)
        {
            if (!updatedKeys.Contains(key))
            {
                keysToRemove ??= [];
                keysToRemove.Add(key);
            }
        }

        if (keysToRemove is not null)
        {
            foreach (var key in keysToRemove)
                workingDictionary.Remove(key);
            
            structureChanged = true;
        }

        // Also rebuild when transitioning from null to a (possibly empty) dictionary
        if (!structureChanged && existingDictionary is null)
        {
            structureChanged = true;
        }

        if (structureChanged)
        {
            var dictionary = context.SubjectFactory.CreateSubjectDictionary(property.Type, workingDictionary);
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                property.SetValue(dictionary);
            }
        }
    }

    /// <summary>
    /// Resolves an existing subject by ID, or creates a new one.
    /// Used by collection operations and complete collection rebuilds.
    /// </summary>
    private static IInterceptorSubject ResolveOrCreateSubject(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        object indexOrKey,
        string subjectId,
        ISubjectIdRegistry idRegistry,
        SubjectUpdateApplyContext context)
    {
        if (idRegistry.TryGetSubjectById(subjectId, out var existing))
        {
            ApplyPropertiesIfAvailable(existing, subjectId, context);
            return existing;
        }

        var newItem = CreateSubjectItem(parent, property, indexOrKey, subjectId, context);
        ApplyPropertiesIfAvailable(newItem, subjectId, context);
        return newItem;
    }

    /// <summary>
    /// Applies property updates to a subject if properties are available and not yet processed.
    /// </summary>
    private static void ApplyPropertiesIfAvailable(
        IInterceptorSubject subject, string subjectId, SubjectUpdateApplyContext context)
    {
        if (context.Subjects.TryGetValue(subjectId, out var properties) &&
            context.TryMarkAsProcessed(subjectId))
        {
            SubjectUpdateApplier.ApplyPropertyUpdates(subject, properties, context);
        }
    }

    /// <summary>
    /// Creates a new subject item and assigns its ID. Does not apply properties.
    /// </summary>
    private static IInterceptorSubject CreateSubjectItem(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        object indexOrKey,
        string subjectId,
        SubjectUpdateApplyContext context)
    {
        var newItem = context.SubjectFactory.CreateCollectionSubject(property, indexOrKey);
        newItem.Context.AddFallbackContext(parent.Context);
        newItem.SetSubjectId(subjectId);
        return newItem;
    }
}
