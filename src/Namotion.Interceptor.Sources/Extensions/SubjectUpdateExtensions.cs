using System.Collections;
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
            (_, type) => Activator.CreateInstance(type) as IInterceptorSubject);
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
            (_, type) => Activator.CreateInstance(type) as IInterceptorSubject);
    }
    
    public static void VisitSubjectUpdate(this IInterceptorSubject subject, SubjectUpdate update,
        Action<PropertyReference, SubjectPropertyUpdate> visitValuePropertyUpdate,
        Func<RegisteredSubjectProperty, Type, IInterceptorSubject?>? createSubject)
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
        Func<RegisteredSubjectProperty, Type, IInterceptorSubject?>? createSubject)
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
                            VisitSubjectUpdate(existingItem, propertyUpdate.Item, visitValuePropertyUpdate, createSubject);
                        }
                        else
                        {
                            // create new item
                            var item = createSubject?.Invoke(registeredProperty, registeredProperty.Type);
                            if (item != null)
                            {
                                VisitSubjectUpdate(item, propertyUpdate.Item, visitValuePropertyUpdate, createSubject);
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
                    if (value is IReadOnlyCollection<IInterceptorSubject> existingCollection)
                    {
                        foreach (var (item, index) in propertyUpdate
                            .Collection
                            .Select((item, index) => (item, index)))
                        {
                            if (item.Item is not null)
                            {
                                if (existingCollection.Count > index)
                                {
                                    // Update existing collection item
                                    VisitSubjectUpdate(existingCollection.ElementAt(index), item.Item!, visitValuePropertyUpdate, createSubject);
                                }
                                else if (existingCollection is IList list)
                                {
                                    // Missing index, create new collection item
                                    var itemType = registeredCollectionProperty.Type.GenericTypeArguments[0];
                                    var newItem = createSubject?.Invoke(registeredCollectionProperty, itemType);
                                    if (newItem is not null)
                                    {
                                        VisitSubjectUpdate(newItem, item.Item!, visitValuePropertyUpdate, createSubject);
                                    }

                                    list.Add(newItem);
                                }
                            }
                        }
                    }
                    else
                    {
                        // create new collection
                        // TODO(perf): Improve performance of collection creation
                        
                        var itemType = registeredCollectionProperty.Type.GenericTypeArguments[0];
                        var collectionType = typeof(List<>).MakeGenericType(itemType);
                        var collection = (IList)Activator.CreateInstance(collectionType)!;
                        propertyUpdate.Collection.ForEach(i =>
                        {
                            var item = createSubject?.Invoke(registeredCollectionProperty, itemType);
                            if (item is not null)
                            {
                                VisitSubjectUpdate(item, i.Item!, visitValuePropertyUpdate, createSubject);
                            }
                            collection.Add(item);
                        });
                    }
                }

                break;
        }
    }
}