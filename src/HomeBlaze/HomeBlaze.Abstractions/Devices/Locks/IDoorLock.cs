using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Locks;

/// <summary>
/// Combined interface for door locks.
/// Composes IDoorLockState (readable) and IDoorLockController (commandable).
/// </summary>
[SubjectAbstraction]
[Description("Combined door lock with state and control operations.")]
public interface IDoorLock : IDoorLockState, IDoorLockController
{
}
