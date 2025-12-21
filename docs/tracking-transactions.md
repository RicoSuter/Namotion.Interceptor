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

## Commit Timeout and Cancellation

**Default: 30-second timeout** for all commits. Throws `TaskCanceledException` if exceeded.

```csharp
// Default 30s timeout
await transaction.CommitAsync(cancellationToken);

// Custom timeout or disable
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    commitTimeout: TimeSpan.FromSeconds(60)); // Custom
    // or: commitTimeout: Timeout.InfiniteTimeSpan); // Disable
```

**Important:** The `cancellationToken` is **ignored during commit** - only the timeout can cancel. This prevents partial commits leaving sources inconsistent (mid-commit cancellation → some sources updated, others not, failed rollbacks).

Timeout protects against hung operations while ensuring commits complete atomically.

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

When creating a transaction, specify a `TransactionFailureHandling` mode to control partial failure behavior:

```csharp
using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
device.Temperature = 25.5;  // Bound to Source A
device.Pressure = 101.3;    // Bound to Source B

await tx.CommitAsync(cancellationToken);
```

Failure modes matter when:
- Properties are bound to **different external sources** (one may succeed, another fail)
- A source has `WriteBatchSize` configured (early batches succeed, later batches fail)

### Behavior Comparison

| Scenario | BestEffort | Rollback |
|----------|------------|----------|
| **All sources succeed** | All changes applied to model<br>No exception thrown | All changes applied to model<br>No exception thrown |
| **Source A succeeds,<br>Source B fails** | Source A: keeps new value (25.5)<br>Source B: not updated<br>Model: Temperature=25.5, Pressure=unchanged<br>`TransactionException` thrown | Source A: **reverted** to original<br>Source B: not updated<br>Model: **unchanged** (both old values)<br>`TransactionException` thrown |
| **Consistency** | Partial updates accepted | All-or-nothing (best effort) |
| **Performance on failure** | Faster (no revert writes) | Slower (reverts successful sources) |
| **Use when** | Partial progress acceptable,<br>maximize successful writes | Consistency critical,<br>minimize source divergence |

**Notes:**
- Rollback revert is best-effort. If revert also fails, the exception includes both failures.
- `TransactionException.FailedChanges` contains details of all failures.

## Transaction Locking

Control how concurrent transactions are synchronized:

```csharp
// Exclusive (default) - lock at begin, serializes all transactions
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    TransactionLocking.Exclusive);

// Optimistic - lock only at commit, allows concurrent capture
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    TransactionLocking.Optimistic,
    conflictBehavior: TransactionConflictBehavior.FailOnConflict);
```

| Aspect | Exclusive (Default) | Optimistic |
|--------|---------------------|------------|
| Lock at Begin | Yes (waits if busy) | No (returns immediately) |
| During Transaction | Other transactions wait | Multiple transactions run concurrently |
| Lock at Commit | Already held | Acquires for commit phase |
| Conflict Detection | N/A (serialized) | Via `FailOnConflict` - throws on concurrent changes |
| Best For | Short transactions, high contention | Long transactions, low contention |

**Optimistic conflict example:**
```csharp
// If another transaction modified Temperature since we started,
// CommitAsync will throw TransactionConflictException
await tx.CommitAsync(cancellationToken);
```

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

Enable external source writes with `WithSourceTransactions()`:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithSourceTransactions(); // Enables external source writes
```

`CommitAsync()` writes to external sources first (grouped by source, written in batches), then applies to the in-process model. Failures are handled per the `TransactionFailureHandling` mode.

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
| `BeginTransactionAsync(failureHandling, locking, requirement, conflictBehavior, commitTimeout, ct)` | Begins a new transaction with the specified options |

**Parameters:**
- `failureHandling` (required): How to handle partial failures (`BestEffort` or `Rollback`)
- `locking` (default: `Exclusive`): Concurrency control mode
- `requirement` (default: `None`): Validation constraints
- `conflictBehavior` (default: `FailOnConflict`): How to handle value conflicts at commit time
- `commitTimeout` (default: 30 seconds): Maximum time for commit operation. Use `Timeout.InfiniteTimeSpan` to disable
- `cancellationToken` (default: `default`): Used before commit starts, ignored during commit phase

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

## Limitations

### Optimistic Concurrency - ABA Problem

The `TransactionConflictBehavior.FailOnConflict` option uses value-based conflict detection.
This means if a property value changes from A → B → A between transaction start and commit,
no conflict will be detected (the "ABA problem").

For most use cases, this is acceptable since the final state matches the expected state.
If strict version-based conflict detection is required, consider implementing external
versioning at the application level.

### Multi-Source Transactions - Eventual Consistency

When a transaction writes to multiple external sources (e.g., OPC UA server and MQTT broker),
true atomicity is not guaranteed. Writes are performed in parallel for performance, but if
one source fails:

1. Successfully written sources will be rolled back (best effort)
2. During the rollback window, sources may temporarily have inconsistent values
3. If rollback fails, the system logs the error but cannot guarantee consistency

For use cases requiring strict atomicity across sources, consider:
- Using a single source per transaction
- Implementing application-level compensation logic
- Using sources that support distributed transactions (2PC)

This limitation is inherent to distributed systems without two-phase commit protocols.
