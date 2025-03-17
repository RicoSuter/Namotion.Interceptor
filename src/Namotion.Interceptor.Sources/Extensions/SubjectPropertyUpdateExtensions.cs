using System.Collections;

namespace Namotion.Interceptor.Sources.Extensions;

public static class SubjectPropertyUpdateExtensions
{
    public static void ApplyPropertyValue(this SubjectPropertyUpdate propertyUpdate, object? value)
    {
        if (value is IDictionary dictionary && dictionary.Values.OfType<IInterceptorSubject>().Any())
        {
            // TODO: Fix dictionary handling logic (how to detect dict?)
            
            propertyUpdate.Action = SubjectPropertyUpdateAction.UpdateCollection;
            propertyUpdate.Collection = dictionary.Keys
                .OfType<object>()
                .Select((key) => new SubjectPropertyCollectionUpdate
                {
                    Item = SubjectUpdate.CreateCompleteUpdate((IInterceptorSubject)dictionary[key]!),
                    Index = key
                })
                .ToList();
        }
        else if (value is IEnumerable<IInterceptorSubject> collection)
        {
            propertyUpdate.Action = SubjectPropertyUpdateAction.UpdateCollection;
            propertyUpdate.Collection = collection
                .Select((itemSubject, index) => new SubjectPropertyCollectionUpdate
                {
                    Item = SubjectUpdate.CreateCompleteUpdate(itemSubject),
                    Index = index
                })
                .ToList();
        }
        else if (value is IInterceptorSubject itemSubject)
        {
            propertyUpdate.Action = SubjectPropertyUpdateAction.UpdateItem;
            propertyUpdate.Item = SubjectUpdate.CreateCompleteUpdate(itemSubject);
        }
        else
        {
            propertyUpdate.Action = SubjectPropertyUpdateAction.UpdateValue;
            propertyUpdate.Value = value;
        }
    }
}