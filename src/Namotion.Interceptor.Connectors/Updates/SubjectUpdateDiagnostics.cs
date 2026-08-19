namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Process-wide tripwire counters for the subject-update pipeline. The pipeline drops rather than
/// buffers when a subject is momentarily unregistered (outbound) or unresolvable (inbound); these
/// counters make such drops observable in production, where no content-divergence detector exists.
/// A steadily rising counter under structural churn indicates a convergence gap worth investigating.
/// </summary>
public static class SubjectUpdateDiagnostics
{
    /// <summary>Outbound changes dropped because their subject was momentarily unregistered.</summary>
    public static long DroppedOutboundChanges => Volatile.Read(ref Internal.SubjectUpdateFactory.DroppedUnregisteredChangeCount);

    /// <summary>Complete-state serializations of momentarily unregistered subjects (metadata fallback path).</summary>
    public static long MetadataFallbackSerializations => Volatile.Read(ref Internal.SubjectUpdateFactory.MetadataFallbackSerializationCount);

    /// <summary>
    /// Inbound updates dropped because the subject they address stayed unresolvable. Counts both
    /// subject updates whose subject was neither in the registry nor created during the apply, and
    /// structural item references (object references, collection items, dictionary entries) that
    /// could not be resolved or placed, leaving the applied structure one item short.
    /// </summary>
    public static long DroppedInboundSubjectUpdates => Volatile.Read(ref Internal.SubjectUpdateApplier.DroppedInboundSubjectUpdateCount);

    /// <summary>Inbound properties skipped because the subject does not declare them.</summary>
    public static long UnknownInboundProperties => Volatile.Read(ref Internal.SubjectUpdateApplier.UnknownInboundPropertyCount);
}
