using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Context for building a SubjectUpdate. Tracks IDs, subjects, and transformations.
/// Designed to be pooled and reused.
/// </summary>
internal sealed class SubjectUpdateFactoryContext
{
    private int _nextId;
    private readonly Dictionary<IInterceptorSubject, string> _subjectToId = new();
    private readonly Dictionary<SubjectPropertyUpdate, (RegisteredSubjectProperty Property, IDictionary<string, SubjectPropertyUpdate> Parent)> _propertyUpdates = new();

    public ISubjectUpdateProcessor[] Processors { get; private set; } = [];
    public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; private set; } = new();
    public HashSet<IInterceptorSubject> ProcessedSubjects { get; } = [];

    public void Initialize(ReadOnlySpan<ISubjectUpdateProcessor> processors)
    {
        Processors = processors.ToArray();
    }

    public string GetOrCreateId(IInterceptorSubject subject)
    {
        if (!_subjectToId.TryGetValue(subject, out var id))
        {
            id = (++_nextId).ToString();
            _subjectToId[subject] = id;
        }
        return id;
    }

    public Dictionary<string, SubjectPropertyUpdate> GetOrCreateProperties(string subjectId)
    {
        if (!Subjects.TryGetValue(subjectId, out var properties))
        {
            properties = new Dictionary<string, SubjectPropertyUpdate>();
            Subjects[subjectId] = properties;
        }
        return properties;
    }

    public bool SubjectHasUpdates(IInterceptorSubject subject)
    {
        if (_subjectToId.TryGetValue(subject, out var id))
        {
            return Subjects.TryGetValue(id, out var props) && props.Count > 0;
        }
        return false;
    }

    public void TrackPropertyUpdate(
        SubjectPropertyUpdate update,
        RegisteredSubjectProperty property,
        IDictionary<string, SubjectPropertyUpdate> parent)
    {
        if (Processors.Length > 0)
        {
            _propertyUpdates[update] = (property, parent);
        }
    }

    public void ApplyTransformations()
    {
        if (Processors.Length == 0)
            return;

        foreach (var (update, info) in _propertyUpdates)
        {
            for (var i = 0; i < Processors.Length; i++)
            {
                var transformed = Processors[i].TransformSubjectPropertyUpdate(info.Property, update);
                if (transformed != update)
                {
                    info.Parent[info.Property.Name] = transformed;
                }
            }
        }
    }

    /// <summary>
    /// Clears the context for reuse. Call before returning to pool.
    /// </summary>
    public void Clear()
    {
        _nextId = 0;
        _subjectToId.Clear();
        _propertyUpdates.Clear();
        ProcessedSubjects.Clear();
        Subjects = new(); // create a fresh dictionary, old one transferred to result
        Processors = [];
    }
}
