namespace Namotion.Devices.Pushover;

/// <summary>
/// The state of a <see cref="PushoverEmergency"/>.
/// </summary>
public enum PushoverEmergencyStatus
{
    /// <summary>
    /// The notification repeats and waits for acknowledgement.
    /// </summary>
    Active,

    /// <summary>
    /// The recipient acknowledged the notification.
    /// </summary>
    Acknowledged,

    /// <summary>
    /// The notification stopped repeating without acknowledgement.
    /// </summary>
    Expired,

    /// <summary>
    /// The notification was canceled before it was acknowledged.
    /// </summary>
    Canceled
}
