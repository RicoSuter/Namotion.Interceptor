namespace Namotion.Devices.Ecowitt.Models;

public class EcowittSensorInfo
{
    public string? SensorId { get; set; }
    public string? Name { get; set; }
    public string? SensorType { get; set; }
    public int? Rssi { get; set; }
    public int? SignalLevel { get; set; }
    public int? Battery { get; set; }
    public int TypeCode { get; set; }
}
