using HomeBlaze.Abstractions.Attributes;

namespace MyCompany.Abstractions;

/// <summary>
/// Shared interface for MyCompany device plugins.
/// </summary>
[SubjectAbstraction]
public interface IMyDevice
{
    [State]
    string DeviceName { get; }

    [State]
    string DeviceType { get; }

    [State]
    double? CurrentValue { get; }
}
