namespace Namotion.Devices.Ecowitt.Models;

public class EcowittLeakData
{
    public int Channel { get; set; }
    public bool IsLeaking { get; set; }
    public int? Battery { get; set; }
}
