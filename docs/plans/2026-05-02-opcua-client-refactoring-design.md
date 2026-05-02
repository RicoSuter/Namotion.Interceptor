# OPC UA Client Refactoring Design

## Problem

`OpcUaSubjectClientSource` (842 lines, 49 private members) has low cohesion: it handles write operations, reconnection diagnostics, health monitoring, fault injection, initial state reads, and lifecycle management. The write path (~170 lines) and diagnostics counters (~40 lines) are independent of the health loop and reconnection logic but live in the same class.

## Goal

Reduce coupling, improve readability, and make the write path independently testable — without changing any public behavior or breaking the existing test suite.

## Approach: Extract by Responsibility

Extract two internal classes, leaving `OpcUaSubjectClientSource` as a thin coordinator (~620 lines) for the remaining lifecycle/health/reconnection concerns which are genuinely coupled.

## Extraction 1: `OpcUaClientWriter`

**File:** `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientWriter.cs`

**Responsibility:** All OPC UA write operations — building write value collections, sending writes to the server, processing partial failures, and notifying ReadAfterWriteManager.

**Constructor dependencies (internal, no interfaces):**
- `SessionManager` — for `CurrentSession` and `ReadAfterWriteManager`
- `OpcUaClientConfiguration` — for `ValueConverter`
- `string opcUaNodeIdKey` — for property data lookup
- `ILogger`

**Methods moved from `OpcUaSubjectClientSource`:**
- `WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange>, CancellationToken)` → `ValueTask<WriteResult>`
- `CreateWriteValuesCollection(ReadOnlyMemory<SubjectPropertyChange>)` → `WriteValueCollection` (private)
- `ProcessWriteResults(StatusCodeCollection, ReadOnlyMemory<SubjectPropertyChange>)` → `WriteResult` (private)
- `TryGetWritableNodeId(SubjectPropertyChange, out NodeId, out RegisteredSubjectProperty)` → `bool` (private)
- `NotifyPropertiesWritten(ReadOnlyMemory<SubjectPropertyChange>)` (private)
- `IsTransientWriteError(StatusCode)` → `bool` (`internal static`, used by tests)

**Property moved:**
- `WriteBatchSize` → `int` (reads `SessionManager.CurrentSession.OperationLimits.MaxNodesPerWrite`)

**Delegation in `OpcUaSubjectClientSource`:**
```csharp
private readonly OpcUaClientWriter _writer;

public int WriteBatchSize => _writer.WriteBatchSize;

public ValueTask<WriteResult> WriteChangesAsync(
    ReadOnlyMemory<SubjectPropertyChange> changes,
    CancellationToken cancellationToken)
    => _writer.WriteChangesAsync(changes, cancellationToken);
```

## Extraction 2: `ReconnectionMetrics`

**File:** `src/Namotion.Interceptor.OpcUa/Client/ReconnectionMetrics.cs`

**Responsibility:** Thread-safe counters for reconnection diagnostics.

**State moved from `OpcUaSubjectClientSource`:**
- `_totalReconnectionAttempts` (long)
- `_successfulReconnections` (long)
- `_failedReconnections` (long)
- `_lastConnectedAtTicks` (long)

**Methods:**
- `RecordAttemptStart()` — `Interlocked.Increment`
- `RecordSuccess()` — increment + update `_lastConnectedAtTicks`
- `RecordFailure()` — `Interlocked.Increment`

**Read-only properties:**
- `TotalAttempts`, `Successful`, `Failed`, `LastConnectedAt`

**Removed from `OpcUaSubjectClientSource`:**
- 4 fields, 4 internal accessor properties, 2 `Record*` methods

**Changes in `OpcUaClientDiagnostics`:**
- Reads from `_source.ReconnectionMetrics.TotalAttempts` instead of `_source.TotalReconnectionAttempts`, etc.

**Changes in `SessionManager`:**
- Calls `_source.ReconnectionMetrics.RecordAttemptStart()` instead of `_source.RecordReconnectionAttemptStart()`

## Simplification: `ExecuteAsync` readability

Extract the three health check branches into named private methods within `OpcUaSubjectClientSource`:

```csharp
// Before: 90-line if/else if/else if block
// After:
if (sessionIsConnected && !isReconnecting)
    await HandleHealthySessionAsync(sessionManager, stoppingToken);
else if (!isReconnecting)
    await HandleDeadSessionAsync(sessionManager, stoppingToken);
else
    HandleReconnectionStallDetection(sessionManager);
```

Each method is ~20-30 lines with a clear name describing what it does.

## What Stays in `OpcUaSubjectClientSource` (~620 lines)

These responsibilities are genuinely coupled (share `_reconnectCts`, `_structureLock`, `_propertyWriter`, `_sessionManager`) and represent "client lifecycle management":

1. BackgroundService health loop (`ExecuteAsync` + 3 extracted methods)
2. Reconnection orchestration (`ReconnectSessionAsync`, `CreateMonitoredItemsForReconnection`)
3. Initial state read (`LoadInitialStateAsync`, `GetOwnedPropertiesWithNodeIds`)
4. Setup/Teardown (`StartListeningAsync`, `DisposeAsync`, `Reset`)
5. Fault injection (`KillSessionAsync`, `DisconnectTransportAsync`)
6. Root node browsing (`TryGetRootNodeAsync`, `BrowseNodeAsync`)

## Addition: Throughput Metrics

**New class: `Client/ThroughputCounter.cs`** (~60 lines)

A thread-safe 60-second sliding window rate counter.

```csharp
internal sealed class ThroughputCounter
{
    private readonly long[] _buckets = new long[60];
    private long _currentSecond;

    public void Add(int count = 1);
    public double GetRate(); // avg changes/s over last 60 seconds
}
```

**Two instances** owned by `OpcUaSubjectClientSource`:
- `_incomingThroughput` — incremented by SubscriptionManager (FastDataChange batch size) and PollingManager (per value change)
- `_outgoingThroughput` — incremented by OpcUaClientWriter (per successful write)

**Counting points:**
- `SubscriptionManager.OnFastDataChange`: `counter.Add(changes.Count)` after building the changes list
- `PollingManager.ProcessValueChange`: `counter.Add(1)` on actual change
- `OpcUaClientWriter.WriteChangesAsync`: `counter.Add(successCount)` after processing results

**Exposed via `OpcUaClientDiagnostics`** (public):
```csharp
public double IncomingChangesPerSecond => _source.IncomingThroughput.GetRate();
public double OutgoingChangesPerSecond => _source.OutgoingThroughput.GetRate();
```

## Verification

- All existing tests must pass unchanged (behavior-preserving refactor)
- Public API snapshot tests: `OpcUaClientDiagnostics` gains two new public properties (`IncomingChangesPerSecond`, `OutgoingChangesPerSecond`) — accept updated snapshot
- Build must succeed with zero warnings
