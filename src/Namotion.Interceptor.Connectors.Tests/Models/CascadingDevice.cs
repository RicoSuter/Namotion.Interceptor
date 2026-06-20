using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

/// <summary>
/// Test model whose OnPrimaryChanged hook cascades into another property, used to pin the source
/// semantics of consequence writes: after #345 the cascade publishes as local origin (Source = null)
/// and flows to bound sources.
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
