namespace Namotion.Interceptor.ConnectorTester.Configuration;

public class ConnectorTesterConfiguration
{
    /// <summary>"opcua", "mqtt", or "websocket"</summary>
    public string Connector { get; set; } = "opcua";

    /// <summary>Number of collection children in the test graph.</summary>
    public int ObjectCount { get; set; } = 31;

    /// <summary>
    /// Number of batches per second for BatchMutationEngine.
    /// 0 = use RandomMutationEngine (single random mutations).
    /// Greater than 0 = use BatchMutationEngine (parallel batched updates).
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
