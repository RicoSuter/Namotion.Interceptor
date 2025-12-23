using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices;

/// <summary>
/// Command-only interface for controlling switch devices.
/// </summary>
public interface ISwitchController
{
    /// <summary>
    /// Turns the switch on.
    /// </summary>
    [Operation]
    Task TurnOnAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Turns the switch off.
    /// </summary>
    [Operation]
    Task TurnOffAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Toggles the switch state. Default implementation checks ISwitchState.IsOn.
    /// </summary>
    [Operation]
    async Task ToggleAsync(CancellationToken cancellationToken)
    {
        if (this is ISwitchState { IsOn: false })
            await TurnOnAsync(cancellationToken);
        else if (this is ISwitchState { IsOn: true })
            await TurnOffAsync(cancellationToken);
    }
}
