namespace Namotion.Interceptor.Registry;

/// <summary>
/// Process-wide tripwire counters for the subject registry. The registry heals rather than throws
/// when an attaching subject carries an ID another subject already holds; this counter makes that
/// otherwise invisible repair observable in production.
/// </summary>
public static class SubjectRegistryDiagnostics
{
    /// <summary>
    /// Attaches of a subject whose ID another subject already holds. The registry keeps the first instance
    /// in its ID index, so the second cannot be addressed by ID. A rising count means an update applier, a
    /// deserializer or application code created a subject for an ID it should have resolved.
    /// </summary>
    public static long DuplicateSubjectIdAttaches => Volatile.Read(ref SubjectRegistry.DuplicateSubjectIdAttachCount);
}
