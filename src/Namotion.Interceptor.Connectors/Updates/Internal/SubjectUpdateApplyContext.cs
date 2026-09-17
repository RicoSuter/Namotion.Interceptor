using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Context for applying a SubjectUpdate. Binds the subjects an update names, which resolves repeated
/// references and prevents cycles. Designed to be pooled and reused.
/// </summary>
internal sealed class SubjectUpdateApplyContext
{
    private readonly Dictionary<string, IInterceptorSubject> _subjectsById = [];
    private readonly HashSet<(string Id, IInterceptorSubject Subject)> _claimedPayloads = new(PayloadClaimComparer.Instance);
    private List<(RegisteredSubjectProperty Property, Exception Exception)>? _failures;
    private List<string>? _droppedStructuralProperties;

    public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; private set; } = null!;
    public ISubjectFactory SubjectFactory { get; private set; } = null!;
    public ChangeOrigin Origin { get; private set; }
    public Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? TransformValueBeforeApply { get; private set; }

    public void Initialize(
        Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects,
        ISubjectFactory subjectFactory,
        ChangeOrigin origin,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply)
    {
        Subjects = subjects;
        SubjectFactory = subjectFactory;
        Origin = origin;
        TransformValueBeforeApply = transformValueBeforeApply;
    }

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="property"/> under the update's origin, using
    /// the written value as the origin's sent-value evidence. See the overload taking a separate
    /// <c>sentValue</c> for the case where the applied value was locally transformed.
    /// </summary>
    public void SetPropertyValue(RegisteredSubjectProperty property, DateTimeOffset? changedTimestamp, object? value)
        => SetPropertyValue(property, changedTimestamp, value, value);

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="property"/> under the update's origin. Local
    /// origins keep the unarmed write path (Local is the default and needs no stamp); for
    /// <see cref="ChangeOriginKind.FromSource"/> and <see cref="ChangeOriginKind.Confirmed"/> the write
    /// goes through <see cref="SubjectChangeContextExtensions.SetValueFromOrigin(PropertyReference, ChangeOrigin, DateTimeOffset?, DateTimeOffset?, object?, object?)"/> so the resulting
    /// change carries the source and echo suppression works. <paramref name="sentValue"/> is the value
    /// the source semantically sent, armed as the origin's survival evidence: when a transform corrects
    /// the applied value it differs from <paramref name="value"/>, so the origin demotes to Local and the
    /// correction is not echo-suppressed back to the source. In all cases <paramref name="changedTimestamp"/>
    /// is applied as the changed timestamp so the inbound timestamp is never replaced with capture-time now.
    /// </summary>
    public void SetPropertyValue(RegisteredSubjectProperty property, DateTimeOffset? changedTimestamp, object? value, object? sentValue)
    {
        if (Origin.Kind == ChangeOriginKind.Local)
        {
            using (SubjectChangeContext.WithChangedTimestamp(changedTimestamp))
            {
                property.SetValue(value);
            }
        }
        else
        {
            property.Reference.SetValueFromOrigin(Origin, changedTimestamp, null, value, sentValue);
        }
    }

    public Dictionary<string, SubjectPropertyUpdate> GetSubjectProperties(string subjectId)
        => Subjects.TryGetValue(subjectId, out var properties)
            ? properties
            : throw new InvalidOperationException($"Subject update references missing subject '{subjectId}'.");

    /// <summary>
    /// Claims the payload of an ID for <paramref name="subject"/>: binds the subject the ID names while
    /// the ID is still unbound, and reports whether <paramref name="subject"/> still has to receive that
    /// payload.
    /// </summary>
    /// <remarks>
    /// An ID already bound to a <em>different</em> instance still yields <c>true</c>, which is the
    /// documented rule for a shared subject: a position that already holds another instance keeps it and
    /// receives the payload. Only the exact subject that already took this ID's payload is turned away,
    /// so a cycle terminates and no instance applies one payload twice.
    /// </remarks>
    public bool TryClaimSubjectPayload(string subjectId, IInterceptorSubject subject)
    {
        if (!_claimedPayloads.Add((subjectId, subject)))
            return false;

        // The first binding wins: it is what later references to this ID resolve to, so a position
        // holding another instance must not steal the ID from the subject that already carries it.
        _subjectsById.TryAdd(subjectId, subject);
        return true;
    }

    /// <summary>
    /// Gets the subject already bound to an ID in this update, or <c>null</c> while it is unbound. IDs are
    /// scoped to one update, so nothing bound here may outlive it.
    /// </summary>
    public IInterceptorSubject? TryGetBoundSubject(string subjectId)
        => _subjectsById.GetValueOrDefault(subjectId);

    /// <summary>
    /// Records a property that could not be applied. The batch continues; the collected failures are
    /// thrown once by the caller when the whole update has been walked.
    /// </summary>
    public void RecordFailure(RegisteredSubjectProperty property, Exception exception)
        => (_failures ??= []).Add((property, exception));

    /// <summary>The failures recorded so far, or <c>null</c> when every property applied.</summary>
    public List<(RegisteredSubjectProperty Property, Exception Exception)>? Failures => _failures;

    /// <summary>
    /// Records a property whose structural payload had nowhere to go because this model cannot write it.
    /// Unlike a dropped value, nothing makes such a child appear later, so the caller reports these once
    /// the whole update has been walked.
    /// </summary>
    public void RecordDroppedStructure(RegisteredSubjectProperty property)
        => (_droppedStructuralProperties ??= []).Add(property.Name);

    /// <summary>
    /// The properties whose structural payload was dropped, or <c>null</c> while none was.
    /// </summary>
    public List<string>? DroppedStructuralProperties => _droppedStructuralProperties;

    /// <summary>
    /// Clears the context for reuse. Call before returning to pool.
    /// </summary>
    public void Clear()
    {
        _subjectsById.Clear();
        _claimedPayloads.Clear();
        _failures = null;
        _droppedStructuralProperties = null;
        Subjects = null!;
        SubjectFactory = null!;
        Origin = default;
        TransformValueBeforeApply = null;
    }

    /// <summary>
    /// Compares payload claims by ID and by subject <em>identity</em>. A subject may override equality,
    /// so the default comparer would let two equal but distinct instances share one claim and silently
    /// skip the second one's payload.
    /// </summary>
    private sealed class PayloadClaimComparer : IEqualityComparer<(string Id, IInterceptorSubject Subject)>
    {
        public static readonly PayloadClaimComparer Instance = new();

        public bool Equals((string Id, IInterceptorSubject Subject) first, (string Id, IInterceptorSubject Subject) second)
            => ReferenceEquals(first.Subject, second.Subject) && first.Id == second.Id;

        public int GetHashCode((string Id, IInterceptorSubject Subject) value)
            => HashCode.Combine(value.Id, RuntimeHelpers.GetHashCode(value.Subject));
    }
}
