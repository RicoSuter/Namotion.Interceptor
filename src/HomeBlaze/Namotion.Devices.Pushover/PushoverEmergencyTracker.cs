using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Pushover.Client;

namespace Namotion.Devices.Pushover;

/// <summary>
/// Tracks the emergencies of a <see cref="PushoverService"/> by target key and receipt. The key, not the target
/// instance, owns an emergency, since a configuration reload replaces the instance but keeps the key.
/// </summary>
internal sealed class PushoverEmergencyTracker
{
    /// <summary>
    /// How many emergencies that are no longer active are kept per target, the newest by send time.
    /// </summary>
    public const int MaximumFinishedEmergencies = 10;

    private readonly PushoverService _service;
    private readonly PushoverClient _client;

    // The send path, the operations, the configuration reload and the receipt polling run on different threads.
    // The stored sets, the target views and the state of the emergencies change only under this lock, so a late
    // poll or cancel of an emergency that already ended cannot overwrite its state.
    private readonly Lock _lock = new();

    // Each set is replaced as a whole, never changed in place, and is the same instance the target shows as its
    // Emergencies.
    private readonly Dictionary<string, Dictionary<string, PushoverEmergency>> _emergenciesByTargetKey = new();

    // The emergency sends in flight per target key, which count toward its MaximumActiveEmergencies.
    private readonly Dictionary<string, int> _sendingEmergencyCountByTargetKey = new();

    // Synchronized, so each subscriber receives the events one at a time.
    private readonly ISubject<PushoverEmergencyAcknowledgedEvent> _acknowledgedEvents =
        Subject.Synchronize(new Subject<PushoverEmergencyAcknowledgedEvent>());

    // The Targets of the service, read without the lock.
    private Dictionary<string, PushoverTarget> _targets = new();

    public PushoverEmergencyTracker(PushoverService service, PushoverClient client)
    {
        _service = service;
        _client = client;
    }

    /// <summary>
    /// Switches to the new targets of the service: each target is attached under its key, a target instance that
    /// replaces another under the same key shows the emergencies of that key, and the emergencies of a removed key
    /// are canceled by its tag in the background, also those sent before a restart.
    /// </summary>
    public void SetTargets(Dictionary<string, PushoverTarget> targets)
    {
        List<(string Key, PushoverEmergency[] ActiveEmergencies)>? removedTargets = null;
        lock (_lock)
        {
            foreach (var (key, previousTarget) in _targets)
            {
                if (!targets.TryGetValue(key, out var target))
                {
                    (removedTargets ??= []).Add((key, GetActiveEmergencies(key)));
                    _emergenciesByTargetKey.Remove(key);
                }
                else if (ReferenceEquals(target, previousTarget))
                {
                    continue;
                }
                else if (_emergenciesByTargetKey.TryGetValue(key, out var emergencies))
                {
                    target.Emergencies = emergencies;
                }

                previousTarget.Detach();
                if (previousTarget.Emergencies.Count > 0)
                {
                    previousTarget.Emergencies = new Dictionary<string, PushoverEmergency>();
                }
            }

            foreach (var (key, target) in targets)
            {
                target.Attach(_service, key);
            }

            _targets = targets;
        }

        foreach (var (key, activeEmergencies) in removedTargets ?? [])
        {
            CancelInBackground(key, () => CancelByTagAsync(key, activeEmergencies, CancellationToken.None));
        }
    }

    /// <summary>
    /// Reserves the send of an emergency notification for the target with <paramref name="targetKey"/>, which
    /// <see cref="ReleaseReservation"/> ends once the send completed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The target already has <paramref name="maximumActiveEmergencies"/>
    /// active or sending emergencies.</exception>
    public void ReserveEmergency(string targetKey, int maximumActiveEmergencies, string targetTitle)
    {
        lock (_lock)
        {
            var sendingCount = _sendingEmergencyCountByTargetKey.GetValueOrDefault(targetKey);
            if (GetActiveEmergencies(targetKey).Length + sendingCount >= maximumActiveEmergencies)
            {
                throw new InvalidOperationException(
                    $"The Pushover target '{targetTitle}' already has {maximumActiveEmergencies} active emergency notifications, " +
                    $"its MaximumActiveEmergencies limit. Cancel one or wait until one ends.");
            }

            _sendingEmergencyCountByTargetKey[targetKey] = sendingCount + 1;
        }
    }

    public void ReleaseReservation(string targetKey)
    {
        lock (_lock)
        {
            _sendingEmergencyCountByTargetKey[targetKey]--;
        }
    }

