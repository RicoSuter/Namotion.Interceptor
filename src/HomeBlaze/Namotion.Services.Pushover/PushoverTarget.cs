using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using Namotion.Interceptor.Attributes;

namespace Namotion.Services.Pushover;

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
    }

    public async Task<NotificationResult> SendNotificationAsync(Notification notification, CancellationToken cancellationToken) =>
        new() { Id = await RequiredService.SendAsync(this, notification, cancellationToken) };

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
}
