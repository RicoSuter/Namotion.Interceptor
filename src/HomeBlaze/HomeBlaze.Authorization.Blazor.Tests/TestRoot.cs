using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Authorization.Blazor.Tests;

/// <summary>
/// A root that holds its children by name in an [InlinePaths] dictionary, as a storage container holds its files.
/// </summary>
[InterceptorSubject]
public partial class TestRoot
{
    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public TestRoot()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }
}
