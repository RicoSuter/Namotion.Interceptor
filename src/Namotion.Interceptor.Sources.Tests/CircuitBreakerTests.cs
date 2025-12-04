using Namotion.Interceptor.Sources.Resilience;

namespace Namotion.Interceptor.Sources.Tests;

/// <summary>
/// Tests for CircuitBreaker focusing on thread-safety and correctness.
/// Verifies that the circuit breaker handles concurrent access correctly and prevents race conditions.
/// </summary>
public class CircuitBreakerTests
{
    [Fact]
    public void Constructor_WithInvalidThreshold_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CircuitBreaker(failureThreshold: 0, cooldownPeriod: TimeSpan.FromSeconds(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CircuitBreaker(failureThreshold: -1, cooldownPeriod: TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Constructor_WithInvalidCooldown_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CircuitBreaker(failureThreshold: 3, cooldownPeriod: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CircuitBreaker(failureThreshold: 3, cooldownPeriod: TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void ShouldAttempt_InitialState_ReturnsTrue()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 3, cooldownPeriod: TimeSpan.FromSeconds(5));

        // Act
        var result = breaker.ShouldAttempt();

        // Assert
        Assert.True(result);
        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public void RecordFailure_BelowThreshold_DoesNotOpenCircuit()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 3, cooldownPeriod: TimeSpan.FromSeconds(5));

        // Act
        var tripped1 = breaker.RecordFailure();
        var tripped2 = breaker.RecordFailure();

        // Assert
        Assert.False(tripped1);
        Assert.False(tripped2);
        Assert.False(breaker.IsOpen);
        Assert.True(breaker.ShouldAttempt());
    }

    [Fact]
    public void RecordFailure_AtThreshold_OpensCircuit()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 3, cooldownPeriod: TimeSpan.FromSeconds(5));

        // Act
        breaker.RecordFailure();
        breaker.RecordFailure();
        var tripped = breaker.RecordFailure(); // Third failure should trip

        // Assert
        Assert.True(tripped);
        Assert.True(breaker.IsOpen);
        Assert.Equal(1, breaker.TripCount);
        Assert.False(breaker.ShouldAttempt()); // Should block attempts
    }

