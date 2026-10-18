using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
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

    /// <summary>
    /// How many emergency notifications may be active at once, at least 1. Sending another one fails before any
    /// request.
    /// </summary>
    [Configuration]
    public partial int MaximumActiveEmergencies { get; set; }

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
        MaximumActiveEmergencies = 5;

        Status = ServiceStatus.Stopped;
        StatusMessage = "Not validated yet";
        Devices = [];
        LastSentAt = null;
        Emergencies = new Dictionary<string, PushoverEmergency>();
    }

    public async Task<NotificationResult> SendNotificationAsync(Notification notification, CancellationToken cancellationToken) =>
        new() { Id = await RequiredService.SendAsync(this, notification, cancellationToken) };

    /// <summary>
    /// Stops the repeats of every emergency notification of this target by its tag, also those sent before a restart.
    /// </summary>
    [Operation(Title = "Cancel all emergencies", Icon = "NotificationsOff", Position = 2, RequiresConfirmation = true)]
    public Task CancelAllEmergenciesAsync(CancellationToken cancellationToken) =>
        Service is { } service && Key is { } key
            ? service.EmergencyTracker.CancelAllAsync(key, cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// Emits one event per acknowledged emergency notification of the key of this target. The subscription keeps
    /// receiving them when a configuration reload replaces this instance under the same key.
    /// </summary>
    /// <exception cref="InvalidOperationException">The target is not part of a Pushover service.</exception>
    public IDisposable Subscribe(IObserver<PushoverEmergencyAcknowledgedEvent> observer) =>
        Key is { } key && Service is { } service
            ? service.AcknowledgedEvents.Subscribe(observer, key)
            : throw new InvalidOperationException($"The target '{Title}' is not part of a Pushover service.");

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

    partial void OnMaximumActiveEmergenciesChanging(ref int newValue, ref bool cancel)
    {
        if (newValue < 1)
        {
            newValue = 1;
        }
    }

    partial void OnDevicesChanging(ref string[] newValue, ref bool cancel) => newValue ??= [];
}
