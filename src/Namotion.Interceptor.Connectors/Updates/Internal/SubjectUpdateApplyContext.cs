using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Context for applying a SubjectUpdate. Tracks processed subjects to prevent cycles.
/// Designed to be pooled and reused.
/// </summary>
internal sealed class SubjectUpdateApplyContext
{
    private readonly HashSet<string> _processedSubjectIds = [];
    private readonly Dictionary<string, IInterceptorSubject> _preResolvedSubjects = [];
    private readonly Dictionary<string, IInterceptorSubject> _boundSubjects = [];
    private readonly List<(IInterceptorSubject Subject, Dictionary<string, SubjectPropertyUpdate> Properties)> _deferredAttributeUpdates = [];
    private List<string>? _droppedSubjectIds;

    public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; private set; } = null!;
    public ISubjectFactory SubjectFactory { get; private set; } = null!;
    public ChangeOrigin Origin { get; private set; }
    public Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? TransformValueBeforeApply { get; private set; }

    /// <summary>
    /// The subject ID registry from the root subject's context. Stored here so that newly created
    /// subjects (whose contexts may not yet have services resolved via fallback) don't need to
    /// look up the registry themselves.
    /// </summary>
    public ISubjectIdRegistry SubjectIdRegistry { get; private set; } = null!;

    /// <summary>
    /// The service provider of the root subject's context, used to construct subjects created by
    /// this update. Resolved once from the root because a subject created during the apply has no
    /// fallback context yet, so asking its own context would yield null and downgrade construction
    /// to a parameterless activation, which fails for subject types with a dependency-injected
    /// constructor nested more than one level deep in a single update.
    /// </summary>
    public IServiceProvider? ServiceProvider { get; private set; }

    private HashSet<string>? _completeSubjectIds;

    /// <summary>
    /// Returns true if the subject ID has complete state in this update.
    /// null means all subjects are complete (e.g., a full initial-state update).
    /// </summary>
    public bool IsSubjectComplete(string subjectId)
        => _completeSubjectIds is null || _completeSubjectIds.Contains(subjectId);

    public void Initialize(
        IInterceptorSubjectContext rootContext,
        Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects,
        HashSet<string>? completeSubjectIds,
        ISubjectFactory subjectFactory,
        ChangeOrigin origin,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply)
    {
        Subjects = subjects;
        _completeSubjectIds = completeSubjectIds;
        SubjectFactory = subjectFactory;
        Origin = origin;
        TransformValueBeforeApply = transformValueBeforeApply;
        SubjectIdRegistry = rootContext.GetService<ISubjectIdRegistry>();
        ServiceProvider = rootContext.TryGetService<IServiceProvider>();
    }

    /// <summary>
    /// Pre-resolves all subject IDs to their instances using the live registry.
    /// Must be called before structural changes are applied, so that subjects
    /// concurrently detached by another mutation can still be found afterwards.
    /// </summary>
    public void PreResolveSubjects(IEnumerable<string> subjectIds)
    {
        foreach (var subjectId in subjectIds)
        {
            if (SubjectIdRegistry.TryGetSubjectById(subjectId, out var subject))
            {
                _preResolvedSubjects[subjectId] = subject;
            }
        }
    }

    /// <summary>
    /// Binds <paramref name="subjectId"/> to <paramref name="subject"/> for the rest of this apply.
    /// </summary>
    /// <remarks>
    /// Two kinds of subject are bound: the ones this apply creates, and the local root subject, bound
    /// to the update's <see cref="SubjectUpdate.Root"/> mapping hint. Neither is reachable through the
    /// ID registry under the update's ID. A created subject is not registered until its subtree is
    /// rooted, which happens after it is populated, and the sender's root ID is not the receiver's.
    /// Without this binding a second reference to the same ID inside one apply misses the registry and
    /// fabricates a duplicate instance that is never populated, because the ID's properties were
    /// already consumed by the first instance. Both instances get rooted, the registry keeps the first
    /// one in its reverse index, and the duplicate becomes an invisible, permanently default-valued
    /// node that no later update can reach.
    /// </remarks>
    public void BindSubject(string subjectId, IInterceptorSubject subject)
        => _boundSubjects[subjectId] = subject;

    /// <summary>
    /// Tries to resolve a subject bound by this apply, see <see cref="BindSubject"/>. Callers must
    /// consult this before deciding that an ID is unknown and a subject has to be created for it.
    /// </summary>
    public bool TryGetBoundSubject(string subjectId, out IInterceptorSubject subject)
        => _boundSubjects.TryGetValue(subjectId, out subject!);

    /// <summary>
    /// Tries to resolve a subject by ID. Checks the subjects bound by this apply first (created here
    /// or mapped from the update's root hint, neither of which the registry can resolve), then the
    /// pre-resolved cache (captured before structural changes), then the live registry (for subjects
    /// this apply has already rooted).
    /// </summary>
    public bool TryResolveSubject(string subjectId, out IInterceptorSubject subject)
    {
        if (_boundSubjects.TryGetValue(subjectId, out subject!))
        {
            return true;
        }

        if (_preResolvedSubjects.TryGetValue(subjectId, out subject!))
        {
            return true;
        }

        return SubjectIdRegistry.TryGetSubjectById(subjectId, out subject!);
    }

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

    public bool TryMarkAsProcessed(string subjectId)
        => _processedSubjectIds.Add(subjectId);

    /// <summary>
    /// The subject IDs this apply could not resolve. Distinct, because one logically missing subject
    /// is reached from more than one site and a caller wants the subjects, not the site count.
    /// </summary>
    public IReadOnlyList<string>? DroppedSubjectIds => _droppedSubjectIds;

    /// <summary>
    /// Records that <paramref name="subjectId"/> could not be resolved and its update was dropped.
    /// </summary>
    public void RecordDroppedSubject(string? subjectId)
    {
        var id = subjectId ?? "(no id)";
        _droppedSubjectIds ??= [];
        if (!_droppedSubjectIds.Contains(id))
        {
            _droppedSubjectIds.Add(id);
        }
    }

    /// <summary>
    /// The attribute updates queued for subjects populated before they entered the graph, in the
    /// order they were queued. Attribute names resolve through the registry, which only knows a
    /// subject once it is rooted, so these are applied after all structural writes have landed.
    /// </summary>
    public IReadOnlyList<(IInterceptorSubject Subject, Dictionary<string, SubjectPropertyUpdate> Properties)> DeferredAttributeUpdates
        => _deferredAttributeUpdates;

    /// <summary>
    /// Queues the attribute updates contained in <paramref name="properties"/> for
    /// <paramref name="subject"/>, to be applied once the subject is rooted.
    /// </summary>
    public void DeferAttributeUpdates(IInterceptorSubject subject, Dictionary<string, SubjectPropertyUpdate> properties)
        => _deferredAttributeUpdates.Add((subject, properties));

    /// <summary>
    /// Clears the context for reuse. Call before returning to pool.
    /// </summary>
    public void Clear()
    {
        _processedSubjectIds.Clear();
        _preResolvedSubjects.Clear();
        _boundSubjects.Clear();
        _deferredAttributeUpdates.Clear();
        _droppedSubjectIds?.Clear();
        _completeSubjectIds = null;
        Subjects = null!;
        SubjectFactory = null!;
        Origin = default;
        TransformValueBeforeApply = null;
        SubjectIdRegistry = null!;
        ServiceProvider = null;
    }
}
