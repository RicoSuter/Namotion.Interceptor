using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Pushover;

/// <summary>
/// An emergency notification of a <see cref="PushoverTarget"/>, kept under its receipt while it repeats and, as one of
/// the newest 10 of its target, after it ended.
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
    /// The title of the notification as sent, or null when it had none.
    /// </summary>
    [State]
    public partial string? NotificationTitle { get; private set; }

    [State]
    public partial string Message { get; private set; }

    [State]
    public partial DateTimeOffset SentAt { get; private set; }

    [State(IsDiscrete = true)]
    public partial PushoverEmergencyStatus Status { get; internal set; }

    [State]
    public partial DateTimeOffset? AcknowledgedAt { get; internal set; }

    [Derived]
    public string Title => NotificationTitle ?? "Emergency";

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

    private readonly PushoverEmergencyTracker _tracker;

    /// <summary>
    /// The key of the target in <see cref="PushoverService.Targets"/>.
    /// </summary>
    internal string TargetKey { get; }

    /// <summary>
    /// The timestamp of <see cref="PushoverService.TimeProvider"/> when tracking started, right after the send, which
    /// measures the intervals from the send unaffected by changes of the wall clock.
    /// </summary>
    internal long SentTimestamp { get; init; }

    /// <summary>
    /// How long the notification repeats on the Pushover side, from <see cref="SentTimestamp"/>.
    /// </summary>
    internal TimeSpan Expire { get; }

    // Written only under the lock of the tracker.
    internal long? LastPolledTimestamp { get; set; }

    internal PushoverEmergency(
        PushoverEmergencyTracker tracker, string targetKey, string receipt, string? title, string message, DateTimeOffset sentAt, TimeSpan expire)
    {
        _tracker = tracker;
        TargetKey = targetKey;
        Receipt = receipt;
        NotificationTitle = title;
        Message = message;
        SentAt = sentAt;
        Expire = expire;

        Status = PushoverEmergencyStatus.Active;
        AcknowledgedAt = null;
    }

    /// <summary>
    /// Stops the repeats of the notification and marks it canceled; does nothing when it is no longer active.
    /// </summary>
    [Operation(Title = "Cancel", Icon = "NotificationsOff", Position = 1, RequiresConfirmation = true)]
    public Task CancelAsync(CancellationToken cancellationToken) =>
        _tracker.CancelAsync(this, cancellationToken);
}
