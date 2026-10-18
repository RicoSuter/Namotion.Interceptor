using System.ComponentModel;
using System.Reactive.Subjects;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using Namotion.Devices.Pushover.Client;
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
    internal static readonly TimeSpan MinimumEmergencyRetry = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumEmergencyExpire = TimeSpan.FromHours(3);

    private readonly Subject<PushoverEmergencyAcknowledgedEvent> _acknowledgedEvents = new();

    // The send path and the receipt polling run on different threads; the receipt and the emergency state change
    // together under this lock, so a poll of a replaced receipt cannot overwrite the state of the new one.
    private readonly Lock _emergencyLock = new();
    private string? _emergencyReceipt;
    private DateTimeOffset _emergencyExpiresAt;

    // Not intercepted: an intercepted property holding the service would attach it as a child of its own child.
    internal PushoverService? Service { get; set; }

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

    [State(IsDiscrete = true)]
    public partial bool IsEmergencyActive { get; internal set; }

    [State(IsDiscrete = true)]
    public partial bool IsEmergencyAcknowledged { get; internal set; }

    [State]
    public partial DateTimeOffset? EmergencyAcknowledgedAt { get; internal set; }

    [State]
    public partial string? EmergencyAcknowledgedByDevice { get; internal set; }

    [State(IsDiscrete = true)]
    public partial bool IsEmergencyExpired { get; internal set; }

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
    [PropertyAttribute("CancelEmergency", KnownAttributes.IsEnabled)]
    public bool CancelEmergency_IsEnabled => IsEmergencyActive;

    internal string? EmergencyReceipt
    {
        get
        {
            lock (_emergencyLock)
            {
                return _emergencyReceipt;
            }
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
        IsEmergencyActive = false;
        IsEmergencyAcknowledged = false;
        EmergencyAcknowledgedAt = null;
        EmergencyAcknowledgedByDevice = null;
        IsEmergencyExpired = false;
    }

    public Task SendNotificationAsync(Notification notification, CancellationToken cancellationToken)
    {
        var service = Service ?? throw new InvalidOperationException($"The target '{Title}' is not part of a Pushover service.");
        return service.SendAsync(this, notification, cancellationToken);
    }

    [Operation(Title = "Cancel emergency", Icon = "NotificationsOff", Position = 2)]
    public async Task CancelEmergencyAsync(CancellationToken cancellationToken)
    {
        if (EmergencyReceipt is not { } receipt)
        {
            return;
        }

        var service = Service ?? throw new InvalidOperationException($"The target '{Title}' is not part of a Pushover service.");
        await service.CancelReceiptAsync(receipt, cancellationToken);
        CloseEmergency(receipt);
    }

    public IDisposable Subscribe(IObserver<PushoverEmergencyAcknowledgedEvent> observer) =>
        _acknowledgedEvents.Subscribe(observer);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        Service?.RequestValidation();
        return Task.CompletedTask;
    }

    internal void SetValidated(string[] devices)
    {
        Devices = devices;
        Status = ServiceStatus.Running;
        StatusMessage = null;
    }

    internal void SetValidationFailed(string message)
    {
        Devices = [];
        Status = ServiceStatus.Error;
        StatusMessage = message;
    }

    internal void StartEmergency(string receipt, DateTimeOffset now)
    {
        lock (_emergencyLock)
        {
            _emergencyReceipt = receipt;
            _emergencyExpiresAt = now + EmergencyExpire;
            IsEmergencyAcknowledged = false;
            EmergencyAcknowledgedAt = null;
            EmergencyAcknowledgedByDevice = null;
            IsEmergencyExpired = false;
            IsEmergencyActive = true;
        }
    }

    internal void ApplyReceipt(string receipt, PushoverResponse response)
    {
        PushoverEmergencyAcknowledgedEvent? acknowledgedEvent = null;
        lock (_emergencyLock)
        {
            if (_emergencyReceipt != receipt)
            {
                return;
            }

            if (response.Acknowledged == 1)
            {
                var acknowledgedAt = DateTimeOffset.FromUnixTimeSeconds(response.AcknowledgedAt);
                _emergencyReceipt = null;
                IsEmergencyAcknowledged = true;
                EmergencyAcknowledgedAt = acknowledgedAt;
                EmergencyAcknowledgedByDevice = response.AcknowledgedByDevice;
                IsEmergencyActive = false;
                acknowledgedEvent = new PushoverEmergencyAcknowledgedEvent
                {
                    Timestamp = acknowledgedAt,
                    Target = this,
                    AcknowledgedByDevice = response.AcknowledgedByDevice
                };
            }
            else if (response.Expired == 1)
            {
                _emergencyReceipt = null;
                IsEmergencyExpired = true;
                IsEmergencyActive = false;
            }
        }

        if (acknowledgedEvent is not null)
        {
            _acknowledgedEvents.OnNext(acknowledgedEvent);
        }
    }

    internal void ApplyReceiptError(string receipt, DateTimeOffset now)
    {
        lock (_emergencyLock)
        {
            // A receipt that cannot be read is given up once the emergency has expired on the Pushover side.
            if (_emergencyReceipt == receipt && now > _emergencyExpiresAt)
            {
                _emergencyReceipt = null;
                IsEmergencyExpired = true;
                IsEmergencyActive = false;
            }
        }
    }

    private void CloseEmergency(string receipt)
    {
        lock (_emergencyLock)
        {
            if (_emergencyReceipt == receipt)
            {
                _emergencyReceipt = null;
                IsEmergencyActive = false;
            }
        }
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

    partial void OnDevicesChanging(ref string[] newValue, ref bool cancel) => newValue ??= [];
}
