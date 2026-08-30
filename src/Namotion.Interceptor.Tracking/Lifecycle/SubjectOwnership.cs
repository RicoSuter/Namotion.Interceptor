using System.Collections.Immutable;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

internal readonly struct IncomingEdge(PropertyReference property, int subjectOrdinal, object? index)
{
    public readonly PropertyReference Property = property;
    public readonly int SubjectOrdinal = subjectOrdinal;
    public readonly object? Index = index;
}

internal sealed class SubjectOwnership
{
    private sealed record State(ImmutableArray<IncomingEdge> Edges,
        ImmutableArray<SubjectParent> Parents,
        ImmutableArray<string> PropertyNames)
    {
        internal static readonly State Empty = new([], [], []);
    }
    private volatile State _state;

    internal SubjectOwnership()
        : this(State.Empty)
    {
    }

    internal SubjectOwnership(ImmutableArray<string> propertyNames)
        : this(new State([], [], propertyNames))
    {
    }

    private SubjectOwnership(State state)
    {
        _state = state;
    }

    public int IncomingCount => _state.Edges.Length;

    internal ImmutableArray<SubjectParent> Parents => _state.Parents;

    internal ImmutableArray<string> PropertyNames => _state.PropertyNames;

    internal void SetPropertyNames(ImmutableArray<string> propertyNames)
    {
        _state = _state with { PropertyNames = propertyNames };
    }

    internal SubjectOwnership Clone() => new(_state);

    public void AddIncoming(PropertyReference property, int subjectOrdinal, object? index)
    {
        Publish(_state.Edges.Add(new IncomingEdge(property, subjectOrdinal, index)));
    }

    public bool RemoveIncoming(PropertyReference property, int subjectOrdinal)
    {
        var edges = _state.Edges;
        for (var index = 0; index < edges.Length; index++)
        {
            var edge = edges[index];
            if (edge.Property.Equals(property) && edge.SubjectOrdinal == subjectOrdinal)
            {
                Publish(edges.RemoveAt(index));
                return true;
            }
        }

        return false;
    }

    public void UpdateIncomingIndices(PropertyReference property, IReadOnlyList<object?> indices)
    {
        var edges = _state.Edges;
        var builder = edges.ToBuilder();
        for (var index = 0; index < edges.Length; index++)
        {
            var edge = edges[index];
            if (edge.Property.Equals(property) && edge.SubjectOrdinal < indices.Count)
            {
                builder[index] = new IncomingEdge(property, edge.SubjectOrdinal, indices[edge.SubjectOrdinal]);
            }
        }

        Publish(builder.MoveToImmutable());
    }

    public bool TryGetSingleIncoming(out IncomingEdge edge)
    {
        var edges = _state.Edges;
        if (edges.Length == 1)
        {
            edge = edges[0];
            return true;
        }

        edge = default;
        return false;
    }

    public void CopyIncomingEdges(List<IncomingEdge> target) => target.AddRange(_state.Edges);

    public bool TryGetPublishedParents(out ImmutableArray<SubjectParent> parents)
    {
        parents = _state.Parents;
        return true;
    }

    public ImmutableArray<SubjectParent> ActivateParents() => _state.Parents;

    public void RepublishParents()
    {
    }

    private void Publish(ImmutableArray<IncomingEdge> edges)
    {
        var parents = ImmutableArray.CreateBuilder<SubjectParent>(edges.Length);
        foreach (var edge in edges)
        {
            parents.Add(new SubjectParent(edge.Property, edge.Index));
        }

        _state = new State(edges, parents.MoveToImmutable(), _state.PropertyNames);
    }
}
