using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Performance;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Applies SubjectUpdate instances to subjects.
/// </summary>
internal static class SubjectUpdateApplier
{
    private static readonly ObjectPool<ApplyContext> ContextPool = new(() => new ApplyContext());

    public static void ApplyUpdate(
        IInterceptorSubject subject,
        SubjectUpdate update,
        ISubjectFactory subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValue)
    {
        if (string.IsNullOrEmpty(update.Root))
            return;

        if (!update.Subjects.TryGetValue(update.Root, out var rootProperties))
            return;

        var context = ContextPool.Rent();
        try
        {
            context.Initialize(update.Subjects, subjectFactory, applyValue);
            context.TryMarkAsProcessed(update.Root);
            ApplyProperties(subject, rootProperties, context);
        }
        finally
        {
            context.Clear();
            ContextPool.Return(context);
        }
    }

    public static object? ConvertValue(object? value, Type targetType)
    {
        if (value is null)
            return null;

        if (value is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.String => jsonElement.GetString(),
                JsonValueKind.Number when targetType == typeof(int) || targetType == typeof(int?) => jsonElement.GetInt32(),
                JsonValueKind.Number when targetType == typeof(long) || targetType == typeof(long?) => jsonElement.GetInt64(),
                JsonValueKind.Number when targetType == typeof(float) || targetType == typeof(float?) => jsonElement.GetSingle(),
                JsonValueKind.Number when targetType == typeof(double) || targetType == typeof(double?) => jsonElement.GetDouble(),
                JsonValueKind.Number when targetType == typeof(decimal) || targetType == typeof(decimal?) => jsonElement.GetDecimal(),
                JsonValueKind.Number => jsonElement.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => value
            };
        }

        return value;
    }

    internal static void ApplyProperties(
        IInterceptorSubject subject,
        Dictionary<string, SubjectPropertyUpdate> properties,
        ApplyContext context)
    {
        var registry = subject.Context.GetService<ISubjectRegistry>();

        foreach (var (propertyName, propertyUpdate) in properties)
        {
            // Apply attributes first
            if (propertyUpdate.Attributes is not null)
            {
                foreach (var (attrName, attrUpdate) in propertyUpdate.Attributes)
                {
                    var registeredAttr = subject.TryGetRegisteredSubject()?
                        .TryGetPropertyAttribute(propertyName, attrName);

                    if (registeredAttr is not null)
                    {
                        ApplyPropertyUpdate(subject, registeredAttr.Name, attrUpdate, context, registry);
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
        ApplyContext context,
        ISubjectRegistry? registry)
    {
        var registeredProperty = subject.TryGetRegisteredProperty(propertyName, registry);
        if (registeredProperty is null)
            return;

        switch (propertyUpdate.Kind)
        {
            case SubjectPropertyUpdateKind.Value:
                context.ApplyValue(registeredProperty, propertyUpdate);
                break;

            case SubjectPropertyUpdateKind.Item:
                ApplyItemUpdate(subject, registeredProperty, propertyUpdate, context);
                break;

            case SubjectPropertyUpdateKind.Collection:
                if (registeredProperty.IsSubjectDictionary)
                    CollectionUpdateApplier.ApplyDictionaryUpdate(subject, registeredProperty, propertyUpdate, context);
                else
                    CollectionUpdateApplier.ApplyCollectionUpdate(subject, registeredProperty, propertyUpdate, context);
                break;
        }
    }

    private static void ApplyItemUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        ApplyContext context)
    {
        if (propertyUpdate.Id is not null &&
            context.Subjects.TryGetValue(propertyUpdate.Id, out var itemProperties))
        {
            if (property.GetValue() is IInterceptorSubject existingItem)
            {
                if (context.TryMarkAsProcessed(propertyUpdate.Id))
                {
                    ApplyProperties(existingItem, itemProperties, context);
                }
            }
            else
            {
                var newItem = context.SubjectFactory.CreateSubject(property);
                newItem.Context.AddFallbackContext(parent.Context);

                if (context.TryMarkAsProcessed(propertyUpdate.Id))
                {
                    ApplyProperties(newItem, itemProperties, context);
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
}
