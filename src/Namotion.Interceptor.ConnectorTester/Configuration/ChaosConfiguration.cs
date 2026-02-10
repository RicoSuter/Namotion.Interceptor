namespace Namotion.Interceptor.ConnectorTester.Configuration;

public class ChaosConfiguration
{
    public TimeSpan IntervalMin { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan IntervalMax { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan DurationMin { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan DurationMax { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Chaos mode: "kill" (hard kill), "disconnect" (soft transport disconnect), or "both" (random choice).
    /// </summary>
    public string Mode { get; set; } = "both";
}
