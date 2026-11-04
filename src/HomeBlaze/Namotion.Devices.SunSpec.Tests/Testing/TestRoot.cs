using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.SunSpec.Tests.Testing;

/// <summary>
/// Attaches a subject to a context with tracking and a registry.
/// </summary>
[InterceptorSubject]
public partial class TestRoot
{
    public TestRoot()
    {
        Child = null;
    }

    public partial IInterceptorSubject? Child { get; set; }

    public static TSubject Attach<TSubject>(TSubject subject)
        where TSubject : IInterceptorSubject
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        _ = new TestRoot(context) { Child = subject };
        return subject;
    }
}
