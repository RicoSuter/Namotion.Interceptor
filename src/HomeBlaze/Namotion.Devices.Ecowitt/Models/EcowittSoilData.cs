namespace Namotion.Devices.Ecowitt.Models;

public class EcowittSoilData
{
    public int Channel { get; set; }
    public decimal? Moisture { get; set; }
    public int? Battery { get; set; }
}
