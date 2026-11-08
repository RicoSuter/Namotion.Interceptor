using System.Collections.Immutable;
using System.Runtime.InteropServices;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Records the graph route of a resolution as its <see cref="ISubjectPathRecorder"/>: the properties whose change can make
/// it find something else, each once, and whether the route is unbounded. Cleared and filled again by the worker, so that
/// a resolution whose route stayed the same allocates nothing. Used by the worker only.
/// </summary>
/// <remarks>
/// A step up from a subject depends on its parents. The route has the properties that hold the subject, which lose it,
/// but the property that gains it next is not known, and a subject with several parents loses one when an ancestor of
/// that parent is detached, which writes no holder; the step is exact only for a subject with at most one parent that
/// tells the worker when it gains a parent: the automation of the worker, and a rule whose only parent is that automation.
/// </remarks>
internal sealed class GraphRouteBuilder(IInterceptorSubject? automation) : ISubjectPathRecorder
{
    private readonly List<PropertyReference> _properties = [];
    private readonly HashSet<PropertyReference> _added = [];

    /// <summary>The properties recorded since the last clear, each once, in the order they were recorded.</summary>
    public ReadOnlySpan<PropertyReference> Properties => CollectionsMarshal.AsSpan(_properties);

    public bool IsUnbounded { get; private set; }

    public void Clear()
    {
        _properties.Clear();
        _added.Clear();
        IsUnbounded = false;
    }

    public void AddProperty(PropertyReference property)
    {
        if (_added.Add(property))
        {
            _properties.Add(property);
        }
    }

    public void AddParentStep(IInterceptorSubject subject, ImmutableArray<SubjectPropertyParent> parents)
    {
        foreach (var parent in parents)
        {
            AddProperty(parent.Property.Reference);
        }

        if (!TellsParentGains(subject, parents))
        {
            MarkUnbounded();
        }
    }

    public void MarkUnbounded() => IsUnbounded = true;

    /// <summary>Gets the recorded route: <paramref name="previous"/> when it is the same, so that nothing is allocated then.</summary>
    public GraphRoute ToRoute(GraphRoute previous)
    {
        if (previous.IsUnbounded == IsUnbounded && previous.Properties.AsSpan().SequenceEqual(Properties))
        {
            return previous;
        }

        return _properties.Count == 0 && !IsUnbounded ? GraphRoute.Empty : new GraphRoute([.. _properties], IsUnbounded);
    }

    private bool TellsParentGains(IInterceptorSubject subject, ImmutableArray<SubjectPropertyParent> parents)
    {
        if (automation is null || parents.Length > 1)
        {
            return false;
        }

        return ReferenceEquals(subject, automation) ||
            (subject is AutomationRule && parents.Length == 1 && ReferenceEquals(parents[0].Property.Subject, automation));
    }
}
