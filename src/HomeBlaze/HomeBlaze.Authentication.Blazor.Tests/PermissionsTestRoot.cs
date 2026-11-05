using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>A root that holds its children by name in an [InlinePaths] dictionary, as a storage container holds its files.</summary>
[InterceptorSubject]
public partial class PermissionsTestRoot
{
    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public PermissionsTestRoot()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }
}
