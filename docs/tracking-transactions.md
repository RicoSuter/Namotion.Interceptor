# Transactions

The `Namotion.Interceptor.Tracking` package provides transaction support for batching property changes and committing them atomically. This is particularly useful when integrating with external data sources (OPC UA, MQTT, databases) where you want to write multiple changes as a single operation, or when you need to ensure consistency across multiple property updates.

## When to Use Transactions

Without transactions, property changes are applied to the in-process model immediately, while external sources synchronize asynchronously in the background. This is suitable when the local model is the source of truth, or eventual consistency is acceptable.

Use transactions when you need guarantees about what was actually persisted to external sources.

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
        Console.WriteLine($"{change.Property.Name}: {change.GetOldValue<object?>()} -> {change.GetNewValue<object?>()}");
    }

    await transaction.CommitAsync(cancellationToken);
}
```

### Last-Write-Wins Semantics

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

## Configuration Options

### Failure Handling

Controls behavior when some writes fail during commit.

```csharp
using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
```

| Value | Description |
|-------|-------------|
| `BestEffort` | Apply successful changes, rollback failed ones to keep each property in sync with its source. |
| `Rollback` | All-or-nothing across all properties - any failure reverts everything. |

**Behavior comparison:**

| Scenario | BestEffort | Rollback |
|----------|------------|----------|
| All succeed | All changes applied | All changes applied |
| Source write fails | Successful applied, failed not applied | All reverted |
| Local apply fails | Successful applied, failed sources rolled back | All reverted |
| Consistency | Per-property (each stays in sync) | All-or-nothing |
| Use when | Partial progress acceptable | Full atomicity required |

### Locking

Controls how concurrent transactions are synchronized.

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

| Value | Description |
|-------|-------------|
| `Exclusive` | Acquires lock at begin, holds until dispose. Other transactions wait. (default) |
| `Optimistic` | No lock at begin, acquires only during commit. Multiple transactions can run concurrently. |

### Conflict Behavior

Controls how optimistic transactions detect concurrent modifications.

| Value | Description |
|-------|-------------|
| `FailOnConflict` | Throw `TransactionConflictException` if values changed since transaction started. (default) |
| `Ignore` | Overwrite any concurrent changes without checking. |

### Requirements

Enforces constraints on transaction scope, useful for protocols like OPC UA.

```csharp
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    requirement: TransactionRequirement.SingleWrite);
```

| Value | Description |
|-------|-------------|
| `None` | No constraints - multiple sources and batches allowed. (default) |
| `SingleWrite` | Requires single source and changes within `WriteBatchSize` limit. Validated at commit time. |

**SingleWrite validation errors:**
- Multiple sources: `"SingleWrite requirement violated: Transaction spans 2 sources, but only 1 is allowed."`
- Exceeds batch size: `"SingleWrite requirement violated: Transaction contains 10 changes, but WriteBatchSize is 5."`

### Commit Timeout

**Default: 30-second timeout** for all commits. Throws `TaskCanceledException` if exceeded.

```csharp
// Custom timeout
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    commitTimeout: TimeSpan.FromSeconds(60));

// Disable timeout
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    commitTimeout: Timeout.InfiniteTimeSpan);
```

**Important:** The `cancellationToken` passed to `CommitAsync()` is **ignored during commit** - only the timeout can cancel. This prevents partial commits leaving sources inconsistent.

## Commit Flow

When `CommitAsync()` is called, changes are processed in stages. The exact flow depends on whether `WithSourceTransactions()` is configured.

### Without Source Transactions

When only `WithTransactions()` is configured (no external sources):

1. **Apply all changes** to the in-process model (calls property setters, triggers `OnChanging/OnChanged` methods)
2. If any apply fails and `Rollback` mode: revert successful applies
3. Fire change notifications

### With Source Transactions

When `WithSourceTransactions()` is configured, commits execute in three stages:

```
┌───────────────────────────────────────────────────────────────────────────┐
│  Stage 1: External Sources (parallel)                                     │
│  Write to OPC UA, MQTT, databases, etc.                                   │
│  Properties with SetSource() are processed here                           │
├───────────────────────────────────────────────────────────────────────────┤
│  Stage 2: Source-Bound Properties                                         │
│  Apply source-bound values to in-process model                            │
│  (After external writes succeed, triggers OnChanging/OnChanged hooks)     │
├───────────────────────────────────────────────────────────────────────────┤
│  Stage 3: Local Properties                                                │
│  Apply changes to properties WITHOUT sources                              │
│  (Calls to OnChanging/OnChanged partial methods can throw exceptions)     │
└───────────────────────────────────────────────────────────────────────────┘
```

**Rollback behavior on failure:**

| Failure Stage | BestEffort | Rollback |
|---------------|------------|----------|
| External sources fail | Only successful ones applied | All sources reverted, nothing applied |
| Source-bound apply fails | Successful applied, failed sources reverted | All reverted |
| Local properties fail | Successful applied, failed sources reverted | All reverted |

Both modes ensure **per-property consistency**: if a property's local apply fails, its source write is reverted. The difference is whether successful properties are kept (BestEffort) or also reverted (Rollback).

Revert operations call setters with old values, which also trigger `OnChanging/OnChanged` methods.

### Source Association

Properties must be associated with a source for external writes:

```csharp
// In your source implementation
propertyReference.SetSource(this);

