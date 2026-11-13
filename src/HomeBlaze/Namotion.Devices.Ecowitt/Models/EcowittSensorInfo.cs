namespace Namotion.Devices.Ecowitt.Models;

public class EcowittSensorInfo
{
    public string? SensorId { get; set; }
    /// <summary>
    /// Gets or sets what the sensor measures as the gateway describes the type, for example Solar &amp; Wind.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the image key of the slot, which names the model, for example wh68.
    /// </summary>
    public string? SensorType { get; set; }
    public int? Rssi { get; set; }
    public int? SignalLevel { get; set; }
    public int? Battery { get; set; }
    public int TypeCode { get; set; }
}
