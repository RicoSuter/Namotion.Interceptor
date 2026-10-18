using Microsoft.Extensions.Logging;
using Namotion.Devices.Pushover.Client;

namespace Namotion.Devices.Pushover;

/// <summary>
/// Tracks the emergencies of a <see cref="PushoverService"/> by target key and receipt. The key, not the target
/// instance, owns an emergency, since a configuration reload replaces the instance but keeps the key.
/// </summary>
internal sealed class PushoverEmergencyTracker
{
    private readonly PushoverService _service;

    // The send path, the operations, the configuration reload and the receipt polling run on different threads.
    // The stored sets, the target views and the state of the emergencies change only under this lock, so a late
    // poll or cancel of an emergency that already ended cannot overwrite its state.
    private readonly Lock _lock = new();

    // Each set is replaced as a whole, never changed in place, and is the same instance the target shows as its
    // Emergencies.
    private readonly Dictionary<string, Dictionary<string, PushoverEmergency>> _emergenciesByTargetKey = new();

    // The current Targets of the service; replaced as a whole, never changed in place, so it can be read without
    // the lock.
    private Dictionary<string, PushoverTarget> _targets = new();

    public PushoverEmergencyTracker(PushoverService service)
    {
        _service = service;
    }

    /// <summary>
    /// Switches to the new targets of the service: a target instance that replaces another under the same key shows
    /// the emergencies of that key, and the active emergencies of a removed key are canceled in the background.
    /// </summary>
    public void SetTargets(Dictionary<string, PushoverTarget> targets)
    {
        List<PushoverEmergency>? removedEmergencies = null;
        lock (_lock)
        {
            foreach (var (key, emergencies) in _emergenciesByTargetKey.ToArray())
            {
                var previousTarget = _targets.GetValueOrDefault(key);
                if (targets.TryGetValue(key, out var target))
                {
                    if (!ReferenceEquals(target, previousTarget))
                    {
                        target.Emergencies = emergencies;
                        ClearView(previousTarget);
                    }
                }
                else
                {
                    _emergenciesByTargetKey.Remove(key);
                    ClearView(previousTarget);
                    (removedEmergencies ??= []).AddRange(emergencies.Values.Where(IsActive));
                }
            }

            _targets = targets;
        }

        if (removedEmergencies is { Count: > 0 })
        {
            CancelInBackground(removedEmergencies.ToArray());
        }
    }

    /// <summary>
    /// Starts tracking a newly sent emergency notification under its target key and receipt. When the key was
    /// removed while the notification was being sent, the emergency is canceled in the background instead.
    /// </summary>
    public PushoverEmergency Add(string targetKey, string receipt, string? title, string message, DateTimeOffset sentAt, DateTimeOffset expiresAt)
    {
        var emergency = new PushoverEmergency(this, targetKey, receipt, title, message, sentAt, expiresAt);
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

        CancelInBackground([emergency]);
        return emergency;
    }

    /// <summary>
    /// Returns the active emergencies whose receipt is due for a poll, recording <paramref name="now"/> as their new
    /// poll time. A receipt is due once the minimum polling interval of the service passed since its last poll, or
    /// since the notification was sent, as it cannot be acknowledged before.
    /// </summary>
    public PushoverEmergency[] StartReceiptPolls(DateTimeOffset now)
    {
        var minimumInterval = _service.MinimumReceiptPollingInterval;
        lock (_lock)
        {
            List<PushoverEmergency>? due = null;
            foreach (var emergencies in _emergenciesByTargetKey.Values)
            {
                foreach (var emergency in emergencies.Values)
                {
                    if (IsActive(emergency) && now - (emergency.LastPolledAt ?? emergency.SentAt) >= minimumInterval)
                    {
                        emergency.LastPolledAt = now;
                        (due ??= []).Add(emergency);
                    }
                }
            }

            return due?.ToArray() ?? [];
        }
    }

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
                emergency.AcknowledgedByDevice = response.AcknowledgedByDevice;
                EndLocked(emergency, PushoverEmergencyStatus.Acknowledged);

