using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Builder for creating a SubjectUpdate. Tracks IDs, subjects, and transformations.
/// Designed to be pooled and reused.
/// </summary>
internal sealed class SubjectUpdateBuilder
{
    private Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> _subjects = new();

    private readonly Dictionary<IInterceptorSubject, string> _subjectToId = new();
    private readonly Dictionary<
        SubjectPropertyUpdate, (RegisteredSubjectProperty Property, 
        IDictionary<string, SubjectPropertyUpdate> Parent)> _propertyUpdates = new();

    public ISubjectUpdateProcessor[] Processors { get; private set; } = [];

    public HashSet<IInterceptorSubject> ProcessedSubjects { get; } = [];

    public void Initialize(IInterceptorSubject rootSubject, ISubjectUpdateProcessor[] processors)
    {
        Processors = processors;
        GetOrCreateId(rootSubject);
    }

    public string GetOrCreateId(IInterceptorSubject subject)
        => GetOrCreateIdWithStatus(subject).Id;

    /// <summary>
    /// Gets an existing ID for a subject, or creates a new one.
    /// Returns true if a new ID was created, false if the subject already had an ID.
    /// </summary>
    public (string Id, bool IsNew) GetOrCreateIdWithStatus(IInterceptorSubject subject)
    {
        if (_subjectToId.TryGetValue(subject, out var id))
            return (id, false);

        id = subject.GetOrAddSubjectId();
        _subjectToId[subject] = id;
        return (id, true);
    }

    public Dictionary<string, SubjectPropertyUpdate> GetOrCreateProperties(string subjectId)
    {
        if (!_subjects.TryGetValue(subjectId, out var properties))
        {
            properties = new Dictionary<string, SubjectPropertyUpdate>();
            _subjects[subjectId] = properties;
        }
        return properties;
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

    /// <summary>
    /// Builds the final SubjectUpdate, applying all transformations.
    /// </summary>
    public SubjectUpdate Build(IInterceptorSubject subject)
    {
        ApplyTransformations();

        var rootId = GetOrCreateId(subject);

        // Build an ordered dictionary with root entry first for deterministic output.
        var orderedSubjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>();
        if (_subjects.TryGetValue(rootId, out var rootProps))
        {
            orderedSubjects[rootId] = rootProps;
        }
        foreach (var (key, value) in _subjects)
        {
            if (key != rootId)
            {
                orderedSubjects[key] = value;
            }
        }

        // Only include root when the root subject has properties in this update.
        var update = new SubjectUpdate
        {
            Root = _subjects.ContainsKey(rootId) ? rootId : null,
            Subjects = orderedSubjects
        };

        for (var i = 0; i < Processors.Length; i++)
        {
            update = Processors[i].TransformSubjectUpdate(subject, update);
        }

        return update;
    }

    /// <summary>
    /// Clears the builder for reuse. Call before returning to pool.
    /// </summary>
    public void Clear()
    {
        _subjects = new(); // create a fresh dictionary, old one transferred to result
        _subjectToId.Clear();
        _propertyUpdates.Clear();

        ProcessedSubjects.Clear();
        Processors = [];
    }

    private void ApplyTransformations()
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
                    // Use AttributeName for attributes, Name for regular properties
                    var key = info.Property.IsAttribute
                        ? info.Property.AttributeMetadata.AttributeName
                        : info.Property.Name;

                    info.Parent[key] = transformed;
                }
            }
        }
    }
}
