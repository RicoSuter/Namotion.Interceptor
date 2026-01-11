using Namotion.Interceptor.Connectors.Updates.Collections;
using Namotion.Interceptor.Connectors.Updates.Items;
using Namotion.Interceptor.Connectors.Updates.Values;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates;

public static class SubjectUpdateExtensions
{
    /// <summary>
    /// Applies all values of the source update data to a subject and optionally creates missing child subjects (e.g. using DefaultSubjectFactory.Instance).
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="update">The update data.</param>
    /// <param name="source">The source the update data is coming from (used for change tracking to prevent echo back).</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="transformValueBeforeApply">The function to transform the update before applying it.</param>
    public static void ApplySubjectUpdateFromSource(
        this IInterceptorSubject subject,
        SubjectUpdate update,
        object source, ISubjectFactory? subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        var receivedTimestamp = DateTimeOffset.UtcNow;

        subject.ApplySubjectPropertyUpdate(update,
            (registeredProperty, propertyUpdate) =>
            {
                transformValueBeforeApply?.Invoke(registeredProperty, propertyUpdate);
                var value = SubjectValueUpdateLogic.ConvertValueToTargetType(propertyUpdate.Value, registeredProperty.Type);
                registeredProperty.SetValueFromSource(source, propertyUpdate.Timestamp, receivedTimestamp, value);
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
                var value = SubjectValueUpdateLogic.ConvertValueToTargetType(propertyUpdate.Value, registeredProperty.Type);
                registeredProperty.SetValue(value);
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
                SubjectItemUpdateLogic.ApplyItemFromUpdate(
                    subject, registeredProperty, propertyUpdate, applyValuePropertyUpdate, subjectFactory);
                break;

            case SubjectPropertyUpdateKind.Collection:
                if (registeredProperty.IsSubjectDictionary)
                {
                    SubjectDictionaryUpdateLogic.ApplyFromUpdate(
                        subject, registeredProperty, propertyUpdate, applyValuePropertyUpdate, subjectFactory);
                }
                else if (registeredProperty.IsSubjectCollection)
                {
                    SubjectCollectionUpdateLogic.ApplyFromUpdate(
                        subject, registeredProperty, propertyUpdate, applyValuePropertyUpdate, subjectFactory);
                }
                else
                {
                    throw new InvalidOperationException("Collection update received for a property that is not a collection or dictionary.");
                }
                break;
        }
    }
}
