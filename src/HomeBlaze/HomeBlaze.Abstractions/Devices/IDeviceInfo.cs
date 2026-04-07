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
    [State]
    string? Manufacturer { get; }

    [State]
    string? Model { get; }

    [State]
    string? ProductCode { get; }

    [State]
    string? SerialNumber { get; }

    [State]
    string? HardwareRevision { get; }
}
