namespace Namotion.Devices.Ecowitt.Models;

public class EcowittRainData
{
    public decimal? RainEvent { get; set; }
    public decimal? RainRate { get; set; }
    public decimal? HourlyRain { get; set; }
    public decimal? Last24HoursRain { get; set; }
    public decimal? DailyRain { get; set; }
    public decimal? WeeklyRain { get; set; }
    public decimal? MonthlyRain { get; set; }
    public decimal? YearlyRain { get; set; }

    /// <summary>
    /// Gets or sets the gateway's own rain total, which only resets when cleared on the gateway.
    /// </summary>
    public decimal? TotalRain { get; set; }

    public int? Battery { get; set; }
}
