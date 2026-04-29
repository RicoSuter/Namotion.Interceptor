namespace Namotion.Devices.Ecowitt.Models;

public class EcowittRainData
{
    public decimal? RainEvent { get; set; }
    public decimal? RainRate { get; set; }
    public decimal? HourlyRain { get; set; }
    public decimal? DailyRain { get; set; }
    public decimal? WeeklyRain { get; set; }
    public decimal? MonthlyRain { get; set; }
    public decimal? YearlyRain { get; set; }
    public int? Battery { get; set; }
}
