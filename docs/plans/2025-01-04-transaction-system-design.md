# Transaction System Design

## Overview

Transactions enable atomic property changes that are written to external sources (OPC UA, MQTT, etc.) before being applied to the local in-memory model. This ensures the local model always reflects the source truth.

### Core Guarantee

After `CommitAsync()` completes (with or without exception), the local model and source are always in sync for each property. Either both have the new value, or both have the old value.

## API

```csharp
await using var tx = await context.BeginTransactionAsync(
    mode: TransactionMode.Rollback,
    requirement: TransactionRequirement.None,
    conflictBehavior: TransactionConflictBehavior.FailOnConflict,
    cancellationToken: ct);

person.FirstName = "John";
person.Age = 30;

await tx.CommitAsync();
```

### Extension Method

```csharp
public static class SubjectTransactionExtensions
{
    public static ValueTask<SubjectTransaction> BeginTransactionAsync(
        this IInterceptorSubjectContext context,
        TransactionMode mode = TransactionMode.Rollback,
        TransactionRequirement requirement = TransactionRequirement.None,
        TransactionConflictBehavior conflictBehavior = TransactionConflictBehavior.FailOnConflict,
        CancellationToken cancellationToken = default);
}
```

## Enums

### TransactionMode

Controls what happens when source writes fail.

| Mode | On Failure | Guarantee |
|------|-----------|-----------|
| **BestEffort** | Apply successful changes only | Local = Source per-property |
| **Rollback** | Revert successful writes, apply nothing | All-or-nothing |

```csharp
public enum TransactionMode
{
    /// <summary>
    /// Apply successful changes only. Failed changes are not applied.
    /// Both source and local model remain in sync per-property.
    /// </summary>
    BestEffort,

    /// <summary>
    /// All-or-nothing. If any write fails, attempt to revert successful writes.
    /// No changes applied to local model on failure.
    /// </summary>
    Rollback
}
```

### TransactionRequirement

Validates transaction can be written atomically.

| Requirement | Validation |
|-------------|-----------|
| **None** | Multi-source, multi-batch allowed |
| **SingleWrite** | One source, one batch (atomic at source level) |

```csharp
public enum TransactionRequirement
{
    /// <summary>
    /// No validation. Multiple sources and batches allowed.
    /// </summary>
    None,

    /// <summary>
    /// All changes must go to one source in one batch.
    /// Guarantees atomic write at source level.
    /// </summary>
    SingleWrite
}
```

### TransactionConflictBehavior

Controls handling of concurrent modifications during the transaction.

| Behavior | On Concurrent Modification |
|----------|---------------------------|
| **FailOnConflict** | Throw `TransactionConflictException` on read/write/commit |
| **Ignore** | Last-write-wins, no conflict checking |

```csharp
public enum TransactionConflictBehavior
{
    /// <summary>
    /// Detect conflicts on read/write and throw TransactionConflictException.
    /// Use for command-style transactions requiring consistency.
    /// </summary>
    FailOnConflict,

    /// <summary>
    /// Ignore conflicts, last write wins.
    /// Use for UI sync where latest user input should always apply.
    /// </summary>
    Ignore
}
```

## Exceptions

```csharp
public class TransactionException : Exception
{
    /// <summary>
    /// Changes that were successfully written to source and applied to local model.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> AppliedChanges { get; }

    /// <summary>
    /// Changes that failed to write to source (not applied to local model).
    /// </summary>
    public IReadOnlyList<SourceWriteFailure> FailedChanges { get; }

    /// <summary>
    /// True if at least one change was applied successfully.
    /// </summary>
    public bool IsPartialSuccess => AppliedChanges.Count > 0 && FailedChanges.Count > 0;
}

public class TransactionConflictException : TransactionException
{
    /// <summary>
    /// Properties that were modified by another source during the transaction.
    /// </summary>
    public IReadOnlyList<PropertyReference> ConflictingProperties { get; }
}

public class SourceWriteFailure
{
    /// <summary>
    /// The change that failed to write.
    /// </summary>
    public SubjectPropertyChange Change { get; }

    /// <summary>
    /// The source that failed.
    /// </summary>
    public ISubjectSource Source { get; }

    /// <summary>
    /// The error that occurred.
    /// </summary>
    public Exception Error { get; }
}
```

## Commit Flow

### BeginTransactionAsync

```
context.BeginTransactionAsync():
  1. Acquire context lock (await SemaphoreSlim)
  2. Store: Context, StartTimestamp, options
  3. Set AsyncLocal<SubjectTransaction>
  4. Return transaction
```

Per-context serialization ensures only one transaction can be active per context at a time. This prevents race conditions when multiple concurrent requests modify the same context.

### Property SET (via interceptor)

