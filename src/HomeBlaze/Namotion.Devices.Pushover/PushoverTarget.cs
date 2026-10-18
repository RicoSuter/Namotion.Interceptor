using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
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

    // The send path, the operations and the receipt polling run on different threads; Emergencies is replaced and
    // the state of its emergencies changes only under this lock, so a late poll or cancel of an emergency that
    // ended or moved to another target cannot overwrite its state.
    private readonly Lock _emergencyLock = new();

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

    /// <summary>
    /// The emergency notifications by receipt: the active ones and those that ended within the retention of the
    /// service. Replaced as a whole, never changed in place.
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
    public bool CancelAllEmergencies_IsEnabled => HasActiveEmergencies;

    internal bool HasActiveEmergencies
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

    public async Task<NotificationResult> SendNotificationAsync(Notification notification, CancellationToken cancellationToken)
    {
        var response = await RequiredService.SendAsync(this, notification, cancellationToken);
        return new NotificationResult { Id = response.Receipt ?? response.Request };
    }

    /// <summary>
    /// Stops the repeats of every active emergency notification. When some fail, the others are still canceled and
    /// the error names the failed ones.
    /// </summary>
    [Operation(Title = "Cancel all emergencies", Icon = "NotificationsOff", Position = 2)]
    public Task CancelAllEmergenciesAsync(CancellationToken cancellationToken) =>
        CancelActiveEmergenciesAsync([this], cancellationToken);

    /// <summary>
    /// Cancels the active emergencies of all <paramref name="targets"/> at once, throwing one
    /// <see cref="PushoverException"/> that names each failed emergency.
    /// </summary>
    internal static Task CancelActiveEmergenciesAsync(PushoverTarget[] targets, CancellationToken cancellationToken)
    {
        var emergencies = targets.SelectMany(target => target.GetActiveEmergencies()).ToArray();
        return PushoverBatch.RunAllAsync(
            emergencies,
            emergency => emergency.CancelAsync(cancellationToken),
            emergency => $"{emergency.Target.Title} ({((ITitleProvider)emergency).Title})",
            "Canceling",
            "emergencies",
            cancellationToken);
    }

    internal async Task CancelEmergencyAsync(PushoverEmergency emergency, CancellationToken cancellationToken)
    {
        if (emergency.Status != PushoverEmergencyStatus.Active)
        {
            return;
        }

        await RequiredService.CancelReceiptAsync(emergency.Receipt, cancellationToken);
        EndEmergency(emergency, PushoverEmergencyStatus.Canceled);
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
    /// Starts tracking a newly sent emergency notification under its <paramref name="receipt"/>, next to the
    /// emergencies the target already tracks.
    /// </summary>
    internal PushoverEmergency AddEmergency(string receipt, string? title, string message, DateTimeOffset sentAt)
    {
        var emergency = new PushoverEmergency(this, receipt, title, message, sentAt);
        lock (_emergencyLock)
        {
            Emergencies = new Dictionary<string, PushoverEmergency>(Emergencies) { [receipt] = emergency };
        }

        return emergency;
    }

    /// <summary>
    /// Moves the emergencies of <paramref name="previous"/>, the same instances, onto this instance. Used when a
    /// configuration reload replaces a target's instance but keeps its dictionary key, so the new instance keeps
    /// polling and can still cancel the emergencies the old instance started.
    /// </summary>
    internal void AdoptEmergenciesFrom(PushoverTarget previous)
    {
        // Locked in a fixed order (the previous instance, then this one) since this is the only direction the
        // method is ever called in, so the two locks can never be taken in the opposite order elsewhere.
        lock (previous._emergencyLock)
        {
            lock (_emergencyLock)
            {
                var emergencies = previous.Emergencies;
                foreach (var emergency in emergencies.Values)
                {
                    emergency.Target = this;
                }

                Emergencies = emergencies;
                previous.Emergencies = new Dictionary<string, PushoverEmergency>();
            }
        }
    }

    /// <summary>
    /// Cancels the active emergencies of this target, which was removed from <paramref name="service"/>, in the
    /// background, logging failures. Returns whether there were any.
    /// </summary>
    internal bool CancelActiveEmergenciesAfterRemoval(PushoverService service)
    {
        var emergencies = GetActiveEmergencies();
        if (emergencies.Length == 0)
        {
            return false;
        }

        _ = CancelEmergenciesInBackgroundAsync(service, emergencies);
        return true;
    }

    // Fire-and-forget: a target dropped from the service is no longer polled by the background loop, so its active
    // emergencies are canceled here instead, outside the request that is replacing the configuration.
    private async Task CancelEmergenciesInBackgroundAsync(PushoverService service, PushoverEmergency[] emergencies)
    {
        foreach (var emergency in emergencies)
        {
            try
            {
                await service.CancelReceiptAsync(emergency.Receipt, CancellationToken.None);
                EndEmergency(emergency, PushoverEmergencyStatus.Canceled);
            }
            catch (Exception exception)
            {
                service.Logger.LogWarning(
                    exception, "Canceling a Pushover emergency of removed target {Target} failed", Title);
            }
        }
    }

    private PushoverEmergency[] GetActiveEmergencies()
    {
        lock (_emergencyLock)
        {
            return Emergencies.Values.Where(emergency => emergency.Status == PushoverEmergencyStatus.Active).ToArray();
        }
    }

    /// <summary>
    /// Returns the receipts of the active emergencies that were not already polled within
    /// <paramref name="minimumInterval"/>, recording <paramref name="now"/> as their new poll time.
    /// </summary>
    internal string[] StartReceiptPolls(DateTimeOffset now, TimeSpan minimumInterval)
    {
        lock (_emergencyLock)
        {
            List<string>? receipts = null;
            foreach (var emergency in Emergencies.Values)
            {
                if (emergency.Status != PushoverEmergencyStatus.Active ||
                    (emergency.LastPolledAt is { } lastPolledAt && now - lastPolledAt < minimumInterval))
                {
                    continue;
                }

                emergency.LastPolledAt = now;
                (receipts ??= []).Add(emergency.Receipt);
            }

            return receipts?.ToArray() ?? [];
        }
    }

    internal void ApplyReceipt(string receipt, PushoverResponse response)
    {
        PushoverEmergencyAcknowledgedEvent? acknowledgedEvent = null;
        lock (_emergencyLock)
        {
            if (!TryGetActiveEmergency(receipt, out var emergency))
            {
                return;
            }

            if (response.Acknowledged == 1)
            {
                var acknowledgedAt = DateTimeOffset.FromUnixTimeSeconds(response.AcknowledgedAt);
                emergency.AcknowledgedAt = acknowledgedAt;
                emergency.AcknowledgedByDevice = response.AcknowledgedByDevice;
                EndEmergencyLocked(emergency, PushoverEmergencyStatus.Acknowledged);
                acknowledgedEvent = new PushoverEmergencyAcknowledgedEvent
                {
                    Timestamp = acknowledgedAt,
                    Target = this,
                    Emergency = emergency,
                    Id = receipt,
                    AcknowledgedByDevice = response.AcknowledgedByDevice
                };
            }
            else if (response.Expired == 1)
            {
                EndEmergencyLocked(emergency, PushoverEmergencyStatus.Expired);
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
            if (TryGetActiveEmergency(receipt, out var emergency) && now > emergency.ExpiresAt)
            {
                EndEmergencyLocked(emergency, PushoverEmergencyStatus.Expired);
            }
        }
    }

    /// <summary>
    /// Removes the emergencies that ended at least <paramref name="retention"/> before <paramref name="now"/>, and
    /// returns when the next of the remaining ended ones is due for removal, or null when none is.
    /// </summary>
    internal DateTimeOffset? RemoveFinishedEmergencies(DateTimeOffset now, TimeSpan retention)
    {
        lock (_emergencyLock)
        {
            DateTimeOffset? nextRemovalAt = null;
            Dictionary<string, PushoverEmergency>? remaining = null;
            foreach (var (receipt, emergency) in Emergencies)
            {
                if (emergency.EndedAt is not { } endedAt)
                {
                    continue;
                }

                var removalAt = endedAt + retention;
                if (removalAt <= now)
                {
                    (remaining ??= new Dictionary<string, PushoverEmergency>(Emergencies)).Remove(receipt);
                }
                else if (nextRemovalAt is null || removalAt < nextRemovalAt)
                {
                    nextRemovalAt = removalAt;
                }
            }

            if (remaining is not null)
            {
                Emergencies = remaining;
            }

            return nextRemovalAt;
        }
    }

    // Caller must hold _emergencyLock.
    private bool TryGetActiveEmergency(string receipt, [NotNullWhen(true)] out PushoverEmergency? emergency) =>
        Emergencies.TryGetValue(receipt, out emergency) && emergency.Status == PushoverEmergencyStatus.Active;

    // The emergency can move to another target while a cancel request runs, so its owner is read again under the
    // owner's lock until both agree.
    private static void EndEmergency(PushoverEmergency emergency, PushoverEmergencyStatus status)
    {
        while (true)
        {
            var owner = emergency.Target;
            lock (owner._emergencyLock)
            {
                if (ReferenceEquals(emergency.Target, owner))
                {
                    if (emergency.Status == PushoverEmergencyStatus.Active)
                    {
                        EndEmergencyLocked(emergency, status);
                    }

                    return;
                }
            }
        }
    }

    // Caller must hold _emergencyLock. Status is the discrete value readers wait on, so it is written last.
    private static void EndEmergencyLocked(PushoverEmergency emergency, PushoverEmergencyStatus status)
    {
        emergency.EndedAt = DateTimeOffset.UtcNow;
        emergency.Status = status;
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
