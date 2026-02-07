using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

[InterceptorSubject]
public partial class IntKeyNode
{
    public IntKeyNode()
    {
        Name = "";
        IntLookup = new Dictionary<int, CycleTestNode>();
    }

    public partial string Name { get; set; }
    public partial Dictionary<int, CycleTestNode> IntLookup { get; set; }
}
