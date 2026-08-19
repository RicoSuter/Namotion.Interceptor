using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Performance;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Applies SubjectUpdate instances to subjects, resolving subjects by stable ID.
/// </summary>
internal static class SubjectUpdateApplier
{
    private static readonly ObjectPool<SubjectUpdateApplyContext> ContextPool = new(() => new SubjectUpdateApplyContext());

    /// <summary>
    /// Tripwire: inbound subject updates and structural item references dropped because their
    /// subject stayed unresolvable.
    /// </summary>
    internal static long DroppedInboundSubjectUpdateCount;

    /// <summary>Tripwire: inbound properties skipped because the subject does not declare them.</summary>
    internal static long UnknownInboundPropertyCount;

    public static void ApplyUpdate(
        IInterceptorSubject subject,
        SubjectUpdate update,
        ISubjectFactory subjectFactory,
        ChangeOrigin origin,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        var context = ContextPool.Rent();
        try
        {
            context.Initialize(subject.Context, update.Subjects, update.CompleteSubjectIds, subjectFactory, origin, transformValueBeforeApply);
            context.PreResolveSubjects(update.Subjects.Keys);

            // Batch scope: defer last-detach processing so subjects moving between structural
            // properties within this update stay attached and registered throughout.
            // PreResolveSubjects above handles the concurrent-mutation race (different thread);
            // the scope handles the apply-path move race (same thread).
            var lifecycle = subject.Context.TryGetLifecycleInterceptor();
            using (lifecycle?.CreateBatchScope(subject.Context))
            {
                if (update.Root is not null && update.Subjects.TryGetValue(update.Root, out var rootProperties))
                {
                    // The Root field identifies which subject ID in the update corresponds to the
                    // local root subject. The root's ID may differ between sender and receiver;
                    // Root is a mapping hint, not an identity assignment.
                    context.TryMarkAsProcessed(update.Root);
                    ApplyPropertyUpdates(subject, rootProperties, context);
                }

                // Process remaining subjects by ID lookup. Partial updates can contain changes to
                // subjects not reachable from the root's changed properties. Subjects not found on
                // the first pass are retried after all known subjects are processed: structural
                // processing in the first pass may create them.
                List<(string SubjectId, Dictionary<string, SubjectPropertyUpdate> Properties)>? deferred = null;
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

                // Applied before the retry pass below, because a subject-valued attribute can be what
                // creates and populates the subject a deferred entry addresses. The retry pass is the
                // last chance for such an entry, and it consumes the ID whether it applies or drops it,
                // so anything still able to create subjects has to have run first.
                var appliedAttributeUpdates = ApplyDeferredAttributeUpdates(context, 0);

                if (deferred is not null)
                {
                    foreach (var (subjectId, properties) in deferred)
                    {
                        if (!context.TryMarkAsProcessed(subjectId))
                        {
                            // Already applied in this pass, so nothing was dropped and the tripwire must
                            // not fire: either the root (whose sender-side ID is a mapping hint that never
                            // resolves in the receiver's registry) or a subject that a later structural
                            // entry created and populated.
                            continue;
                        }

                        if (context.SubjectIdRegistry.TryGetSubjectById(subjectId, out var targetSubject))
                        {
                            ApplyPropertyUpdates(targetSubject, properties, context);
                        }
                        else
                        {
                            // The subject was not created by structural processing and is not in the
                            // registry: drop the update. The next update carrying the subject's
                            // complete state converges it. The counter is the production tripwire.
                            Interlocked.Increment(ref DroppedInboundSubjectUpdateCount);
                        }
                    }
                }

                // The retry pass can root further subjects, which queue attribute updates of their own.
                ApplyDeferredAttributeUpdates(context, appliedAttributeUpdates);
            }
        }
        finally
        {
            context.Clear();
            ContextPool.Return(context);
        }
    }

