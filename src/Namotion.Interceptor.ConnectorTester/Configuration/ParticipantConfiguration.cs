namespace Namotion.Interceptor.ConnectorTester.Configuration;

public class ParticipantConfiguration
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Value mutations per second.</summary>
    public int MutationRate { get; set; } = 50;

    /// <summary>Structural mutations per second (0 = disabled).</summary>
    public int StructuralMutationRate { get; set; } = 0;

    /// <summary>Null means no chaos for this participant.</summary>
    public ChaosConfiguration? Chaos { get; set; }
}
