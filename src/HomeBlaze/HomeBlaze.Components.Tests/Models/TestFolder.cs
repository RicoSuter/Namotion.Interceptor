using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Components.Tests.Models;

/// <summary>
/// Test folder whose children are addressed by key, like a storage folder.
/// </summary>
[InterceptorSubject]
public partial class TestFolder
{
    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public TestFolder()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }
}
