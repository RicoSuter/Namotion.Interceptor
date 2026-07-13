namespace Namotion.Interceptor;

/// <summary>
/// The kind of origin of a property change. Byte-backed deliberately: inside
/// <c>SubjectPropertyChange</c> the runtime can fold a byte into padding where a wider
/// enum could grow the struct. Do not widen.
/// </summary>
public enum ChangeOriginKind : byte
{
    /// <summary>
    /// The value was computed locally: user writes, hook cascades, INPC handler write-backs,
    /// and derived recalculations. Local changes flow to every bound source.
    /// </summary>
    Local = 0,

    /// <summary>
    /// The stored value is exactly the value an external source sent in an inbound update.
    /// The outbound queue skips the change for that source (echo suppression).
    /// </summary>
    FromSource = 1,

    /// <summary>
    /// The stored value was acknowledged by the source through a transaction commit.
    /// Skipped for the confirming source, delivered to all other bound sources.
    /// </summary>
    Confirmed = 2,

    /// <summary>
    /// An outbound synchronization synthesized because a source silently dropped the model's value:
    /// an inbound value the equality check suppressed while it still diverged from the stored value.
    /// The source field records which source diverged. A correction carries equal old and new values,
    /// is not a model change, and is delivered only through the change queue, never the observable.
    /// </summary>
    Correction = 3,
}

/// <summary>
/// Typed provenance of a property change. A change carries a source only when its stored
/// value is exactly the value that source sent (<see cref="ChangeOriginKind.FromSource"/>)
/// or confirmed (<see cref="ChangeOriginKind.Confirmed"/>). Everything else is
/// <see cref="ChangeOriginKind.Local"/>. The default value is Local.
/// </summary>
public readonly struct ChangeOrigin
{
    public ChangeOriginKind Kind { get; }

    /// <summary>Non-null exactly when <see cref="Kind"/> is not Local.</summary>
    public object? Source { get; }

    private ChangeOrigin(ChangeOriginKind kind, object? source)
    {
        Kind = kind;
        Source = source;
    }

    public static ChangeOrigin Local => default;

    public static ChangeOrigin FromSource(object source) =>
        new(ChangeOriginKind.FromSource, source ?? throw new ArgumentNullException(nameof(source)));

    /// <summary>
    /// Only transaction commit replay may stamp Confirmed; stamping it elsewhere claims an
    /// acknowledgment the source never gave and breaks echo semantics.
    /// </summary>
    public static ChangeOrigin Confirmed(object source) =>
        new(ChangeOriginKind.Confirmed, source ?? throw new ArgumentNullException(nameof(source)));

    /// <summary>
    /// A synthesized outbound synchronization for a value the given source silently dropped. Produced
    /// only by correction detection; the source records which participant diverged.
    /// </summary>
    public static ChangeOrigin Correction(object source) =>
        new(ChangeOriginKind.Correction, source ?? throw new ArgumentNullException(nameof(source)));
}
