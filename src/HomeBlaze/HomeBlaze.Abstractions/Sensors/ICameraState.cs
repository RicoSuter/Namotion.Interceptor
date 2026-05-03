using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// State interface for cameras.
/// </summary>
[SubjectAbstraction]
[Description("Reports the current captured image from a camera.")]
public interface ICameraState
{
    /// <summary>
    /// The current captured image.
    /// </summary>
    [State(Position = 270)]
    Image? CurrentImage { get; }
}
