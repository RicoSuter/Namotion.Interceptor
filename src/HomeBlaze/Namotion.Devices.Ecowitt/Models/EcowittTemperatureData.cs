namespace Namotion.Devices.Ecowitt.Models;

public class EcowittTemperatureData
{
    public int Channel { get; set; }
    public decimal? Temperature { get; set; }
    public int? Battery { get; set; }
}
