namespace Namotion.Interceptor.ConnectorTester.Configuration;

public class ConnectorTesterConfiguration
{
    /// <summary>"opcua", "mqtt", or "websocket"</summary>
    public string Connector { get; set; } = "opcua";

    public TimeSpan MutatePhaseDuration { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan ConvergenceTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// When true, snapshot comparison uses subject IDs to match subjects across participants.
    /// When false, subjects are matched by graph position (path from root) — required for
    /// connectors like OPC UA and MQTT where each participant creates independent subject IDs.
    /// </summary>
    public bool CompareBySubjectId { get; set; } = false;

    public ParticipantConfiguration Server { get; set; } = new()
    {
        Name = "server",
        ValueMutationRate = 1000
    };

    public List<ParticipantConfiguration> Clients { get; set; } = [];

    public List<ChaosProfileConfiguration> ChaosProfiles { get; set; } = [];
}
