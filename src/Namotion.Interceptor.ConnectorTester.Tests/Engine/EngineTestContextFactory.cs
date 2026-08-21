using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine;

/// <summary>
/// The context both the mutation-engine and verification-engine tests build their <c>TestNode</c>
/// graphs against: full property tracking, a registry, parent tracking and lifecycle callbacks.
/// </summary>
internal static class EngineTestContextFactory
{
    public static IInterceptorSubjectContext Create()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithLifecycle();
}
