namespace Namotion.Devices.Ecowitt.Models;

public class EcowittLightningData
{
    public decimal? Distance { get; set; }
    public int? StrikeCount { get; set; }
    public DateTimeOffset? LastStrikeTime { get; set; }
    public int? Battery { get; set; }
}
