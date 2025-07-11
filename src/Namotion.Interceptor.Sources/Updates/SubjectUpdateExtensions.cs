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
    /// <param name="source">The source the update data is coming from.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="transformValueBeforeApply">The function to transform the update before applying it.</param>
    public static void ApplySubjectUpdateFromSource(
        this IInterceptorSubject subject, 
        SubjectUpdate update,
        ISubjectSource source, ISubjectFactory? subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        subject.ApplySubjectPropertyUpdate(update,
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
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="transformValueBeforeApply">The function to transform the update before applying it.</param>
    public static void ApplySubjectUpdate(
        this IInterceptorSubject subject, 
        SubjectUpdate update,
        ISubjectFactory? subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        subject.ApplySubjectPropertyUpdate(update,
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
    /// <param name="applyValuePropertyUpdate">The action to apply a given update to the property value.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="registry">The optional registry. Might need to be passed because it is not yet accessible via subject.</param>
    public static void ApplySubjectPropertyUpdate(
        this IInterceptorSubject subject, 
        SubjectUpdate update,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValuePropertyUpdate,
        ISubjectFactory? subjectFactory,
        ISubjectRegistry? registry = null)
    {
        foreach (var (propertyName, propertyUpdate) in update.Properties)
        {
            if (propertyUpdate.Attributes is not null)
            {
                foreach (var (attributeName, attributeUpdate) in propertyUpdate.Attributes)
                {
                    var registeredAttribute = subject.GetRegisteredAttribute(propertyName, attributeName);
                    ApplySubjectPropertyUpdate(subject, registeredAttribute.Property.Name, attributeUpdate, applyValuePropertyUpdate, subjectFactory, registry);
                }
            }

            ApplySubjectPropertyUpdate(subject, propertyName, propertyUpdate, applyValuePropertyUpdate, subjectFactory, registry);
        }
    }

    private static void ApplySubjectPropertyUpdate(
        IInterceptorSubject subject, string propertyName,
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
                SubjectMutationContext.ApplyChangesWithTimestamp(propertyUpdate.Timestamp, () =>
                    applyValuePropertyUpdate.Invoke(registeredProperty, propertyUpdate));
                break;

            case SubjectPropertyUpdateKind.Item:
                if (propertyUpdate.Item is not null)
                {
                    if (registeredProperty.GetValue() is IInterceptorSubject existingItem)
                    {
                        // update existing item
                        ApplySubjectPropertyUpdate(existingItem, propertyUpdate.Item, applyValuePropertyUpdate, subjectFactory);
                    }
                    else
                    {
                        // create new item
                        var item = subjectFactory?.CreateSubject(registeredProperty, null);
                        if (item != null)
                        {
                            item.Context.AddFallbackContext(subject.Context);

                            var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
                            item.ApplySubjectPropertyUpdate(propertyUpdate.Item, applyValuePropertyUpdate, subjectFactory, parentRegistry);

                            SubjectMutationContext.ApplyChangesWithTimestamp(propertyUpdate.Timestamp, () =>
                                registeredProperty.SetValue(item));
                        }
                    }
                }
                else
                {
                    // set item to null
                    SubjectMutationContext.ApplyChangesWithTimestamp(propertyUpdate.Timestamp, () =>
                        registeredProperty.SetValue(null));
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
                                    ApplySubjectPropertyUpdate(existingCollection.ElementAt(index), item.Item!, applyValuePropertyUpdate, subjectFactory);
                                }
                                else if (existingCollection is IList list)
                                {
                                    // Missing index, create new collection item
                                    var newItem = subjectFactory?.CreateSubject(registeredProperty, index);
                                    if (newItem is not null)
                                    {
                                        newItem.Context.AddFallbackContext(subject.Context);

                                        var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
                                        ApplySubjectPropertyUpdate(newItem, item.Item!, applyValuePropertyUpdate, subjectFactory, parentRegistry);
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
                                    item.Context.AddFallbackContext(subject.Context);

                                    var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
                                    item.ApplySubjectPropertyUpdate(i.Item!, applyValuePropertyUpdate, subjectFactory, parentRegistry);
                                }
                                return item;
                            }) ?? [];
                        
                        var collection = subjectFactory.CreateSubjectCollection(registeredProperty, items);
                        SubjectMutationContext.ApplyChangesWithTimestamp(propertyUpdate.Timestamp, () => registeredProperty.SetValue(collection));
                    }
                }

                break;
        }
    }
}