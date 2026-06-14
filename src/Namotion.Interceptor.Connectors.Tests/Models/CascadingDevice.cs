using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

/// <summary>
/// Test model whose OnChanged hook writes another property, used to pin the scope semantics of
/// commit applies: synchronous consequences of a source-scoped apply inherit the source (#343).
/// </summary>
[InterceptorSubject]
public partial class CascadingDevice
{
    public partial int Primary { get; set; }

    public partial int Secondary { get; set; }

    partial void OnPrimaryChanged(int newValue)
    {
        Secondary = newValue * 2;
    }
}
