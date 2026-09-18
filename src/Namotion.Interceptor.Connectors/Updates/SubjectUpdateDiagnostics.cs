namespace Namotion.Interceptor.Connectors.Updates;

/// <summary>
/// Process-wide tripwire counters for the subject-update pipeline. The pipeline drops rather than
/// buffers when a subject has no Registry metadata (outbound) or is unresolvable (inbound); these
/// counters make such drops observable in production, where no content-divergence detector exists.
/// A steadily rising counter under structural churn indicates a convergence gap worth investigating.
/// </summary>
public static class SubjectUpdateDiagnostics
{
    private static long _droppedOutboundChanges;
    private static long _metadataFallbackSerializations;
    private static long _droppedInboundSubjectUpdates;
    private static long _unknownInboundProperties;

    /// <summary>
    /// Outbound changes dropped because their subject had no Registry metadata when the update was built: it
    /// left the graph after the change was captured, or its attach was still in progress. A change that
    /// attaches it again carries its complete state.
    /// </summary>
    public static long DroppedOutboundChanges => Volatile.Read(ref _droppedOutboundChanges);

    /// <summary>
    /// Complete-state serializations produced from a subject's own property metadata instead of its registry
    /// entry, for a subject whose attach was still in progress or that left the graph after a change naming it
    /// was captured. A context configured without a registry has no registry entry for any subject, so every
    /// subject of every complete update takes this path: a steady count proportional to update volume is a
    /// configuration signal, not a churn signal. Processors cannot filter the properties serialized this way.
    /// </summary>
    public static long MetadataFallbackSerializations => Volatile.Read(ref _metadataFallbackSerializations);

    /// <summary>
    /// Inbound updates dropped because the subject they address stayed unresolvable.
    /// </summary>
    /// <remarks>
    /// Incremented at independent sites: a <c>subjects</c> entry whose subject was neither in the registry
    /// nor created by this update's own structural apply, unless it is named only through properties the
    /// receiver does not declare; an unresolvable <c>Object</c> reference; an unresolvable collection item
    /// or dictionary entry; a dictionary entry that carries no key; and the attribute updates of a subject
    /// the update created that never entered the graph. One logically unresolvable subject can bump the
    /// counter more than once in a single apply, so read it as a rate that should settle, not as a count
    /// of distinct lost subjects.
    /// </remarks>
    public static long DroppedInboundSubjectUpdates => Volatile.Read(ref _droppedInboundSubjectUpdates);

    /// <summary>
    /// Inbound property and attribute updates skipped because the receiver's subject does not declare the
    /// named property. Counted per property update, so one model-drift mismatch on a frequently changing
    /// property increments on every update that carries it. This is the model-drift signal between
    /// sender and receiver.
    /// </summary>
    public static long UnknownInboundProperties => Volatile.Read(ref _unknownInboundProperties);

    internal static void RecordDroppedOutboundChange() => Interlocked.Increment(ref _droppedOutboundChanges);

    internal static void RecordMetadataFallbackSerialization() => Interlocked.Increment(ref _metadataFallbackSerializations);

    internal static void RecordDroppedInboundSubjectUpdate() => Interlocked.Increment(ref _droppedInboundSubjectUpdates);

    internal static void RecordUnknownInboundProperty() => Interlocked.Increment(ref _unknownInboundProperties);
}
