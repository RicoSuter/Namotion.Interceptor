namespace Namotion.Devices.Ecowitt.Models;

/// <summary>
/// Parsed live data from the Ecowitt gateway, with all values converted to metric SI units.
/// </summary>
public class EcowittLiveData
{
    public EcowittOutdoorData? Outdoor { get; set; }
    public EcowittIndoorData? Indoor { get; set; }
    public EcowittRainData? Rain { get; set; }
    public EcowittRainData? PiezoRain { get; set; }
    public EcowittLightningData? Lightning { get; set; }
    public EcowittChannelData[] Channels { get; set; } = [];
    public EcowittSoilData[] SoilMoisture { get; set; } = [];
    public EcowittLeafData[] LeafWetness { get; set; } = [];
    public EcowittTemperatureData[] Temperatures { get; set; } = [];
    public EcowittPm25Data[] Pm25 { get; set; } = [];
    public EcowittCo2Data[] Co2 { get; set; } = [];
    public EcowittLeakData[] Leaks { get; set; } = [];
}
