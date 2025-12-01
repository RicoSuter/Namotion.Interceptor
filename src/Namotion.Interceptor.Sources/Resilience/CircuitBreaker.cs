using System;
using System.Threading;

namespace Namotion.Interceptor.Sources.Resilience;

/// <summary>
/// Circuit breaker pattern implementation that prevents resource exhaustion during persistent failures.
/// Thread-safe implementation using volatile fields and interlocked operations.
/// Can be used by any source implementation (MQTT, OPC UA, HTTP, etc.) to handle reconnection failures.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldownPeriod;

    private int _consecutiveFailures;
    private int _circuitOpen; // 0 = closed, 1 = open
    private long _circuitOpenedAtTicks; // Using long (ticks) for atomic operations instead of DateTimeOffset struct
    private long _tripCount;

    /// <summary>
    /// Creates a new circuit breaker.
    /// </summary>
    /// <param name="failureThreshold">Number of consecutive failures before opening the circuit.</param>
    /// <param name="cooldownPeriod">Time to wait before allowing retry after circuit opens.</param>
    public CircuitBreaker(int failureThreshold, TimeSpan cooldownPeriod)
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
    /// Useful for monitoring and alerting.
    /// </summary>
    public long TripCount => Interlocked.Read(ref _tripCount);

    /// <summary>
    /// Attempts to execute an operation through the circuit breaker.
    /// Returns true if the circuit is closed or cooldown has elapsed, false if circuit is open.
    /// When cooldown elapses, returns true but keeps circuit open - RecordSuccess() will close it atomically.
    /// </summary>
    public bool ShouldAttempt()
    {
        if (Volatile.Read(ref _circuitOpen) == 0)
        {
            return true; // Circuit closed, allow operation
        }

        // Circuit open, check if cooldown has elapsed
        // Volatile read ensures we see the latest value atomically
        var openedAtTicks = Volatile.Read(ref _circuitOpenedAtTicks);
        var timeSinceOpened = DateTimeOffset.UtcNow - new DateTimeOffset(openedAtTicks, TimeSpan.Zero);

        // After cooldown, allow retry attempt but keep circuit open
        // RecordSuccess() will close it if the attempt succeeds
        // This prevents race conditions where multiple threads try to close simultaneously
        return timeSinceOpened >= _cooldownPeriod;
    }

    /// <summary>
    /// Gets the time remaining until the circuit breaker cooldown completes.
    /// Returns TimeSpan.Zero if the circuit is closed.
    /// Useful for logging and waiting.
    /// </summary>
    public TimeSpan GetCooldownRemaining()
    {
        if (Volatile.Read(ref _circuitOpen) == 0)
        {
            return TimeSpan.Zero;
        }

        // Volatile read ensures we see the latest value atomically
        var openedAtTicks = Volatile.Read(ref _circuitOpenedAtTicks);
        var timeSinceOpened = DateTimeOffset.UtcNow - new DateTimeOffset(openedAtTicks, TimeSpan.Zero);
        var remaining = _cooldownPeriod - timeSinceOpened;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Records a successful operation, closing the circuit and resetting the failure count atomically.
    /// Call this after a successful connection/operation attempt.
    /// </summary>
    public void RecordSuccess()
    {
        // Close circuit and reset failures atomically
        // Order matters: close circuit first to prevent new failures from reopening it
        Volatile.Write(ref _circuitOpen, 0);
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }

    /// <summary>
    /// Records a failed operation, potentially opening the circuit if threshold is reached.
    /// Returns true if the circuit was opened as a result of this failure.
    /// Call this after each failed connection/operation attempt.
    /// </summary>
    public bool RecordFailure()
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        if (failures >= _failureThreshold)
        {
            // Try to open the circuit (CAS ensures only one thread succeeds)
            if (Interlocked.CompareExchange(ref _circuitOpen, 1, 0) == 0)
            {
                // Volatile write ensures atomic timestamp update visible to all threads
                Volatile.Write(ref _circuitOpenedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
                Interlocked.Increment(ref _tripCount);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Manually resets the circuit breaker to closed state with zero failures.
    /// Useful for manual recovery or testing.
    /// </summary>
    public void Reset()
    {
        Volatile.Write(ref _circuitOpen, 0);
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }
}
