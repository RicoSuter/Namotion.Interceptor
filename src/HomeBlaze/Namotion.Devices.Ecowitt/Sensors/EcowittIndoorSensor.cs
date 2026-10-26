using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Ecowitt.Sensors;

[InterceptorSubject]
public partial class EcowittIndoorSensor :
    ITemperatureSensor,
    IHumiditySensor,
    IBarometricPressureSensor,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; internal set; }

    [State(Unit = StateUnit.Percent)]
    public partial decimal? Humidity { get; internal set; }

    [State(Unit = StateUnit.Hectopascal)]
    public partial decimal? AbsolutePressure { get; internal set; }

    [State(Unit = StateUnit.Hectopascal)]
    public partial decimal? RelativePressure { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    /// <summary>
    /// Gets the paired sensors that can feed these readings, the same subjects as in the gateway's <c>Sensors</c>.
    /// </summary>
    [State]
    public partial Dictionary<string, EcowittSensor> Sources { get; internal set; }

    [Derived]
    public string? Title => "Indoor";

    [Derived]
    public string IconName => "Home";

    public string? IconColor => null;

    public EcowittIndoorSensor()
    {
        Temperature = null;
        Humidity = null;
        AbsolutePressure = null;
        RelativePressure = null;
        LastUpdated = null;
        Sources = new Dictionary<string, EcowittSensor>(StringComparer.Ordinal);
    }
}
