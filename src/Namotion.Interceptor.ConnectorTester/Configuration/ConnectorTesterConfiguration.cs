using Namotion.Interceptor.ConnectorTester.Connectors;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Configuration;

public class ConnectorTesterConfiguration
{
    /// <summary>"opcua", "mqtt", or "websocket"</summary>
    public string Connector { get; set; } = "opcua";

    /// <summary>Parsed connector kind based on <see cref="Connector"/>.</summary>
    public ConnectorKind ConnectorKind =>
        Enum.TryParse<ConnectorKind>(Connector, ignoreCase: true, out var kind)
            ? kind
            : throw new InvalidOperationException(
                $"Unknown ConnectorTester:Connector value '{Connector}'. Expected one of: {string.Join(", ", Enum.GetNames<ConnectorKind>())}.");

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

    /// <summary>
    /// Whether each participant writes only the <c>TestNode</c> value property at its own index and, after each
    /// converged cycle, the write-durability oracle checks that every participant's model still holds its own last
    /// write. Requires at most <see cref="TestNode.ValuePropertyCount"/> participants.
    /// </summary>
    public bool VerifyWriteDurability { get; set; }

    public ParticipantConfiguration Server { get; set; } = new()
    {
        Name = "server",
        ValueMutationRate = 1000
    };

    public List<ParticipantConfiguration> Clients { get; set; } = [];

    public List<ChaosProfileConfiguration> ChaosProfiles { get; set; } = [];

    /// <summary>Throws when <see cref="VerifyWriteDurability"/> is set with more participants than value properties.</summary>
    public void ValidateVerifyWriteDurability()
    {
        if (!VerifyWriteDurability)
        {
            return;
        }

        var participantCount = Clients.Count + 1;
        if (participantCount > TestNode.ValuePropertyCount)
        {
            throw new InvalidOperationException(
                $"VerifyWriteDurability allows at most {TestNode.ValuePropertyCount} participants, one per TestNode value property, but {participantCount} are configured.");
        }
    }
}
