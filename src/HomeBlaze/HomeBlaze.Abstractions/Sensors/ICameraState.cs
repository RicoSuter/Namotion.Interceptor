using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// State interface for cameras.
/// </summary>
public interface ICameraState
{
    /// <summary>
    /// The current captured image.
    /// </summary>
    [State]
    Image? CurrentImage { get; }
}
