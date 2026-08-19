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
            context.SetPropertyValue(property, propertyUpdate.Timestamp, null);
            return;
        }

        var idRegistry = context.SubjectIdRegistry;

        // Phase 1: Resolve or create subjects, set IDs on new subjects.
        // For NEW subjects (no context, no interceptors): apply properties immediately.
        // This builds the full subgraph before it enters the graph, so concurrent
        // mutations that read the backing store after Phase 2 get fully-populated instances.
        // For EXISTING subjects (have context + interceptors): defer to Phase 3 (after rooting).
        var newItems = new List<(IInterceptorSubject Subject, string Id, bool IsNew)>(propertyUpdate.Items.Count);
        foreach (var itemUpdate in propertyUpdate.Items)
        {
            var (item, isNew) = ResolveOrCreateSubject(
                parent, property, newItems.Count, itemUpdate.Id, idRegistry, context);

            if (item is null)
                continue; // Subject not found and not complete, skip and self-heal on the next update

            if (isNew)
            {
                item.SetSubjectId(itemUpdate.Id);
                ApplyPropertiesIfAvailable(item, itemUpdate.Id, context, deferAttributes: true);
            }

            newItems.Add((item, itemUpdate.Id, isNew));
        }

        // Phase 2: Assign collection to graph (roots all items via lifecycle attach,
        // which discovers the fully-populated subgraph from backing store values)
        var subjects = new IInterceptorSubject[newItems.Count];
        for (var i = 0; i < newItems.Count; i++)
            subjects[i] = newItems[i].Subject;

        var collection = context.SubjectFactory.CreateSubjectCollection(metadata.Type, subjects);
        context.SetPropertyValue(property, propertyUpdate.Timestamp, collection);

        // Note: eager discovery of pre-populated children was investigated and abandoned because
        // AttachSubjectToContext already provides eager seeding via FindSubjectsInProperties.

        // Phase 3: Apply properties for EXISTING subjects (now rooted, lifecycle works correctly).
        // New subjects were already applied in Phase 1.
        foreach (var (item, id, _) in newItems)
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
            context.SetPropertyValue(property, propertyUpdate.Timestamp, null);
            return;
        }

        var idRegistry = context.SubjectIdRegistry;
        var targetKeyType = metadata.Type.GenericTypeArguments[0];

        // Phase 1: Resolve or create subjects, set IDs on new subjects.
        // For NEW subjects (no context, no interceptors): apply properties immediately.
        // This builds the full subgraph before it enters the graph, so concurrent
        // mutations that read the backing store after Phase 2 get fully-populated instances.
        // For EXISTING subjects (have context + interceptors): defer to Phase 3 (after rooting).
        // Does NOT read the backing store, which avoids a race with concurrent structural mutations
        // whose next() wrote a different dictionary before acquiring the lifecycle lock.
        var newItems = new List<(object Key, IInterceptorSubject Subject, string Id, bool IsNew)>(propertyUpdate.Items.Count);
        foreach (var itemUpdate in propertyUpdate.Items)
        {
            if (itemUpdate.Key is null)
            {
                // A dictionary entry without a key cannot be placed, so the entry is lost until the
                // next update carrying complete state for this dictionary.
                Interlocked.Increment(ref SubjectUpdateApplier.DroppedInboundSubjectUpdateCount);
                continue;
            }

            var key = DictionaryKeyConverter.Convert(itemUpdate.Key, targetKeyType);
            var (item, isNew) = ResolveOrCreateSubject(
                parent, property, key, itemUpdate.Id, idRegistry, context);

            if (item is null)
                continue; // Subject not found and not complete, skip and self-heal on the next update

            if (isNew)
            {
                item.SetSubjectId(itemUpdate.Id);
                ApplyPropertiesIfAvailable(item, itemUpdate.Id, context, deferAttributes: true);
            }

            newItems.Add((key, item, itemUpdate.Id, isNew));
        }

        // Phase 2: Build dictionary and assign to graph (roots all items via lifecycle attach,
        // which discovers the fully-populated subgraph from backing store values)
        var workingDictionary = new Dictionary<object, IInterceptorSubject>(newItems.Count);
        foreach (var (key, subject, _, _) in newItems)
            workingDictionary[key] = subject;

        var dictionary = context.SubjectFactory.CreateSubjectDictionary(metadata.Type, workingDictionary);
        context.SetPropertyValue(property, propertyUpdate.Timestamp, dictionary);

        // Note: eager discovery of pre-populated children was investigated and abandoned because
        // AttachSubjectToContext already provides eager seeding via FindSubjectsInProperties.

        // Phase 3: Apply properties for EXISTING subjects (now rooted, lifecycle works correctly).
        // New subjects were already applied in Phase 1.
        foreach (var (_, subject, id, _) in newItems)
        {
            ApplyPropertiesIfAvailable(subject, id, context);
        }
    }

    /// <summary>
    /// Resolves an existing subject by ID, or creates a new one when the update carries its complete
    /// state. Returns <c>null</c> when the ID is neither resolvable nor creatable; that reference is
    /// dropped and counted. The created instance is bare: the caller sets its ID and applies its
    /// properties before assigning the collection or dictionary to the graph, so the subgraph is
    /// complete by the time it enters the graph.
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
            // mutation removed it from the ID registry). Skip, it self-heals on the next update
            // that includes complete state for this subject. Until then the applied collection or
            // dictionary is one item short, so count the drop.
            Interlocked.Increment(ref SubjectUpdateApplier.DroppedInboundSubjectUpdateCount);
            return (null, false);
        }

        var newItem = CreateSubjectItem(parent, property, indexOrKey, context);
        return (newItem, true);
    }

    /// <summary>
    /// Applies property updates to a subject if properties are available and not yet processed.
    /// Set <paramref name="deferAttributes"/> for a subject that is not yet part of the graph.
    /// </summary>
    private static void ApplyPropertiesIfAvailable(
        IInterceptorSubject subject, string subjectId, SubjectUpdateApplyContext context, bool deferAttributes = false)
    {
        if (context.Subjects.TryGetValue(subjectId, out var properties) &&
            context.TryMarkAsProcessed(subjectId))
        {
            SubjectUpdateApplier.ApplyPropertyUpdates(subject, properties, context, deferAttributes);
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