    [Fact]
    public void RecordSuccess_ResetsFailureCount()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 3, cooldownPeriod: TimeSpan.FromSeconds(5));

        // Act - Record some failures, then success
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordSuccess();
        breaker.RecordFailure();
        breaker.RecordFailure();

        // Assert - Should not trip (count was reset by success)
        Assert.False(breaker.IsOpen);
        Assert.True(breaker.ShouldAttempt());
    }

    [Fact]
    public void RecordSuccess_ClosesOpenCircuit()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 2, cooldownPeriod: TimeSpan.FromSeconds(5));
        breaker.RecordFailure();
        breaker.RecordFailure(); // Trip circuit
        Assert.True(breaker.IsOpen);

        // Act
        breaker.RecordSuccess();

        // Assert
        Assert.False(breaker.IsOpen);
        Assert.True(breaker.ShouldAttempt());
    }

    [Fact]
    public void ShouldAttempt_DuringCooldown_ReturnsFalse()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 1, cooldownPeriod: TimeSpan.FromSeconds(10));
        breaker.RecordFailure(); // Trip circuit

        // Act & Assert
        Assert.False(breaker.ShouldAttempt());
        Assert.True(breaker.IsOpen);
    }

    [Fact]
    public async Task ShouldAttempt_AfterCooldown_ReturnsTrue()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 1, cooldownPeriod: TimeSpan.FromMilliseconds(100));
        breaker.RecordFailure(); // Trip circuit
        Assert.True(breaker.IsOpen);

        // Act - Wait for cooldown
        await Task.Delay(150);

        // Assert - Should allow attempt but circuit stays open until RecordSuccess
        Assert.True(breaker.ShouldAttempt());
        Assert.True(breaker.IsOpen); // Circuit still open until successful operation
    }

    [Fact]
    public async Task ShouldAttempt_AfterCooldown_ClosesOnSuccess()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 1, cooldownPeriod: TimeSpan.FromMilliseconds(100));
        breaker.RecordFailure(); // Trip circuit
        await Task.Delay(150); // Wait for cooldown

        // Act - Simulate successful retry
        Assert.True(breaker.ShouldAttempt()); // Cooldown elapsed
        breaker.RecordSuccess(); // Simulate successful operation

        // Assert - Circuit should now be closed
        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public void GetCooldownRemaining_WhenClosed_ReturnsZero()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 3, cooldownPeriod: TimeSpan.FromSeconds(5));

        // Act
        var remaining = breaker.GetCooldownRemaining();

        // Assert
        Assert.Equal(TimeSpan.Zero, remaining);
    }

    [Fact]
    public void GetCooldownRemaining_WhenOpen_ReturnsPositiveValue()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 1, cooldownPeriod: TimeSpan.FromSeconds(10));
        breaker.RecordFailure(); // Trip circuit

        // Act
        var remaining = breaker.GetCooldownRemaining();

        // Assert
        Assert.True(remaining > TimeSpan.Zero);
        Assert.True(remaining <= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Reset_ClearsStateAndClosesCircuit()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 1, cooldownPeriod: TimeSpan.FromSeconds(10));
        breaker.RecordFailure(); // Trip circuit
        Assert.True(breaker.IsOpen);

        // Act
        breaker.Reset();

        // Assert
        Assert.False(breaker.IsOpen);
        Assert.True(breaker.ShouldAttempt());
        Assert.Equal(TimeSpan.Zero, breaker.GetCooldownRemaining());
    }

    [Fact]
    public void RecordFailure_MultipleTrips_IncrementsTripCount()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 1, cooldownPeriod: TimeSpan.FromSeconds(1));

        // Act - Trip multiple times
        breaker.RecordFailure(); // Trip 1
        Assert.Equal(1, breaker.TripCount);

        breaker.RecordSuccess(); // Close
        breaker.RecordFailure(); // Trip 2
        Assert.Equal(2, breaker.TripCount);

        breaker.Reset(); // Close
        breaker.RecordFailure(); // Trip 3

        // Assert
        Assert.Equal(3, breaker.TripCount);
    }

    [Fact]
    public async Task ConcurrentShouldAttempt_AfterCooldown_IsThreadSafe()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 1, cooldownPeriod: TimeSpan.FromMilliseconds(100));
        breaker.RecordFailure(); // Trip circuit
        await Task.Delay(150); // Wait for cooldown

        // Act - Multiple threads call ShouldAttempt concurrently
        var tasks = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => breaker.ShouldAttempt()))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert - All should return true (circuit still open but cooldown elapsed)
        Assert.All(results, result => Assert.True(result));

        // Circuit should still be open (waiting for RecordSuccess)
        Assert.True(breaker.IsOpen);
    }

    [Fact]
    public async Task ConcurrentRecordFailure_OnlyTripsOnce()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 3, cooldownPeriod: TimeSpan.FromSeconds(5));

        // Act - Multiple threads record failures concurrently
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => breaker.RecordFailure()))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert - Trip count should be exactly 1 (only one thread successfully opened circuit)
        Assert.Equal(1, breaker.TripCount);
        Assert.True(breaker.IsOpen);
    }

    [Fact]
    public async Task ConcurrentRecordSuccessAndFailure_IsThreadSafe()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 100, cooldownPeriod: TimeSpan.FromSeconds(5));
        var random = new Random();

        // Act - Mix of concurrent successes and failures
        var tasks = Enumerable.Range(0, 1000)
            .Select(i => Task.Run(() =>
            {
                if (random.Next(2) == 0)
                    breaker.RecordSuccess();
                else
                    breaker.RecordFailure();
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert - No exceptions, state is consistent
        var isOpen = breaker.IsOpen;
        var tripCount = breaker.TripCount;

        // Verify observable state is consistent
        Assert.True(tripCount >= 0);
        if (isOpen)
        {
            Assert.True(tripCount > 0);
        }
    }

    [Fact]
    public async Task RecordFailure_WhenAlreadyOpen_DoesNotIncrementTripCount()
    {
        // Arrange
        var breaker = new CircuitBreaker(failureThreshold: 1, cooldownPeriod: TimeSpan.FromMilliseconds(50));

        // Act
        var tripped1 = breaker.RecordFailure(); // First trip
        var tripped2 = breaker.RecordFailure(); // Already open
        var tripped3 = breaker.RecordFailure(); // Still open

        // Assert
        Assert.True(tripped1);
        Assert.False(tripped2); // Should return false (already open)
        Assert.False(tripped3); // Should return false (still open)
        Assert.Equal(1, breaker.TripCount); // Only one trip

        // Wait for cooldown and verify retry attempt
        await Task.Delay(100);
        Assert.True(breaker.ShouldAttempt());
        breaker.RecordSuccess();
        Assert.False(breaker.IsOpen);
    }
}
