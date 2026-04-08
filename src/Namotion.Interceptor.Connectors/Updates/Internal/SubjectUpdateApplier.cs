using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Performance;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Applies SubjectUpdate instances to subjects.
/// </summary>
internal static class SubjectUpdateApplier
{
    private static readonly ObjectPool<SubjectUpdateApplyContext> ContextPool = new(() => new SubjectUpdateApplyContext());

    public static void ApplyUpdate(
        IInterceptorSubject subject,
        SubjectUpdate update,
        ISubjectFactory subjectFactory,
        Action<PropertyReference, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        var context = ContextPool.Rent();
        try
        {
            context.Initialize(subject.Context, update.Subjects, update.CompleteSubjectIds, subjectFactory, transformValueBeforeApply);
            context.PreResolveSubjects(update.Subjects.Keys, context.SubjectIdRegistry);

            // Batch scope: defer isLastDetach processing so subjects moving between
            // structural properties within this update stay in _attachedSubjects and
            // _knownSubjects throughout. Fixes the apply-path subject move race.
            // PreResolveSubjects handles the concurrent-mutation race (different thread).
            var lifecycle = subject.Context.TryGetLifecycleInterceptor();
            using (lifecycle?.CreateBatchScope(subject.Context))
            {
                if (update.Root is not null && update.Subjects.TryGetValue(update.Root, out var rootProperties))
                {
                    // The Root field identifies which subject ID in the update
                    // corresponds to the local root subject. The root's ID may
                    // differ between sender and receiver — Root is a mapping hint,
                    // not an identity assignment.
                    context.TryMarkAsProcessed(update.Root);

                    ApplyPropertyUpdates(subject, rootProperties, context);
                }

                // Always process remaining subjects by subject ID lookup.
                // When the root path ran above, it recursively processed subjects reachable
                // from the root's structural properties. But partial updates can contain changes
                // to subjects NOT reachable from the root's changed properties (e.g., a deeply
                // nested ObjectRef change in the same batch as a root scalar change).
                // TryMarkAsProcessed ensures no subject is processed twice.
                //
                // Subjects not found on the first pass are retried after all known subjects
                // are processed. This handles the case where a value update for a new subject
                // arrives before (or in the same batch as) the structural update that creates it.
                // The structural processing in the first pass may create the subject, making it
                // findable on retry.
                List<(string subjectId, Dictionary<string, SubjectPropertyUpdate> properties)>? deferred = null;
                foreach (var (subjectId, properties) in update.Subjects)
                {
                    if (context.TryResolveSubject(subjectId, out var targetSubject))
                    {
                        if (context.TryMarkAsProcessed(subjectId))
                        {
                            ApplyPropertyUpdates(targetSubject, properties, context);
                        }
                    }
                    else
                    {
                        deferred ??= [];
                        deferred.Add((subjectId, properties));
                    }
                }

                // Retry deferred subjects — structural processing above may have created them.
                if (deferred is not null)
                {
                    foreach (var (subjectId, properties) in deferred)
                    {
                        if (context.SubjectIdRegistry.TryGetSubjectById(subjectId, out var targetSubject) &&
                            context.TryMarkAsProcessed(subjectId))
                        {
                            ApplyPropertyUpdates(targetSubject, properties, context);
                        }
                    }
                }
            }
        }
        finally
        {
            context.Clear();
            ContextPool.Return(context);
        }
    }

    internal static void ApplyPropertyUpdates(
        IInterceptorSubject subject,
        Dictionary<string, SubjectPropertyUpdate> properties,
        SubjectUpdateApplyContext context)
    {
        foreach (var (propertyName, propertyUpdate) in properties)
        {
            // Apply attributes first
            if (propertyUpdate.Attributes is not null)
            {
                foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
                {
                    var registeredAttribute = subject
                        .TryGetRegisteredSubject()?
                        .TryGetPropertyAttribute(propertyName, attributeName);

                    if (registeredAttribute is not null)
                    {
                        ApplyPropertyUpdate(subject, new PropertyReference(subject, registeredAttribute.Name), attributeUpdate, context);
                    }
                }
            }

            ApplyPropertyUpdate(subject, new PropertyReference(subject, propertyName), propertyUpdate, context);
        }
    }

