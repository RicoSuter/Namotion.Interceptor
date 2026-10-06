using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Services.Tests.Models;

/// <summary>
/// Test root whose children are addressed by key, like a storage folder.
/// </summary>
[InterceptorSubject]
public partial class TestReferenceRoot
{
    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public TestReferenceRoot()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }
}
