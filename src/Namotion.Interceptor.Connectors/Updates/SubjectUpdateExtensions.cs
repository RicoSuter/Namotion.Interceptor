using Namotion.Interceptor.Connectors.Updates.Internal;

namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Extension methods for applying SubjectUpdate to subjects.
/// </summary>
public static class SubjectUpdateExtensions
{
    private static readonly (string?, string) ApplyLockKey = (null, "Namotion.Interceptor.Connectors.ApplyLock");

    /// <summary>
    /// Gets the per-subject apply lock. Use this to serialize operations that read
    /// graph state (e.g., hash computation) with concurrent applies.
    /// </summary>
    public static object GetApplyLock(this IInterceptorSubject subject)
        => subject.Data.GetOrAdd(ApplyLockKey, new object())!;

    /// <summary>
    /// Applies update to a subject. Serialized per subject — concurrent applies
    /// to the same root subject are safe.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="update">The update data.</param>
    /// <param name="subjectFactory">The subject factory to create missing subjects, null to ignore updates on missing subjects.</param>
    /// <param name="transformValueBeforeApply">The function to transform the update before applying it.</param>
    public static void ApplySubjectUpdate(
        this IInterceptorSubject subject,
        SubjectUpdate update,
        ISubjectFactory? subjectFactory,
        Action<PropertyReference, SubjectPropertyUpdate>? transformValueBeforeApply = null)
    {
        var applyLock = subject.GetApplyLock();
        lock (applyLock)
        {
            SubjectUpdateApplier.ApplyUpdate(
                subject,
                update,
                subjectFactory ?? DefaultSubjectFactory.Instance,
                transformValueBeforeApply);
        }
    }
}
