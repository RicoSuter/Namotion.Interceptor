using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

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
    public Action<PropertyReference, SubjectPropertyUpdate>? TransformValueBeforeApply { get; private set; }

    /// <summary>
    /// The subject registry from the root subject's context.
    /// Stored here so that newly created subjects (whose contexts may not yet have services
    /// resolved via fallback) don't need to look up the registry themselves.
    /// </summary>
    public ISubjectRegistry SubjectRegistry { get; private set; } = null!;

    /// <summary>
    /// The subject ID registry from the root subject's context.
    /// Stored here for the same reason as <see cref="SubjectRegistry"/>.
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
        Action<PropertyReference, SubjectPropertyUpdate>? transformValueBeforeApply)
    {
        Subjects = subjects;
        _completeSubjectIds = completeSubjectIds;
        SubjectFactory = subjectFactory;
        TransformValueBeforeApply = transformValueBeforeApply;
        SubjectRegistry = rootContext.GetService<ISubjectRegistry>();
        SubjectIdRegistry = rootContext.GetService<ISubjectIdRegistry>();
    }

    /// <summary>
    /// Pre-resolves all subject IDs to their instances using the live registry.
    /// Must be called before structural changes are applied, so that subjects
    /// concurrently detached by the mutation engine can still be found in Step 2.
    /// The batch scope handles applier-path races (same thread); this handles
    /// concurrent-mutation races (different thread).
    /// </summary>
    public void PreResolveSubjects(
        IEnumerable<string> subjectIds,
        ISubjectIdRegistry idRegistry)
    {
        foreach (var subjectId in subjectIds)
        {
            if (idRegistry.TryGetSubjectById(subjectId, out var subject))
            {
                _preResolvedSubjects[subjectId] = subject;
            }
        }
    }

    /// <summary>
    /// Tries to resolve a subject by ID. Checks the pre-resolved cache first
    /// (captured before structural changes), then falls back to the live registry
    /// (for subjects created during the apply, e.g., by structural processing).
    /// </summary>
    public bool TryResolveSubject(string subjectId, out IInterceptorSubject subject)
    {
        if (_preResolvedSubjects.TryGetValue(subjectId, out subject!))
        {
            return true;
        }

        return SubjectIdRegistry.TryGetSubjectById(subjectId, out subject!);
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
        TransformValueBeforeApply = null;
        SubjectRegistry = null!;
        SubjectIdRegistry = null!;
    }
}
