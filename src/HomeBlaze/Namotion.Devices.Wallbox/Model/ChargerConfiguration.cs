using System.Text.Json.Serialization;

namespace Namotion.Devices.Wallbox.Model;

internal class ChargerConfiguration
{
    [JsonPropertyName("charger_id")]
    public int ChargerId { get; set; }

    [JsonPropertyName("serial_number")]
    public string? SerialNumber { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("locked")]
    public int? Locked { get; set; }

    [JsonPropertyName("max_charging_current")]
    public decimal? MaximumChargingCurrent { get; set; }

    [JsonPropertyName("icp_max_current")]
    public int IcpMaxCurrent { get; set; }

    [JsonPropertyName("energy_price")]
    public decimal EnergyPrice { get; set; }

    [JsonPropertyName("max_available_current")]
    public int MaxAvailableCurrent { get; set; }

    [JsonPropertyName("part_number")]
    public string? PartNumber { get; set; }

    [JsonPropertyName("software")]
    public ChargerSoftware? Software { get; set; }

    [JsonPropertyName("currency")]
    public ChargerCurrency? Currency { get; set; }

    [JsonPropertyName("group_id")]
    public int GroupId { get; set; }

    [JsonPropertyName("ecosmart")]
    public ChargerEcoSmart? Ecosmart { get; set; }
}
