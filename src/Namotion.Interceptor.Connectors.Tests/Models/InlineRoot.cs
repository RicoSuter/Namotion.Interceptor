using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

[InterceptorSubject]
public partial class InlineRoot
{
    public InlineRoot()
    {
        Members = new Dictionary<int, Person>();
    }

    [InlinePaths]
    public partial Dictionary<int, Person> Members { get; set; }
}
