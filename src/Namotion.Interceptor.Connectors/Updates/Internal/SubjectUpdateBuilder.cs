using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Builder for creating a SubjectUpdate. Tracks IDs, subjects, and transformations.
/// Designed to be pooled and reused.
/// </summary>
internal sealed class SubjectUpdateBuilder
{
    private int _nextId;
    private ChangeMerger? _changeMerger;
    private readonly Dictionary<IInterceptorSubject, string> _subjectToId = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SubjectPropertyUpdate, (RegisteredSubjectProperty Property, IDictionary<string, SubjectPropertyUpdate> Parent)> _propertyUpdates = new();

    /// <summary>
    /// Set when a subject reached while building carried no Registry metadata, which means the update
    /// holds an id no payload will ever be written for.
    /// </summary>
    public bool HasUnregisteredSubjects { get; set; }

    public ISubjectUpdateProcessor[] Processors { get; private set; } = [];
    
    public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; private set; } = new();

    public HashSet<IInterceptorSubject> ProcessedSubjects { get; } = new(ReferenceEqualityComparer.Instance);

    public HashSet<IInterceptorSubject> PathVisited { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Changes held back until the subject-holding changes of a batch are processed, with the registered
    /// property already resolved for each.
    /// </summary>
    public List<(int Index, RegisteredSubjectProperty Property)> DeferredChanges { get; } = [];

    /// <summary>
    /// The subject references this batch reassigned to a different subject, whose update entries are marked
    /// <see cref="SubjectPropertyUpdateMode.Replaced"/> once the update is built.
    /// </summary>
    public List<RegisteredSubjectProperty> ReplacedReferences { get; } = [];

    public IInterceptorSubject RootSubject { get; private set; } = null!;

    public ReadOnlySpan<SubjectPropertyChange> MergeChanges(ReadOnlySpan<SubjectPropertyChange> changes)
        => changes.Length <= 1 ? changes : (_changeMerger ??= new ChangeMerger()).Merge(changes).Span;

    public void Initialize(IInterceptorSubject rootSubject, ISubjectUpdateProcessor[] processors)
    {
        Processors = processors;
        RootSubject = rootSubject;
        GetOrCreateId(rootSubject); // Ensure root subject gets ID "1"
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
        {
            return (id, false);
        }

        id = (++_nextId).ToString();
        _subjectToId[subject] = id;
        return (id, true);
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

    /// <summary>
    /// The id of a subject that already carries property updates in this build, or <c>null</c> when it
    /// has no id yet or nothing has been written for it. Never mints an id, so asking does not put an
    /// empty subject into the update.
    /// </summary>
    public string? TryGetIdWithUpdates(IInterceptorSubject subject)
        => _subjectToId.TryGetValue(subject, out var id) &&
           Subjects.TryGetValue(id, out var properties) && properties.Count > 0
            ? id
            : null;

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
        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = Subjects
        };

        for (var i = 0; i < Processors.Length; i++)
        {
            update = Processors[i].TransformSubjectUpdate(subject, update);
        }

        if (HasUnregisteredSubjects)
        {
            OmitDanglingReferences(subject, update);
        }

        return update;
    }

    /// <summary>
    /// Omits properties with missing subject payloads and logs a warning, preserving the receiver's values.
    /// </summary>
    private void OmitDanglingReferences(IInterceptorSubject rootSubject, SubjectUpdate update)
    {
        List<(string SubjectId, List<string> PropertyNames)>? omittedProperties = null;
        foreach (var (subjectId, properties) in update.Subjects)
        {
            if (OmitDanglingReferences(properties, update.Subjects) is { } propertyNames)
            {
                (omittedProperties ??= []).Add((subjectId, propertyNames));
            }
        }

        if (omittedProperties is null)
        {
            return;
        }

        SubjectUpdateLog.TryGetWarningLogger(rootSubject)?.LogWarning(
            "Omitted the update properties {OmittedProperties} because they reference subjects without Registry " +
            "metadata. Register the referenced subjects or exclude these properties with an ISubjectUpdateProcessor. " +
            "A subject detached after its change was captured needs neither and converges with the next update.",
            DescribeOmittedProperties(omittedProperties));
    }

    /// <summary>
    /// Removes the properties and attributes with missing subject payloads, and returns their names, or
    /// <c>null</c> when none was removed.
    /// </summary>
    private static List<string>? OmitDanglingReferences(
        Dictionary<string, SubjectPropertyUpdate> properties,
        Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects)
    {
        List<string>? omittedNames = null;
        List<string>? danglingProperties = null;
        foreach (var (name, property) in properties)
        {
            if (HasDanglingReference(property, subjects))
            {
                (danglingProperties ??= []).Add(name);
            }
            else if (property.Attributes is not null &&
                     OmitDanglingReferences(property.Attributes, subjects) is { } omittedAttributes)
            {
                omittedNames ??= [];
                foreach (var attributeName in omittedAttributes)
                {
                    omittedNames.Add($"{name}@{attributeName}");
                }
            }
        }

        if (danglingProperties is null)
        {
            return omittedNames;
        }

        foreach (var name in danglingProperties)
        {
            properties.Remove(name);
        }

        if (omittedNames is null)
        {
            return danglingProperties;
        }

        omittedNames.AddRange(danglingProperties);
        return omittedNames;
    }

    /// <summary>
    /// Names each omitted property once, qualified by the type of the subject that owns it.
    /// </summary>
    private string DescribeOmittedProperties(List<(string SubjectId, List<string> PropertyNames)> omittedProperties)
    {
        var subjectTypes = new Dictionary<string, Type>(_subjectToId.Count);
        foreach (var (subject, id) in _subjectToId)
        {
            subjectTypes[id] = subject.GetType();
        }

        var names = new List<string>();
        foreach (var (subjectId, propertyNames) in omittedProperties)
        {
            var ownerName = subjectTypes.TryGetValue(subjectId, out var subjectType) ? subjectType.Name : null;
            foreach (var propertyName in propertyNames)
            {
                var name = ownerName is null ? propertyName : $"{ownerName}.{propertyName}";
                if (!names.Contains(name))
                {
                    names.Add(name);
                }
            }
        }

        return string.Join(", ", names);
    }

    private static bool HasDanglingReference(
        SubjectPropertyUpdate property,
        Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects)
    {
        switch (property.Kind)
        {
            case SubjectPropertyUpdateKind.Object:
                return IsDangling(property.Id, subjects);

            case SubjectPropertyUpdateKind.Collection:
            case SubjectPropertyUpdateKind.Dictionary:
                if (property.Items is not null)
                {
                    foreach (var item in property.Items)
                    {
                        if (IsDangling(item.Id, subjects))
                            return true;
                    }
                }

                if (property.Operations is not null)
                {
                    foreach (var operation in property.Operations)
                    {
                        // Only an insert carries a payload; a remove or a move needs nothing but its index.
                        if (operation.Action == SubjectCollectionOperationType.Insert && IsDangling(operation.Id, subjects))
                            return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private static bool IsDangling(string? subjectId, Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects)
        => subjectId is not null && !subjects.ContainsKey(subjectId);

    /// <summary>
    /// Clears the builder for reuse. Call before returning to pool.
    /// </summary>
    public void Clear()
    {
        _nextId = 0;
        _changeMerger?.Reset();
        HasUnregisteredSubjects = false;
        _subjectToId.Clear();
        _propertyUpdates.Clear();
        ProcessedSubjects.Clear();
        PathVisited.Clear();
        DeferredChanges.Clear();
        ReplacedReferences.Clear();
        Subjects = new(); // create a fresh dictionary, old one transferred to result
        Processors = [];
        RootSubject = null!;
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
