using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Controller interface for cameras.
/// </summary>
[SubjectAbstraction]
[Description("Controls a camera with image capture operations.")]
public interface ICameraController
{
    /// <summary>
    /// Captures an image from the camera.
    /// </summary>
    [Operation]
    Task<Image?> CaptureImageAsync(CancellationToken cancellationToken);
}
