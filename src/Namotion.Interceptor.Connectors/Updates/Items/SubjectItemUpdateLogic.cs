using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates.Items;

/// <summary>
/// Internal logic for creating and applying item (single subject reference) property updates.
/// </summary>
internal static class SubjectItemUpdateLogic
{
    /// <summary>
    /// Applies an item (subject reference) to a property update (create side).
    /// Sets Kind to Item and creates the nested SubjectUpdate.
    /// </summary>
    internal static void ApplyItemToUpdate(
        SubjectPropertyUpdate update,
        IInterceptorSubject? itemSubject,
        ReadOnlySpan<ISubjectUpdateProcessor> processors,
        Dictionary<IInterceptorSubject, SubjectUpdate> knownSubjectUpdates,
        Dictionary<SubjectPropertyUpdate, SubjectPropertyUpdateReference>? propertyUpdates,
        HashSet<IInterceptorSubject> currentPath)
    {
        update.Kind = SubjectPropertyUpdateKind.Item;
        update.Item = itemSubject is not null
            ? SubjectUpdateFactory.GetOrCreateCompleteUpdate(itemSubject, processors, knownSubjectUpdates, propertyUpdates, currentPath)
            : null;
    }

    /// <summary>
    /// Applies an item property update to a subject (apply side).
    /// Handles updating existing items, creating new items, or setting to null.
    /// </summary>
    internal static void ApplyItemFromUpdate(
        IInterceptorSubject subject,
        RegisteredSubjectProperty registeredProperty,
        SubjectPropertyUpdate propertyUpdate,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValuePropertyUpdate,
        ISubjectFactory? subjectFactory)
    {
        if (propertyUpdate.Item is not null)
        {
            if (registeredProperty.GetValue() is IInterceptorSubject existingItem)
            {
                // Update existing item
                existingItem.ApplySubjectPropertyUpdate(propertyUpdate.Item, applyValuePropertyUpdate, subjectFactory);
            }
            else
            {
                // Create new item
                var item = subjectFactory?.CreateSubject(registeredProperty);
                if (item != null)
                {
                    item.Context.AddFallbackContext(subject.Context);

                    var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
                    item.ApplySubjectPropertyUpdate(propertyUpdate.Item, applyValuePropertyUpdate, subjectFactory, parentRegistry);

                    using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                    {
                        registeredProperty.SetValue(item);
                    }
                }
            }
        }
        else
        {
            // Set item to null
            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
            {
                registeredProperty.SetValue(null);
            }
        }
    }
}
