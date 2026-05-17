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

    [Path("opc", "LongValue")]
    [Path("mqtt", "LongValue")]
    [Path("ws", "LongValue")]
    public partial long LongValue { get; set; }

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
        LongValue = 0;
        ObjectRef = null;
        Collection = [];
        Items = new Dictionary<string, TestNode>();
    }

    /// <summary>
    /// Creates a TestNode root with a configurable number of children.
    /// </summary>
    /// <param name="context">Interceptor context for the root node.</param>
    /// <param name="collectionCount">Number of collection children.</param>
    /// <param name="dictionaryCount">Number of dictionary entries.</param>
    public static TestNode CreateWithGraph(IInterceptorSubjectContext context, int collectionCount = 20, int dictionaryCount = 10)
    {
        return new TestNode(context)
        {
            Collection = Enumerable.Range(0, collectionCount)
                .Select(_ => new TestNode())
                .ToArray(),
            Items = Enumerable.Range(0, dictionaryCount)
                .ToDictionary(i => $"item-{i}", _ => new TestNode())
        };
    }
}
