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

    public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; private set; } = null!;
    public ISubjectFactory SubjectFactory { get; private set; } = null!;
    public Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? TransformValueBeforeApply { get; private set; }

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

    public void Initialize(
        IInterceptorSubjectContext rootContext,
        Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects,
        ISubjectFactory subjectFactory,
        Action<RegisteredSubjectProperty, SubjectPropertyUpdate>? transformValueBeforeApply)
    {
        Subjects = subjects;
        SubjectFactory = subjectFactory;
        TransformValueBeforeApply = transformValueBeforeApply;
        SubjectRegistry = rootContext.GetService<ISubjectRegistry>();
        SubjectIdRegistry = rootContext.GetService<ISubjectIdRegistry>();
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
        SubjectRegistry = null!;
        SubjectIdRegistry = null!;
    }
}
