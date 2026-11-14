using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.ConnectorTester.Connectors.OpcUa;

/// <summary>Logs the endpoint security mode and policy of every session the client creates.</summary>
public sealed class EndpointLoggingSessionFactory : DefaultSessionFactory
{
    private readonly ILogger _logger;

    public EndpointLoggingSessionFactory(ITelemetryContext telemetryContext, ILogger logger)
        : base(telemetryContext)
    {
        _logger = logger;
    }

    public override async Task<ISession> CreateAsync(
        ApplicationConfiguration configuration,
        ConfiguredEndpoint endpoint,
        bool updateBeforeConnect,
        string sessionName,
        uint sessionTimeout,
        IUserIdentity? identity,
        IList<string>? preferredLocales,
        CancellationToken ct = default)
    {
        var session = await base.CreateAsync(
            configuration, endpoint, updateBeforeConnect, sessionName, sessionTimeout, identity, preferredLocales, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "OPC UA session created on {EndpointUrl} with security mode {SecurityMode} and policy {SecurityPolicyUri}.",
            endpoint.Description.EndpointUrl,
            endpoint.Description.SecurityMode,
            endpoint.Description.SecurityPolicyUri);

        return session;
    }
}
