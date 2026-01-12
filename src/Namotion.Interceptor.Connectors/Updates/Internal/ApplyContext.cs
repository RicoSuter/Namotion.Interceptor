using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Context for applying a SubjectUpdate. Tracks processed subjects to prevent cycles.
/// Designed to be pooled and reused.
/// </summary>
internal sealed class ApplyContext
{
    private readonly HashSet<string> _processedSubjectIds = [];

    public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; private set; } = null!;
    public ISubjectFactory SubjectFactory { get; private set; } = null!;
    public Action<RegisteredSubjectProperty, SubjectPropertyUpdate> ApplyValue { get; private set; } = null!;

    public void Initialize(
        Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects,
        ISubjectFactory subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate> applyValue)
    {
        Subjects = subjects;
        SubjectFactory = subjectFactory;
        ApplyValue = applyValue;
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
        ApplyValue = null!;
    }
}
