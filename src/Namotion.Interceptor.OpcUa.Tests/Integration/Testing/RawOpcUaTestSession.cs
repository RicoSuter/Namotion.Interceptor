using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// A bare OPC UA client session over a running test server, with no connector, no subject tree and no
/// subscriptions. Tests that assert what a client observes need to shape the request itself (an index
/// range, a chosen source timestamp, a deliberately wrong type, several nodes in one request), which
/// <see cref="OpcUaTestClient{TRoot}"/> cannot express because it only ever sends what the outbound
/// writer builds. Its absence of a subject tree is also what keeps the server's node under test from
/// being written back by a second connector while the test is asserting on it.
/// </summary>
internal sealed class RawOpcUaTestSession : IAsyncDisposable
{
    private readonly ISession _session;

    private RawOpcUaTestSession(ISession session)
    {
        _session = session;
    }

    public static async Task<RawOpcUaTestSession> ConnectAsync(string serverUrl, string certificateStoreBasePath)
    {
        // Reuses the connector's own application setup so the test client is configured exactly like the
        // one under test (same certificate store layout, same transport quotas), only without its session
        // management.
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = serverUrl,
            CertificateStoreBasePath = certificateStoreBasePath
        };

        var application = await configuration.CreateApplicationInstanceAsync();
        var endpointConfiguration = EndpointConfiguration.Create(application.ApplicationConfiguration);
        var serverUri = new Uri(serverUrl);

        EndpointDescriptionCollection endpoints;
        using (var discoveryClient = await DiscoveryClient.CreateAsync(
                   application.ApplicationConfiguration, serverUri, endpointConfiguration))
        {
            endpoints = await discoveryClient.GetEndpointsAsync(null);
        }

        var endpointDescription = CoreClientUtils.SelectEndpoint(
            application.ApplicationConfiguration,
            serverUri,
            endpoints,
            useSecurity: false,
            configuration.ResolvedTelemetryContext);

        var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

        var session = await configuration.ActualSessionFactory.CreateAsync(
            application.ApplicationConfiguration,
            endpoint,
            updateBeforeConnect: false,
            sessionName: "Namotion.Interceptor.RawTestSession",
            sessionTimeout: (uint)TimeSpan.FromSeconds(60).TotalMilliseconds,
            identity: new UserIdentity(),
            preferredLocales: null,
            CancellationToken.None);

        return new RawOpcUaTestSession(session);
    }

    /// <summary>
    /// Writes one node and returns the per-node status the server answered with. A source timestamp of
    /// <see cref="DateTime.MinValue"/> is what the SDK reads as "not supplied" and replaces with its own.
    /// </summary>
    public async Task<StatusCode> WriteAsync(
        NodeId nodeId,
        object? value,
        string? indexRange = null,
        DateTime? sourceTimestamp = null)
    {
        var statusCodes = await WriteManyAsync(
            new WriteValue
            {
                NodeId = nodeId,
                AttributeId = Opc.Ua.Attributes.Value,
                IndexRange = indexRange,
                Value = new DataValue
                {
                    Value = value,
                    StatusCode = StatusCodes.Good,
                    SourceTimestamp = sourceTimestamp ?? DateTime.UtcNow
                }
            });

        return statusCodes[0];
    }

    /// <summary>
    /// Writes several nodes in a single Write request, which is what makes one node's outcome evidence
    /// about the handling of the others.
    /// </summary>
    public async Task<StatusCodeCollection> WriteManyAsync(params WriteValue[] writeValues)
    {
        var response = await _session.WriteAsync(
            requestHeader: null, new WriteValueCollection(writeValues), CancellationToken.None);

        return response.Results;
    }

    /// <summary>
    /// Reads a node's value straight from the server, bypassing every client-side cache, so the result is
    /// what a client actually observes on a read-back.
    /// </summary>
    public async Task<DataValue> ReadAsync(NodeId nodeId)
    {
        var response = await _session.ReadAsync(
            requestHeader: null,
            maxAge: 0,
            TimestampsToReturn.Both,
            [new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.Value }],
            CancellationToken.None);

        return response.Results[0];
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _session.CloseAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // The session is torn down with the server either way; a close that races the shutdown is
            // not something a test should fail on.
        }

        _session.Dispose();
    }
}