    /// <summary>
    /// Applies a single property update using the subject's own property metadata
    /// (via <see cref="PropertyReference"/>). This does not depend on the
    /// <see cref="SubjectRegistry"/> — the subject always knows its own properties,
    /// even when momentarily unregistered due to concurrent structural mutations.
    /// </summary>
    private static void ApplyPropertyUpdate(
        IInterceptorSubject subject,
        PropertyReference property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        if (!subject.Properties.ContainsKey(property.Name))
            return; // Unknown property

        var metadata = property.Metadata;

        switch (propertyUpdate.Kind)
        {
            case SubjectPropertyUpdateKind.Value:
                context.TransformValueBeforeApply?.Invoke(property, propertyUpdate);
                using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                {
                    var value = ConvertValue(propertyUpdate.Value, metadata.Type);
                    metadata.SetValue?.Invoke(subject, value);
                }
                break;

            case SubjectPropertyUpdateKind.Object:
                ApplyObjectUpdate(subject, property, propertyUpdate, context);
                break;

            case SubjectPropertyUpdateKind.Collection:
                SubjectItemsUpdateApplier.ApplyCollectionUpdate(subject, property, propertyUpdate, context);
                break;

            case SubjectPropertyUpdateKind.Dictionary:
                SubjectItemsUpdateApplier.ApplyDictionaryUpdate(subject, property, propertyUpdate, context);
                break;
        }
    }

    private static void ApplyObjectUpdate(
        IInterceptorSubject parent,
        PropertyReference property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        var metadata = property.Metadata;

        if (propertyUpdate.Id is null)
        {
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                metadata.SetValue?.Invoke(parent, null);
            }
            return;
        }

        // Resolve target subject from registry — does NOT read the backing store to avoid
        // race with concurrent structural mutations whose next() wrote a different subject
        // before acquiring the lifecycle lock.
        IInterceptorSubject targetItem;
        bool isNew;

        if (context.SubjectIdRegistry.TryGetSubjectById(propertyUpdate.Id, out var existing))
        {
            targetItem = existing;
            isNew = false;
        }
        else if (context.IsSubjectComplete(propertyUpdate.Id))
        {
            // Subject has complete state in this update — safe to create.
            var serviceProvider = parent.Context.TryGetService<IServiceProvider>();
            targetItem = context.SubjectFactory.CreateSubject(metadata.Type, serviceProvider);
            isNew = true;
            // No AddFallbackContext here — ContextInheritanceHandler adds it
            // automatically when the subject enters the graph via SetValue below.
        }
        else
        {
            // Reference to a subject that should exist but doesn't (concurrent structural
            // mutation removed it from the ID registry). Skip — self-heals on next update
            // that includes complete state for this subject.
            return;
        }

        if (isNew || targetItem.TryGetSubjectId() != propertyUpdate.Id)
        {
            targetItem.SetSubjectId(propertyUpdate.Id);
        }

        // For NEW subjects (no context, no interceptors): apply properties before SetValue.
        // This builds the full subgraph before it enters the graph, so concurrent mutations
        // that read the backing store after SetValue get fully-populated instances.
        if (isNew)
        {
            if (context.Subjects.TryGetValue(propertyUpdate.Id, out var newItemProperties) &&
                context.TryMarkAsProcessed(propertyUpdate.Id))
            {
                ApplyPropertyUpdates(targetItem, newItemProperties, context);
            }
        }

        // Assign to graph — lifecycle interceptor diffs against _lastProcessedValues
        // and discovers the fully-populated subgraph from backing store values.
        using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
        {
            metadata.SetValue?.Invoke(parent, targetItem);
        }

        // For EXISTING subjects (have context + interceptors): apply properties after rooting.
        if (!isNew)
        {
            if (context.Subjects.TryGetValue(propertyUpdate.Id, out var itemProperties) &&
                context.TryMarkAsProcessed(propertyUpdate.Id))
            {
                ApplyPropertyUpdates(targetItem, itemProperties, context);
            }
        }
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        return value switch
        {
            null => null,
            JsonElement jsonElement => jsonElement.Deserialize(targetType),
            _ => value
        };
    }
}
