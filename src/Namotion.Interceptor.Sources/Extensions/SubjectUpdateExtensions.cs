using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Sources.Extensions;

public static class SubjectUpdateExtensions
{
    public static void ApplySubjectSourceUpdate(this IInterceptorSubject subject, SubjectUpdate update, ISubjectSource source,
        Action<PropertyReference, SubjectPropertyUpdate>? transform = null)
    {
        subject.VisitSubjectUpdate(update, (propertyReference, propertyUpdate) =>
        {
            transform?.Invoke(propertyReference, propertyUpdate);
            propertyReference.SetValueFromSource(source, propertyUpdate.Value);
        });
    }
    
    public static void ApplySubjectUpdate(this IInterceptorSubject subject, SubjectUpdate update,
        Action<PropertyReference, SubjectPropertyUpdate>? transform = null)
    {
        subject.VisitSubjectUpdate(update, (propertyReference, propertyUpdate) =>
        {
            transform?.Invoke(propertyReference, propertyUpdate);
            propertyReference.Metadata.SetValue?.Invoke(propertyReference.Subject, propertyUpdate.Value);
        });
    }
    
    public static void VisitSubjectUpdate(this IInterceptorSubject subject, SubjectUpdate update,
        Action<PropertyReference, SubjectPropertyUpdate> visitValuePropertyUpdate)
    {
        foreach (var (propertyName, propertyUpdate) in update.Properties)
        {
            if (propertyUpdate.Attributes is not null)
            {
                foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
                {
                    var registeredAttribute = subject.GetRegisteredAttribute(propertyName, attributeName);
                    VisitSubjectPropertyUpdate(subject, registeredAttribute.Property.Name, attributeUpdate, visitValuePropertyUpdate);
                }
            }

            VisitSubjectPropertyUpdate(subject, propertyName, propertyUpdate, visitValuePropertyUpdate);
        }
    }

    private static void VisitSubjectPropertyUpdate(
        IInterceptorSubject subject, string propertyName,
        SubjectPropertyUpdate propertyUpdate,
        Action<PropertyReference, SubjectPropertyUpdate> visitValuePropertyUpdate)
    {
        switch (propertyUpdate.Action)
        {
            case SubjectPropertyUpdateAction.UpdateValue:
                var propertyReference = new PropertyReference(subject, propertyName);
                visitValuePropertyUpdate.Invoke(propertyReference, propertyUpdate);
                break;

            case SubjectPropertyUpdateAction.UpdateItem:
                if (subject.TryGetRegisteredProperty(propertyName) is { } registeredProperty)
                {
                    if (registeredProperty.GetValue() is IInterceptorSubject existingItem)
                    {
                        if (propertyUpdate.Item is not null)
                        {
                            VisitSubjectUpdate(existingItem, propertyUpdate.Item, visitValuePropertyUpdate);
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
                            VisitSubjectUpdate(item, propertyUpdate.Collection![index].Item!, visitValuePropertyUpdate);
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
}