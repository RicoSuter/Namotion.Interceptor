using System.Text.Json.Serialization;

namespace Namotion.Devices.Shelly.Model;

/// <summary>
/// Status of an emdata:N component from Shelly.GetStatus (energy totals).
/// </summary>
internal class ShellyEmDataStatus
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("total_act")]
    public decimal? TotalActiveEnergy { get; set; }

    [JsonPropertyName("total_act_ret")]
    public decimal? TotalActiveReturnedEnergy { get; set; }

    [JsonPropertyName("a_total_act_energy")]
    public decimal? PhaseATotalActiveEnergy { get; set; }

    [JsonPropertyName("a_total_act_ret_energy")]
    public decimal? PhaseATotalReturnedEnergy { get; set; }

    [JsonPropertyName("b_total_act_energy")]
    public decimal? PhaseBTotalActiveEnergy { get; set; }

    [JsonPropertyName("b_total_act_ret_energy")]
    public decimal? PhaseBTotalReturnedEnergy { get; set; }

    [JsonPropertyName("c_total_act_energy")]
    public decimal? PhaseCTotalActiveEnergy { get; set; }

    [JsonPropertyName("c_total_act_ret_energy")]
    public decimal? PhaseCTotalReturnedEnergy { get; set; }
}
