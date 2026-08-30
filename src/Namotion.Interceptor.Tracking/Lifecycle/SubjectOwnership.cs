using System.Collections.Immutable;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

internal readonly struct IncomingEdge(PropertyReference property, int subjectOrdinal, object? index)
{
    public readonly PropertyReference Property = property;
    public readonly int SubjectOrdinal = subjectOrdinal;
    public readonly object? Index = index;
}

internal sealed record SubjectOwnership(
    ImmutableArray<IncomingEdge> Edges,
    ImmutableArray<SubjectParent> Parents,
    ImmutableArray<string> PropertyNames)
{
    internal SubjectOwnership() : this([], [], [])
    {
    }

    internal SubjectOwnership(ImmutableArray<string> propertyNames) : this([], [], propertyNames)
    {
    }

    public int IncomingCount => Edges.Length;

    public bool ContainsIncoming(PropertyReference property, int subjectOrdinal) =>
        FindIncomingIndex(property, subjectOrdinal) >= 0;

    public SubjectOwnership AddIncoming(PropertyReference property, int subjectOrdinal, object? index) =>
        WithEdges(Edges.Add(new IncomingEdge(property, subjectOrdinal, index)));

    public bool TryRemoveIncoming(
        PropertyReference property,
        int subjectOrdinal,
        out SubjectOwnership ownership)
    {
        var index = FindIncomingIndex(property, subjectOrdinal);
        ownership = index < 0 ? this : WithEdges(Edges.RemoveAt(index));
        return index >= 0;
    }

    private int FindIncomingIndex(PropertyReference property, int subjectOrdinal)
    {
        for (var index = 0; index < Edges.Length; index++)
        {
            var edge = Edges[index];
            if (edge.Property.Equals(property) && edge.SubjectOrdinal == subjectOrdinal)
            {
                return index;
            }
        }

        return -1;
    }

    public SubjectOwnership UpdateIncomingIndices(PropertyReference property, IReadOnlyList<object?> indices)
    {
        var builder = Edges.ToBuilder();
        for (var index = 0; index < Edges.Length; index++)
        {
            var edge = Edges[index];
            if (edge.Property.Equals(property) && edge.SubjectOrdinal < indices.Count)
            {
                builder[index] = new IncomingEdge(property, edge.SubjectOrdinal, indices[edge.SubjectOrdinal]);
            }
        }

        return WithEdges(builder.MoveToImmutable());
    }

    public bool TryGetSingleIncoming(out IncomingEdge edge)
    {
        if (Edges.Length == 1)
        {
            edge = Edges[0];
            return true;
        }

        edge = default;
        return false;
    }

    public void CopyIncomingEdges(List<IncomingEdge> target) => target.AddRange(Edges);

    private SubjectOwnership WithEdges(ImmutableArray<IncomingEdge> edges)
    {
        var parents = ImmutableArray.CreateBuilder<SubjectParent>(edges.Length);
        foreach (var edge in edges)
        {
            parents.Add(new SubjectParent(edge.Property, edge.Index));
        }

        return new SubjectOwnership(edges, parents.MoveToImmutable(), PropertyNames);
    }
}
