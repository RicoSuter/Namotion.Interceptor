namespace Namotion.Interceptor.Registry;

/// <summary>
/// Process-wide tripwire counters for the subject registry. The registry heals rather than throws
/// when an attaching subject carries an ID another subject already holds; this counter makes that
/// otherwise invisible repair observable in production.
/// </summary>
public static class SubjectRegistryDiagnostics
{
    /// <summary>
    /// A duplicate subject ID was seen at attach, which means something fabricated a subject: two
    /// distinct instances claim the same stable ID, so one of them was created for an ID that already
    /// belonged to another. The registry keeps the first instance in its ID index and leaves the
    /// second unreachable by ID, which is why nothing else reports the problem. A rising counter means
    /// an update applier, a deserializer or application code created a subject it should have resolved,
    /// and the unreachable instance stays permanently default-valued because no update can address it.
    /// </summary>
    public static long DuplicateSubjectIdAttaches => Volatile.Read(ref SubjectRegistry.DuplicateSubjectIdAttachCount);
}
