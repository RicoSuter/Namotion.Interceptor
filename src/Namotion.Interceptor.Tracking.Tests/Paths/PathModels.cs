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
