namespace HomeBlaze.AI.Agents;

/// <summary>
/// Decides when background runs are due: poll interval, change requests coalesced by the minimum run interval,
/// manual runs, failure backoff and pauses. Not thread-safe; <c>AgentTriggerScheduler</c> serializes access.
/// </summary>
public sealed class AgentTriggerPolicy
{
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromDays(365);

    private TimeSpan? _pollInterval;
    private TimeSpan _minRunInterval;
    private DateTimeOffset? _nextPoll;
    private string? _pendingReason;
    private bool _pendingIsManual;
    private DateTimeOffset? _lastRunStart;
    private int _consecutiveFailures;
    private DateTimeOffset? _backoffUntil;
    private DateTimeOffset? _pausedUntil;
    private bool _isPausedUntilReconfigured;

    public AgentTriggerPolicy(TimeSpan? pollInterval, TimeSpan minRunInterval, DateTimeOffset now)
    {
        Reconfigure(pollInterval, minRunInterval, now);
    }

    /// <summary>Why automatic runs are paused, or null.</summary>
    public string? PauseReason { get; private set; }

    /// <summary>
    /// Applies new intervals, restarts the poll period and lifts a pause caused by a configuration error.
    /// </summary>
    public void Reconfigure(TimeSpan? pollInterval, TimeSpan minRunInterval, DateTimeOffset now)
    {
        if (pollInterval > MaximumInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), pollInterval, $"Poll interval must not exceed {MaximumInterval}.");
        }

        if (minRunInterval > MaximumInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(minRunInterval), minRunInterval, $"Minimum run interval must not exceed {MaximumInterval}.");
        }

        _pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : null;
        _minRunInterval = minRunInterval > TimeSpan.Zero ? minRunInterval : TimeSpan.Zero;
        _nextPoll = now + _pollInterval;
        if (_isPausedUntilReconfigured)
        {
            _isPausedUntilReconfigured = false;
            PauseReason = null;
        }
    }

    /// <summary>Requests a run; a pending request keeps its first reason.</summary>
    public void RequestRun(string reason) => _pendingReason ??= reason;

    /// <summary>Requests a run that ignores cooldown, backoff and pauses.</summary>
    public void RequestManualRun()
    {
        _pendingReason = "manual";
        _pendingIsManual = true;
    }

    /// <summary>
    /// Takes the pending run if it is due at <paramref name="now"/> and records its start.
    /// </summary>
    public bool TryTakeDueRun(DateTimeOffset now, out string reason)
    {
        ExpireUsageLimitPauseIfDue(now);

        if (_nextPoll is { } nextPoll && now >= nextPoll)
        {
            _pendingReason ??= "poll";
            _nextPoll = now + _pollInterval;
        }

        reason = string.Empty;
        if (_pendingReason is null)
        {
            return false;
        }

        if (!_pendingIsManual && (GetEarliestAutomaticStart(now) is not { } earliest || earliest > now))
        {
            return false;
        }

        reason = _pendingReason;
        _pendingReason = null;
        _pendingIsManual = false;
        _lastRunStart = now;
        return true;
    }

    /// <summary>
    /// The next time a run may become due, or null when only a new request can make one due.
    /// </summary>
    public DateTimeOffset? GetNextWakeTime(DateTimeOffset now)
    {
        ExpireUsageLimitPauseIfDue(now);

        DateTimeOffset? pendingDue = _pendingReason is null
            ? null
            : _pendingIsManual ? now : GetEarliestAutomaticStart(now);

        return (pendingDue, _nextPoll) switch
        {
            (null, var poll) => poll,
            (var pending, null) => pending,
            ({ } pending, { } poll) => pending < poll ? pending : poll
        };
    }

    /// <summary>Resets backoff and lifts every pause.</summary>
    public void OnRunSucceeded()
    {
        _consecutiveFailures = 0;
        _backoffUntil = null;
        _pausedUntil = null;
        _isPausedUntilReconfigured = false;
        PauseReason = null;
    }

    /// <summary>Applies the reaction to a failed run.</summary>
    public void OnRunFailed(DateTimeOffset now, AgentFailureKind kind, DateTimeOffset? retryAfter, string message)
    {
        _consecutiveFailures++;
        var backoff = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, _consecutiveFailures - 1), MaximumBackoff.TotalMinutes));
        switch (kind)
        {
            case AgentFailureKind.Configuration:
                _isPausedUntilReconfigured = true;
                PauseReason = message;
                break;
            case AgentFailureKind.UsageLimit:
                _pausedUntil = retryAfter ?? now + backoff;
                if (!_isPausedUntilReconfigured)
                {
                    PauseReason = message;
                }

                break;
            default:
                _backoffUntil = now + backoff;
                break;
        }
    }

    private DateTimeOffset? GetEarliestAutomaticStart(DateTimeOffset now)
    {
        if (_isPausedUntilReconfigured)
        {
            return null;
        }

        var earliest = Max(now, _lastRunStart + _minRunInterval);
        earliest = Max(earliest, _backoffUntil);
        earliest = Max(earliest, _pausedUntil);
        return earliest;
    }

    /// <summary>Clears an expired usage-limit pause; a configuration pause always takes precedence.</summary>
    private void ExpireUsageLimitPauseIfDue(DateTimeOffset now)
    {
        if (!_isPausedUntilReconfigured && _pausedUntil is { } pausedUntil && now >= pausedUntil)
        {
            _pausedUntil = null;
            PauseReason = null;
        }
    }

    private static DateTimeOffset Max(DateTimeOffset value, DateTimeOffset? candidate)
        => candidate is { } other && other > value ? other : value;
}
