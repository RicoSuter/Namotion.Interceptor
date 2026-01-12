using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Context for applying a SubjectUpdate. Tracks processed subjects to prevent cycles.
/// Designed to be pooled and reused.
/// </summary>
internal sealed class SubjectUpdateApplyContext
{
    private readonly HashSet<string> _processedSubjectIds = [];

    public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; private set; } = null!;
    public ISubjectFactory SubjectFactory { get; private set; } = null!;
    public Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? TransformValueBeforeApply { get; private set; }

    public void Initialize(
        Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects,
        ISubjectFactory subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply)
    {
        Subjects = subjects;
        SubjectFactory = subjectFactory;
        TransformValueBeforeApply = transformValueBeforeApply;
    }

    public bool TryMarkAsProcessed(string subjectId)
        => _processedSubjectIds.Add(subjectId);

    /// <summary>
    /// Clears the context for reuse. Call before returning to pool.
    /// </summary>
    public void Clear()
    {
        _processedSubjectIds.Clear();
        Subjects = null!;
        SubjectFactory = null!;
        TransformValueBeforeApply = null;
    }
}