```
Property SET:
  1. Validate: tx.Context == property.Subject.Context
     → If not: throw InvalidOperationException
  2. If FailOnConflict:
     → Check: LastChangedTimestamp > tx.StartTimestamp?
     → If yes: throw TransactionConflictException
  3. Store in PendingChanges: { Property, NewValue, OriginalValue }
```

### Property GET (via interceptor)

```
Property GET:
  1. If property in PendingChanges → return buffered NewValue
  2. If FailOnConflict:
     → Check: LastChangedTimestamp > tx.StartTimestamp?
     → If yes: throw TransactionConflictException
  3. Return current model value
```

### CommitAsync

```
CommitAsync():
  1. If FailOnConflict:
     → For each pending change: current value == OriginalValue?
     → If no: throw TransactionConflictException

  2. Validate SingleWrite requirement if set
     → All changes must go to one source
     → Change count must fit in source's WriteBatchSize

  3. Group changes by source, write to each source
     → Track successful/failed per source

  4. If any source failed AND mode == Rollback:
     → Attempt to revert successful source writes

  5. Determine which changes to apply based on TransactionMode:
     → BestEffort: apply successful only
     → Rollback: apply all or nothing

  6. Apply changes to local model

  7. If any failures: throw TransactionException
```

### DisposeAsync

```
DisposeAsync():
  1. Clear PendingChanges (discard uncommitted changes)
  2. Clear AsyncLocal
  3. Release context lock (SemaphoreSlim)
```

## Use Case Examples

### Command Execution (Strict Consistency)

All-or-nothing semantics with conflict detection. Use for coordinated updates where partial success is not acceptable.

```csharp
await using var tx = await context.BeginTransactionAsync(
    mode: TransactionMode.Rollback,
    conflictBehavior: TransactionConflictBehavior.FailOnConflict);

try
{
    robot.PositionX = 100;
    robot.PositionY = 200;
    robot.PositionZ = 50;

    await tx.CommitAsync();
}
catch (TransactionConflictException ex)
{
    // Another client modified these properties - retry with fresh data
    logger.LogWarning("Conflict detected on: {Properties}",
        string.Join(", ", ex.ConflictingProperties.Select(p => p.Metadata.Name)));
}
catch (TransactionException ex)
{
    // Write failed - all changes reverted
    logger.LogError("Command failed: {Error}", ex.Message);
}
```

### UI Property Sync (Partial Success OK)

Last-write-wins semantics for interactive editing. Partial failures are reported but don't block successful changes.

```csharp
await using var tx = await context.BeginTransactionAsync(
    mode: TransactionMode.BestEffort,
    conflictBehavior: TransactionConflictBehavior.Ignore);

settings.Temperature = userInput.Temperature;
settings.Pressure = userInput.Pressure;
settings.FlowRate = userInput.FlowRate;

try
{
    await tx.CommitAsync();
    return new Result { Success = true };
}
catch (TransactionException ex)
{
    // Report what worked and what didn't
    return new Result
    {
        Success = false,
        AppliedProperties = ex.AppliedChanges.Select(c => c.Property.Metadata.Name).ToList(),
        FailedProperties = ex.FailedChanges.Select(f => new FailureInfo
        {
            Property = f.Change.Property.Metadata.Name,
            Error = f.Error.Message
        }).ToList()
    };
}
```

### Atomic Batch Write

Ensure all changes are written in a single source operation.

```csharp
await using var tx = await context.BeginTransactionAsync(
    mode: TransactionMode.Rollback,
    requirement: TransactionRequirement.SingleWrite);

// All properties must belong to the same source
// and fit within source's WriteBatchSize
device.SetpointA = 100;
device.SetpointB = 200;

await tx.CommitAsync(); // Single atomic write to source
```

## Breaking Changes from Current Implementation

1. **`BeginTransaction()` removed** - Use `await context.BeginTransactionAsync(...)` instead
2. **Context required upfront** - Enables per-context serialization
3. **`TransactionMode.Strict` removed** - Use `Rollback` (all-or-nothing) or `BestEffort` (partial success)
4. **`AggregateException` replaced** - Use `TransactionException` with structured failure info
5. **New conflict detection** - `TransactionConflictBehavior` enum controls optimistic concurrency
6. **Per-context serialization** - Only one transaction active per context at a time

## Follow-up Considerations

### Read After Error

On source write errors (especially timeouts), consider reading back from source to verify consistency. Network errors may leave ambiguous state where it's unclear if the write succeeded.

Potential implementation:
- Add `ISubjectSource.ReadPropertiesAsync()` method
- On timeout/network error, read affected properties to verify state
- Reconcile local model with actual source state

### Multi-Context Transactions (Future)

Current design supports single-context transactions only. Future extension for multi-context:

```csharp
// Future API (non-breaking addition)
await using var tx = await SubjectTransaction.BeginTransactionAsync(
    new[] { context1, context2 },
    mode: TransactionMode.Rollback);
```

Implementation would acquire locks in consistent order to prevent deadlocks.
