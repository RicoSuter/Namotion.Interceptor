using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Locks;

/// <summary>
/// Controller interface for door locks that can pull the latch.
/// </summary>
[SubjectAbstraction]
[Description("Pulls the latch of a door lock so that the door can be pushed open.")]
public interface IDoorLatchController
{
    /// <summary>
    /// Unlocks the door when needed and pulls the latch, so that the door can be pushed open.
    /// </summary>
    [Operation(RequiresConfirmation = true)]
    Task UnlatchAsync(CancellationToken cancellationToken);
}
