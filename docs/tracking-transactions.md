# Transactions

The `Namotion.Interceptor.Tracking` package provides transaction support for batching property changes and committing them atomically. This is particularly useful when integrating with external data sources (OPC UA, MQTT, databases) where you want to write multiple changes as a single operation, or when you need to ensure consistency across multiple property updates.

## When to Use Transactions

Without transactions, property changes are applied to the in-process model immediately, while external sources synchronize asynchronously in the background. This is suitable when the local model is the source of truth, or eventual consistency is acceptable.

Use transactions when you need guarantees about what was actually persisted to external sources. See [Transaction Modes](#transaction-modes) and [Transaction Requirements](#transaction-requirements) for configuration options.

## Overview

Transactions provide:
- **Configurable commit modes**: Choose between best-effort or rollback behavior on partial failures
- **Read-your-writes consistency**: Reading a property inside a transaction returns the pending value
- **Notification suppression**: Change notifications are suppressed during the transaction and fired after commit
- **External source integration**: Changes can be written to external sources before being applied to the in-process model
- **Rollback on dispose**: Uncommitted changes are discarded when the transaction is disposed

## Setup

Enable transaction support in your interceptor context:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithTransactions() // Required for in-memory transaction support (opt-in)
    .WithSourceTransactions(); // Required for source write transaction support (opt-in)
```

## Basic Usage

### Starting a Transaction

```csharp
var person = new Person(context);

using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    person.FirstName = "John";
    person.LastName = "Doe";

    // Changes are captured but not yet applied to the model
    // No change notifications are fired yet

    await transaction.CommitAsync(cancellationToken);

    // All changes are now applied and notifications are fired
}
```

### Read-Your-Writes Consistency

Inside a transaction, reading a property returns the pending value:

```csharp
using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    person.FirstName = "John";

    // Reading returns the pending value, not the committed value
    Console.WriteLine(person.FirstName); // Output: John

    await transaction.CommitAsync(cancellationToken);
}
```

### Rollback (Implicit)

If a transaction is disposed without committing, changes are discarded:

```csharp
using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    person.FirstName = "John";

    // No commit - changes are discarded when using block exits
}

Console.WriteLine(person.FirstName); // Output: (original value, not "John")
```

### Inspecting Pending Changes

You can inspect which changes are pending before committing:

```csharp
using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    person.FirstName = "John";
    person.LastName = "Doe";

    var pendingChanges = transaction.GetPendingChanges();
    foreach (var change in pendingChanges)
    {
        Console.WriteLine($"{change.Property.Name}: {change.GetOldValue<object>()} -> {change.GetNewValue<object>()}");
    }

    await transaction.CommitAsync(cancellationToken);
}
```

## Last-Write-Wins Semantics

If the same property is written multiple times within a transaction, only the final value is committed:

```csharp
using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    person.FirstName = "John";
    person.FirstName = "Jane";
    person.FirstName = "Bob";

    // Only one pending change for FirstName
    // OldValue = original value, NewValue = "Bob"

    await transaction.CommitAsync(cancellationToken);
}
```

## Exception Handling

### Already Committed

Calling `CommitAsync()` twice throws an exception:

```csharp
using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    person.FirstName = "John";
    await transaction.CommitAsync(cancellationToken);

    await transaction.CommitAsync(cancellationToken); // Throws InvalidOperationException
}
```

### Disposed Transaction

Using a disposed transaction throws an exception:

```csharp
var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
transaction.Dispose();

await transaction.CommitAsync(cancellationToken); // Throws ObjectDisposedException
```

### Nested Transactions

Nested transactions are not supported:

```csharp
using (var transaction1 = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    using (var transaction2 = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort)) // Throws InvalidOperationException
    {
    }
}
```

## Failure Handling

When creating a transaction, you must specify a `TransactionFailureHandling` to control how partial failures are handled:

```csharp
using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
```

Transaction modes are relevant in two scenarios:

1. **Multiple sources**: When properties in the transaction are bound to different external sources, some sources may succeed while others fail.

2. **Source batching**: When a source has a `WriteBatchSize` configured, changes are written in batches. If an early batch succeeds but a later batch fails, the source is considered failed but some changes were already written.

In both cases, the transaction mode determines what happens to successfully written changes when a failure occurs.

### BestEffort

BestEffort mode maximizes the number of successful writes. When some external sources fail, the changes that succeeded are still applied to the in-process model. This is useful when partial progress is acceptable and you want to handle failures gracefully without losing successful updates.

```csharp
using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
device.Temperature = 25.5;  // Bound to Source A
device.Pressure = 101.3;    // Bound to Source B