    /// <summary>
    /// Starts tracking a newly sent emergency notification under its target key and receipt. When the key was
    /// removed while the notification was being sent, the emergency is canceled in the background instead.
    /// </summary>
    public PushoverEmergency Add(string targetKey, string receipt, string? title, string message, DateTimeOffset sentAt, TimeSpan expire)
    {
        var emergency = new PushoverEmergency(this, targetKey, receipt, title, message, sentAt, expire)
        {
            SentTimestamp = _service.TimeProvider.GetTimestamp()
        };
        lock (_lock)
        {
            if (_targets.TryGetValue(targetKey, out var target))
            {
                var emergencies = _emergenciesByTargetKey.TryGetValue(targetKey, out var current)
                    ? new Dictionary<string, PushoverEmergency>(current)
                    : new Dictionary<string, PushoverEmergency>();
                emergencies[receipt] = emergency;
                Publish(targetKey, target, emergencies);
                return emergency;
            }
        }

        CancelInBackground(targetKey, () => CancelAsync(emergency, CancellationToken.None));
        return emergency;
    }

    /// <summary>
    /// Reads the receipts that are due and applies them. A receipt is due once the receipt polling interval of the
    /// service passed since its last poll, or since the notification was sent. A receipt that cannot be read is
    /// retried on the next poll, and given up once its emergency expired; a rejected application token is thrown.
    /// </summary>
    public async Task PollReceiptsAsync(string token, CancellationToken cancellationToken)
    {
        foreach (var emergency in StartReceiptPolls())
        {
            try
            {
                var response = await _client.GetReceiptAsync(token, emergency.Receipt, cancellationToken);
                ApplyReceipt(emergency, response);
            }
            catch (Exception exception) when (PushoverService.IsRetriedFailure(exception, cancellationToken))
            {
                _service.Logger.LogWarning(exception, "Reading an emergency receipt of Pushover target {Target} failed", emergency.TargetKey);
                ApplyReceiptError(emergency);
            }
        }
    }

    /// <summary>
    /// Returns how long the receipt polling may wait until the next receipt is due, or
    /// <see cref="Timeout.InfiniteTimeSpan"/> when no emergency is active.
    /// </summary>
    public TimeSpan GetTimeUntilNextReceiptPoll()
    {
        var interval = _service.ReceiptPollingInterval;
        var timeProvider = _service.TimeProvider;
        lock (_lock)
        {
            var now = timeProvider.GetTimestamp();
            var timeout = Timeout.InfiniteTimeSpan;
            foreach (var emergency in GetAllActiveEmergencies())
            {
                var untilDue = interval - timeProvider.GetElapsedTime(emergency.LastPolledTimestamp ?? emergency.SentTimestamp, now);
                untilDue = untilDue < TimeSpan.Zero ? TimeSpan.Zero : untilDue;
                if (timeout == Timeout.InfiniteTimeSpan || untilDue < timeout)
                {
                    timeout = untilDue;
                }
            }

            return timeout;
        }
    }

    /// <summary>
    /// Ends <paramref name="emergency"/> if it is still active and the receipt says it was acknowledged, raising the
    /// event, or that it expired.
    /// </summary>
    public void ApplyReceipt(PushoverEmergency emergency, PushoverResponse response)
    {
        PushoverEmergencyAcknowledgedEvent? acknowledgedEvent = null;
        lock (_lock)
        {
            if (!IsActive(emergency))
            {
                return;
            }

            if (response.Acknowledged == 1)
            {
                var acknowledgedAt = DateTimeOffset.FromUnixTimeSeconds(response.AcknowledgedAt);
                emergency.AcknowledgedAt = acknowledgedAt;
                EndLocked(emergency, PushoverEmergencyStatus.Acknowledged);
                acknowledgedEvent = new PushoverEmergencyAcknowledgedEvent
                {
                    Timestamp = acknowledgedAt,
                    TargetKey = emergency.TargetKey,
                    Emergency = emergency,
                    Id = emergency.Receipt
                };
            }
            else if (response.Expired == 1)
            {
                EndLocked(emergency, PushoverEmergencyStatus.Expired);
            }
        }

        if (acknowledgedEvent is not null)
        {
            _acknowledgedEvents.OnNext(acknowledgedEvent);
        }
    }

    public void ApplyReceiptError(PushoverEmergency emergency)
    {
        // A receipt that cannot be read is given up once the emergency has expired on the Pushover side.
        if (_service.TimeProvider.GetElapsedTime(emergency.SentTimestamp) > emergency.Expire)
        {
            End(emergency, PushoverEmergencyStatus.Expired);
        }
    }

    /// <summary>
    /// Stops the repeats of <paramref name="emergency"/> and marks it canceled; does nothing when it is no longer
    /// active.
    /// </summary>
    public async Task CancelAsync(PushoverEmergency emergency, CancellationToken cancellationToken)
    {
        if (!IsActive(emergency))
        {
            return;
        }

        await _client.CancelReceiptAsync(_service.GetRequiredApplicationToken(), emergency.Receipt, cancellationToken);
        End(emergency, PushoverEmergencyStatus.Canceled);
    }

