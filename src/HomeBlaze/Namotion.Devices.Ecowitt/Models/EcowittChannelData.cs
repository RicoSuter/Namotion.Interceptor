namespace Namotion.Devices.Ecowitt.Models;

public class EcowittChannelData
{
    public int Channel { get; set; }
    public string? Name { get; set; }
    public decimal? Temperature { get; set; }
    public decimal? Humidity { get; set; }
    public int? Battery { get; set; }
}
