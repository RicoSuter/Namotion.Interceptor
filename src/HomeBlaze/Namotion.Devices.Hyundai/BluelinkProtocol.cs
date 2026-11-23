namespace Namotion.Devices.Hyundai;

/// <summary>The protocol of a vehicle, from <c>ccuCCS2ProtocolSupport</c> of the vehicle list.</summary>
public enum BluelinkProtocol
{
    /// <summary>Vehicles without a ccNC head unit, for example the first Kona Electric. Commands need no PIN.</summary>
    Legacy,

    /// <summary>Vehicles with a ccNC head unit. Charging and climate commands need the PIN.</summary>
    Ccs2
}