// On commit, WriteChangesInBatchesAsync is called on the source
```

Properties without an associated source are applied directly in Stage 3.

## Error Handling

### TransactionException

Thrown when one or more changes fail during commit:

```csharp
try
{
    await transaction.CommitAsync(cancellationToken);
}
catch (TransactionException ex)
{
    Console.WriteLine($"Failed: {ex.FailedChanges.Count}, Applied: {ex.AppliedChanges.Count}");

    foreach (var change in ex.FailedChanges)
    {
        Console.WriteLine($"  {change.Property.Name}: {change.Error?.Message}");
    }
}
```

### TransactionConflictException

Thrown when optimistic locking detects concurrent modifications:

```csharp
try
{
    await transaction.CommitAsync(cancellationToken);
}
catch (TransactionConflictException ex)
{
    Console.WriteLine($"Conflicts on: {string.Join(", ", ex.ConflictingProperties.Select(p => p.Name))}");
}
```

### Other Exceptions

| Exception | Cause |
|-----------|-------|
| `InvalidOperationException` | Nested transactions, already committed, transactions not enabled |
| `ObjectDisposedException` | Using a disposed transaction |
| `TaskCanceledException` | Commit timeout exceeded |

## Advanced Topics

### Local Property Failures

Properties can fail during commit if their `OnChanging/OnChanged` partial methods throw exceptions. This is useful for hardware integrations like GPIO.

```csharp
[InterceptorSubject]
public partial class GpioDevice
{
    public partial bool LedA { get; set; }
    public partial bool LedB { get; set; }

    partial void OnLedAChanged(bool newValue) => _gpio.Write(pinA, newValue); // can throw!
    partial void OnLedBChanged(bool newValue) => _gpio.Write(pinB, newValue); // can throw!
}
```

When `OnChanging/OnChanged` throws:
- **BestEffort mode**: Other successful changes are applied, failure reported
- **Rollback mode**: All previous stages are reverted (sources + successful local changes)

### Derived Properties

Derived properties (marked with `[Derived]`) are handled specially:

- **During capture**: Derived property recalculation notifications are skipped
- **During read**: Derived properties return calculated value based on pending changes
- **After commit**: Derived properties are recalculated with committed values and notifications fired

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

    Console.WriteLine(person.FullName); // Output: John Doe (from pending values)

    await transaction.CommitAsync(cancellationToken);
    // FullName change notification is fired after commit
}
```

### Capture and Commit Replay

Transactions buffer property writes in a pending dictionary instead of applying them immediately. Consider a motor with a configurable speed limit, where a validator rejects `MotorSpeed` values that exceed `MaxAllowedSpeed`:

```csharp
[InterceptorSubject]
public partial class Motor
{
    public partial int MaxAllowedSpeed { get; set; } // Default: 100
    public partial int MotorSpeed { get; set; }      // Validated: must be <= MaxAllowedSpeed
}
```

**During the transaction (capture phase):**

```
motor.MaxAllowedSpeed = 200;
    → ValidationInterceptor: validates 200 for MaxAllowedSpeed → OK
    → TransactionInterceptor: captures in pending[MaxAllowedSpeed] = 200, stops chain
    (no field write, no notifications)

motor.MotorSpeed = 150;
    → ValidationInterceptor: validates 150 for MotorSpeed
        reads MaxAllowedSpeed → TransactionInterceptor returns pending value 200
        150 <= 200 → OK
    → TransactionInterceptor: captures in pending[MotorSpeed] = 150, stops chain
    (no field write, no notifications)
```

**On commit (replay phase):**

Changes are replayed in insertion order through the full interceptor chain against the real model:

```
await transaction.CommitAsync(cancellationToken);

Apply pending[MaxAllowedSpeed] = 200:
    → ValidationInterceptor: validates 200 → OK
    → TransactionInterceptor: IsCommitting=true → calls next (no capture)
    → Field write: MaxAllowedSpeed = 200
    → Notifications fired

Apply pending[MotorSpeed] = 150:
    → ValidationInterceptor: validates 150
        reads MaxAllowedSpeed from real model → 200 (already committed above)
        150 <= 200 → OK
    → TransactionInterceptor: IsCommitting=true → calls next (no capture)
    → Field write: MotorSpeed = 150
    → Notifications fired
```

**Why insertion order matters:** If `MotorSpeed` were committed before `MaxAllowedSpeed`, the validator would read `MaxAllowedSpeed = 100` from the real model and reject the write.

