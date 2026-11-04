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
    [Category("Device")]
    string? Manufacturer { get; }

    [State(Position = 801)]
    [Category("Device")]
    string? Model { get; }

    [State(Position = 802)]
    [Category("Device")]
    string? ProductCode { get; }

    [State(Position = 803)]
    [Category("Device")]
    string? SerialNumber { get; }

    [State(Position = 804)]
    [Category("Device")]
    string? HardwareRevision { get; }
}
