using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices;

/// <summary>
/// Command and state interface for hubs that add new devices during a pairing window, such as Z-Wave inclusion or Zigbee permit join.
/// Pairing with a setup code, such as Matter commissioning, is not covered.
/// </summary>
[SubjectAbstraction]
[Description("Opens and closes the window in which a hub accepts new devices.")]
public interface IPairingController
{
    /// <summary>
    /// Whether the hub accepts new devices, or null until the hub reports its state. Turns false when the window closes
    /// on its own or when <see cref="StopPairingAsync"/> is called, and depending on the protocol after a device was added.
    /// </summary>
    [State(IsDiscrete = true, Position = 190)]
    [Category("Pairing")]
    bool? IsPairing { get; }

    /// <summary>
    /// Opens the pairing window, or restarts an open window with the new duration.
    /// </summary>
    /// <param name="duration">How long the window stays open, or null for the default of the hub. A duration above the maximum of the hub is clamped to that maximum.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> is zero or negative.</exception>
    [Operation(Title = "Start Pairing", Icon = "Add", Position = 1, Description = "Opens the window in which the hub accepts new devices.")]
    [Category("Pairing")]
    Task StartPairingAsync(TimeSpan? duration, CancellationToken cancellationToken);

    /// <summary>
    /// Closes the pairing window; does nothing while no window is open.
    /// </summary>
    [Operation(Title = "Stop Pairing", Icon = "Stop", Position = 2, Description = "Closes the window in which the hub accepts new devices.")]
    [Category("Pairing")]
    Task StopPairingAsync(CancellationToken cancellationToken);
}
