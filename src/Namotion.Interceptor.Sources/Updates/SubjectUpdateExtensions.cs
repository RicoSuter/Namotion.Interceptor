using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Extensions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Sources.Updates;

public static class SubjectUpdateExtensions
{
    public static void ApplySubjectUpdateToSource(this IInterceptorSubject subject, SubjectUpdate update, ISubjectSource source,
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
    
    public static void ApplySubjectPropertyUpdate(
        this IInterceptorSubject subject, SubjectUpdate update,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValuePropertyUpdate,
        Func<RegisteredSubjectProperty, Type, IInterceptorSubject>? createSubject,
        ISubjectRegistry? registry = null)
    {
        foreach (var (propertyName, propertyUpdate) in update.Properties)
        {
            if (propertyUpdate.Attributes is not null)
            {
                foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
                {
                    var registeredAttribute = subject.GetRegisteredAttribute(propertyName, attributeName);
                    ApplySubjectPropertyUpdate(subject, registeredAttribute.Property.Name, attributeUpdate, applyValuePropertyUpdate, createSubject, registry);
                }
            }

            ApplySubjectPropertyUpdate(subject, propertyName, propertyUpdate, applyValuePropertyUpdate, createSubject, registry);
        }
    }

    private static void ApplySubjectPropertyUpdate(
        IInterceptorSubject subject, string propertyName,
        SubjectPropertyUpdate propertyUpdate,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValuePropertyUpdate,
        Func<RegisteredSubjectProperty, Type, IInterceptorSubject>? createSubject,
        ISubjectRegistry? registry)
    {
        var registeredProperty = subject.TryGetRegisteredProperty(propertyName, registry);
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
                            var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
                            RegisterSubject(parentRegistry, item, registeredProperty, null);
                            item.ApplySubjectPropertyUpdate(propertyUpdate.Item, applyValuePropertyUpdate, createSubject, parentRegistry);
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
                                        var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
                                        RegisterSubject(parentRegistry, newItem, registeredProperty, list.Count);
                                        ApplySubjectPropertyUpdate(newItem, item.Item!, applyValuePropertyUpdate, createSubject, parentRegistry);
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
                                var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
                                RegisterSubject(parentRegistry, item, registeredProperty, list.Count);
                                item.ApplySubjectPropertyUpdate(i.Item!, applyValuePropertyUpdate, createSubject, parentRegistry);
                            }
                            list.Add(item);
                        });
                    }
                }

                break;
        }
    }

    private static void RegisterSubject(ISubjectRegistry registry, IInterceptorSubject subject, RegisteredSubjectProperty property, object? index)
    {
        (registry as ILifecycleHandler)?.Attach(new SubjectLifecycleChange(subject, property.Property, index,1));
    }
}