using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Performance;

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
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        var context = ContextPool.Rent();
        try
        {
            context.Initialize(update.Subjects, subjectFactory, transformValueBeforeApply);

            if (update.Root is not null && update.Subjects.TryGetValue(update.Root, out var rootProperties))
            {
                // Complete update or rooted partial update with root entry — apply from root.
                // Set the root's subject ID to match the sender's ID so snapshots converge.
                subject.SetSubjectId(update.Root);
                context.TryMarkAsProcessed(update.Root);
                ApplyPropertyUpdates(subject, rootProperties, context);
            }

            // Always process remaining subjects by subject ID lookup.
            // When the root path ran above, it recursively processed subjects reachable
            // from the root's structural properties. But partial updates can contain changes
            // to subjects NOT reachable from the root's changed properties (e.g., a deeply
            // nested ObjectRef change in the same batch as a root scalar change).
            // TryMarkAsProcessed ensures no subject is processed twice.
            var idRegistry = subject.Context.GetService<ISubjectIdRegistry>();
            foreach (var (subjectId, properties) in update.Subjects)
            {
                if (idRegistry.TryGetSubjectById(subjectId, out var targetSubject))
                {
                    if (context.TryMarkAsProcessed(subjectId))
                    {
                        ApplyPropertyUpdates(targetSubject, properties, context);
                    }
                }
                // If subject not found, do NOT mark as processed.
                // The subject may be created later by a structural operation
                // (e.g., Collection Insert) which will apply its properties.
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
        var registry = subject.Context.GetService<ISubjectRegistry>();

        foreach (var (propertyName, propertyUpdate) in properties)
        {
            // Apply attributes first
            if (propertyUpdate.Attributes is not null)
            {
                foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
                {
                    var registeredAttribute = subject.TryGetRegisteredSubject()?
                        .TryGetPropertyAttribute(propertyName, attributeName);

                    if (registeredAttribute is not null)
                    {
                        ApplyPropertyUpdate(subject, registeredAttribute.Name, attributeUpdate, context, registry);
                    }
                }
            }

            ApplyPropertyUpdate(subject, propertyName, propertyUpdate, context, registry);
        }
    }

    private static void ApplyPropertyUpdate(
        IInterceptorSubject subject,
        string propertyName,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context,
        ISubjectRegistry? registry)
    {
        var registeredProperty = subject.TryGetRegisteredProperty(propertyName, registry);
        if (registeredProperty is null)
            return;

        switch (propertyUpdate.Kind)
        {
            case SubjectPropertyUpdateKind.Value:
                context.TransformValueBeforeApply?.Invoke(registeredProperty, propertyUpdate);
                using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                {
                    var value = ConvertValue(propertyUpdate.Value, registeredProperty.Type);
                    registeredProperty.SetValue(value);
                }
                break;

            case SubjectPropertyUpdateKind.Object:
                ApplyObjectUpdate(subject, registeredProperty, propertyUpdate, context);
                break;

            case SubjectPropertyUpdateKind.Collection:
                SubjectItemsUpdateApplier.ApplyCollectionUpdate(subject, registeredProperty, propertyUpdate, context);
                break;

            case SubjectPropertyUpdateKind.Dictionary:
                SubjectItemsUpdateApplier.ApplyDictionaryUpdate(subject, registeredProperty, propertyUpdate, context);
                break;
        }
    }

    private static void ApplyObjectUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        if (propertyUpdate.Id is null)
        {
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                property.SetValue(null);
            }
            return;
        }

        context.Subjects.TryGetValue(propertyUpdate.Id, out var itemProperties);

        var existingItem = property.GetValue() as IInterceptorSubject;
        IInterceptorSubject? targetItem;

        // Check if the existing item is the SAME logical subject (matching subject ID)
        // or a DIFFERENT subject that needs to be replaced.
        var isSameSubject = existingItem is not null &&
            existingItem.TryGetSubjectId() == propertyUpdate.Id;

        if (existingItem is not null && isSameSubject)
        {
            // Same logical subject — keep the existing CLR object.
            targetItem = existingItem;
        }
        else
        {
            // Either no existing item, or existing item is a DIFFERENT subject (replacement).
            // Try to reuse an existing subject by subject ID (may exist elsewhere in the graph).
            targetItem = null;
            var idRegistry = parent.Context.GetService<ISubjectIdRegistry>();
            if (idRegistry.TryGetSubjectById(propertyUpdate.Id, out var existing))
            {
                targetItem = existing;
            }

            if (targetItem is null && itemProperties is not null)
            {
                targetItem = context.SubjectFactory.CreateSubject(property);
                targetItem.Context.AddFallbackContext(parent.Context);
                targetItem.SetSubjectId(propertyUpdate.Id);
            }
        }

        if (targetItem is not null)
        {
            if (itemProperties is not null && context.TryMarkAsProcessed(propertyUpdate.Id))
            {
                ApplyPropertyUpdates(targetItem, itemProperties, context);
            }

            if (existingItem != targetItem)
            {
                using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                {
                    property.SetValue(targetItem);
                }
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
