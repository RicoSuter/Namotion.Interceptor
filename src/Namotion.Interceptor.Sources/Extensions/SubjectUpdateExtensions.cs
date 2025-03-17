using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Sources.Extensions;

public static class SubjectUpdateExtensions
{
    public static void VisitSubjectValueUpdates(this IInterceptorSubject subject, SubjectUpdate update, 
        Action<PropertyReference, SubjectPropertyUpdate> applySubjectValueUpdate)
    {
        foreach (var (propertyName, propertyUpdate) in update.Properties)
        {
            if (propertyUpdate.Attributes is not null)
            {
                foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
                {
                    VisitSubjectValueUpdates(subject, attributeName, attributeUpdate, applySubjectValueUpdate);
                }
            }

            VisitSubjectValueUpdates(subject, propertyName, propertyUpdate, applySubjectValueUpdate);
        }
    }

    private static void VisitSubjectValueUpdates(
        IInterceptorSubject subject, string propertyName, SubjectPropertyUpdate propertyUpdate,
        Action<PropertyReference, SubjectPropertyUpdate> applySubjectValueUpdate)
    {
        switch (propertyUpdate.Action)
        {
            case SubjectPropertyUpdateAction.UpdateValue:
                var propertyReference = new PropertyReference(subject, propertyName);
                applySubjectValueUpdate.Invoke(propertyReference, propertyUpdate);
                break;
            
            case SubjectPropertyUpdateAction.UpdateItem:
                if (subject.TryGetRegisteredProperty(propertyName) is { } registeredProperty)
                {
                    if (registeredProperty.GetValue() is IInterceptorSubject existingItem)
                    {
                        if (propertyUpdate.Item is not null)
                        {
                            ApplySubjectUpdate(existingItem, propertyUpdate.Item, applySubjectValueUpdate);
                        }
                        else
                        {
                            // TODO: Implement removal
                        }
                    }
                    else
                    {
                        // TODO: Implement add/set item
                    }
                }
                break;
                
            case SubjectPropertyUpdateAction.UpdateCollection:
                if (subject.TryGetRegisteredProperty(propertyName) is { } registeredCollectionProperty)
                {
                    // TODO: Handle dictionary

                    var value = registeredCollectionProperty.GetValue();
                    if (value is IEnumerable<IInterceptorSubject> existingCollection)
                    {
                        foreach (var (item, index) in existingCollection.Select((item, index) => (item, index)))
                        {
                            ApplySubjectUpdate(item, propertyUpdate.Collection![index].Item!, applySubjectValueUpdate);
                        }
                    }
                    else
                    {
                        // TODO: Implement add collection
                    }
                }
                break;
        }
    }

    public static void ApplySubjectUpdate(this IInterceptorSubject subject, SubjectUpdate update, 
        Action<PropertyReference, SubjectPropertyUpdate>? transform = null)
    {
        subject.VisitSubjectValueUpdates(update, (propertyReference, propertyUpdate) =>
        {
            transform?.Invoke(propertyReference, propertyUpdate);
            propertyReference.Metadata.SetValue?.Invoke(propertyReference.Subject, propertyUpdate.Value);
        });
    }

    public static void ApplySubjectUpdate(this IInterceptorSubject subject, SubjectUpdate update, ISubjectSource source, 
        Action<PropertyReference, SubjectPropertyUpdate>? transform = null)
    {
        subject.VisitSubjectValueUpdates(update, (propertyReference, propertyUpdate) =>
        {
            transform?.Invoke(propertyReference, propertyUpdate);
            propertyReference.SetValueFromSource(source, propertyUpdate.Value);
        });
    }
}