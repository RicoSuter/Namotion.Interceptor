using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

[InterceptorSubject]
public partial class CascadingDevice
{
    public partial int Primary { get; set; }

    public partial int Secondary { get; set; }

    // Cascade: writing Primary computes Secondary locally. Secondary is the local model's own
    // computation, so its change must publish as local origin (Source = null).
    partial void OnPrimaryChanged(int newValue)
    {
        Secondary = newValue * 2;
    }
}
