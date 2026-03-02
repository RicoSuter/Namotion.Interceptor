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
        var workingItems = (property.GetValue() as IEnumerable<IInterceptorSubject>)?.ToList() ?? [];
        var structureChanged = false;
        var idRegistry = parent.Context.GetService<ISubjectIdRegistry>();

        // Complete collection from items (defines the full ordered state)
        if (propertyUpdate.Items is not null)
        {
            var newItems = new List<IInterceptorSubject>(propertyUpdate.Items.Count);
            foreach (var itemUpdate in propertyUpdate.Items)
            {
                var item = ResolveOrCreateSubject(
                    parent, property, newItems.Count, itemUpdate.Id, idRegistry, context);
                newItems.Add(item);
            }

            workingItems = newItems;
            structureChanged = true;
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

    /// <summary>
    /// Applies a dictionary update to a property using complete-state items.
    /// </summary>
    internal static void ApplyDictionaryUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        var existingDictionary = property.GetValue() as IDictionary;
        var targetKeyType = property.Type.GenericTypeArguments[0];
        var workingDictionary = new Dictionary<object, IInterceptorSubject>();
        var structureChanged = false;
        var idRegistry = parent.Context.GetService<ISubjectIdRegistry>();

        if (existingDictionary is not null)
        {
            foreach (DictionaryEntry entry in existingDictionary)
            {
                if (entry.Value is IInterceptorSubject item)
                    workingDictionary[entry.Key] = item;
            }
        }

        // Apply item updates (complete state for dictionaries)
        if (propertyUpdate.Items is not null)
        {
            var updatedKeys = new HashSet<object>();

            foreach (var collUpdate in propertyUpdate.Items)
            {
                if (collUpdate.Key is null)
                    continue;

                var key = DictionaryKeyConverter.Convert(collUpdate.Key, targetKeyType);
                updatedKeys.Add(key);

                if (context.Subjects.TryGetValue(collUpdate.Id, out var itemProps))
                {
                    if (workingDictionary.TryGetValue(key, out var existing))
                    {
                        // Set subject ID on pre-existing dictionary item to match the sender's ID.
                        existing.SetSubjectId(collUpdate.Id);
                        if (context.TryMarkAsProcessed(collUpdate.Id))
                        {
                            SubjectUpdateApplier.ApplyPropertyUpdates(existing, itemProps, context);
                        }
                    }
                    else
                    {
                        var newItem = ResolveExistingOrCreateSubject(
                            parent, property, key, collUpdate.Id, itemProps, idRegistry, context);
                        workingDictionary[key] = newItem;
                        structureChanged = true;
                    }
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
    /// Resolves an existing subject by ID, or creates a new one.
    /// Always returns a non-null subject. Used by dictionary operations where the
    /// subject properties are guaranteed to exist in the context.
    /// </summary>
    private static IInterceptorSubject ResolveExistingOrCreateSubject(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        object indexOrKey,
        string subjectId,
        Dictionary<string, SubjectPropertyUpdate> subjectProperties,
        ISubjectIdRegistry idRegistry,
        SubjectUpdateApplyContext context)
    {
        var subject = idRegistry.TryGetSubjectById(subjectId, out var existing)
            ? existing
            : CreateSubjectItem(parent, property, indexOrKey, subjectId, context);

        if (context.TryMarkAsProcessed(subjectId))
        {
            SubjectUpdateApplier.ApplyPropertyUpdates(subject, subjectProperties, context);
        }

        return subject;
    }

    /// <summary>
    /// Applies property updates to a subject if properties are available and not yet processed.
    /// </summary>
    private static void ApplyPropertiesIfAvailable(
        IInterceptorSubject subject,
        string subjectId,
        SubjectUpdateApplyContext context)
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
