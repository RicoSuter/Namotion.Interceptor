using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Extensions;

public static class SubjectUpdateExtensions
{
    public static void ApplySubjectSourceUpdate(this IInterceptorSubject subject, SubjectUpdate update, ISubjectSource source,
        Action<PropertyReference, SubjectPropertyUpdate>? transform = null)
    {
        subject.VisitSubjectUpdate(update, 
            (propertyReference, propertyUpdate) =>
            {
                transform?.Invoke(propertyReference, propertyUpdate);
                propertyReference.SetValueFromSource(source, propertyUpdate.Value);
            }, 
        property => Activator.CreateInstance(property.Type) as IInterceptorSubject);
    }
    
    public static void ApplySubjectUpdate(this IInterceptorSubject subject, SubjectUpdate update,
        Action<PropertyReference, SubjectPropertyUpdate>? transform = null)
    {
        subject.VisitSubjectUpdate(update, 
            (propertyReference, propertyUpdate) =>
            {
                transform?.Invoke(propertyReference, propertyUpdate);
                propertyReference.Metadata.SetValue?.Invoke(propertyReference.Subject, propertyUpdate.Value);
            }, 
            property => Activator.CreateInstance(property.Type) as IInterceptorSubject);
    }
    
    public static void VisitSubjectUpdate(this IInterceptorSubject subject, SubjectUpdate update,
        Action<PropertyReference, SubjectPropertyUpdate> visitValuePropertyUpdate,
        Func<RegisteredSubjectProperty, IInterceptorSubject?>? createSubject = null)
    {
        foreach (var (propertyName, propertyUpdate) in update.Properties)
        {
            if (propertyUpdate.Attributes is not null)
            {
                foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
                {
                    var registeredAttribute = subject.GetRegisteredAttribute(propertyName, attributeName);
                    VisitSubjectPropertyUpdate(subject, registeredAttribute.Property.Name, attributeUpdate, visitValuePropertyUpdate, createSubject);
                }
            }

            VisitSubjectPropertyUpdate(subject, propertyName, propertyUpdate, visitValuePropertyUpdate, createSubject);
        }
    }

    private static void VisitSubjectPropertyUpdate(
        IInterceptorSubject subject, string propertyName,
        SubjectPropertyUpdate propertyUpdate,
        Action<PropertyReference, SubjectPropertyUpdate> visitValuePropertyUpdate,
        Func<RegisteredSubjectProperty, IInterceptorSubject?>? createSubject)
    {
        switch (propertyUpdate.Kind)
        {
            case SubjectPropertyUpdateKind.Value:
                var propertyReference = new PropertyReference(subject, propertyName);
                visitValuePropertyUpdate.Invoke(propertyReference, propertyUpdate);
                break;

            case SubjectPropertyUpdateKind.Item:
                if (subject.TryGetRegisteredProperty(propertyName) is { } registeredProperty)
                {
                    if (propertyUpdate.Item is not null)
                    {
                        if (registeredProperty.GetValue() is IInterceptorSubject existingItem)
                        {
                            // update existing item
                            VisitSubjectUpdate(existingItem, propertyUpdate.Item, visitValuePropertyUpdate);
                        }
                        else
                        {
                            // create new item
                            var item = createSubject?.Invoke(registeredProperty);
                            if (item != null)
                            {
                                VisitSubjectUpdate(item, propertyUpdate.Item, visitValuePropertyUpdate);
                                registeredProperty.SetValue(item);
                            }
                        }
                    }
                    else
                    {
                        // set item to null
                        registeredProperty.SetValue(null);
                    }
                }
                break;

            case SubjectPropertyUpdateKind.Collection:
                if (subject.TryGetRegisteredProperty(propertyName) is { } registeredCollectionProperty &&
                    propertyUpdate.Collection is not null)
                {
                    // TODO: Handle dictionary
                    // TODO: should we loop first through update.collection

                    var value = registeredCollectionProperty.GetValue();
                    if (value is IEnumerable<IInterceptorSubject> existingCollection)
                    {
                        foreach (var (item, index) in existingCollection.Select((item, index) => (item, index)))
                        {
                            VisitSubjectUpdate(item, propertyUpdate.Collection[index].Item!, visitValuePropertyUpdate);
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