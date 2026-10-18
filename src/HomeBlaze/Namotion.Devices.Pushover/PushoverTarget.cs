using System.ComponentModel;
using System.Reactive.Subjects;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using Microsoft.Extensions.Logging;
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
    /// <summary>
    /// The lowest <see cref="EmergencyRetry"/> Pushover allows.
    /// </summary>
    public static readonly TimeSpan MinimumEmergencyRetry = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The highest <see cref="EmergencyExpire"/> Pushover allows.
    /// </summary>
    public static readonly TimeSpan MaximumEmergencyExpire = TimeSpan.FromHours(3);

    private readonly Subject<PushoverEmergencyAcknowledgedEvent> _acknowledgedEvents = new();

    // The send path and the receipt polling run on different threads; the receipt and the emergency state change
    // together under this lock, so a poll of a replaced receipt cannot overwrite the state of the new one.
    private readonly Lock _emergencyLock = new();
    private string? _emergencyReceipt;
    private DateTimeOffset _emergencyExpiresAt;
    private DateTimeOffset? _lastReceiptPolledAt;

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

    public async Task<NotificationResult> SendNotificationAsync(Notification notification, CancellationToken cancellationToken)
    {
        var response = await RequiredService.SendAsync(this, notification, cancellationToken);
        return new NotificationResult { Id = response.Request };
    }

    [Operation(Title = "Cancel emergency", Icon = "NotificationsOff", Position = 2)]
    public async Task CancelEmergencyAsync(CancellationToken cancellationToken)
    {
        if (EmergencyReceipt is not { } receipt)
        {
            return;
        }

        await RequiredService.CancelReceiptAsync(receipt, cancellationToken);
        CloseEmergency(receipt);
    }

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

    /// <summary>
    /// Starts tracking a newly sent emergency receipt, returning the receipt of an emergency that was still open
    /// on this target, or null if none was; the caller cancels that previous receipt, since a target only waits
    /// on one emergency at a time.
    /// </summary>
    internal string? StartEmergency(string receipt, DateTimeOffset now)
    {
        lock (_emergencyLock)
        {
            var previousReceipt = _emergencyReceipt;
            _emergencyReceipt = receipt;
            _emergencyExpiresAt = now + EmergencyExpire;
            // A new emergency's first poll may happen at once, regardless of when the previous one was last polled.
            _lastReceiptPolledAt = null;
            IsEmergencyAcknowledged = false;
            EmergencyAcknowledgedAt = null;
            EmergencyAcknowledgedByDevice = null;
            IsEmergencyExpired = false;
            IsEmergencyActive = true;
            return previousReceipt;
        }
    }

    /// <summary>
    /// Copies the open emergency receipt, expiry, poll time and public emergency state from <paramref name="previous"/>
    /// onto this instance. Used when a configuration reload replaces a target's instance but keeps its dictionary key,
    /// so the new instance keeps polling and can still cancel the emergency the old instance started.
    /// </summary>
    internal void AdoptEmergencyFrom(PushoverTarget previous)
    {
        // Locked in a fixed order (the previous instance, then this one) since this is the only direction the
        // method is ever called in, so the two locks can never be taken in the opposite order elsewhere.
        lock (previous._emergencyLock)
        {
            lock (_emergencyLock)
            {
                _emergencyReceipt = previous._emergencyReceipt;
                _emergencyExpiresAt = previous._emergencyExpiresAt;
                _lastReceiptPolledAt = previous._lastReceiptPolledAt;
                IsEmergencyActive = previous.IsEmergencyActive;
                IsEmergencyAcknowledged = previous.IsEmergencyAcknowledged;
                EmergencyAcknowledgedAt = previous.EmergencyAcknowledgedAt;
                EmergencyAcknowledgedByDevice = previous.EmergencyAcknowledgedByDevice;
                IsEmergencyExpired = previous.IsEmergencyExpired;
            }
        }
    }

    /// <summary>
    /// Returns the receipt to poll when an emergency is active and it was not already polled within
    /// <paramref name="minimumInterval"/>, recording <paramref name="now"/> as the new poll time; otherwise null.
    /// </summary>
    internal string? TryStartReceiptPoll(DateTimeOffset now, TimeSpan minimumInterval)
    {
        lock (_emergencyLock)
        {
            if (_emergencyReceipt is not { } receipt)
            {
                return null;
            }

            if (_lastReceiptPolledAt is { } lastPolledAt && now - lastPolledAt < minimumInterval)
            {
                return null;
            }

            _lastReceiptPolledAt = now;
            return receipt;
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
                EndEmergency(isExpired: true);
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
                EndEmergency(isExpired: true);
            }
        }
    }

    private void CloseEmergency(string receipt)
    {
        lock (_emergencyLock)
        {
            if (_emergencyReceipt == receipt)
            {
                EndEmergency(isExpired: false);
            }
        }
    }

    // Caller must hold _emergencyLock.
    private void EndEmergency(bool isExpired)
    {
        _emergencyReceipt = null;
        if (isExpired)
        {
            IsEmergencyExpired = true;
        }

        IsEmergencyActive = false;
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
