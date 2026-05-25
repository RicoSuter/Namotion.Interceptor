using Namotion.Interceptor.ConnectorTester.Connectors;

namespace Namotion.Interceptor.ConnectorTester.Configuration;

public class ConnectorTesterConfiguration
{
    /// <summary>"opcua", "mqtt", or "websocket"</summary>
    public string Connector { get; set; } = "opcua";

    /// <summary>Parsed connector kind based on <see cref="Connector"/>.</summary>
    public ConnectorKind ConnectorKind => Connector?.ToLowerInvariant() switch
    {
        "opcua"     => Namotion.Interceptor.ConnectorTester.Connectors.ConnectorKind.OpcUa,
        "mqtt"      => Namotion.Interceptor.ConnectorTester.Connectors.ConnectorKind.Mqtt,
        "websocket" => Namotion.Interceptor.ConnectorTester.Connectors.ConnectorKind.WebSocket,
        _ => throw new InvalidOperationException(
            $"Unknown ConnectorTester:Connector value '{Connector}'. Expected one of: opcua, mqtt, websocket.")
    };

    /// <summary>Number of collection children in the test graph.</summary>
    public int CollectionCount { get; set; } = 20;

    /// <summary>Number of dictionary entries in the test graph.</summary>
    public int DictionaryCount { get; set; } = 10;

    /// <summary>
    /// Number of batches per second for the value mutation loop.
    /// 0 = use RandomValueMutationStrategy (single random mutations).
    /// Greater than 0 = use BatchValueMutationStrategy (parallel batched updates).
    /// Each batch mutates ceil(ValueMutationRate / NumberOfBatches) nodes.
    /// </summary>
    public int NumberOfBatches { get; set; } = 0;

    /// <summary>How often performance metrics are logged to console and performance-*.csv.</summary>
    public TimeSpan MetricsReportingInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan MutatePhaseDuration { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan ConvergenceTimeout { get; set; } = TimeSpan.FromMinutes(1);

    public ParticipantConfiguration Server { get; set; } = new()
    {
        Name = "server",
        ValueMutationRate = 1000
    };

    public List<ParticipantConfiguration> Clients { get; set; } = [];

    public List<ChaosProfileConfiguration> ChaosProfiles { get; set; } = [];
}
