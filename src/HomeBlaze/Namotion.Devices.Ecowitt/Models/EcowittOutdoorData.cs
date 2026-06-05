namespace Namotion.Devices.Ecowitt.Models;

public class EcowittOutdoorData
{
    public decimal? Temperature { get; set; }
    public decimal? Humidity { get; set; }
    public decimal? DewPoint { get; set; }
    public decimal? FeelsLikeTemperature { get; set; }
    public decimal? WindSpeed { get; set; }
    public decimal? WindGust { get; set; }
    public decimal? MaxDailyGust { get; set; }
    public decimal? WindDirection { get; set; }
    public decimal? Illuminance { get; set; }
    public decimal? UvIndex { get; set; }
    public decimal? SolarRadiation { get; set; }
    public decimal? VaporPressureDeficit { get; set; }
}
