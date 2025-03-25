using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Sources.Updates;

public static class SubjectUpdateExtensions
{
    /// <summary>
    /// Applies all values of the source update data to a subject and optionally creates missing child subjects (e.g. using DefaultSubjectFactory.Instance).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="update">The update data.</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="source">The source the update data is coming from.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="transformValueBeforeApply">The function to transform the update before applying it.</param>
    public static void ApplySubjectUpdateFromSource(
        this IInterceptorSubject subject, 
        SubjectUpdate update, DateTimeOffset timestamp,
        ISubjectSource source, ISubjectFactory? subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        subject.ApplySubjectPropertyUpdate(update, timestamp,
            (registeredProperty, propertyUpdate) =>
            {
                transformValueBeforeApply?.Invoke(registeredProperty, propertyUpdate);
                registeredProperty.SetValueFromSource(source, propertyUpdate.Value);
            }, 
            subjectFactory);
    }

    /// <summary>
    /// Applies all values of the update data to a subject and optionally creates missing child subjects (e.g. using DefaultSubjectFactory.Instance).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="update">The update data.</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="transformValueBeforeApply">The function to transform the update before applying it.</param>
    public static void ApplySubjectUpdate(
        this IInterceptorSubject subject, 
        SubjectUpdate update, DateTimeOffset timestamp,
        ISubjectFactory? subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        subject.ApplySubjectPropertyUpdate(update, timestamp,
            (registeredProperty, propertyUpdate) =>
            {
                transformValueBeforeApply?.Invoke(registeredProperty, propertyUpdate);
                registeredProperty.SetValue(propertyUpdate.Value);
            }, 
            subjectFactory ?? DefaultSubjectFactory.Instance);
    }

    /// <summary>
    /// Applies all values of the update data to a subject property and optionally creates missing child subjects (e.g. using DefaultSubjectFactory.Instance).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="update">The update data.</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="applyValuePropertyUpdate">The action to apply a given update to the property value.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="registry">The optional registry. Might need to be passed because it is not yet accessible via subject.</param>
    public static void ApplySubjectPropertyUpdate(
        this IInterceptorSubject subject, 
        SubjectUpdate update, DateTimeOffset timestamp,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValuePropertyUpdate,
        ISubjectFactory? subjectFactory,
        ISubjectRegistry? registry = null)
    {
        SubjectMutationContext.SetCurrentTimestamp(timestamp);
        try
        {
            foreach (var (propertyName, propertyUpdate) in update.Properties)
            {
                if (propertyUpdate.Attributes is not null)
                {
                    foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
                    {
                        var registeredAttribute = subject.GetRegisteredAttribute(propertyName, attributeName);
                        ApplySubjectPropertyUpdate(subject, registeredAttribute.Property.Name, timestamp, attributeUpdate, applyValuePropertyUpdate, subjectFactory, registry);
                    }
                }

                ApplySubjectPropertyUpdate(subject, propertyName, timestamp, propertyUpdate, applyValuePropertyUpdate, subjectFactory, registry);
            }
        }
        finally
        {
            SubjectMutationContext.ResetCurrentTimestamp();
        }
    }

    private static void ApplySubjectPropertyUpdate(
        IInterceptorSubject subject, string propertyName, DateTimeOffset timestamp,
        SubjectPropertyUpdate propertyUpdate,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValuePropertyUpdate,
        ISubjectFactory? subjectFactory,
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
                        ApplySubjectPropertyUpdate(existingItem, propertyUpdate.Item, timestamp, applyValuePropertyUpdate, subjectFactory);
                    }
                    else
                    {
                        // create new item
                        var item = subjectFactory?.CreateSubject(registeredProperty, null);
                        if (item != null)
                        {
                            var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
                            RegisterSubject(parentRegistry, item, registeredProperty, null);
                            item.ApplySubjectPropertyUpdate(propertyUpdate.Item, timestamp, applyValuePropertyUpdate, subjectFactory, parentRegistry);
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
                                    ApplySubjectPropertyUpdate(existingCollection.ElementAt(index), item.Item!, timestamp, applyValuePropertyUpdate, subjectFactory);
                                }
                                else if (existingCollection is IList list)
                                {
                                    // Missing index, create new collection item
                                    var newItem = subjectFactory?.CreateSubject(registeredProperty, index);
                                    if (newItem is not null)
                                    {
                                        var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
                                        RegisterSubject(parentRegistry, newItem, registeredProperty, list.Count);
                                        ApplySubjectPropertyUpdate(newItem, item.Item!, timestamp, applyValuePropertyUpdate, subjectFactory, parentRegistry);
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
                    else if (subjectFactory is not null)
                    {
                        // create new collection
                        var items = propertyUpdate
                            .Collection?
                            .Select(i =>
                            {
                                var item = subjectFactory?.CreateSubject(registeredProperty, i.Index);
                                if (item is not null)
                                {
                                    var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
                                    RegisterSubject(parentRegistry, item, registeredProperty, i.Index);
                                    item.ApplySubjectPropertyUpdate(i.Item!, timestamp, applyValuePropertyUpdate, subjectFactory, parentRegistry);
                                }
                                return item;
                            }) ?? [];
                        
                        var collection = subjectFactory
                            .CreateSubjectCollection(registeredProperty, items);
                        
                        registeredProperty.SetValue(collection);
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