**Write order limitation:** Only the final value per property is stored (last write wins), and the commit position is determined by the *first* write to each property. If a property is re-written with a value that has different dependency requirements than the initial value, the commit order (based on first-write positions) may no longer match the dependency order needed by the final values. This can cause commit-time validation to fail even though the final set of values is consistent. To avoid this, ensure that re-writes do not shift dependency requirements relative to the original insertion order. See [#192](https://github.com/RicoSuter/Namotion.Interceptor/issues/192) for details and potential solutions.

### Retry After Conflict Detection

When using `TransactionConflictBehavior.FailOnConflict`, a `SubjectTransactionConflictException` is thrown *before* any writes are applied. The pending changes remain intact, so you can modify them and retry:

```csharp
using var transaction = await context.BeginTransactionAsync(
    TransactionFailureHandling.BestEffort,
    conflictBehavior: TransactionConflictBehavior.FailOnConflict);

motor.MaxAllowedSpeed = 200;
motor.MotorSpeed = 150;

try
{
    await transaction.CommitAsync(cancellationToken);
}
catch (SubjectTransactionConflictException ex)
{
    // Conflict detected before any writes — pending changes are still intact.
    // Re-read current state, adjust, and retry.
    motor.MaxAllowedSpeed = 250;
    await transaction.CommitAsync(cancellationToken);
}
```

Post-write failures (`SubjectTransactionException` from `BestEffort` or `Rollback`) clear the pending changes as part of the commit process, so retry is not possible — dispose the transaction and start a new one.

Note that concurrent `CommitAsync` calls on the same transaction are rejected — only one commit attempt can be in progress at a time.

### Thread Safety

- `BeginTransactionAsync()` uses `AsyncLocal<T>` to store the current transaction
- Exclusive transactions use a per-context semaphore
- Each async execution context has its own transaction scope
- The transaction is automatically cleared on `Dispose()`
- Concurrent `CommitAsync` calls on the same transaction instance are rejected

## Best Practices

1. **Always use `using` blocks** - Ensures proper disposal even on exceptions
2. **Keep transactions short** - Long transactions hold pending changes in memory
3. **Register transactions before notifications** - Call `WithTransactions()` before `WithPropertyChangeObservable()`
4. **Handle exceptions from CommitAsync** - Commits can fail partially
5. **Don't share transactions across threads** - Each async context should have its own transaction

## API Reference

### BeginTransactionAsync

```csharp
Task<SubjectTransaction> BeginTransactionAsync(
    TransactionFailureHandling failureHandling,
    TransactionLocking locking = TransactionLocking.Exclusive,
    TransactionRequirement requirement = TransactionRequirement.None,
    TransactionConflictBehavior conflictBehavior = TransactionConflictBehavior.FailOnConflict,
    TimeSpan? commitTimeout = null, // default: 30 seconds
    CancellationToken cancellationToken = default)
```

### SubjectTransaction

| Member | Description |
|--------|-------------|
| `CommitAsync(CancellationToken)` | Commits all pending changes |
| `GetPendingChanges()` | Returns list of pending changes |
| `Dispose()` | Discards uncommitted changes and releases lock |
| `Context` | The context this transaction is bound to |
| `Locking` | The locking mode |
| `ConflictBehavior` | The conflict detection behavior |

### Enums

**TransactionFailureHandling:**
- `BestEffort` - Apply successful changes, rollback failed ones (per-property consistency)
- `Rollback` - All-or-nothing across all properties

**TransactionLocking:**
- `Exclusive` - Lock at begin, hold until dispose
- `Optimistic` - Lock only during commit

**TransactionRequirement:**
- `None` - No constraints
- `SingleWrite` - Single source, within batch size

**TransactionConflictBehavior:**
- `FailOnConflict` - Throw on concurrent changes
- `Ignore` - Overwrite without checking

## Limitations

### Single Context Binding

A transaction is bound to a single `IInterceptorSubjectContext`. Attempting to modify a property on a subject from a different context throws `InvalidOperationException`:

```csharp
var context1 = InterceptorSubjectContext.Create().WithTransactions();
var context2 = InterceptorSubjectContext.Create().WithTransactions();

var person1 = new Person(context1);
var person2 = new Person(context2);

using var tx = await context1.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

person1.FirstName = "John";  // OK - same context as transaction
person2.FirstName = "Jane";  // THROWS InvalidOperationException:
                              // "Cannot modify property 'FirstName': Transaction is bound to a different context."
```

### Nested Transactions

Nested transactions are not supported. Attempting to start a transaction while one is already active in the same execution context throws `InvalidOperationException`:

```csharp
using var tx1 = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

// THROWS: "Nested transactions are not supported."
using var tx2 = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
```

### Optimistic Concurrency - ABA Problem

`FailOnConflict` uses value-based detection. If a property changes A → B → A between start and commit, no conflict is detected. For strict version-based detection, implement external versioning.

### Multi-Source Transactions

When writing to multiple sources, true atomicity is not guaranteed:
1. Writes are parallel for performance
2. On failure, successful sources are rolled back (best effort)
3. During rollback, sources may temporarily have inconsistent values

For strict atomicity, use a single source per transaction or implement application-level compensation.

### Rollback is Best-Effort

Rollback operations can also fail. If revert fails, `TransactionException` includes both the original failure and the revert failure. The system cannot guarantee consistency in this case.
