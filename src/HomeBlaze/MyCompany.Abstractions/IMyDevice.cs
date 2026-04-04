using HomeBlaze.Abstractions.Attributes;

namespace MyCompany.Abstractions;

/// <summary>
/// Shared interface for MyCompany device plugins.
/// </summary>
[SubjectAbstraction]
public interface IMyDevice
{
    string DeviceName { get; }
    string DeviceType { get; }
    double? CurrentValue { get; }
}
