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
        if (string.IsNullOrEmpty(update.Root))
            return;

        if (!update.Subjects.TryGetValue(update.Root, out var rootProperties))
            return;

        var context = ContextPool.Rent();
        try
        {
            context.Initialize(update.Subjects, subjectFactory, transformValueBeforeApply);
            context.TryMarkAsProcessed(update.Root);
            ApplyPropertyUpdates(subject, rootProperties, context);
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
        var registeredSubject = registry.TryGetRegisteredSubject(subject);

        foreach (var (propertyName, propertyUpdate) in properties)
        {
            // Apply attributes first
            if (propertyUpdate.Attributes is not null)
            {
                var registeredMember = registeredSubject?.TryGetMember(propertyName);
                foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
                {
                    var registeredAttribute = registeredMember?.TryGetAttribute(attributeName);
                    if (registeredAttribute is not null)
                    {
                        ApplyPropertyUpdate(subject, registeredAttribute, attributeUpdate, context);
                    }
                }
            }

            var registeredProperty = registeredSubject?.TryGetProperty(propertyName);
            if (registeredProperty is not null)
            {
                ApplyPropertyUpdate(subject, registeredProperty, propertyUpdate, context);
            }
        }
    }

    private static void ApplyPropertyUpdate(
        IInterceptorSubject subject,
        RegisteredSubjectProperty registeredProperty,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
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
        if (propertyUpdate.Id is not null &&
            context.Subjects.TryGetValue(propertyUpdate.Id, out var itemProperties))
        {
            if (property.GetValue() is IInterceptorSubject existingItem)
            {
                if (context.TryMarkAsProcessed(propertyUpdate.Id))
                {
                    ApplyPropertyUpdates(existingItem, itemProperties, context);
                }
            }
            else
            {
                var newItem = context.SubjectFactory.CreateSubject(property);
                newItem.Context.AddFallbackContext(parent.Context);

                if (context.TryMarkAsProcessed(propertyUpdate.Id))
                {
                    ApplyPropertyUpdates(newItem, itemProperties, context);
                }

                using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                {
                    property.SetValue(newItem);
                }
            }
        }
        else
        {
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                property.SetValue(null);
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
