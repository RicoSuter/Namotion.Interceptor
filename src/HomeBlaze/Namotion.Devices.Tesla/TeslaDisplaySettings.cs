using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Mapping;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// The units and the clock format of the display of a Tesla vehicle. The values are Tesla's own texts, such as "km/hr", "C" or "Bar".
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaDisplaySettings : ITitleProvider, IIconProvider
{
    private readonly TeslaSectionClock _clock = new();

    public string Title => "Display Settings";

    public string IconName => "Settings";

    [State(IsDiscrete = true, Position = 1)] public partial bool? Is24HourTime { get; internal set; }
    [State(Position = 2)] public partial string? ChargeRateUnits { get; internal set; }
    [State(Position = 3)] public partial string? DistanceUnits { get; internal set; }

    /// <summary>The kind of range the display shows, such as "Rated".</summary>
    [State(Position = 4)] public partial string? RangeDisplay { get; internal set; }

    [State(Position = 5)] public partial string? TemperatureUnits { get; internal set; }
    [State(Position = 6)] public partial string? TirePressureUnits { get; internal set; }
    [State(IsDiscrete = true, Position = 7)] public partial bool? ShowsRangeUnits { get; internal set; }

    internal TeslaDisplaySettings()
    {
        Is24HourTime = null;
        ChargeRateUnits = null;
        DistanceUnits = null;
        RangeDisplay = null;
        TemperatureUnits = null;
        TirePressureUnits = null;
        ShowsRangeUnits = null;
    }

    internal void Update(TeslaGuiSettingsDto dto)
    {
        if (!_clock.TryAdvance(TeslaValues.FromUnixMilliseconds(dto.Timestamp)))
        {
            return;
        }

        Is24HourTime = dto.Gui24HourTime;
        ChargeRateUnits = TeslaValues.Text(dto.GuiChargeRateUnits);
        DistanceUnits = TeslaValues.Text(dto.GuiDistanceUnits);
        RangeDisplay = TeslaValues.Text(dto.GuiRangeDisplay);
        TemperatureUnits = TeslaValues.Text(dto.GuiTemperatureUnits);
        TirePressureUnits = TeslaValues.Text(dto.GuiTirePressureUnits);
        ShowsRangeUnits = dto.ShowRangeUnits;
    }
}
