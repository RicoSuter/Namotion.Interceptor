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

    /// <summary>
    /// Complete-state serializations produced from a subject's own property metadata instead of its
    /// registry entry. Under structural churn this counts subjects that were momentarily unregistered.
    /// A context configured without a registry has no registry entry for any subject, so every subject
    /// of every update takes this path: a steady count proportional to update volume is a configuration
    /// signal, not a churn signal. The fallback path skips processor filtering and emits no timestamps.
    /// </summary>
    public static long MetadataFallbackSerializations => Volatile.Read(ref Internal.SubjectUpdateFactory.MetadataFallbackSerializationCount);

    /// <summary>
    /// Inbound updates dropped because the subject they address stayed unresolvable.
    /// </summary>
    /// <remarks>
    /// Incremented at four structurally different sites: a <c>subjects</c> entry whose subject was
    /// neither in the registry nor created by this update's own structural apply; an unresolvable
    /// <c>Object</c> reference; an unresolvable collection item or dictionary entry, which leaves the
    /// applied structure one item short; and a dictionary entry that carries no key and therefore
    /// cannot be placed. The sites are independent, so one logically unresolvable subject can bump the
    /// counter more than once in a single apply, typically twice: once where it is referenced as a
    /// collection item or dictionary entry and once for its own <c>subjects</c> entry. Read the counter
    /// as a rate that should settle, not as a count of distinct lost subjects.
    /// </remarks>
    public static long DroppedInboundSubjectUpdates => Volatile.Read(ref Internal.SubjectUpdateApplier.DroppedInboundSubjectUpdateCount);

    /// <summary>
    /// Inbound property updates skipped because the receiver's subject type does not declare the named
    /// property. Counted per property update, so one model-drift mismatch on a frequently changing
    /// property increments on every update that carries it. This is the model-drift signal between
    /// sender and receiver.
    /// </summary>
    public static long UnknownInboundProperties => Volatile.Read(ref Internal.SubjectUpdateApplier.UnknownInboundPropertyCount);
}
