namespace Namotion.Devices.Ecowitt.Models;

public class EcowittPm25Data
{
    public int Channel { get; set; }
    public decimal? Pm25 { get; set; }
    public decimal? Pm25Avg24h { get; set; }
    public int? Battery { get; set; }
}
