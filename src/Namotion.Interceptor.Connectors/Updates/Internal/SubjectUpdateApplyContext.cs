using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Context for applying a SubjectUpdate. Binds the subjects an update names and tracks which payloads were
/// applied. Designed to be pooled and reused.
/// </summary>
internal sealed class SubjectUpdateApplyContext
{
    private readonly Dictionary<string, IInterceptorSubject> _boundSubjects = [];
    private readonly HashSet<string> _processedSubjectIds = [];
    private readonly Queue<(IInterceptorSubject Subject, string SubjectId, string PropertyName, Dictionary<string, SubjectPropertyUpdate> Attributes)> _deferredAttributeUpdates = [];
    private IInterceptorSubject _rootSubject = null!;
    private HashSet<string>? _completeSubjectIds;
    private HashSet<string>? _ignoredSubjectIds;
    private HashSet<string>? _namedSubjectIds;
    private bool _areNamedSubjectIdsCollected;
    private List<(PropertyReference Property, Exception Exception)>? _failures;
    private List<(Type SubjectType, string PropertyName)>? _droppedStructuralProperties;

    public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; private set; } = null!;
    public ISubjectFactory SubjectFactory { get; private set; } = null!;
    public ChangeOrigin Origin { get; private set; }
    public Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? TransformValueBeforeApply { get; private set; }

    /// <summary>
    /// The subject ID registry of the root subject's context, which resolves the subjects this update names.
    /// </summary>
    public ISubjectIdRegistry SubjectIdRegistry { get; private set; } = null!;

    /// <summary>
    /// The service provider of the root subject's context, which constructs the subjects this update creates.
    /// </summary>
    public IServiceProvider? ServiceProvider { get; private set; }

    public void Initialize(
        IInterceptorSubject rootSubject,
        SubjectUpdate update,
        ISubjectFactory subjectFactory,
        ChangeOrigin origin,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply)
    {
        var rootContext = rootSubject.Context;
        _rootSubject = rootSubject;
        Subjects = update.Subjects;
        _completeSubjectIds = update.CompleteSubjectIds;
        SubjectFactory = subjectFactory;
        Origin = origin;
        TransformValueBeforeApply = transformValueBeforeApply;
        SubjectIdRegistry = rootContext.GetService<ISubjectIdRegistry>();
        ServiceProvider = rootContext.TryGetService<IServiceProvider>();
    }

    /// <summary>
    /// Returns true if the subject ID has complete state in this update.
    /// null means all subjects are complete (e.g., a full initial-state update).
    /// </summary>
    public bool IsSubjectComplete(string subjectId)
        => _completeSubjectIds is null || _completeSubjectIds.Contains(subjectId);

    /// <summary>
    /// Binds <paramref name="subjectId"/> to <paramref name="subject"/> for the rest of this apply, which a
    /// subject the registry cannot resolve by that ID needs: the local root, whose ID differs from the
    /// sender's, a created subject until it is rooted, and a subject that adopts the ID.
    /// </summary>
    /// <remarks>
    /// Every later reference to the ID within this apply has to resolve to the bound instance, including one
    /// inside the bound subject's own payload. A reference that misses would create a second instance which
    /// never receives the ID's payload, because the first instance consumed it.
    /// </remarks>
    public void BindSubject(string subjectId, IInterceptorSubject subject)
        => _boundSubjects[subjectId] = subject;

    /// <summary>Resolves a subject bound by this apply, then through the registry.</summary>
    public bool TryResolveSubject(string subjectId, out IInterceptorSubject subject)
        => _boundSubjects.TryGetValue(subjectId, out subject!) || SubjectIdRegistry.TryGetSubjectById(subjectId, out subject!);

    /// <summary>
    /// Creates the subject <paramref name="subjectId"/> names, binds it and applies its payload, or returns
    /// <c>null</c> and counts the drop when the update does not mark it complete.
    /// </summary>
    /// <exception cref="InvalidOperationException">The update marks the subject complete but carries no payload for it.</exception>
    public IInterceptorSubject? TryCreateSubject(string subjectId, Type type)
    {
        if (!IsSubjectComplete(subjectId))
        {
            // The sender assumes the receiver already holds the subject. A subject created from nothing would
            // stay default-valued, because the sender does not resend its state; the next update that
            // completes it heals the gap.
            RecordDroppedSubject(subjectId);
            return null;
        }

        if (!Subjects.TryGetValue(subjectId, out var properties))
        {
            throw new InvalidOperationException(
                $"The update references subject '{subjectId}' as complete but carries no properties for it.");
        }

        var subject = SubjectFactory.CreateSubject(type, ServiceProvider);
        subject.SetSubjectId(subjectId);
        BindSubject(subjectId, subject);
        _processedSubjectIds.Add(subjectId);

        // Populated before it enters the graph, so that a concurrent reader never observes it partly applied.
        // A property failing after this leaves it bound, so a later reference to the ID still roots it.
        SubjectUpdateApplier.ApplyPropertyUpdates(subject, subjectId, properties, this);
        return subject;
    }

    /// <summary>
    /// Binds <paramref name="subjectId"/>, which resolves to no subject here, to <paramref name="subject"/>, a
    /// subject held by a property this model cannot write, and gives the subject that ID in place of any it has.
    /// Returns <c>false</c> when this apply already names the subject by another ID.
    /// </summary>
    public bool TryAdoptSubject(string subjectId, IInterceptorSubject subject)
    {
        var existingId = subject.TryGetSubjectId();
        if (existingId != subjectId)
        {
            // The sender still uses an ID it names anywhere in the update for another subject.
            if (ReferenceEquals(subject, _rootSubject) || (existingId is not null && IsNamed(existingId)))
            {
                return false;
            }

            // The receiver may have given the subject an ID of its own, or the sender restarted with new IDs.
            subject.ReplaceSubjectId(subjectId);
        }

        BindSubject(subjectId, subject);
        return true;
    }

    /// <summary>
    /// Whether the update names <paramref name="subjectId"/>: as an entry, a reference or an item.
    /// </summary>
    private bool IsNamed(string subjectId)
    {
        var namedSubjectIds = _namedSubjectIds ??= [];
        if (!_areNamedSubjectIdsCollected)
        {
            _areNamedSubjectIdsCollected = true;
            foreach (var (entrySubjectId, properties) in Subjects)
            {
                namedSubjectIds.Add(entrySubjectId);
                foreach (var (_, propertyUpdate) in properties)
                {
                    AddNamedSubjects(namedSubjectIds, propertyUpdate);
                }
            }
        }

        return namedSubjectIds.Contains(subjectId);
    }

    private static void AddNamedSubjects(HashSet<string> namedSubjectIds, SubjectPropertyUpdate propertyUpdate)
    {
        if (propertyUpdate.Id is { } subjectId)
        {
            namedSubjectIds.Add(subjectId);
        }

        if (propertyUpdate.Items is { } items)
        {
            foreach (var item in items)
            {
                namedSubjectIds.Add(item.Id);
            }
        }

        if (propertyUpdate.Attributes is { } attributes)
        {
            foreach (var (_, attributeUpdate) in attributes)
            {
                AddNamedSubjects(namedSubjectIds, attributeUpdate);
            }
        }
    }

    /// <summary>
    /// Applies the payload <paramref name="subjectId"/> carries to <paramref name="subject"/>, unless this
    /// apply already applied it.
    /// </summary>
    public void ApplySubjectPayload(IInterceptorSubject subject, string subjectId)
    {
        if (Subjects.TryGetValue(subjectId, out var properties) && TryMarkAsProcessed(subjectId))
        {
            SubjectUpdateApplier.ApplyPropertyUpdates(subject, subjectId, properties, this);
        }
    }

    /// <summary>
    /// Records the subjects <paramref name="propertyUpdate"/> names, and those their entries name in turn, as
    /// ignored: named by a property this model does not declare, an entry left unapplied is no drop.
    /// </summary>
    public void IgnoreNamedSubjects(SubjectPropertyUpdate propertyUpdate)
    {
        if (propertyUpdate.Id is { } subjectId)
        {
            IgnoreSubject(subjectId);
        }

        if (propertyUpdate.Items is { } items)
        {
            foreach (var item in items)
            {
                IgnoreSubject(item.Id);
            }
        }

        if (propertyUpdate.Attributes is { } attributes)
        {
            foreach (var (_, attributeUpdate) in attributes)
            {
                IgnoreNamedSubjects(attributeUpdate);
            }
        }
    }

    private void IgnoreSubject(string subjectId)
    {
        if ((_ignoredSubjectIds ??= []).Add(subjectId) && Subjects.TryGetValue(subjectId, out var properties))
        {
            foreach (var (_, propertyUpdate) in properties)
            {
                IgnoreNamedSubjects(propertyUpdate);
            }
        }
    }

    public bool IsIgnored(string subjectId) => _ignoredSubjectIds?.Contains(subjectId) == true;

    public bool TryMarkAsProcessed(string subjectId)
        => _processedSubjectIds.Add(subjectId);

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="property"/> under the update's origin,
    /// using the written value as the origin's sent-value evidence. See the overload taking a
    /// separate <c>sentValue</c> for the case where the applied value was locally transformed.
    /// </summary>
    public void SetPropertyValue(PropertyReference property, DateTimeOffset? changedTimestamp, object? value)
        => SetPropertyValue(property, changedTimestamp, value, value);

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="property"/> under the update's origin.
    /// Local origins keep the unarmed write path (Local is the default and needs no stamp); for
    /// FromSource and Confirmed origins the write goes through SetValueFromOrigin so the resulting
    /// change carries the source and echo suppression works. <paramref name="sentValue"/> is the
    /// value the source semantically sent, armed as the origin's survival evidence: when a
    /// transform corrects the applied value it differs from <paramref name="value"/>, so the
    /// origin demotes to Local and the correction is not echo-suppressed back to the source.
    /// In all cases <paramref name="changedTimestamp"/> is applied as the changed timestamp so
    /// the inbound timestamp is never replaced with capture-time now.
    /// </summary>
    public void SetPropertyValue(PropertyReference property, DateTimeOffset? changedTimestamp, object? value, object? sentValue)
    {
        if (Origin.Kind == ChangeOriginKind.Local)
        {
            using (SubjectChangeContext.WithChangedTimestamp(changedTimestamp))
            {
                property.Metadata.SetValue?.Invoke(property.Subject, value);
            }
        }
        else
        {
            property.SetValueFromOrigin(Origin, changedTimestamp, null, value, sentValue);
        }
    }

    /// <summary>
    /// Queues the attribute updates of a subject the registry does not know yet, to be applied once the
    /// update has rooted it.
    /// </summary>
    public void DeferAttributeUpdates(
        IInterceptorSubject subject, string subjectId, string propertyName, Dictionary<string, SubjectPropertyUpdate> attributes)
        => _deferredAttributeUpdates.Enqueue((subject, subjectId, propertyName, attributes));

    public bool TryDequeueDeferredAttributeUpdates(
        out (IInterceptorSubject Subject, string SubjectId, string PropertyName, Dictionary<string, SubjectPropertyUpdate> Attributes) entry)
        => _deferredAttributeUpdates.TryDequeue(out entry);

    /// <summary>
    /// Records an inbound subject that could not be resolved and was left out of the apply. Every drop of
    /// an inbound subject goes through here.
    /// </summary>
    public void RecordDroppedSubject(string subjectId)
        => SubjectUpdateDiagnostics.RecordDroppedInboundSubjectUpdate();

    /// <summary>
    /// Records a property whose structure this model cannot store because it has no setter, and ignores the
    /// subjects it names, which are no drop: nothing here could hold them. The caller reports the properties in
    /// one warning once the whole update was applied.
    /// </summary>
    public void DropStructure(PropertyReference property, SubjectPropertyUpdate propertyUpdate)
    {
        IgnoreNamedSubjects(propertyUpdate);

        var droppedProperty = (property.Subject.GetType(), property.Name);
        var droppedProperties = _droppedStructuralProperties ??= [];
        if (!droppedProperties.Contains(droppedProperty))
        {
            droppedProperties.Add(droppedProperty);
        }
    }

    /// <summary>The properties whose structure was dropped, or <c>null</c> when none was.</summary>
    public List<(Type SubjectType, string PropertyName)>? DroppedStructuralProperties => _droppedStructuralProperties;

    /// <summary>
    /// Records a property that could not be applied. The batch continues; the collected failures are
    /// thrown once by the caller when the whole update has been walked.
    /// </summary>
    public void RecordFailure(PropertyReference property, Exception exception)
        => (_failures ??= []).Add((property, exception));

    /// <summary>The failures recorded so far, or <c>null</c> when every property applied.</summary>
    public List<(PropertyReference Property, Exception Exception)>? Failures => _failures;

    /// <summary>
    /// Clears the context for reuse. Call before returning to pool.
    /// </summary>
    public void Clear()
    {
        _boundSubjects.Clear();
        _processedSubjectIds.Clear();
        _deferredAttributeUpdates.Clear();
        _rootSubject = null!;
        _completeSubjectIds = null;
        _ignoredSubjectIds?.Clear();
        _namedSubjectIds?.Clear();
        _areNamedSubjectIdsCollected = false;
        _failures = null;
        _droppedStructuralProperties = null;
        Subjects = null!;
        SubjectFactory = null!;
        Origin = default;
        TransformValueBeforeApply = null;
        SubjectIdRegistry = null!;
        ServiceProvider = null;
    }
}
