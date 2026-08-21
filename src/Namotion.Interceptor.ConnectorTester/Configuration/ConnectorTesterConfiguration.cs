using Namotion.Interceptor.ConnectorTester.Connectors;

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
    /// Whether each participant mutates only the property at its own index, so that every property has
    /// exactly one writer. Required by the write-durability oracle, which cannot tell a lost write from
    /// a legitimate overwrite when two participants write the same property. Default false.
    /// </summary>
    /// <remarks>
    /// Governs <see cref="Namotion.Interceptor.ConnectorTester.Engine.Mutation.RandomValueMutationStrategy"/>
    /// only. When <see cref="NumberOfBatches"/> is greater than zero,
    /// <see cref="Namotion.Interceptor.ConnectorTester.Engine.Mutation.BatchValueMutationStrategy"/> runs
    /// instead, and it fixes each participant to one property unconditionally, for a reason unrelated to
    /// this option: see its own remarks. That happens to satisfy this option's requirement too, provided
    /// the participant count does not exceed <see cref="MutablePropertyCount"/>, which
    /// <see cref="ValidateDisjointProperties"/> checks regardless of which strategy is active.
    /// </remarks>
    public bool DisjointProperties { get; set; }

    public ParticipantConfiguration Server { get; set; } = new()
    {
        Name = "server",
        ValueMutationRate = 1000
    };

    public List<ParticipantConfiguration> Clients { get; set; } = [];

    public List<ChaosProfileConfiguration> ChaosProfiles { get; set; } = [];

    /// <summary>
    /// TestNode has this many mutable value properties, one per DisjointProperties participant. Shared
    /// by both mutation strategies and by
    /// <see cref="Namotion.Interceptor.ConnectorTester.Engine.Verification.WriteDurabilityLedger"/>'s
    /// property reader, so the property count cannot drift between them. Public rather than internal
    /// because the tests that pin the strategies' disjointness against this count live in a separate
    /// test project, and this project is a standalone tool (not packed, no tracked public API), so
    /// widening it costs nothing.
    /// </summary>
    public const int MutablePropertyCount = 4;

    /// <summary>
    /// Throws when DisjointProperties cannot assign every configured participant a property of its
    /// own. Above <see cref="MutablePropertyCount"/> participants, two would share a property, and the
    /// write-durability oracle would then report their legitimate overwrites as if they were losses.
    /// </summary>
    public void ValidateDisjointProperties()
    {
        var participantCount = Clients.Count + 1;
        if (DisjointProperties && participantCount > MutablePropertyCount)
        {
            throw new InvalidOperationException(
                $"DisjointProperties requires at most {MutablePropertyCount} participants, one per mutable property on TestNode, but {participantCount} are configured. " +
                "The write-durability oracle is unsound with more, because two participants would write the same property.");
        }
    }
}
