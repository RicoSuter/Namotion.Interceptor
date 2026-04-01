using System.Text.Json.Serialization;

namespace Namotion.Devices.MyStrom.Model;

internal class MyStromSwitchReport
{
    [JsonPropertyName("power")]
    public decimal Power { get; set; }

    [JsonPropertyName("Ws")]
    public decimal Ws { get; set; }

    [JsonPropertyName("relay")]
    public bool Relay { get; set; }

    [JsonPropertyName("temperature")]
    public decimal Temperature { get; set; }

    [JsonPropertyName("energy_since_boot")]
    public decimal EnergySinceBoot { get; set; }

    [JsonPropertyName("time_since_boot")]
    public long TimeSinceBoot { get; set; }
}
