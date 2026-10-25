using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Energy;
using Namotion.Devices.Ecowitt.Models;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Ecowitt.Sensors;

/// <summary>
/// A physical sensor paired with the gateway, with the device health the gateway reports for it.
/// </summary>
[InterceptorSubject]
public partial class EcowittSensor :
    IBatteryState,
    ITitleProvider,
    IIconProvider
{
    /// <summary>
    /// Gets the sensor ID, for example 0x9637.
    /// </summary>
    [State]
    [Category("Device")]
    public partial string SensorId { get; private set; }

    /// <summary>
    /// Gets the type code of the slot the sensor is paired in, as the gateway's sensor list reports it.
    /// </summary>
    [State]
    [Category("Device")]
    public partial int TypeCode { get; internal set; }

    /// <summary>
    /// Gets the sensor model for the type code, for example WS90 or WH40, or null for a type code that is not mapped.
    /// </summary>
    [State]
    [Category("Device")]
    public partial string? TypeName { get; internal set; }

    /// <summary>
    /// Gets what the sensor measures as the gateway describes the type, for example Solar &amp; Wind.
    /// </summary>
    [State]
    [Category("Device")]
    public partial string? TypeDescription { get; internal set; }

    /// <summary>
    /// Gets whether the gateway currently receives the sensor.
    /// </summary>
    [State(IsDiscrete = true)]
    public partial bool IsOnline { get; internal set; }

    /// <summary>
    /// Gets the last time the gateway received the sensor, kept while the sensor is offline.
    /// It is kept in memory and starts over when the gateway connection is lost or the service restarts.
    /// </summary>
    [State]
    public partial DateTimeOffset? LastSeen { get; internal set; }

    [State(Unit = StateUnit.Percent)]
    public partial decimal? BatteryLevel { get; internal set; }

    /// <summary>
    /// Gets the reception quality in bars from 0 to 4.
    /// </summary>
    [State]
    [Category("Signal")]
    public partial int? SignalStrength { get; internal set; }

    /// <summary>
    /// Gets the received signal strength in dBm.
    /// </summary>
    [State]
    [Category("Signal")]
    public partial int? Rssi { get; internal set; }

    [Derived]
    public string? Title => TypeName != null ? $"{TypeName} {SensorId}" : SensorId;

    [Derived]
    public string IconName => IsOnline ? "Sensors" : "SensorsOff";

    [Derived]
    public string? IconColor => IsOnline ? null : "Warning";

    public EcowittSensor(string sensorId)
    {
        SensorId = sensorId;
        TypeCode = 0;
        TypeName = null;
        TypeDescription = null;
        IsOnline = false;
        LastSeen = null;
        BatteryLevel = null;
        SignalStrength = null;
        Rssi = null;
    }

    internal void Update(EcowittSensorInfo sensorInfo, DateTimeOffset now)
    {
        TypeCode = sensorInfo.TypeCode;
        TypeName = EcowittSensorTypes.GetTypeName(sensorInfo.TypeCode, sensorInfo.SensorType);
        TypeDescription = sensorInfo.Name;
        BatteryLevel = EcowittValueParser.NormalizeBatteryLevel(
            sensorInfo.Battery, EcowittSensorTypes.HasBinaryBattery(sensorInfo.TypeCode));
        SignalStrength = sensorInfo.SignalLevel;
        Rssi = sensorInfo.Rssi;

        // Without bars in the list, a reported RSSI counts as a signal.
        IsOnline = sensorInfo.SignalLevel is { } signalLevel ? signalLevel > 0 : sensorInfo.Rssi is not null;
        if (IsOnline)
        {
            LastSeen = now;
        }
    }
}