                if (_targets.TryGetValue(emergency.TargetKey, out var target))
                {
                    acknowledgedEvent = new PushoverEmergencyAcknowledgedEvent
                    {
                        Timestamp = acknowledgedAt,
                        Target = target,
                        Emergency = emergency,
                        Id = emergency.Receipt,
                        AcknowledgedByDevice = response.AcknowledgedByDevice
                    };
                }
            }
            else if (response.Expired == 1)
            {
                EndLocked(emergency, PushoverEmergencyStatus.Expired);
            }
        }

        if (acknowledgedEvent is not null)
        {
            acknowledgedEvent.Target.PublishAcknowledged(acknowledgedEvent);
        }
    }

    public void ApplyReceiptError(PushoverEmergency emergency, DateTimeOffset now)
    {
        lock (_lock)
        {
            // A receipt that cannot be read is given up once the emergency has expired on the Pushover side.
            if (IsActive(emergency) && now > emergency.ExpiresAt)
            {
                EndLocked(emergency, PushoverEmergencyStatus.Expired);
            }
        }
    }

    /// <summary>
    /// Stops the repeats of <paramref name="emergency"/>; does nothing when it is no longer active.
    /// </summary>
    public async Task CancelAsync(PushoverEmergency emergency, CancellationToken cancellationToken)
    {
        if (!IsActive(emergency))
        {
            return;
        }

        await _service.CancelReceiptAsync(emergency.Receipt, cancellationToken);
        lock (_lock)
        {
            if (IsActive(emergency))
            {
                EndLocked(emergency, PushoverEmergencyStatus.Canceled);
            }
        }
    }

    /// <summary>
    /// Cancels the active emergencies of every target at once, throwing one <see cref="PushoverException"/> that
    /// names each failed emergency.
    /// </summary>
    public Task CancelAllAsync(CancellationToken cancellationToken) =>
        CancelAllAsync(GetActiveEmergencies(targetKey: null), cancellationToken);

    /// <summary>
    /// Cancels the active emergencies of the target with <paramref name="targetKey"/> at once, throwing one
    /// <see cref="PushoverException"/> that names each failed emergency.
    /// </summary>
    public Task CancelAllAsync(string targetKey, CancellationToken cancellationToken) =>
        CancelAllAsync(GetActiveEmergencies(targetKey), cancellationToken);

    /// <summary>
    /// Removes the emergencies that ended at least the retention of the service before <paramref name="now"/>, and
    /// returns when the next of the remaining ended ones is due for removal, or null when none is.
    /// </summary>
    public DateTimeOffset? RemoveFinishedEmergencies(DateTimeOffset now)
    {
        var retention = _service.FinishedEmergencyRetention;
        lock (_lock)
        {
            DateTimeOffset? nextRemovalAt = null;
            List<(string TargetKey, Dictionary<string, PushoverEmergency> Remaining)>? changes = null;
            foreach (var (targetKey, emergencies) in _emergenciesByTargetKey)
            {
                Dictionary<string, PushoverEmergency>? remaining = null;
                foreach (var (receipt, emergency) in emergencies)
                {
                    if (emergency.EndedAt is not { } endedAt)
                    {
                        continue;
                    }

                    var removalAt = endedAt + retention;
                    if (removalAt <= now)
                    {
                        (remaining ??= new Dictionary<string, PushoverEmergency>(emergencies)).Remove(receipt);
                    }
                    else if (nextRemovalAt is null || removalAt < nextRemovalAt)
                    {
                        nextRemovalAt = removalAt;
                    }
                }

                if (remaining is not null)
                {
                    (changes ??= []).Add((targetKey, remaining));
                }
            }

            foreach (var (targetKey, remaining) in changes ?? [])
            {
                Publish(targetKey, _targets[targetKey], remaining);
            }

            return nextRemovalAt;
        }
    }

    /// <summary>
    /// Returns how long the receipt polling loop may wait: until the next receipt poll while an emergency is active,
    /// and no longer than until <paramref name="nextRemovalAt"/>; with neither pending, until a signal only.
    /// </summary>
    public TimeSpan GetWaitTimeout(DateTimeOffset now, DateTimeOffset? nextRemovalAt)
    {
        var timeout = HasActiveEmergencies() ? _service.ReceiptPollingInterval : Timeout.InfiniteTimeSpan;
        if (nextRemovalAt is { } removalAt)
        {
            var untilRemoval = removalAt - now;
            if (untilRemoval < TimeSpan.Zero)
            {
                untilRemoval = TimeSpan.Zero;
            }

            if (timeout == Timeout.InfiniteTimeSpan || untilRemoval < timeout)
            {
                timeout = untilRemoval;
            }
        }

        return timeout;
    }

    private Task CancelAllAsync(PushoverEmergency[] emergencies, CancellationToken cancellationToken) =>
        PushoverBatch.RunAllAsync(
            emergencies,
            emergency => CancelAsync(emergency, cancellationToken),
            emergency => $"{GetTargetTitle(emergency.TargetKey)} ({emergency.Title})",
            "Canceling",
            "emergencies",
            cancellationToken);

    // Run on the thread pool, outside the lock and outside the configuration change or send that removed the
    // target: no target polls or cancels these anymore.
    private void CancelInBackground(PushoverEmergency[] emergencies) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await CancelAllAsync(emergencies, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _service.Logger.LogWarning(exception, "Canceling the emergencies of removed Pushover targets failed");
            }
        });

    private PushoverEmergency[] GetActiveEmergencies(string? targetKey)
    {
        lock (_lock)
        {
            if (targetKey is null)
            {
                return _emergenciesByTargetKey.Values.SelectMany(emergencies => emergencies.Values).Where(IsActive).ToArray();
            }

            return _emergenciesByTargetKey.TryGetValue(targetKey, out var targetEmergencies)
                ? targetEmergencies.Values.Where(IsActive).ToArray()
                : [];
        }
    }

    private bool HasActiveEmergencies()
    {
        lock (_lock)
        {
            foreach (var emergencies in _emergenciesByTargetKey.Values)
            {
                foreach (var emergency in emergencies.Values)
                {
                    if (IsActive(emergency))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    private string GetTargetTitle(string targetKey) =>
        _targets.TryGetValue(targetKey, out var target) ? target.Title : targetKey;

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

    private static void ClearView(PushoverTarget? target)
    {
        if (target is { Emergencies.Count: > 0 })
        {
            target.Emergencies = new Dictionary<string, PushoverEmergency>();
        }
    }

    private static bool IsActive(PushoverEmergency emergency) => emergency.Status == PushoverEmergencyStatus.Active;

    // Caller must hold _lock. Status is the discrete value readers wait on, so it is written last.
    private static void EndLocked(PushoverEmergency emergency, PushoverEmergencyStatus status)
    {
        emergency.EndedAt = DateTimeOffset.UtcNow;
        emergency.Status = status;
    }
}
