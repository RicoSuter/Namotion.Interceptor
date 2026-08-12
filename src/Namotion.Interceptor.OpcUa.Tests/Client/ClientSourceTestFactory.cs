using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Builds an OPC UA client source that never contacts a server, for tests that only exercise what
/// the source reports about itself.
/// </summary>
internal static class ClientSourceTestFactory
{
    /// <param name="withPropertyTracking">
    /// <c>false</c> leaves the context without a <c>PropertyChangeInterceptor</c>, which makes the
    /// pump fail its configuration guard on the first attempt. That is the only way to reach
    /// <c>ConnectorMetrics.MarkStarted</c> without a server.
    /// </param>
    internal static OpcUaSubjectClientSource CreateClientSource(bool withPropertyTracking = true)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithLifecycle();

        if (withPropertyTracking)
        {
            context.WithFullPropertyTracking();
        }

        var root = new TestRoot(context);
        return new OpcUaSubjectClientSource(root, CreateConfiguration(), NullLogger.Instance);
    }

    internal static OpcUaClientConfiguration CreateConfiguration() => new()
    {
        // Never dialled: nothing in these tests starts a connect attempt that gets as far as the wire.
        ServerUrl = "opc.tcp://localhost:4840",
        TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
        ValueConverter = new OpcUaValueConverter(),
        SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
    };
}
