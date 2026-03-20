using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.ConnectorTester.Model;

[InterceptorSubject]
public partial class TestNode
{
    [Path("opc", "StringValue")]
    [Path("mqtt", "StringValue")]
    [Path("ws", "StringValue")]
    public partial string StringValue { get; set; }

    [Path("opc", "DecimalValue")]
    [Path("mqtt", "DecimalValue")]
    [Path("ws", "DecimalValue")]
    public partial decimal DecimalValue { get; set; }

    [Path("opc", "IntValue")]
    [Path("mqtt", "IntValue")]
    [Path("ws", "IntValue")]
    public partial int IntValue { get; set; }

    [Path("opc", "ObjectRef")]
    [Path("mqtt", "ObjectRef")]
    [Path("ws", "ObjectRef")]
    public partial TestNode? ObjectRef { get; set; }

    [Path("opc", "Collection")]
    [Path("mqtt", "Collection")]
    [Path("ws", "Collection")]
    public partial TestNode[] Collection { get; set; }

    [Path("opc", "Items")]
    [Path("mqtt", "Items")]
    [Path("ws", "Items")]
    public partial Dictionary<string, TestNode> Items { get; set; }

    public TestNode()
    {
        StringValue = string.Empty;
        DecimalValue = 0;
        IntValue = 0;
        ObjectRef = null;
        Collection = [];
        Items = new Dictionary<string, TestNode>();
    }

    /// <summary>
    /// Creates a TestNode root pre-populated near MaxTotalNodes (500) with a multi-level
    /// graph so the test starts in steady-state rather than a growth phase.
    /// Depth 0 (root): 20 collection + 10 dict = 30 children
    /// Depth 1: each has 15 collection children (leaves at depth 2)
    /// Total: 1 + 30 + (30 * 15) = 481 nodes
    /// </summary>
    public static TestNode CreateWithGraph(IInterceptorSubjectContext context)
    {
        TestNode CreateDepth1Node() => new()
        {
            Collection = Enumerable.Range(0, 15)
                .Select(_ => new TestNode())
                .ToArray()
        };

        var root = new TestNode(context)
        {
            Collection = Enumerable.Range(0, 20)
                .Select(_ => CreateDepth1Node())
                .ToArray(),

            Items = Enumerable.Range(0, 10)
                .ToDictionary(i => $"item-{i}", _ => CreateDepth1Node())
        };

        return root;
    }
}
