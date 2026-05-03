using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Light;

/// <summary>
/// State interface for devices with color temperature (warm/cool white).
/// </summary>
[SubjectAbstraction]
[Description("Reports color temperature (0=warm white, 1=cool white).")]
public interface IColorTemperatureState
{
    /// <summary>
    /// The current color temperature (0 = warm, 1 = cool).
    /// </summary>
    [State(Unit = StateUnit.Percent, Position = 112)]
    decimal? ColorTemperature { get; }
}
