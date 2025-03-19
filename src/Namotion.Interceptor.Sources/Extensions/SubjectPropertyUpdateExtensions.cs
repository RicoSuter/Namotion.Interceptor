using System.Collections;

namespace Namotion.Interceptor.Sources.Extensions;

public static class SubjectPropertyUpdateExtensions
{
    public static void ApplyPropertyValue(this SubjectPropertyUpdate propertyUpdate, object? value)
    {
        if (value is IDictionary dictionary && dictionary.Values.OfType<IInterceptorSubject>().Any())
        {
            // TODO: Fix dictionary handling logic (how to detect dict?)
            
            propertyUpdate.Kind = SubjectPropertyUpdateKind.Collection;
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
            propertyUpdate.Kind = SubjectPropertyUpdateKind.Collection;
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
            propertyUpdate.Kind = SubjectPropertyUpdateKind.Item;
            propertyUpdate.Item = SubjectUpdate.CreateCompleteUpdate(itemSubject);
        }
        else
        {
            propertyUpdate.Kind = SubjectPropertyUpdateKind.Value;
            propertyUpdate.Value = value;
        }
    }
}