using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

/// <summary>
/// Simple model for testing non-string dictionary keys.
/// </summary>
[InterceptorSubject]
public partial class IntKeyDictionaryNode
{
    public partial string? Name { get; set; }

    public partial Dictionary<int, IntKeyDictionaryNode>? Children { get; set; }
}
