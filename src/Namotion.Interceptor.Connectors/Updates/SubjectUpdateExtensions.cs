using Namotion.Interceptor.Connectors.Updates.Internal;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Extension methods for applying SubjectUpdate to subjects.
/// </summary>
public static class SubjectUpdateExtensions
{
    /// <summary>
    /// Applies update from an external source with source tracking.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="update">The update data.</param>
    /// <param name="source">The source the update data is coming from (used for change tracking to prevent echo back).</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="transformValueBeforeApply">The function to transform the update before applying it.</param>
    public static void ApplySubjectUpdateFromSource(
        this IInterceptorSubject subject,
        SubjectUpdate update,
        object source,
        ISubjectFactory? subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        var receivedTimestamp = DateTimeOffset.UtcNow;

        subject.ApplySubjectUpdate(update, subjectFactory, (property, propertyUpdate) =>
        {
            transformValueBeforeApply?.Invoke(property, propertyUpdate);
            var value = SubjectUpdateApplier.ConvertValue(propertyUpdate.Value, property.Type);
            property.SetValueFromSource(source, propertyUpdate.Timestamp, receivedTimestamp, value);
        });
    }

    /// <summary>
    /// Applies update to a subject.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="update">The update data.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="applyValueOverride">The function to apply an update to a property.</param>
    public static void ApplySubjectUpdate(
        this IInterceptorSubject subject,
        SubjectUpdate update,
        ISubjectFactory? subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? applyValueOverride = null)
    {
        SubjectUpdateApplier.Apply(
            subject,
            update,
            subjectFactory ?? DefaultSubjectFactory.Instance,
            applyValueOverride ?? SubjectUpdateApplier.DefaultApplyValue);
    }
}
