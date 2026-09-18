using System.Runtime.InteropServices;
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
    private Dictionary<(string Id, Type Type), IInterceptorSubject>? _subjectsByIdAndType;

    // By identity, because a subject may override Equals and two equal but distinct instances each take a payload.
    private readonly Dictionary<IInterceptorSubject, string> _claimedIdsBySubject = new(ReferenceEqualityComparer.Instance);

    private List<(RegisteredSubjectProperty Property, Exception Exception)>? _failures;
    private List<(Type SubjectType, string PropertyName)>? _droppedStructuralProperties;

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
    /// Claims the payload of an ID for <paramref name="subject"/>, and binds the subject the ID names while
    /// the ID is still unbound. A subject takes the payload of at most one ID per update. Pass
    /// <paramref name="createdForType"/> for a subject created for a position of that type, so it is bound
    /// for that type when the ID already names a subject that does not fit it.
    /// </summary>
    /// <remarks>
    /// An ID already bound to a <em>different</em> instance still yields <see cref="SubjectPayloadClaim.Claimed"/>,
    /// which is the documented rule for a shared subject: a position that already holds another instance
    /// keeps it and receives the payload, unless that instance already took another ID's payload.
    /// <see cref="SubjectPayloadClaim.AlreadyClaimed"/> is what terminates a cycle.
    /// </remarks>
    public SubjectPayloadClaim ClaimSubjectPayload(string subjectId, IInterceptorSubject subject, Type? createdForType = null)
    {
        ref var claimedId = ref CollectionsMarshal.GetValueRefOrAddDefault(_claimedIdsBySubject, subject, out var exists);
        if (exists)
        {
            return claimedId == subjectId
                ? SubjectPayloadClaim.AlreadyClaimed
                : SubjectPayloadClaim.ClaimedByAnotherId;
        }

        claimedId = subjectId;

        // The first binding wins: it is what later references to this ID resolve to, so a position
        // holding another instance must not steal the ID from the subject that already carries it.
        // A subject created only because that one does not fit its type is bound for the type before
        // its payload is applied, or a reference to the same ID and type inside that payload would
        // create another instance on every level without end.
        if (!_subjectsById.TryAdd(subjectId, subject) && createdForType is not null)
        {
            (_subjectsByIdAndType ??= []).TryAdd((subjectId, createdForType), subject);
        }

        return SubjectPayloadClaim.Claimed;
    }

    /// <summary>Whether an ID is already bound to a subject in this update.</summary>
    public bool IsBound(string subjectId)
        => _subjectsById.ContainsKey(subjectId);

    /// <summary>
    /// Gets the subject an ID is bound to for a position of <paramref name="declaredType"/>, or <c>null</c>
    /// when none fits yet. One ID names one subject within an update, so a later reference resolves to
    /// that instance rather than a copy. Where that instance does not fit the position, any instance
    /// already created for the ID that does fit is used, so each ID yields at most one instance per
    /// declared type. IDs are scoped to one update, so nothing bound here may outlive it.
    /// </summary>
    public IInterceptorSubject? TryGetBoundSubject(string subjectId, Type declaredType)
    {
        var subject = _subjectsById.GetValueOrDefault(subjectId);
        if (subject is null || declaredType.IsInstanceOfType(subject))
            return subject;

        if (_subjectsByIdAndType is null)
            return null;

        if (_subjectsByIdAndType.TryGetValue((subjectId, declaredType), out subject))
            return subject;

        // An instance created for a more derived type also fits this one.
        foreach (var ((id, _), candidate) in _subjectsByIdAndType)
        {
            if (id == subjectId && declaredType.IsInstanceOfType(candidate))
                return candidate;
        }

        return null;
    }

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
    /// the whole update has been walked. A property that several instances of one type drop is recorded once.
    /// </summary>
    public void RecordDroppedStructure(RegisteredSubjectProperty property)
    {
        var droppedProperty = (property.Subject.GetType(), property.Name);
        var droppedProperties = _droppedStructuralProperties ??= [];
        if (!droppedProperties.Contains(droppedProperty))
        {
            droppedProperties.Add(droppedProperty);
        }
    }

    /// <summary>
    /// The properties whose structural payload was dropped, each with the type of the subject that owns
    /// it, or <c>null</c> while none was.
    /// </summary>
    public List<(Type SubjectType, string PropertyName)>? DroppedStructuralProperties => _droppedStructuralProperties;

    /// <summary>
    /// Clears the context for reuse. Call before returning to pool.
    /// </summary>
    public void Clear()
    {
        _subjectsById.Clear();
        _subjectsByIdAndType?.Clear();
        _claimedIdsBySubject.Clear();
        _failures = null;
        _droppedStructuralProperties = null;
        Subjects = null!;
        SubjectFactory = null!;
        Origin = default;
        TransformValueBeforeApply = null;
    }
}