    /// <summary>
    /// Cancels the emergencies of the target with <paramref name="targetKey"/> by its tag, also those sent before a
    /// restart, and marks the tracked active ones canceled.
    /// </summary>
    public Task CancelAllAsync(string targetKey, CancellationToken cancellationToken) =>
        CancelByTagAsync(targetKey, GetActiveEmergencies(targetKey), cancellationToken);

    /// <summary>
    /// Emits the acknowledged events of the target with <paramref name="targetKey"/>, or of every target when null.
    /// </summary>
    public IDisposable Subscribe(IObserver<PushoverEmergencyAcknowledgedEvent> observer, string? targetKey)
    {
        ArgumentNullException.ThrowIfNull(observer);

        // Each subscriber is wrapped so that one throwing from OnNext cannot stop the subject from notifying the
        // others, or propagate into the receipt polling that raises the event.
        return _acknowledgedEvents.Subscribe(
            value =>
            {
                if (targetKey is not null && value.TargetKey != targetKey)
                {
                    return;
                }

                try
                {
                    observer.OnNext(value);
                }
                catch (Exception exception)
                {
                    _service.Logger.LogWarning(exception, "A Pushover emergency acknowledgement subscriber of target {Target} threw", value.TargetKey);
                }
            },
            observer.OnError,
            observer.OnCompleted);
    }

    // The tracked emergencies are read before the request, so one sent while it runs is not marked canceled.
    private async Task CancelByTagAsync(string targetKey, PushoverEmergency[] trackedEmergencies, CancellationToken cancellationToken)
    {
        await _client.CancelByTagAsync(
            _service.GetRequiredApplicationToken(), PushoverMessageMapping.GetEmergencyTag(targetKey), cancellationToken);
        foreach (var emergency in trackedEmergencies)
        {
            End(emergency, PushoverEmergencyStatus.Canceled);
        }
    }

    // Runs on the thread pool, outside the lock and outside the configuration change or send that removed the
    // target: no target polls or cancels these emergencies anymore.
    private void CancelInBackground(string targetKey, Func<Task> cancel) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await cancel();
            }
            catch (Exception exception)
            {
                _service.Logger.LogWarning(exception, "Canceling the emergencies of the removed Pushover target {Target} failed", targetKey);
            }
        });

    // Enters the lock, which is reentrant, so callers that already hold it can use it.
    private PushoverEmergency[] GetActiveEmergencies(string targetKey)
    {
        lock (_lock)
        {
            return _emergenciesByTargetKey.TryGetValue(targetKey, out var emergencies)
                ? emergencies.Values.Where(IsActive).ToArray()
                : [];
        }
    }

    // Caller must hold _lock.
    private IEnumerable<PushoverEmergency> GetAllActiveEmergencies() =>
        _emergenciesByTargetKey.Values.SelectMany(emergencies => emergencies.Values).Where(IsActive);

    private PushoverEmergency[] StartReceiptPolls()
    {
        var interval = _service.ReceiptPollingInterval;
        var timeProvider = _service.TimeProvider;
        lock (_lock)
        {
            var now = timeProvider.GetTimestamp();
            var due = GetAllActiveEmergencies()
                .Where(emergency => timeProvider.GetElapsedTime(emergency.LastPolledTimestamp ?? emergency.SentTimestamp, now) >= interval)
                .ToArray();
            foreach (var emergency in due)
            {
                emergency.LastPolledTimestamp = now;
            }

            return due;
        }
    }

    // Caller must hold _lock.
    private void Publish(string targetKey, PushoverTarget target, Dictionary<string, PushoverEmergency> emergencies)
    {
        if (emergencies.Count == 0)
        {
            _emergenciesByTargetKey.Remove(targetKey);
        }
        else
        {
            _emergenciesByTargetKey[targetKey] = emergencies;
        }

        target.Emergencies = emergencies;
    }

    private static bool IsActive(PushoverEmergency emergency) => emergency.Status == PushoverEmergencyStatus.Active;

    private void End(PushoverEmergency emergency, PushoverEmergencyStatus status)
    {
        lock (_lock)
        {
            if (IsActive(emergency))
            {
                EndLocked(emergency, status);
            }
        }
    }

    // Caller must hold _lock. Status is the discrete value readers wait on, so it is written after the other state.
    private void EndLocked(PushoverEmergency emergency, PushoverEmergencyStatus status)
    {
        emergency.Status = status;

        var targetKey = emergency.TargetKey;
        if (!_emergenciesByTargetKey.TryGetValue(targetKey, out var emergencies))
        {
            return;
        }

        var removed = emergencies.Values
            .Where(candidate => !IsActive(candidate))
            .OrderByDescending(candidate => candidate.SentTimestamp)
            .Skip(MaximumFinishedEmergencies)
            .ToArray();
        if (removed.Length > 0)
        {
            var remaining = new Dictionary<string, PushoverEmergency>(emergencies);
            foreach (var candidate in removed)
            {
                remaining.Remove(candidate.Receipt);
            }

            Publish(targetKey, _targets[targetKey], remaining);
        }
    }
}
