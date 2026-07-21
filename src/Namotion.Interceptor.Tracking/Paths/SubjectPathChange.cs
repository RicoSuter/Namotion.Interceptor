using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>An observable transition of a watched path. <see cref="Cause"/> is the real property write that triggered it (supplementary provenance, not something a consumer must decode).</summary>
public readonly struct SubjectPathChange<TValue>
{
    internal SubjectPathChange(
        SubjectPathChangeKind kind,
        SubjectPathValue<TValue> old,
        SubjectPathValue<TValue> @new,
        in SubjectPropertyChange cause)
    {
        Kind = kind;
        Old = old;
        New = @new;
        Cause = cause;
    }

    public SubjectPathChangeKind Kind { get; }

    /// <summary>Observed state before this event.</summary>
    public SubjectPathValue<TValue> Old { get; }

    /// <summary>Observed state now (a fresh walk, or after a divergent retrack the retrack's reads; never copied from the causing write payload).</summary>
    public SubjectPathValue<TValue> New { get; }

    /// <summary>The real write that triggered this event. <c>Cause.Origin</c> is the trigger's origin verbatim and is deliberately not provenance for <see cref="New"/>.</summary>
    public SubjectPropertyChange Cause { get; }
}
