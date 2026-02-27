using Namotion.Interceptor.Connectors.TwinCAT.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests;

/// <summary>
/// Shared factory methods for common test setup.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Creates a context with property tracking and registry (no lifecycle).
    /// </summary>
    public static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }

    /// <summary>
    /// Creates a context with property tracking, registry, and lifecycle.
    /// </summary>
    public static IInterceptorSubjectContext CreateContextWithLifecycle()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();
    }

    /// <summary>
    /// Creates a default ADS client configuration for unit tests.
    /// </summary>
    public static AdsClientConfiguration CreateConfiguration(char pathSeparator = '.')
    {
        return new AdsClientConfiguration
        {
            Host = "127.0.0.1",
            AmsNetId = "127.0.0.1.1.1",
            AmsPort = 851,
            PathProvider = new AttributeBasedPathProvider(AdsConstants.DefaultConnectorName, pathSeparator)
        };
    }

    /// <summary>
    /// Creates an ADS subject loader for unit tests.
    /// </summary>
    public static AdsSubjectLoader CreateLoader()
    {
        var pathProvider = new AttributeBasedPathProvider(AdsConstants.DefaultConnectorName, '.');
        return new AdsSubjectLoader(pathProvider);
    }
}
