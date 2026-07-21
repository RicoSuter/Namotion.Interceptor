using System.Collections.Generic;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

[InterceptorSubject]
public partial class Node
{
    public Node() { Name = string.Empty; Children = []; ByName = new(); }

    public partial string Name { get; set; }
    public partial Node? Child { get; set; }
    public partial Node[] Children { get; set; }
    public partial Dictionary<string, Node> ByName { get; set; }
    public int PlainField;                 // not a property
    public int Index { get; set; }         // used to build an invalid index-arg expression
}

[InterceptorSubject]
public partial class GridHolder
{
    public GridHolder() { Grid = new Node[0, 0]; Rows = []; }

    public partial Node[,] Grid { get; set; }           // multi-dimensional indexer
    public partial List<List<Node>> Rows { get; set; }  // nested indexer receiver
    public partial int Number { get; set; }             // non-subject intermediate target
}
