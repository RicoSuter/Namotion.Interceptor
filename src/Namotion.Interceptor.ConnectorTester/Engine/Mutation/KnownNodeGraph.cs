using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Engine.Mutation;

/// <summary>
/// Holds the graph-traversal state for a single MutationEngine: the lock,
/// the flat list of known nodes (used by value strategies to pick a random target),
/// and the subset eligible for structural mutation (depth up to MaxDepth).
/// All access is under NodeLock.
/// </summary>
public sealed class KnownNodeGraph
{
    private const int MaxDepth = 3;

    public Lock NodeLock { get; } = new();
    public List<TestNode> KnownNodes { get; private set; } = [];
    public List<TestNode> StructuralTargets { get; private set; } = [];

    public void Rebuild(TestNode root)
    {
        lock (NodeLock)
        {
            KnownNodes = [];
            StructuralTargets = [];
            VisitNode(root, depth: 0, visited: []);
        }
    }

    private void VisitNode(TestNode node, int depth, HashSet<TestNode> visited)
    {
        if (!visited.Add(node))
        {
            return;
        }

        KnownNodes.Add(node);

        if (depth < MaxDepth)
        {
            StructuralTargets.Add(node);
        }

        var collection = node.Collection;
        if (collection is not null)
        {
            foreach (var child in collection)
            {
                VisitNode(child, depth + 1, visited);
            }
        }

        var items = node.Items;
        if (items is not null)
        {
            foreach (var child in items.Values)
            {
                VisitNode(child, depth + 1, visited);
            }
        }

        var objectRef = node.ObjectRef;
        if (objectRef is not null)
        {
            VisitNode(objectRef, depth + 1, visited);
        }
    }
}
