using Namotion.Interceptor.Connectors.Updates.Internal;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Extension methods for applying SubjectUpdate to subjects.
/// </summary>
public static class SubjectUpdateExtensions
{
    /// <summary>
    /// Applies update to a subject.
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
        SubjectUpdateApplier.ApplyUpdate(
            subject,
            update,
            subjectFactory ?? DefaultSubjectFactory.Instance,
            transformValueBeforeApply);
    }
}
