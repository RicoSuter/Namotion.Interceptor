namespace Namotion.Devices.Ecowitt.Models;

public class EcowittNetworkInfo
{
    public string? MacAddress { get; set; }
    public string? IpAddress { get; set; }
    public string? Gateway { get; set; }
    public string? SubnetMask { get; set; }
    public string? Dns { get; set; }
    public bool? IsWireless { get; set; }
}
