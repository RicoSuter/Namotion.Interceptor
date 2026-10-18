using System.ComponentModel;
using System.Reactive.Subjects;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Pushover;

/// <summary>
/// A Pushover user or group that receives notifications through its <see cref="PushoverService"/>.
/// </summary>
[Description("Pushover user or group that receives notifications")]
[DisplayName("Pushover target")]
[SubjectIcon("Person")]
[InterceptorSubject]
public partial class PushoverTarget :
    IConfigurable,
    INotificationPublisher,
    IObservable<PushoverEmergencyAcknowledgedEvent>,
    IMonitoredService,
    ITitleProvider,
    IIconProvider
{
    /// <summary>
    /// The lowest <see cref="EmergencyRetry"/> Pushover allows.
    /// </summary>
    public static readonly TimeSpan MinimumEmergencyRetry = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The highest <see cref="EmergencyExpire"/> Pushover allows.
    /// </summary>
    public static readonly TimeSpan MaximumEmergencyExpire = TimeSpan.FromHours(3);

    private readonly Subject<PushoverEmergencyAcknowledgedEvent> _acknowledgedEvents = new();

    // Not intercepted: an intercepted property holding the service would attach it as a child of its own child.
    internal PushoverService? Service { get; set; }

    /// <summary>
    /// The key of this target in <see cref="PushoverService.Targets"/>, assigned together with <see cref="Service"/>.
    /// </summary>
    internal string? Key { get; set; }

    private PushoverService RequiredService =>
        Service ?? throw new InvalidOperationException($"The target '{Title}' is not part of a Pushover service.");

    [Configuration]
    public partial string Name { get; set; }

    /// <summary>
    /// The Pushover user or group key.
    /// </summary>
    [Configuration(IsSecret = true)]
    public partial string? UserKey { get; set; }

    /// <summary>
    /// How often an emergency notification repeats until acknowledged, at least 30 seconds.
    /// </summary>
    [Configuration]
    public partial TimeSpan EmergencyRetry { get; set; }

    /// <summary>
    /// How long an emergency notification repeats, at most 3 hours.
    /// </summary>
    [Configuration]
    public partial TimeSpan EmergencyExpire { get; set; }

    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    /// <summary>
    /// The devices of the user, as reported when the key was validated.
    /// </summary>
    [State]
    public partial string[] Devices { get; internal set; }

    [State]
    public partial DateTimeOffset? LastSentAt { get; internal set; }

    /// <summary>
    /// The emergency notifications by receipt: the active ones and those that ended within the retention of the
    /// service. Replaced as a whole, never changed in place. Set by the service, which tracks the emergencies by
    /// the key of the target, so an instance that replaces this one under the same key shows the same emergencies.
    /// </summary>
    [State]
    public partial Dictionary<string, PushoverEmergency> Emergencies { get; internal set; }

    [Derived]
    public string Title => string.IsNullOrEmpty(Name) ? "Pushover target" : Name;

    public string IconName => "Person";

    [Derived]
    public string? IconColor => Status switch
    {
        ServiceStatus.Running => "Success",
        ServiceStatus.Error => "Error",
        _ => null
    };

    [Derived]
    [PropertyAttribute("CancelAllEmergencies", KnownAttributes.IsEnabled)]
    public bool CancelAllEmergencies_IsEnabled
    {
        get
        {
            foreach (var emergency in Emergencies.Values)
            {
                if (emergency.Status == PushoverEmergencyStatus.Active)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public PushoverTarget()
    {
        Name = string.Empty;
        UserKey = null;
        EmergencyRetry = TimeSpan.FromSeconds(60);
        EmergencyExpire = TimeSpan.FromHours(1);

        Status = ServiceStatus.Stopped;
        StatusMessage = "Not validated yet";
        Devices = [];
        LastSentAt = null;
        Emergencies = new Dictionary<string, PushoverEmergency>();
    }

    public async Task<NotificationResult> SendNotificationAsync(Notification notification, CancellationToken cancellationToken) =>
        new() { Id = await RequiredService.SendAsync(this, notification, cancellationToken) };

    /// <summary>
    /// Stops the repeats of every active emergency notification. When some fail, the others are still canceled and
    /// the error names the failed ones.
    /// </summary>
    [Operation(Title = "Cancel all emergencies", Icon = "NotificationsOff", Position = 2)]
    public Task CancelAllEmergenciesAsync(CancellationToken cancellationToken) =>
        Service is { } service && Key is { } key
            ? service.EmergencyTracker.CancelAllAsync(key, cancellationToken)
            : Task.CompletedTask;

    // Each subscriber is wrapped so that one throwing from OnNext cannot stop the subject from notifying the
    // others, or propagate into the receipt-polling loop that raises the event.
    public IDisposable Subscribe(IObserver<PushoverEmergencyAcknowledgedEvent> observer) =>
        _acknowledgedEvents.Subscribe(
            value =>
            {
                try
                {
                    observer.OnNext(value);
                }
                catch (Exception exception)
                {
                    Service?.Logger.LogWarning(exception, "A Pushover emergency acknowledgement subscriber of target {Target} threw", Title);
                }
            },
            observer.OnError,
            observer.OnCompleted);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        Service?.RequestValidation();
        return Task.CompletedTask;
    }

    internal void SetValidated(string[] devices)
    {
        // Status is the discrete value readers wait on; writing it last avoids a window where a waiter sees the
        // new Status with the previous Devices or StatusMessage still in place.
        Devices = devices;
        StatusMessage = null;
        Status = ServiceStatus.Running;
    }

    internal void SetValidationFailed(string message)
    {
        Devices = [];
        StatusMessage = message;
        Status = ServiceStatus.Error;
    }

    internal void PublishAcknowledged(PushoverEmergencyAcknowledgedEvent acknowledgedEvent) =>
        _acknowledgedEvents.OnNext(acknowledgedEvent);

    partial void OnEmergencyRetryChanging(ref TimeSpan newValue, ref bool cancel)
    {
        if (newValue < MinimumEmergencyRetry)
        {
            newValue = MinimumEmergencyRetry;
        }
    }

    partial void OnEmergencyExpireChanging(ref TimeSpan newValue, ref bool cancel)
    {
        if (newValue < MinimumEmergencyRetry)
        {
            newValue = MinimumEmergencyRetry;
        }
        else if (newValue > MaximumEmergencyExpire)
        {
            newValue = MaximumEmergencyExpire;
        }
    }

    partial void OnDevicesChanging(ref string[] newValue, ref bool cancel) => newValue ??= [];
}
