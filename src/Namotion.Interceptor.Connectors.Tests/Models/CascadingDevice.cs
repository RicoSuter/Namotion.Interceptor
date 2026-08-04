using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

/// <summary>
/// Test model whose OnChanged hook writes another property, used to pin the origin semantics of
/// commit applies: a nested hook cascade is a locally computed value, so it publishes with a null
/// (Local) source rather than inheriting the triggering write's source.
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
