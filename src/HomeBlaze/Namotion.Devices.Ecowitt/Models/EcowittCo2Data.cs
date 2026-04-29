namespace Namotion.Devices.Ecowitt.Models;

public class EcowittCo2Data
{
    public int Channel { get; set; }
    public decimal? Co2 { get; set; }
    public decimal? Co2Avg24h { get; set; }
    public decimal? Temperature { get; set; }
    public decimal? Humidity { get; set; }
    public int? Battery { get; set; }
}
