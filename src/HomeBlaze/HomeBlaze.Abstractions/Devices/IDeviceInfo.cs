using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices;

/// <summary>
/// Reports hardware identity: manufacturer, model, product code, serial number.
/// Inspired by OPC UA Device Nameplate.
/// </summary>
[SubjectAbstraction]
[Description("Reports hardware identity information.")]
public interface IDeviceInfo
{
    [State(Position = 800)]
    string? Manufacturer { get; }

    [State(Position = 801)]
    string? Model { get; }

    [State(Position = 802)]
    string? ProductCode { get; }

    [State(Position = 803)]
    string? SerialNumber { get; }

    [State(Position = 804)]
    string? HardwareRevision { get; }
}