await tx.CommitAsync(cancellationToken);
```

**When all sources succeed:**
- All changes are applied to the in-process model
- No exception is thrown

**When Source A succeeds but Source B fails:**
- Source A has the new Temperature value (25.5)
- Source B was not updated (Pressure write failed)
- In-process model: Temperature is updated to 25.5, Pressure unchanged
- `TransactionException` is thrown containing the Source B failure

Use BestEffort when partial updates are acceptable and you prefer to maximize successful writes rather than failing entirely.

### Rollback

Rollback mode provides the strongest consistency guarantee. If any external source fails, successfully written sources are **reverted** to their original values (best effort). This minimizes divergence between external sources and the local model.

```csharp
using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
device.Temperature = 25.5;  // Bound to Source A
device.Pressure = 101.3;    // Bound to Source B

await tx.CommitAsync(cancellationToken);
```

**When all sources succeed:**
- All changes are applied to the in-process model
- No exception is thrown

**When Source A succeeds but Source B fails:**
- Source A is **reverted**: Temperature written back to original value
- Source B was not updated (Pressure write failed)
- In-process model: **Unchanged** (both keep old values)
- `TransactionException` is thrown (may include revert failures if revert also failed)
- **Consistency**: All sources and local model should have the same (old) values

Use Rollback when consistency is critical and you want to minimize the chance of external sources having different values than the local model. Note that rollback is best-effort - if the revert write also fails, you'll receive both the original failure and the revert failure in the exception.

> **Performance note**: Rollback mode may perform additional writes on failure (to revert successful sources), making it slower than BestEffort mode in failure scenarios.

## Transaction Locking

Transactions support two locking modes that control how concurrent transactions are synchronized:

```csharp
// Exclusive locking (default) - serializes all transactions
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    TransactionLocking.Exclusive);

// Optimistic locking - allows concurrent transactions, serializes only at commit
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    TransactionLocking.Optimistic);
```

### Exclusive (Default)

Exclusive locking acquires a lock when the transaction begins and holds it until the transaction is disposed. This ensures only one transaction executes at a time per context, preventing any possibility of conflicts.

**Behavior:**
- Lock acquired at `BeginTransactionAsync`
- Other transactions wait until this transaction completes
- No conflict detection needed (transactions are serialized)
- Best for scenarios where transactions are short-lived

### Optimistic

Optimistic locking allows multiple transactions to run concurrently, acquiring the lock only during the commit phase. This enables higher throughput when transactions rarely conflict.

**Behavior:**
- No lock at `BeginTransactionAsync` - returns immediately
- Multiple transactions can capture changes concurrently
- Lock acquired only during `CommitAsync`
- Conflicts detected via `TransactionConflictBehavior.FailOnConflict`

**Example with conflict detection:**

```csharp
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    TransactionLocking.Optimistic,
    conflictBehavior: TransactionConflictBehavior.FailOnConflict);

device.Temperature = 25.5;

// If another transaction modified Temperature since we started,
// CommitAsync will throw TransactionConflictException
await tx.CommitAsync(cancellationToken);
```

### Locking Mode Comparison

| Aspect | Exclusive | Optimistic |
|--------|-----------|------------|
| Lock at Begin | Yes (waits if busy) | No (immediate) |
| During Transaction | Other transactions wait | Multiple transactions run concurrently |
| Lock at Commit | Already held | Acquires for commit phase |
| Conflict Detection | N/A (serialized) | Via `FailOnConflict` behavior |
| Best For | Short transactions, high contention | Long transactions, low contention |

## Transaction Requirements

In addition to failure handling, you can specify a `TransactionRequirement` to enforce constraints on the transaction scope. This is particularly useful for industrial protocols like OPC UA where you want to ensure predictable behavior.

```csharp
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    requirement: TransactionRequirement.SingleWrite);
```

### None (Default)

No constraints - multiple sources and multiple batches are allowed. This is the default behavior.

### SingleWrite

The `SingleWrite` requirement ensures that all changes are written in a single `WriteChangesAsync` call. This is validated at commit time and requires:

1. **Single source**: All changes with an associated source must use the same source
2. **Single batch**: The number of changes must not exceed the source's `WriteBatchSize`

Changes to properties without a source are always allowed and don't count toward these constraints.

```csharp
// OPC UA scenario: maximum safety with single write requirement
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    TransactionRequirement.SingleWrite);

device.Temperature = 25.5;  // Bound to OPC UA source
device.Pressure = 101.3;    // Same OPC UA source

await tx.CommitAsync(cancellationToken);
```

**Why use SingleWrite with Rollback?**

For protocols like OPC UA that don't guarantee atomic multi-node writes:
- `SingleWrite` ensures only one `WriteChangesAsync` call is made
- If that single write fails (even partially), `Rollback` mode will attempt to revert
- Since there's only one source and one batch, the rollback is simpler and more reliable
- Validation fails early (before any writes) if the requirements aren't met

**Validation errors:**

If the requirements aren't met, `CommitAsync()` throws a `TransactionException` with a descriptive error:

```csharp
// Multiple sources - will fail validation
new PropertyReference(device, "Temperature").SetSource(opcUaSource);
new PropertyReference(device, "Pressure").SetSource(mqttSource);  // Different source!

