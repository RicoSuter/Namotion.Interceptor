using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Records the <see cref="GraphRoute"/> of resolutions, reused across them so that a resolution whose route stayed the
/// same allocates nothing. Used by the worker only.
/// </summary>
internal sealed class GraphRouteBuilder
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

    public void AddRange(ReadOnlySpan<PropertyReference> properties)
    {
        foreach (var property in properties)
        {
            Add(property);
        }
    }

    /// <summary>The properties recorded since the last clear, each once.</summary>
    public ReadOnlySpan<PropertyReference> Properties => CollectionsMarshal.AsSpan(_properties);

    public bool IsUnbounded => _isUnbounded;

    /// <summary>Adds the properties that hold the subject.</summary>
    public void AddHolders(ImmutableArray<SubjectPropertyParent> parents)
    {
        foreach (var parent in parents)
        {
            Add(parent.Property.Reference);
        }
    }

    /// <summary>Marks the route as not known exactly, so that every change of a property that holds subjects counts.</summary>
    public void MarkUnbounded() => _isUnbounded = true;

    /// <summary>Gets the recorded route, which is <paramref name="previous"/> itself when it is equal.</summary>
    public GraphRoute ToRoute(GraphRoute previous) =>
        previous.IsUnbounded == _isUnbounded && CollectionsMarshal.AsSpan(_properties).SequenceEqual(previous.Properties)
            ? previous
            : GraphRoute.Of([.. _properties], _isUnbounded);
}
