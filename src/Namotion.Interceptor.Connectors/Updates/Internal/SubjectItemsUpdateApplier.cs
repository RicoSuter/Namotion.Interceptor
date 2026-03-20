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

        var idRegistry = context.SubjectIdRegistry;

        // Phase 1: Resolve or create subjects, set IDs on new subjects immediately
        // so that GetOrAddSubjectId (called by ChangeQueueProcessor flush) finds the
        // pre-assigned ID instead of generating a conflicting one.
        var newItems = new List<(IInterceptorSubject Subject, string Id)>(propertyUpdate.Items.Count);
        foreach (var itemUpdate in propertyUpdate.Items)
        {
            var (item, isNew) = ResolveOrCreateSubject(
                parent, property, newItems.Count, itemUpdate.Id, idRegistry, context);

            if (isNew)
            {
                item.SetSubjectId(itemUpdate.Id);
            }

            newItems.Add((item, itemUpdate.Id));
        }

        // Phase 2: Assign collection to graph (roots all items via lifecycle attach,
        // which populates the reverse ID index from the pre-assigned IDs in Data)
        var subjects = new IInterceptorSubject[newItems.Count];
        for (var i = 0; i < newItems.Count; i++)
            subjects[i] = newItems[i].Subject;

        var collection = context.SubjectFactory.CreateSubjectCollection(property.Type, subjects);
        using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
        {
            property.SetValue(collection);
        }

        // Phase 3: Apply properties (subjects are now rooted with IDs set)
        foreach (var (item, id) in newItems)
        {
            ApplyPropertiesIfAvailable(item, id, context);
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

        var idRegistry = context.SubjectIdRegistry;

        var targetKeyType = property.Type.GenericTypeArguments[0];
        var structureChanged = false;
        var updatedKeys = new HashSet<object>();

        // Track subjects that need properties applied after graph assignment.
        List<(IInterceptorSubject Subject, string Id)>? pendingPropertyApply = null;

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
                    var (item, isNew) = ResolveOrCreateSubject(
                        parent, property, key, collectionUpdate.Id, idRegistry, context);
                    workingDictionary[key] = item;

                    // Set ID immediately so ChangeQueueProcessor flush finds it
                    if (isNew) item.SetSubjectId(collectionUpdate.Id);

                    pendingPropertyApply ??= [];
                    pendingPropertyApply.Add((item, collectionUpdate.Id));
                    structureChanged = true;
                }
                else
                {
                    // Same logical subject (or no ID yet) — converge ID.
                    if (existing.TryGetSubjectId() != collectionUpdate.Id)
                    {
                        existing.SetSubjectId(collectionUpdate.Id);
                    }

                    pendingPropertyApply ??= [];
                    pendingPropertyApply.Add((existing, collectionUpdate.Id));
                }
            }
            else
            {
                var (item, isNew) = ResolveOrCreateSubject(
                    parent, property, key, collectionUpdate.Id, idRegistry, context);
                workingDictionary[key] = item;

                // Set ID immediately so ChangeQueueProcessor flush finds it
                if (isNew) item.SetSubjectId(collectionUpdate.Id);

                pendingPropertyApply ??= [];
                pendingPropertyApply.Add((item, collectionUpdate.Id));
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

        // Assign to graph first so subjects are rooted
        if (structureChanged)
        {
            var dictionary = context.SubjectFactory.CreateSubjectDictionary(property.Type, workingDictionary);
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                property.SetValue(dictionary);
            }
        }

        // Apply properties (IDs already set before graph assignment)
        if (pendingPropertyApply is not null)
        {
            foreach (var (subject, id) in pendingPropertyApply)
            {
                ApplyPropertiesIfAvailable(subject, id, context);
            }
        }
    }

    /// <summary>
    /// Resolves an existing subject by ID, or creates a new one.
    /// For newly created subjects, the ID is NOT set here — the caller must
    /// first assign the item to the graph (via SetValue on the collection/dictionary),
    /// then set IDs and apply properties.
    /// </summary>
    private static (IInterceptorSubject Subject, bool IsNew) ResolveOrCreateSubject(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        object indexOrKey,
        string subjectId,
        ISubjectIdRegistry idRegistry,
        SubjectUpdateApplyContext context)
    {
        if (idRegistry.TryGetSubjectById(subjectId, out var existing))
        {
            return (existing, false);
        }

        var newItem = CreateSubjectItem(parent, property, indexOrKey, context);
        return (newItem, true);
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
    /// Creates a new subject item. Does not assign ID, fallback context, or apply properties.
    /// Fallback context is added automatically by ContextInheritanceHandler when the subject
    /// enters the graph via SetValue. The caller must assign the item to the graph first.
    /// </summary>
    private static IInterceptorSubject CreateSubjectItem(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        object indexOrKey,
        SubjectUpdateApplyContext context)
    {
        return context.SubjectFactory.CreateCollectionSubject(property, indexOrKey);
    }
}
