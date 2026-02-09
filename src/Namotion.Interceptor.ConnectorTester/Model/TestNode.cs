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

    [Path("opc", "BoolValue")]
    [Path("mqtt", "BoolValue")]
    [Path("ws", "BoolValue")]
    public partial bool BoolValue { get; set; }

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
        BoolValue = false;
        ObjectRef = null;
        Collection = [];
        Items = new Dictionary<string, TestNode>();
    }

    /// <summary>
    /// Creates a TestNode root with initial graph: 20 collection children + 10 dictionary entries.
    /// </summary>
    public static TestNode CreateWithGraph(IInterceptorSubjectContext context)
    {
        var root = new TestNode(context);

        root.Collection = Enumerable.Range(0, 20)
            .Select(_ => new TestNode(context))
            .ToArray();

        root.Items = Enumerable.Range(0, 10)
            .ToDictionary(i => $"item-{i}", i => new TestNode(context));

        return root;
    }
}
