namespace Namotion.Interceptor.ResilienceTest.Configuration;

public class ParticipantConfiguration
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Mutations per second.</summary>
    public int MutationRate { get; set; } = 50;

    /// <summary>Null means no chaos for this participant.</summary>
    public ChaosConfiguration? Chaos { get; set; }
}
