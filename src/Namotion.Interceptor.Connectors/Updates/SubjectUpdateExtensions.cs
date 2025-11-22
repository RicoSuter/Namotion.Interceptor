using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates;

public static class SubjectUpdateExtensions
{
    /// <summary>
    /// Applies all values of the connector update data to a subject and optionally creates missing child subjects (e.g. using DefaultSubjectFactory.Instance).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="update">The update data.</param>
    /// <param name="connector">The connector the update data is coming from.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="transformValueBeforeApply">The function to transform the update before applying it.</param>
    public static void ApplySubjectUpdateFromConnector(
        this IInterceptorSubject subject,
        SubjectUpdate update,
        ISubjectConnector connector, ISubjectFactory? subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        subject.ApplySubjectPropertyUpdate(update,
            (registeredProperty, propertyUpdate) =>
            {
                transformValueBeforeApply?.Invoke(registeredProperty, propertyUpdate);
                registeredProperty.SetValueFromConnector(connector, propertyUpdate.Timestamp, propertyUpdate.Value);
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
                    var registeredAttribute = subject
                        .TryGetRegisteredSubject()?
                        .TryGetPropertyAttribute(propertyName, attributeName)
                            ?? throw new InvalidOperationException("Attribute not found on property.");

                    ApplySubjectPropertyUpdate(subject, registeredAttribute.Name, attributeUpdate, applyValuePropertyUpdate, subjectFactory, registry);
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
                using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                {
                    applyValuePropertyUpdate.Invoke(registeredProperty, propertyUpdate);
                }

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
                        var item = subjectFactory?.CreateSubject(registeredProperty);
                        if (item != null)
                        {
                            item.Context.AddFallbackContext(subject.Context);

                            var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
                            item.ApplySubjectPropertyUpdate(propertyUpdate.Item, applyValuePropertyUpdate, subjectFactory, parentRegistry);

                            using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                            {
                                registeredProperty.SetValue(item);
                            }
                        }
                    }
                }
                else
                {
                    // set item to null
                    using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                    {
                        registeredProperty.SetValue(null);
                    }
                }
                break;

            case SubjectPropertyUpdateKind.Collection:
                if (propertyUpdate.Collection is null)
                {
                    break;
                }

                if (registeredProperty.IsSubjectDictionary)
                {
                    // TODO: Handle dictionary
                }
                else if (registeredProperty.IsSubjectCollection)
                {
                    var value = registeredProperty.GetValue();
                    if (value is not null)
                    {
                        // Update existing collection
                        foreach (var item in propertyUpdate.Collection)
                        {
                            if (item.Item is null)
                            {
                                continue;
                            }

                            var index = (int)item.Index;
                            if (value is ICollection existingCollection &&
                                existingCollection.Count > index &&
                                existingCollection.Cast<object>().ElementAt(index) is IInterceptorSubject collectionElement)
                            {
                                // Update existing collection item
                                ApplySubjectPropertyUpdate(collectionElement, item.Item!, applyValuePropertyUpdate, subjectFactory);
                            }
                            else if (value is IList list)
                            {
                                // Missing item at index, create and add new collection item
                                var newItem = subjectFactory?.CreateCollectionSubject(registeredProperty, index);
                                if (newItem is not null)
                                {
                                    newItem.Context.AddFallbackContext(subject.Context);

                                    var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
                                    ApplySubjectPropertyUpdate(newItem, item.Item!, applyValuePropertyUpdate, subjectFactory, parentRegistry);
                                }

                                // TODO: Trigger property changed event (mutating list does not trigger change event)?
                                list.Add(newItem);
                            }
                            else
                            {
                                throw new InvalidOperationException("Cannot add item to non-list collection.");
                            }
                        }
                    }
                    else if (subjectFactory is not null)
                    {
                        // Create new collection
                        var items = propertyUpdate
                            .Collection?
                            .Select(i =>
                            {
                                var item = subjectFactory?.CreateCollectionSubject(registeredProperty, i.Index);
                                if (item is not null)
                                {
                                    // TODO: Is setting fallback context needed here or even too early?
                                    item.Context.AddFallbackContext(subject.Context);

                                    var parentRegistry = subject.Context.GetService<ISubjectRegistry>();
                                    item.ApplySubjectPropertyUpdate(i.Item!, applyValuePropertyUpdate, subjectFactory, parentRegistry);
                                }

                                return item;
                            }) ?? [];

                        var collection = subjectFactory.CreateSubjectCollection(registeredProperty.Type, items);
                        using (SubjectChangeContext.WithChangedTimestamp(propertyUpdate.Timestamp))
                        {
                            registeredProperty.SetValue(collection);
                        }
                    }
                }
                else
                {
                    throw new InvalidOperationException("Collection update received for a property that is not a collection or dictionary.");
                }
                break;
        }
    }
}
