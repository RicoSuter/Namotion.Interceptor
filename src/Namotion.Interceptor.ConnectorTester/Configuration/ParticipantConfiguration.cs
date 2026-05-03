namespace Namotion.Interceptor.ConnectorTester.Configuration;

public class ParticipantConfiguration
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Assigned at startup from config position (server=0, clients=1,2,...). Determines which property to mutate in batch mode.</summary>
    public int Index { get; set; }

    /// <summary>Value mutations per second.</summary>
    public int ValueMutationRate { get; set; } = 50;

    /// <summary>Structural mutations per second (0 = disabled).</summary>
    public int StructuralMutationRate { get; set; } = 0;

    /// <summary>Whether the mutation engine wraps writes in transactions (requires WithSourceTransactions on context).</summary>
    public bool UseTransactions { get; set; } = false;

    /// <summary>Null means no chaos for this participant.</summary>
    public ChaosConfiguration? Chaos { get; set; }
}