    /// <summary>
    /// Applies the property updates of <paramref name="properties"/> to <paramref name="subject"/>.
    /// Set <paramref name="deferAttributes"/> for a subject that is still being populated before it
    /// enters the graph: attribute names resolve through the registry, which does not know the subject
    /// until it is rooted, so its attribute updates are queued and applied once the whole update is in.
    /// </summary>
    internal static void ApplyPropertyUpdates(
        IInterceptorSubject subject,
        Dictionary<string, SubjectPropertyUpdate> properties,
        SubjectUpdateApplyContext context,
        bool deferAttributes = false)
    {
        var hasDeferredAttributes = false;
        foreach (var (propertyName, propertyUpdate) in properties)
        {
            // Apply attributes first
            if (propertyUpdate.Attributes is not null)
            {
                if (deferAttributes)
                {
                    hasDeferredAttributes = true;
                }
                else
                {
                    ApplyAttributeUpdates(subject, propertyName, propertyUpdate.Attributes, context);
                }
            }

            ApplyPropertyUpdate(subject, new PropertyReference(subject, propertyName), propertyUpdate, context);
        }

        if (hasDeferredAttributes)
        {
            context.DeferAttributeUpdates(subject, properties);
        }
    }

    private static void ApplyAttributeUpdates(
        IInterceptorSubject subject,
        string propertyName,
        Dictionary<string, SubjectPropertyUpdate> attributes,
        SubjectUpdateApplyContext context)
    {
        foreach (var (attributeName, attributeUpdate) in attributes)
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

    /// <summary>
    /// Applies the attribute updates queued for subjects that were created and populated before they
    /// entered the graph, starting at <paramref name="startIndex"/>. The subjects those entries belong
    /// to are rooted by the time an entry is queued, so the registry can map attribute names to their
    /// backing properties. Returns the number of entries applied so far, to pass as the next start
    /// index, so a second call picks up only what was queued after the first one returned.
    /// </summary>
    private static int ApplyDeferredAttributeUpdates(SubjectUpdateApplyContext context, int startIndex)
    {
        // Indexed on purpose: applying a subject-valued attribute whose target is newly created can
        // queue that target's own attribute updates, appending to this list while it is walked. The
        // index loop processes the appended entries instead of throwing on a modified collection.
        var deferredUpdates = context.DeferredAttributeUpdates;
        for (var index = startIndex; index < deferredUpdates.Count; index++)
        {
            var (subject, properties) = deferredUpdates[index];
            foreach (var (propertyName, propertyUpdate) in properties)
            {
                if (propertyUpdate.Attributes is not null)
                {
                    ApplyAttributeUpdates(subject, propertyName, propertyUpdate.Attributes, context);
                }
            }
        }

        return deferredUpdates.Count;
    }

    /// <summary>
    /// Applies a single property update using the subject's own property metadata
    /// (via <see cref="PropertyReference"/>). This does not depend on the registry:
    /// the subject always knows its own properties, even when momentarily unregistered
    /// or not yet attached (a newly created subject being populated before rooting).
    /// </summary>
    private static void ApplyPropertyUpdate(
        IInterceptorSubject subject,
        PropertyReference property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        if (!subject.Properties.ContainsKey(property.Name))
        {
            Interlocked.Increment(ref UnknownInboundPropertyCount);
            return;
        }

        switch (propertyUpdate.Kind)
        {
            case SubjectPropertyUpdateKind.Value:
                ApplyValueUpdate(subject, property, propertyUpdate, context);
                break;

            case SubjectPropertyUpdateKind.Object:
                ApplyObjectUpdate(property, propertyUpdate, context);
                break;

            case SubjectPropertyUpdateKind.Collection:
                SubjectItemsUpdateApplier.ApplyCollectionUpdate(property, propertyUpdate, context);
                break;

            case SubjectPropertyUpdateKind.Dictionary:
                SubjectItemsUpdateApplier.ApplyDictionaryUpdate(property, propertyUpdate, context);
                break;
        }
    }

    private static void ApplyValueUpdate(
        IInterceptorSubject subject,
        PropertyReference property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        var registeredProperty = context.TransformValueBeforeApply is not null
            ? subject.TryGetRegisteredProperty(property.Name)
            : null;

        if (context.TransformValueBeforeApply is not null && registeredProperty is not null)
        {
            // Convert once BEFORE the transform runs; this converted instance is the value the
            // source semantically sent and doubles as the origin's survival evidence. If the
            // transform does not replace propertyUpdate.Value (reference unchanged), reuse that
            // same instance as the written value too: converting a JSON value twice yields two
            // reference-distinct instances for reference types (int[], DTOs), which fail the
            // reference-equality survival check and wrongly demote a genuine unchanged source
            // write to Local, defeating echo suppression. Only re-convert when the transform
            // substituted a new value, so a locally corrected value differs from the evidence
            // and the origin correctly demotes to Local.
            var rawValue = propertyUpdate.Value;
            var sentValue = ConvertValue(rawValue, property.Metadata.Type);
            context.TransformValueBeforeApply.Invoke(registeredProperty, propertyUpdate);
            var value = ReferenceEquals(propertyUpdate.Value, rawValue)
                ? sentValue
                : ConvertValue(propertyUpdate.Value, property.Metadata.Type);
            context.SetPropertyValue(property, propertyUpdate.Timestamp, value, sentValue);
        }
        else
        {
            var value = ConvertValue(propertyUpdate.Value, property.Metadata.Type);
            context.SetPropertyValue(property, propertyUpdate.Timestamp, value);
        }
    }

    private static void ApplyObjectUpdate(
        PropertyReference property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        if (propertyUpdate.Id is null)
        {
            context.SetPropertyValue(property, propertyUpdate.Timestamp, null);
            return;
        }

        // Resolve the target subject from the ID registry; do NOT read the backing store, to
        // avoid racing a concurrent structural mutation whose write landed before its
        // lifecycle processing.
        IInterceptorSubject targetItem;
        bool isNew;

        if (context.SubjectIdRegistry.TryGetSubjectById(propertyUpdate.Id, out var existing))
        {
            targetItem = existing;
            isNew = false;
        }
        else if (context.IsSubjectComplete(propertyUpdate.Id))
        {
            // The subject has complete state in this update, so creating it is safe.
            targetItem = context.SubjectFactory.CreateSubject(property.Metadata.Type, context.ServiceProvider);
            isNew = true;
        }
        else
        {
            // A reference to a subject that should exist but doesn't (a concurrent structural
            // mutation removed it). Skip: the next update carrying its complete state heals it.
            // The reference is lost until then, so count it as a drop.
            Interlocked.Increment(ref DroppedInboundSubjectUpdateCount);
            return;
        }

        if (isNew || targetItem.TryGetSubjectId() != propertyUpdate.Id)
        {
            targetItem.SetSubjectId(propertyUpdate.Id);
        }

        // For NEW subjects (no context, no interceptors yet): populate properties before the
        // SetValue below, so the subgraph is complete before it enters the graph and concurrent
        // readers of the backing store see fully populated instances.
        if (isNew)
        {
            if (context.Subjects.TryGetValue(propertyUpdate.Id, out var newItemProperties) &&
                context.TryMarkAsProcessed(propertyUpdate.Id))
            {
                ApplyPropertyUpdates(targetItem, newItemProperties, context, deferAttributes: true);
            }
        }

        context.SetPropertyValue(property, propertyUpdate.Timestamp, targetItem);

        // For EXISTING subjects (context and interceptors live): apply properties after rooting.
        if (!isNew)
        {
            if (context.Subjects.TryGetValue(propertyUpdate.Id, out var itemProperties) &&
                context.TryMarkAsProcessed(propertyUpdate.Id))
            {
                ApplyPropertyUpdates(targetItem, itemProperties, context);
            }
        }
    }

    internal static object? ConvertValue(object? value, Type targetType)
    {
        return value switch
        {
            null => null,
            JsonElement jsonElement => jsonElement.Deserialize(targetType),
            _ => value
        };
    }
}
