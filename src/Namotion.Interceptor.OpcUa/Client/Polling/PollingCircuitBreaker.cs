namespace Namotion.Interceptor.OpcUa.Client.Polling;

/// <summary>
/// Circuit breaker for polling operations that prevents resource exhaustion during persistent failures.
/// Thread-safe implementation using volatile fields and interlocked operations.
/// </summary>
internal sealed class PollingCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldownPeriod;

    private int _consecutiveFailures;
    private int _circuitOpen; // 0 = closed, 1 = open
    private DateTimeOffset _circuitOpenedAt;
    private long _tripCount;

    public PollingCircuitBreaker(int failureThreshold, TimeSpan cooldownPeriod)
    {
        if (failureThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(failureThreshold), "Failure threshold must be greater than zero");

        if (cooldownPeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cooldownPeriod), "Cooldown period must be greater than zero");

        _failureThreshold = failureThreshold;
        _cooldownPeriod = cooldownPeriod;
    }

    /// <summary>
    /// Gets whether the circuit breaker is currently open (blocking operations).
    /// </summary>
    public bool IsOpen => Volatile.Read(ref _circuitOpen) == 1;

    /// <summary>
    /// Gets the total number of times the circuit breaker has tripped.
    /// </summary>
    public long TripCount => Interlocked.Read(ref _tripCount);

    /// <summary>
    /// Attempts to execute an operation through the circuit breaker.
    /// Returns true if the circuit is closed or cooldown has elapsed, false if circuit is open.
    /// </summary>
    public bool ShouldAttempt()
    {
        if (Volatile.Read(ref _circuitOpen) == 0)
        {
            return true; // Circuit closed, allow operation
        }

        // Circuit open, check if cooldown has elapsed
        // Memory barrier ensures we see the latest value
        Interlocked.MemoryBarrier();
        var timeSinceOpened = DateTimeOffset.UtcNow - _circuitOpenedAt;
        if (timeSinceOpened >= _cooldownPeriod)
        {
            // Cooldown elapsed, attempt to close the circuit
            Volatile.Write(ref _circuitOpen, 0);
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            return true;
        }

        return false; // Circuit still open
    }

    /// <summary>
    /// Gets the time remaining until the circuit breaker cooldown completes.
    /// Returns TimeSpan.Zero if the circuit is closed.
    /// </summary>
    public TimeSpan GetCooldownRemaining()
    {
        if (Volatile.Read(ref _circuitOpen) == 0)
        {
            return TimeSpan.Zero;
        }

        // Memory barrier ensures we see the latest value
        Interlocked.MemoryBarrier();
        var timeSinceOpened = DateTimeOffset.UtcNow - _circuitOpenedAt;
        var remaining = _cooldownPeriod - timeSinceOpened;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Records a successful operation, resetting the failure count.
    /// </summary>
    public void RecordSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }

    /// <summary>
    /// Records a failed operation, potentially opening the circuit if threshold is reached.
    /// Returns true if the circuit was opened as a result of this failure.
    /// </summary>
    public bool RecordFailure()
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        if (failures >= _failureThreshold)
        {
            // Try to open the circuit (CAS ensures only one thread succeeds)
            if (Interlocked.CompareExchange(ref _circuitOpen, 1, 0) == 0)
            {
                _circuitOpenedAt = DateTimeOffset.UtcNow;
                Interlocked.MemoryBarrier(); // Ensure write is visible to all threads
                Interlocked.Increment(ref _tripCount);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Resets the circuit breaker to closed state with zero failures.
    /// </summary>
    public void Reset()
    {
        Volatile.Write(ref _circuitOpen, 0);
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }
}
