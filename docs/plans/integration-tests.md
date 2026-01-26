# OPC UA Integration Tests - Flakiness Investigation

## Problem Statement

OPC UA integration tests are flaky when running in parallel (4 tests at a time). Tests pass individually but fail intermittently when run together.

GitHub Actions reference: https://github.com/RicoSuter/Namotion.Interceptor/actions/runs/21368824952/job/61509569503

## Key Findings

### 1. Session Timeout Mismatch (Original Issue)

**Root Cause**: Client `SessionTimeout` (originally 5s) was less than server's `MinSessionTimeout` (10s).

- OPC UA session negotiation: Client requests timeout, server revises to minimum
- Session lifetime = 2× revised timeout ≈ 20 seconds
- Under parallel load, keep-alives were delayed, causing premature session closure

**Fix Applied**: Increased client `SessionTimeout` to 30s in `OpcUaTestClient.cs`:
```csharp
SessionTimeout = TimeSpan.FromSeconds(30),
KeepAliveInterval = TimeSpan.FromSeconds(5),
OperationTimeout = TimeSpan.FromSeconds(30),
```

### 2. Port Reuse and TIME_WAIT

**Issue**: When tests ran sequentially (attempted fix), ports entered TIME_WAIT state causing connection failures on reuse.

**Symptoms**:
- First connection attempt timing out (30s OperationTimeout)
- Subsequent retry succeeding
- Log message: "No connection could be made because the target machine actively refused it"

**Fix Applied**: Rewrote `OpcUaTestPortPool.cs` with:
- 100 ports (4840-4939) instead of 4
- Round-robin assignment to avoid immediate reuse
- Semaphore limiting to 4 concurrent tests

```csharp
private const int BasePort = 4840;
private const int PoolSize = 100;
private const int MaxParallel = 4;
private static int _nextPortIndex = -1;

// Port assignment: round-robin through 100 ports
var index = Interlocked.Increment(ref _nextPortIndex) % PoolSize;
return new PortLease(BasePort + index);
```

### 3. Disconnection Detection Timing

**Key Insight**: `SessionTimeout` is the primary driver of disconnection detection, not `KeepAliveInterval`.

- OPC UA SDK declares connection lost after SessionTimeout elapses without successful communication
- With SessionTimeout=30s and KeepAliveInterval=1s, disconnection still takes ~30s to detect
- For fast disconnection detection, both must be configured:
  ```csharp
  config.SessionTimeout = TimeSpan.FromSeconds(10); // Minimum allowed by server
  config.KeepAliveInterval = TimeSpan.FromSeconds(1);
  ```

### 4. Different Tests Need Different Configurations

| Test Type | SessionTimeout | KeepAliveInterval | Rationale |
|-----------|---------------|-------------------|-----------|
| Basic read/write | 30s (default) | 5s (default) | Stability over speed |
| Disconnection wait tests | 10s | 1s | Need fast detection |
| Instant restart tests | 30s | 2s | SDK needs time to recover |
| Stall detection tests | 10s | 1s | Need fast detection |

### 5. Remaining Issues

Tests are **still flaky** even with all fixes. Observed behaviors:

1. **Variable disconnection detection time**: Same configuration produces different detection times (10s to 45s)
2. **TCP connection behavior**: When server restarts instantly, TCP may not cleanly break, causing client to wait for TCP-level timeouts
3. **Resource contention**: Under parallel load, server startup can be delayed significantly (observed 19s startup time)
4. **SDK reconnection complexity**: The OPC UA SDK has internal state machines that don't always behave predictably

## Files Modified

### `OpcUaTestPortPool.cs` (Significant Changes)
- Complete rewrite with 100-port pool and round-robin assignment

### `OpcUaTestClient.cs` (Timeout Configuration)
```csharp
// Session stability - use longer timeouts to prevent flaky tests under CI load
SessionTimeout = TimeSpan.FromSeconds(30),
KeepAliveInterval = TimeSpan.FromSeconds(5),
OperationTimeout = TimeSpan.FromSeconds(30),
```

### `OpcUaStallDetectionTests.cs` (Fast Detection Config)
```csharp
private readonly Action<OpcUaClientConfiguration> _fastStallConfig = config =>
{
    config.SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(2);
    config.MaxReconnectDuration = TimeSpan.FromSeconds(15);
    config.ReconnectInterval = TimeSpan.FromSeconds(1);
    config.SessionTimeout = TimeSpan.FromSeconds(10);
    config.KeepAliveInterval = TimeSpan.FromSeconds(1);
};
```

### `OpcUaReconnectionTests.cs` (Multiple Configs)
- `FastDisconnectionConfig`: SessionTimeout=10s, KeepAliveInterval=1s
- `InstantRestartConfig`: SessionTimeout=30s, KeepAliveInterval=2s, ReconnectInterval=2s
- `StartServerAndClientAsync()`: Now accepts optional config parameter

## Recommendations for Future Work

### Option A: Reduce Parallelism
Reduce `MaxParallel` from 4 to 2 in `OpcUaTestPortPool.cs`. Less contention may improve stability.

### Option B: Increase Timeouts Further
Some tests use 60s or 90s timeouts. Consider increasing to 120s+ for reconnection scenarios.

### Option C: Sequential Execution for Reconnection Tests
Use xUnit `[Collection]` attribute to run reconnection/stall tests sequentially while keeping basic tests parallel.

```csharp
[Collection("OpcUa Reconnection")]
public class OpcUaReconnectionTests { }

[Collection("OpcUa Reconnection")]
public class OpcUaStallDetectionTests { }
```

### Option D: Investigate SDK Behavior
The OPC UA SDK (OPCFoundation.NetStandard.Opc.Ua) may have configuration options or known issues with reconnection under load. Consider:
- Checking SDK release notes for reconnection improvements
- Testing with newer SDK versions
- Adding more detailed logging of SDK internal state

### Option E: Mock-Based Testing
For unit-level confidence, consider adding mock-based tests that don't require actual OPC UA connections. Reserve integration tests for smoke testing only.

## Test Results Summary

| Run | Passed | Failed | Notes |
|-----|--------|--------|-------|
| 1 | 12 | 2 | StallDetection, LargeSubscription failed |
| 2 | 13 | 1 | Instant restart failed |
| 3 | 14 | 0 | All passed |
| 4 | 12 | 2 | Different tests failed |

Tests are non-deterministic - the same test may pass or fail on different runs.

## Configuration Reference

### Server Configuration (`OpcUaTestServer.cs`)
- MinSessionTimeout: 10 seconds (hardcoded in OPC UA server)
- Default port: 4840

### Client Configuration (`OpcUaTestClient.cs` defaults)
- SessionTimeout: 30s
- KeepAliveInterval: 5s
- OperationTimeout: 30s
- ReconnectInterval: 5s
- MaxReconnectDuration: 15s

### Port Pool (`OpcUaTestPortPool.cs`)
- Base port: 4840
- Pool size: 100 ports
- Max parallel: 4 tests
