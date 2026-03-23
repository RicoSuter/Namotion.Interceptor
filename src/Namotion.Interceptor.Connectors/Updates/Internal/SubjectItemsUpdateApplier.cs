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
        PropertyReference property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        var metadata = property.Metadata;

        if (propertyUpdate.Items is null)
        {
            // Null items mean the collection itself is null
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                metadata.SetValue?.Invoke(parent, null);
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

            if (item is null)
                continue; // Subject not found and not complete — skip, self-heals on next update

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

        var collection = context.SubjectFactory.CreateSubjectCollection(metadata.Type, subjects);
        using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
        {
            metadata.SetValue?.Invoke(parent, collection);
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
        PropertyReference property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        var metadata = property.Metadata;

        if (propertyUpdate.Items is null)
        {
            // Null items mean the dictionary itself is null
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                metadata.SetValue?.Invoke(parent, null);
            }
            return;
        }

        var idRegistry = context.SubjectIdRegistry;
        var targetKeyType = metadata.Type.GenericTypeArguments[0];

        // Phase 1: Resolve or create subjects, set IDs on new subjects immediately
        // so that GetOrAddSubjectId (called by ChangeQueueProcessor flush) finds the
        // pre-assigned ID instead of generating a conflicting one.
        // Does NOT read the backing store — avoids race with concurrent structural mutations
        // whose next() wrote a different dictionary before acquiring the lifecycle lock.
        var newItems = new List<(object Key, IInterceptorSubject Subject, string Id)>(propertyUpdate.Items.Count);
        foreach (var itemUpdate in propertyUpdate.Items)
        {
            if (itemUpdate.Key is null)
                continue;

            var key = DictionaryKeyConverter.Convert(itemUpdate.Key, targetKeyType);
            var (item, isNew) = ResolveOrCreateSubject(
                parent, property, key, itemUpdate.Id, idRegistry, context);

            if (item is null)
                continue; // Subject not found and not complete — skip, self-heals on next update

            if (isNew)
            {
                item.SetSubjectId(itemUpdate.Id);
            }

            newItems.Add((key, item, itemUpdate.Id));
        }

        // Phase 2: Build dictionary and assign to graph (roots all items via lifecycle attach,
        // which populates the reverse ID index from the pre-assigned IDs in Data)
        var workingDictionary = new Dictionary<object, IInterceptorSubject>(newItems.Count);
        foreach (var (key, subject, _) in newItems)
            workingDictionary[key] = subject;

        var dictionary = context.SubjectFactory.CreateSubjectDictionary(metadata.Type, workingDictionary);
        using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
        {
            metadata.SetValue?.Invoke(parent, dictionary);
        }

        // Phase 3: Apply properties (subjects are now rooted with IDs set)
        foreach (var (_, subject, id) in newItems)
        {
            ApplyPropertiesIfAvailable(subject, id, context);
        }
    }

    /// <summary>
    /// Resolves an existing subject by ID, or creates a new one.
    /// For newly created subjects, the ID is NOT set here — the caller must
    /// first assign the item to the graph (via SetValue on the collection/dictionary),
    /// then set IDs and apply properties.
    /// </summary>
    private static (IInterceptorSubject? Subject, bool IsNew) ResolveOrCreateSubject(
        IInterceptorSubject parent,
        PropertyReference property,
        object indexOrKey,
        string subjectId,
        ISubjectIdRegistry idRegistry,
        SubjectUpdateApplyContext context)
    {
        if (idRegistry.TryGetSubjectById(subjectId, out var existing))
        {
            return (existing, false);
        }

        if (!context.IsSubjectComplete(subjectId))
        {
            // Reference to a subject that should exist but doesn't (concurrent structural
            // mutation removed it from the ID registry). Skip — self-heals on next update
            // that includes complete state for this subject.
            return (null, false);
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
        PropertyReference property,
        object indexOrKey,
        SubjectUpdateApplyContext context)
    {
        var serviceProvider = parent.Context.TryGetService<IServiceProvider>();
        return context.SubjectFactory.CreateCollectionSubject(property.Metadata.Type, indexOrKey, serviceProvider);
    }
}
