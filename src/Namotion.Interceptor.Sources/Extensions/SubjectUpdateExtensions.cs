using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Sources.Extensions;

public static class SubjectUpdateExtensions
{
    public static void ApplySubjectSourceUpdate(this IInterceptorSubject subject, SubjectUpdate update, ISubjectSource source,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        subject.ApplySubjectPropertyUpdate(update, 
            (registeredProperty, propertyUpdate) =>
            {
                transformValueBeforeApply?.Invoke(registeredProperty, propertyUpdate);
                registeredProperty.Property.SetValueFromSource(source, propertyUpdate.Value);
            }, 
            (_, type) => Activator.CreateInstance(type) as IInterceptorSubject 
                ?? throw new InvalidOperationException("Cannot create subject."));
    }
    
    public static void ApplySubjectUpdate(this IInterceptorSubject subject, SubjectUpdate update,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        subject.ApplySubjectPropertyUpdate(update, 
            (registeredProperty, propertyUpdate) =>
            {
                transformValueBeforeApply?.Invoke(registeredProperty, propertyUpdate);
                registeredProperty.SetValue(propertyUpdate.Value);
            }, 
            (_, type) => Activator.CreateInstance(type) as IInterceptorSubject 
                ?? throw new InvalidOperationException("Cannot create subject."));
    }
    
    public static void ApplySubjectPropertyUpdate(this IInterceptorSubject subject, SubjectUpdate update,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValuePropertyUpdate,
        Func<RegisteredSubjectProperty, Type, IInterceptorSubject>? createSubject)
    {
        foreach (var (propertyName, propertyUpdate) in update.Properties)
        {
            if (propertyUpdate.Attributes is not null)
            {
                foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
                {
                    var registeredAttribute = subject.GetRegisteredAttribute(propertyName, attributeName);
                    ApplySubjectPropertyUpdate(subject, registeredAttribute.Property.Name, attributeUpdate, applyValuePropertyUpdate, createSubject);
                }
            }

            ApplySubjectPropertyUpdate(subject, propertyName, propertyUpdate, applyValuePropertyUpdate, createSubject);
        }
    }

    private static void ApplySubjectPropertyUpdate(
        IInterceptorSubject subject, string propertyName,
        SubjectPropertyUpdate propertyUpdate,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValuePropertyUpdate,
        Func<RegisteredSubjectProperty, Type, IInterceptorSubject>? createSubject)
    {
        var registeredProperty = subject.TryGetRegisteredProperty(propertyName);
        if (registeredProperty is null)
            return;
        
        switch (propertyUpdate.Kind)
        {
            case SubjectPropertyUpdateKind.Value:
                applyValuePropertyUpdate.Invoke(registeredProperty, propertyUpdate);
                break;

            case SubjectPropertyUpdateKind.Item:
                if (propertyUpdate.Item is not null)
                {
                    if (registeredProperty.GetValue() is IInterceptorSubject existingItem)
                    {
                        // update existing item
                        ApplySubjectPropertyUpdate(existingItem, propertyUpdate.Item, applyValuePropertyUpdate, createSubject);
                    }
                    else
                    {
                        // create new item
                        var item = createSubject?.Invoke(registeredProperty, registeredProperty.Type);
                        if (item != null)
                        {
                            ApplySubjectPropertyUpdate(item, propertyUpdate.Item, applyValuePropertyUpdate, createSubject);
                            registeredProperty.SetValue(item);
                        }
                    }
                }
                else
                {
                    // set item to null
                    registeredProperty.SetValue(null);
                }
                break;

            case SubjectPropertyUpdateKind.Collection:
                if (propertyUpdate.Collection is not null)
                {
                    // TODO: Handle dictionary

                    var value = registeredProperty.GetValue();
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
                                    ApplySubjectPropertyUpdate(existingCollection.ElementAt(index), item.Item!, applyValuePropertyUpdate, createSubject);
                                }
                                else if (existingCollection is IList list)
                                {
                                    // Missing index, create new collection item
                                    var itemType = registeredProperty.Type.GenericTypeArguments[0];
                                    var newItem = createSubject?.Invoke(registeredProperty, itemType);
                                    if (newItem is not null)
                                    {
                                        ApplySubjectPropertyUpdate(newItem, item.Item!, applyValuePropertyUpdate, createSubject);
                                    }

                                    list.Add(newItem);
                                }
                                else
                                {
                                    throw new InvalidOperationException("Cannot add item to non-list collection.");
                                }
                            }
                        }
                    }
                    else
                    {
                        // create new collection
                        
                        // TODO(perf): Improve performance of collection creation
                        
                        var itemType = registeredProperty.Type.GenericTypeArguments[0];
                        var collectionType = typeof(List<>).MakeGenericType(itemType);
                        var list = (IList)Activator.CreateInstance(collectionType)!;
                        propertyUpdate.Collection.ForEach(i =>
                        {
                            var item = createSubject?.Invoke(registeredProperty, itemType);
                            if (item is not null)
                            {
                                ApplySubjectPropertyUpdate(item, i.Item!, applyValuePropertyUpdate, createSubject);
                            }
                            list.Add(item);
                        });
                    }
                }

                break;
        }
    }
}