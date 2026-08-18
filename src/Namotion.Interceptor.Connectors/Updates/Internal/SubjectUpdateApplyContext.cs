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
    /// Tries to resolve a subject by ID. Checks the pre-resolved cache first (captured before
    /// structural changes), then falls back to the live registry (for subjects created during
    /// the apply, e.g., by structural processing).
    /// </summary>
    public bool TryResolveSubject(string subjectId, out IInterceptorSubject subject)
    {
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
    /// Clears the context for reuse. Call before returning to pool.
    /// </summary>
    public void Clear()
    {
        _processedSubjectIds.Clear();
        _preResolvedSubjects.Clear();
        _completeSubjectIds = null;
        Subjects = null!;
        SubjectFactory = null!;
        Origin = default;
        TransformValueBeforeApply = null;
        SubjectIdRegistry = null!;
    }
}
