namespace Namotion.Interceptor.ResilienceTest.Configuration;

public class ChaosConfiguration
{
    /// <summary>"transport", "lifecycle", or "both"</summary>
    public string Mode { get; set; } = "transport";

    public TimeSpan IntervalMin { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan IntervalMax { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan DurationMin { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan DurationMax { get; set; } = TimeSpan.FromSeconds(30);
}
