using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Pushover;

/// <summary>
/// An emergency notification of a <see cref="PushoverTarget"/>, kept under its receipt while it repeats and for a
/// while after it ended.
/// </summary>
[Description("Pushover emergency notification that repeats until acknowledged")]
[DisplayName("Pushover emergency")]
[SubjectIcon("NotificationImportant")]
[InterceptorSubject]
public partial class PushoverEmergency : ITitleProvider, IIconProvider
{
    /// <summary>
    /// The receipt Pushover returned for the notification, the same as the <c>NotificationResult.Id</c> of its send.
    /// </summary>
    [State]
    public partial string Receipt { get; private set; }

    /// <summary>
    /// The title of the notification, or null when it had none.
    /// </summary>
    [State]
    public partial string? Title { get; private set; }

    [State]
    public partial string Message { get; private set; }

    [State]
    public partial DateTimeOffset SentAt { get; private set; }

    [State(IsDiscrete = true)]
    public partial PushoverEmergencyStatus Status { get; internal set; }

    [State]
    public partial DateTimeOffset? AcknowledgedAt { get; internal set; }

    /// <summary>
    /// The name of the device the notification was acknowledged on.
    /// </summary>
    [State]
    public partial string? AcknowledgedByDevice { get; internal set; }

    string? ITitleProvider.Title => Title ?? "Emergency";

    public string IconName => "NotificationImportant";

    [Derived]
    public string? IconColor => Status switch
    {
        PushoverEmergencyStatus.Active => "Error",
        PushoverEmergencyStatus.Acknowledged => "Success",
        _ => null
    };

    [Derived]
    [PropertyAttribute("Cancel", KnownAttributes.IsEnabled)]
    public bool Cancel_IsEnabled => Status == PushoverEmergencyStatus.Active;

    // Not intercepted, for the same reason as Service on PushoverTarget. The target owns the emergency; a
    // configuration reload moves it to the target instance that replaces its owner.
    internal PushoverTarget Target { get; set; }

    // The members below are written only under the lock of the owning Target.
    internal DateTimeOffset ExpiresAt { get; set; }

    internal DateTimeOffset? LastPolledAt { get; set; }

    internal DateTimeOffset? EndedAt { get; set; }

    internal PushoverEmergency(PushoverTarget target, string receipt, string? title, string message, DateTimeOffset sentAt)
    {
        Target = target;
        Receipt = receipt;
        Title = title;
        Message = message;
        SentAt = sentAt;
        ExpiresAt = sentAt + target.EmergencyExpire;

        Status = PushoverEmergencyStatus.Active;
        AcknowledgedAt = null;
        AcknowledgedByDevice = null;
    }

    /// <summary>
    /// Stops the repeats of the notification; does nothing when it is no longer active.
    /// </summary>
    [Operation(Title = "Cancel", Icon = "NotificationsOff", Position = 1)]
    public Task CancelAsync(CancellationToken cancellationToken) =>
        Target.CancelEmergencyAsync(this, cancellationToken);
}
