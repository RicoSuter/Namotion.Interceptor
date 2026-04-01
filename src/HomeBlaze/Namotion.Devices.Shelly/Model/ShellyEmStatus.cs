using System.Text.Json.Serialization;

namespace Namotion.Devices.Shelly.Model;

/// <summary>
/// Status of an em:N component from Shelly.GetStatus (energy meter).
/// </summary>
internal class ShellyEmStatus
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("a_current")]
    public decimal? PhaseACurrent { get; set; }

    [JsonPropertyName("a_voltage")]
    public decimal? PhaseAVoltage { get; set; }

    [JsonPropertyName("a_act_power")]
    public decimal? PhaseAActivePower { get; set; }

    [JsonPropertyName("a_aprt_power")]
    public decimal? PhaseAApparentPower { get; set; }

    [JsonPropertyName("a_pf")]
    public decimal? PhaseAPowerFactor { get; set; }

    [JsonPropertyName("a_freq")]
    public decimal? PhaseAFrequency { get; set; }

    [JsonPropertyName("b_current")]
    public decimal? PhaseBCurrent { get; set; }

    [JsonPropertyName("b_voltage")]
    public decimal? PhaseBVoltage { get; set; }

    [JsonPropertyName("b_act_power")]
    public decimal? PhaseBActivePower { get; set; }

    [JsonPropertyName("b_aprt_power")]
    public decimal? PhaseBApparentPower { get; set; }

    [JsonPropertyName("b_pf")]
    public decimal? PhaseBPowerFactor { get; set; }

    [JsonPropertyName("b_freq")]
    public decimal? PhaseBFrequency { get; set; }

    [JsonPropertyName("c_current")]
    public decimal? PhaseCCurrent { get; set; }

    [JsonPropertyName("c_voltage")]
    public decimal? PhaseCVoltage { get; set; }

    [JsonPropertyName("c_act_power")]
    public decimal? PhaseCActivePower { get; set; }

    [JsonPropertyName("c_aprt_power")]
    public decimal? PhaseCApparentPower { get; set; }

    [JsonPropertyName("c_pf")]
    public decimal? PhaseCPowerFactor { get; set; }

    [JsonPropertyName("c_freq")]
    public decimal? PhaseCFrequency { get; set; }

    [JsonPropertyName("n_current")]
    public decimal? NeutralCurrent { get; set; }

    [JsonPropertyName("total_current")]
    public decimal? TotalCurrent { get; set; }

    [JsonPropertyName("total_act_power")]
    public decimal? TotalActivePower { get; set; }

    [JsonPropertyName("total_aprt_power")]
    public decimal? TotalApparentPower { get; set; }
}
