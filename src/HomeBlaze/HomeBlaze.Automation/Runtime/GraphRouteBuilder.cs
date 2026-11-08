using System.Collections.Immutable;
using System.Runtime.InteropServices;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Records the <see cref="GraphRoute"/> of resolutions as their <see cref="ISubjectPathRecorder"/>, reused across them so
/// that a resolution whose route stayed the same allocates nothing. Used by the worker only.
/// </summary>
/// <remarks>
/// A step up from a subject depends on its parents. The route has the properties that hold the subject, which lose it,
/// but the property that gains it next is not known; the step is exact only for a subject that tells the worker when it
/// gains a parent: the automation of the worker, and a rule whose only parent is that automation.
/// </remarks>
internal sealed class GraphRouteBuilder(IInterceptorSubject? automation) : ISubjectPathRecorder
{
    private readonly List<PropertyReference> _properties = [];
    private readonly HashSet<PropertyReference> _added = [];
    private bool _isUnbounded;

    /// <summary>
    /// The structure version of the registry read before the resolution started, or -1 for a version read during a
    /// change or without a registry.
    /// </summary>
    public long ResolvedAt { get; private set; } = -1;

    /// <summary>Starts a route of a resolution that starts at <paramref name="structureVersion"/>.</summary>
    public void Clear(long structureVersion)
    {
        Clear();
        ResolvedAt = structureVersion >= 0 && structureVersion % 2 == 0 ? structureVersion : -1;
    }

    public void Clear()
    {
        _properties.Clear();
        _added.Clear();
        _isUnbounded = false;
        ResolvedAt = -1;
    }

    public void Add(PropertyReference property)
    {
        if (_added.Add(property))
        {
            _properties.Add(property);
        }
    }

    /// <summary>The properties recorded since the last clear, each once.</summary>
    public ReadOnlySpan<PropertyReference> Properties => CollectionsMarshal.AsSpan(_properties);

    public bool IsUnbounded => _isUnbounded;

    /// <summary>Marks the route as not known exactly, so that every change of a property that holds subjects counts.</summary>
    public void MarkUnbounded() => _isUnbounded = true;

    void ISubjectPathRecorder.AddProperty(PropertyReference property) => Add(property);

    void ISubjectPathRecorder.AddParentStep(IInterceptorSubject subject, ImmutableArray<SubjectPropertyParent> parents)
    {
        foreach (var parent in parents)
        {
            Add(parent.Property.Reference);
        }

        if (!TellsParentGains(subject, parents))
        {
            MarkUnbounded();
        }
    }

    /// <summary>Gets the recorded route, which is <paramref name="previous"/> itself when it is equal.</summary>
    public GraphRoute ToRoute(GraphRoute previous) =>
        previous.IsUnbounded == _isUnbounded && CollectionsMarshal.AsSpan(_properties).SequenceEqual(previous.Properties)
            ? previous
            : GraphRoute.Of([.. _properties], _isUnbounded);

    private bool TellsParentGains(IInterceptorSubject subject, ImmutableArray<SubjectPropertyParent> parents)
    {
        if (automation is null)
        {
            return false;
        }

        return ReferenceEquals(subject, automation) ||
            (subject is AutomationRule && parents.Length == 1 && ReferenceEquals(parents[0].Property.Subject, automation));
    }
}