using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    TransactionRequirement.SingleWrite);

device.Temperature = 25.5;
device.Pressure = 101.3;

// Throws: "SingleWrite requirement violated: Transaction contains changes for 2 sources, but only 1 is allowed."
await tx.CommitAsync(cancellationToken);
```

## External Source Integration

When integrating with external data sources (OPC UA, MQTT, databases), use `WithSourceTransactions()` from the `Namotion.Interceptor.Sources` package:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithSourceTransactions(); // Enables external source writes
```

With this configuration, `CommitAsync()` will:

1. **Write to external sources first**: Changes are grouped by their associated source and written in batches
2. **Handle failures per mode**: BestEffort applies successes, Rollback requires all to succeed
3. **Apply to in-process model**: After external writes complete (and pass mode checks), changes are applied
4. **Report failures**: A `TransactionException` is thrown containing all source write failures

```csharp
try
{
    using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback))
    {
        // These properties are bound to an OPC UA source
        device.Temperature = 25.5;
        device.Pressure = 101.3;

        await transaction.CommitAsync(cancellationToken);
    }
}
catch (TransactionException ex)
{
    foreach (var failure in ex.FailedChanges)
    {
        Console.WriteLine($"Failed to write {failure.Change.Property.Name}: {failure.Error?.Message}");
    }
}
```

### Source Association

Properties must be associated with a source for external writes:

```csharp
// In your source implementation
propertyReference.SetSource(this); // Associate property with this source

// Later, when the transaction commits, WriteChangesInBatchesAsync is called on the source
```

Properties without an associated source are applied directly to the in-process model without external writes.

## Derived Properties

Derived properties (marked with `[Derived]`) are handled specially during transactions:

- **During capture**: Derived property recalculation is skipped
- **During read**: Derived properties return their calculated value based on pending changes
- **After commit**: Derived properties are recalculated with the committed values

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }

    [Derived]
    public string FullName => $"{FirstName} {LastName}";
}

using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
{
    person.FirstName = "John";
    person.LastName = "Doe";

    // FullName is calculated from pending values
    Console.WriteLine(person.FullName); // Output: John Doe

    await transaction.CommitAsync(cancellationToken);
    // FullName change notification is fired after commit
}
```

## Thread Safety

- `context.BeginTransactionAsync(TransactionFailureHandling.BestEffort)` uses `AsyncLocal<T>` to store the current transaction, making it safe for async/await patterns
- Exclusive transactions use a per-context semaphore to ensure only one transaction executes at a time
- Each async execution context has its own transaction scope
- The transaction is automatically cleared when `Dispose()` is called
- On `CommitAsync()`, write callbacks are resolved from each subject's context, supporting multi-context scenarios

## Best Practices

1. **Always use `using` blocks**: This ensures transactions are properly disposed even if exceptions occur
2. **Keep transactions short**: Long-running transactions hold pending changes in memory
3. **Register transactions before notifications**: Ensure `WithTransactions()` is called before `WithPropertyChangeObservable()` or `WithPropertyChangeQueue()`
4. **Handle exceptions from CommitAsync**: Especially when using external sources, commits can fail partially
5. **Don't share transactions across threads**: Each async context should have its own transaction

## API Reference

### IInterceptorSubjectContext Extensions

| Member | Description |
|--------|-------------|
| `BeginTransactionAsync(failureHandling, locking, requirement, conflictBehavior, ct)` | Begins a new transaction with the specified options |

**Parameters:**
- `failureHandling` (required): How to handle partial failures (`BestEffort` or `Rollback`)
- `locking` (default: `Exclusive`): Concurrency control mode
- `requirement` (default: `None`): Validation constraints
- `conflictBehavior` (default: `FailOnConflict`): How to handle value conflicts at commit time

### TransactionFailureHandling

| Value | Description |
|-------|-------------|
| `BestEffort` | Apply successful changes even if some sources fail |
| `Rollback` | All-or-nothing with best-effort revert of successful source writes |

### TransactionLocking

| Value | Description |
|-------|-------------|
| `Exclusive` | Acquires lock at begin, holds until dispose (default) |
| `Optimistic` | No lock at begin, acquires only during commit phase |

### TransactionRequirement

| Value | Description |
|-------|-------------|
| `None` | No requirements - multiple sources and batches allowed (default) |
| `SingleWrite` | Requires single source and changes within `WriteBatchSize` limit |

### TransactionConflictBehavior

| Value | Description |
|-------|-------------|
| `FailOnConflict` | Throw `TransactionConflictException` if values changed since transaction started (default) |
| `Ignore` | Overwrite any concurrent changes without checking |
