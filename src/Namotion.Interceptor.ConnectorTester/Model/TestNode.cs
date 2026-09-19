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

    /// <summary>The number of value properties that <see cref="WriteValueProperty"/> and <see cref="ReadValueProperty"/> select by index.</summary>
    public const int ValuePropertyCount = 4;

    /// <summary>Writes a value derived from <paramref name="counter"/> to the value property at <paramref name="property"/> and returns it.</summary>
    public static object WriteValueProperty(TestNode node, int property, long counter)
    {
        switch (property)
        {
            case 0:
                var stringValue = counter.ToString("x8");
                node.StringValue = stringValue;
                return stringValue;
            case 1:
                var decimalValue = counter / 100m;
                node.DecimalValue = decimalValue;
                return decimalValue;
            case 2:
                var intValue = (int)(counter % int.MaxValue);
                node.IntValue = intValue;
                return intValue;
            case 3:
                node.LongValue = counter;
                return counter;
            default:
                throw new ArgumentOutOfRangeException(nameof(property));
        }
    }

    /// <summary>Returns the name and current value of the value property at <paramref name="property"/>.</summary>
    public static (string Name, object Value) ReadValueProperty(TestNode node, int property) => property switch
    {
        0 => (nameof(StringValue), node.StringValue),
        1 => (nameof(DecimalValue), node.DecimalValue),
        2 => (nameof(IntValue), node.IntValue),
        3 => (nameof(LongValue), node.LongValue),
        _ => throw new ArgumentOutOfRangeException(nameof(property))
    };

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
