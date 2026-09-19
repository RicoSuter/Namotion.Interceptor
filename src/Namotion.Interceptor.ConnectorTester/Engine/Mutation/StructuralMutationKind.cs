namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// The structural graph shapes <see cref="StructuralMutator"/> can produce.
/// The first three grow and shrink the graph; the remaining four rewire it
/// without changing the node count.
/// </summary>
public enum StructuralMutationKind
{
    /// <summary>Appends a brand new node to a collection, or removes one entry from it.</summary>
    Collection,

    /// <summary>Adds a brand new node under a fresh dictionary key, or removes one entry.</summary>
    Dictionary,

    /// <summary>Sets a node's object reference to a brand new node, or clears it.</summary>
    ObjectRef,

    /// <summary>Takes an existing node out of one parent's container and attaches it to a different parent's.</summary>
    CrossParentMove,

    /// <summary>Detaches an existing node from its parent and immediately attaches the same instance back to that parent.</summary>
    ReAdd,

    /// <summary>Permutes a collection without changing its membership.</summary>
    Reorder,

    /// <summary>Points a second parent's object reference at a node that already has a parent, making the model a DAG.</summary>
    SharedReference
}
