using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Samples;

/// <summary>
/// A simulated door lock with a door sensor. Lock actions pass through their transition for
/// <see cref="TransitionDuration"/>.
/// </summary>
[Category("Samples")]
[Description("Simulated door lock with a door sensor")]
[DisplayName("Simulated door lock")]
[SubjectIcon("Lock")]
[InterceptorSubject]
public partial class SimulatedDoorLock : IConfigurable, IDoorLock, IDoorLatchController, IDoorSensor, ITitleProvider, IIconProvider
{
    // Guards the generation together with the state writes that depend on it.
    private readonly System.Threading.Lock _transitionLock = new();
    private int _transitionGeneration;

    public SimulatedDoorLock()
    {
        Name = string.Empty;
        TransitionDuration = TimeSpan.FromSeconds(1);
        LockState = DoorLockState.Locked;
        DoorState = HomeBlaze.Abstractions.Sensors.DoorState.Closed;
    }

    /// <summary>Name of the lock.</summary>
    [Configuration]
    public partial string Name { get; set; }

    /// <summary>How long a lock action takes.</summary>
    [Configuration]
    public partial TimeSpan TransitionDuration { get; set; }

    public partial DoorLockState? LockState { get; set; }

    public partial DoorState? DoorState { get; set; }

    public string? Title => Name;

    [Derived]
    public string? IconName => LockState switch
    {
        DoorLockState.Locked => "Lock",
        DoorLockState.Jammed => "LockReset",
        _ => "LockOpen"
    };

    [Derived]
    [PropertyAttribute("Lock", KnownAttributes.IsEnabled)]
    public bool Lock_IsEnabled => LockState is DoorLockState.Unlocked or DoorLockState.Unlatched;

    [Derived]
    [PropertyAttribute("Unlock", KnownAttributes.IsEnabled)]
    public bool Unlock_IsEnabled => LockState is DoorLockState.Locked;

    [Derived]
    [PropertyAttribute("ToggleLock", KnownAttributes.IsEnabled)]
    public bool ToggleLock_IsEnabled => LockState is DoorLockState.Locked or DoorLockState.Unlocked;

    [Derived]
    [PropertyAttribute("Unlatch", KnownAttributes.IsEnabled)]
    public bool Unlatch_IsEnabled => LockState is DoorLockState.Locked or DoorLockState.Unlocked;

    public Task LockAsync(CancellationToken cancellationToken) =>
        TransitionAsync(DoorLockState.Locking, DoorLockState.Locked, cancellationToken);

    public Task UnlockAsync(CancellationToken cancellationToken) =>
        TransitionAsync(DoorLockState.Unlocking, DoorLockState.Unlocked, cancellationToken);

    public Task UnlatchAsync(CancellationToken cancellationToken) =>
        TransitionAsync(DoorLockState.Unlatching, DoorLockState.Unlatched, cancellationToken);

    [Operation(Title = "Set door state", Description = "Sets the state of the simulated door.", Position = 10)]
    [Category("Door")]
    public void SetDoorState(DoorState? doorState) => DoorState = doorState;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private async Task TransitionAsync(DoorLockState transition, DoorLockState target, CancellationToken cancellationToken)
    {
        int generation;
        lock (_transitionLock)
        {
            generation = ++_transitionGeneration;
            LockState = transition;
        }

        try
        {
            if (TransitionDuration > TimeSpan.Zero)
            {
                await Task.Delay(TransitionDuration, cancellationToken);
            }
        }
        finally
        {
            // The simulated motor finishes its move also when the caller stops waiting, unless a newer operation or a
            // direct write of the state replaced the move meanwhile.
            lock (_transitionLock)
            {
                if (_transitionGeneration == generation && LockState == transition)
                {
                    LockState = target;
                }
            }
        }
    }
}
