# Transactions

The `Namotion.Interceptor.Tracking` package batches property changes in a transaction and commits them together. Without a transaction, a write lands in the local model immediately and external sources (OPC UA, MQTT, databases) are synchronized in the background. This suits a local model that is the source of truth, or eventual consistency. Use a transaction when several changes must land together, or when you need to know what an external source accepted before the local model changes.

A transaction provides:
- **Read-your-writes**: reading a property inside the transaction returns its pending value.
- **Silent capture**: captured non-derived writes fire no notifications until the commit applies them.
- **Source-first commits**: with source transactions, changes are written to external sources before they are applied to the local model.
- **Configurable failure handling**: keep what succeeded (`BestEffort`) or revert everything (`Rollback`).
- **Rollback on dispose**: disposing an uncommitted transaction discards its changes.

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

### Last-Write-Wins and Commit Order

Each property has at most one pending change. Its new value is the last value written, its old value is the landed value before the first write, and its position is that of the first write. The commit applies the changes in this order.

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

Writing a property back to its landed value cancels its pending change, so the change no longer appears in `GetPendingChanges()` and is neither written, conflict-checked nor checked by handlers. A later write restores the change with its original old value and position, so conflict detection still sees a concurrent change that landed in between.

## Configuration Options

### Failure Handling

Controls what happens when some changes fail during commit.

```csharp
using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
```

| Value | On failure |
|-------|------------|
| `BestEffort` | Keeps the changes that succeeded. When a local apply fails, that property's source write is reverted, so each property stays in sync with its source. Use when partial progress is acceptable. |
| `Rollback` | Reverts every source write and every local apply. Use when you need all or nothing. |

With [transaction handlers](#transaction-handlers), such as [validation](validation.md#transactions), a failed local apply reverts everything in both modes. See [Commit Failures](#commit-failures) for every case.

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

Controls value-based conflict detection in both locking modes; `BestEffort` failure handling does not disable it.

| Value | Description |
|-------|-------------|
| `FailOnConflict` | Throw `SubjectTransactionConflictException` if values changed since transaction started. (default) |
| `Ignore` | Overwrite any concurrent changes without checking. |

### Validation Mode

Controls when transaction handlers, such as property validation, validate the written values: `Immediate` (default) validates each write and the final set at commit, `Deferred` only the final set at commit. See [Validation in transactions](validation.md#transactions).

```csharp
using var tx = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    validationMode: TransactionValidationMode.Deferred);
```

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
| `SingleWrite` | Requires a single source and at most `WriteBatchSize` changes for it. |

The transaction writer enforces `SingleWrite` at commit, before writing anything. A violation is reported as a failed source write of the source-bound changes, with an error such as `SingleWrite requirement violated: Transaction spans 2 sources (...), but only 1 is allowed.` or `SingleWrite requirement violated: Transaction contains 10 changes for source '...', but WriteBatchSize is 5.`

### Commit Timeout

**Default: 30 seconds.** The timeout bounds only the transaction writer's source writes and reverts; commits without a writer are not timed. What a timeout causes depends on when it fires, see [Commit Failures](#commit-failures).

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

**Important:** the `cancellationToken` passed to `CommitAsync()` only cancels waiting for the optimistic lock. Once the commit runs, only the timeout can cancel it, so a caller cannot leave sources partially written.

## Commit Flow

`CommitAsync()` runs these stages in order:

1. **Commit check**: the [transaction handlers](#transaction-handlers) check the final set of changes.
2. **Conflict detection**: with `FailOnConflict`, a property whose landed value changed since its first write aborts the commit.
3. **Source writes** (with source transactions, that is `WithSourceTransactions()` or a custom `ITransactionWriter`): source-bound changes are written to their sources, in parallel when there are several. Nothing is applied to the local model yet.
4. **Pre-apply check** (with source transactions): the handlers check the changes that will be applied again.
5. **Local apply**: source-bound and local changes are applied in a single pass, in commit order, except those whose source write failed. Applying calls the property setters, so `OnChanging`/`OnChanged` hooks run and notifications fire.
6. **Compensation** on failure: the reverts described in [Commit Failures](#commit-failures). A local revert writes the old value through the setter, so hooks run again.

Nothing is written before stage 3, so a failure in stages 1 and 2 leaves the transaction retryable.

Stage 5 applies source-bound changes marked with a `Confirmed` origin carrying the source that accepted them in stage 3, so the outbound change queue treats their notifications as echoes and a committed value is written to its source exactly once. See [Change notification source semantics](connectors.md#change-notification-source-semantics) for the full contract, including cascade and derived property behavior.

Stage 3 is performed by an `ITransactionWriter`; the built-in `SourceTransactionWriter` is registered by `WithSourceTransactions()`. Replacing it is an advanced scenario, see [Implementing a Custom Transaction Writer](#implementing-a-custom-transaction-writer).

### Source Association

Properties must be associated with a source for external writes:

```csharp
// In your source implementation
propertyReference.SetSource(this);

// On commit, WriteChangesInBatchesAsync is called on the source
```

Properties without an associated source are applied in stage 5 alongside source-bound properties.

## Guarantees

- **Commit order**: changes are applied in commit order, see [Last-Write-Wins and Commit Order](#last-write-wins-and-commit-order).
- **Per-property consistency**: in both failure handling modes, a property whose local apply fails has its source write reverted.
- **All or nothing**: `Rollback` lands every change or none. With transaction handlers, a failed local apply or a pre-apply rejection lands none of the checked changes in both modes. Both hold unless a revert fails.
- **Serialized commits**: every commit holds the context's transaction lock, so transactional writes cannot interleave with a commit.
- **Quiescent consistency, not isolation**: sources are written before the local model, so while a commit runs a source can hold a new value the local model does not have yet, and during `Rollback` compensation a source briefly holds a value that is then taken back. Source and local model agree once the commit settles, unless a revert fails (see [When a Revert Fails](#when-a-revert-fails)).

Writes outside transactions are not serialized with commits, see [Edge Cases and Limitations](#edge-cases-and-limitations).

## Commit Failures

### Outcomes

A commit attempt ends in one of three states:

| Outcome | Pending changes | What to do |
|---------|-----------------|------------|
| Committed | Cleared | Dispose the transaction. |
| Retryable failure | Intact | Fix the cause and call `CommitAsync` again on the same transaction, or dispose it. |
| Terminal failure | Cleared | Dispose the transaction and start a new one. A second `CommitAsync` throws `InvalidOperationException`. |

A failure is retryable when nothing was written yet, or when the commit timeout's `OperationCanceledException` propagates (see the [Failure Table](#failure-table) and [Implementing a Custom Transaction Writer](#implementing-a-custom-transaction-writer)). Every other failure is terminal: writes may have reached sources or the local model and compensation already ran once, so a retry would replay the snapshot onto state that has already moved, for example re-pushing values to a source that already accepted them. A committed or terminally failed transaction is inactive even before disposal: later reads and writes use the landed model.

### Failure Table

"Old" is the value before the transaction, "new" the transaction's value. "Without a source" means changes of properties without a source. "With handlers" means the transaction has at least one [transaction handler](#transaction-handlers), for example validation. Unless noted, the exception is a `SubjectTransactionException`.

| Failure | End state | `FailedChanges` | Retry |
|---------|-----------|-----------------|-------|
| Commit check rejects | All old, nothing written. The handler's exception propagates unchanged, for validation a `SubjectTransactionValidationException`. | Validation: the invalid changes | Yes |
| Conflict detected (`SubjectTransactionConflictException`, see `ConflictingProperties`) | All old, nothing written | Empty | Yes |
| Optimistic lock wait cancelled (`OperationCanceledException`) | All old, nothing written | n/a | Yes |
| Source write fails or times out, `BestEffort` | Successful writes new at the source and locally, failed ones old | The failed source writes | No |
| Source write fails or times out, or a custom writer reports errors without failed changes, `Rollback` | All old, written sources reverted | The failed source writes, the changes without a source, and any failed source revert | No |
| Commit timeout during that `Rollback` revert (`OperationCanceledException`) | Local all old, sources partially reverted | n/a | Yes, a successful retry writes every source again |
| Pre-apply check rejects, any mode | All old, written sources reverted | Every change (failed source writes first), then each failed source revert again | No |
| Local apply fails, `BestEffort` without handlers | Applied changes new, failed ones old with their source write reverted | The failed source writes and the failed applies | No |
| Local apply fails, `Rollback` without handlers | All old, local applies and source writes reverted | The failed applies and any failed local or source revert; with source writes, also the changes without a source | No |
| Local apply fails, with handlers, any mode | All old: no further change is applied, the applied ones and the source writes are reverted | Every change (failed source writes first), then each failed local revert (in reverse apply order), then each remaining failed source revert | No |
| Custom writer reports errors without failed changes, `BestEffort` | All new | Empty, the errors are in `Errors` | No |
| Custom writer throws, or violates its contract | Local all old, sources unknown and not reverted | Every change | No |

Notes:
- `AppliedChanges` lists the changes that landed. It is empty whenever the commit reverted or applied nothing.
- A change whose apply threw after its value was written, for example because a derived recalculation failed, keeps that value: the failed apply itself is not reverted ([#674](https://github.com/RicoSuter/Namotion.Interceptor/issues/674)).
- `Errors` is not aligned one to one with `FailedChanges`: a failed source write or revert yields one error per source, a failed local apply or revert one per change.
- A commit timeout during a source write is reported as a failed write. During a source revert after a pre-apply rejection or after the local apply started, it is reported as a failed revert.
- Usage errors throw `InvalidOperationException` (nested transaction, already committed, transactions not enabled, commit from a different async flow, concurrent `CommitAsync`) or `ObjectDisposedException` (transaction disposed, also while the commit check ran).

### When a Revert Fails

Reverts are compensating writes, not an undo, and they can fail too. A source that just failed or timed out is asked to accept another write, so this is the most likely way to end inconsistent. The affected property then has different values at the source and in the local model:

| Stuck revert | Source ends | Local ends | Reported |
|--------------|-------------|------------|----------|
| A source revert fails or throws, or times out after a pre-apply rejection or after the local apply started | New | Old | In `FailedChanges` and `Errors`, terminal |
| A local revert fails (`Rollback`, or with handlers) | Old | New | In `FailedChanges` and `Errors`, terminal |
| The commit timeout interrupts the `Rollback` revert after a failed source write or writer error | Partially reverted | Old | `OperationCanceledException`, retryable; the divergence is transient, a successful retry writes everything again |
| A custom writer throws from `WriteToSourcesAsync` | Unknown, never reverted | Old | Not identifiable: the transaction cannot know which sources were touched |

`FailedChanges` can also hold properties that ended consistent, such as changes without a source that a `Rollback` commit reverted cleanly. When it lists every change (after a pre-apply rejection, or a failed apply with handlers), each stuck property is listed a second time after the full list, so it can be told apart.

### Local Apply Failures

A local apply fails when an `OnChanging`/`OnChanged` hook or a write interceptor throws, for example in hardware integrations:

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

### Retrying

Conflicts and commit check rejections are thrown before anything is written, so the pending changes stay intact and you can adjust them and commit again. Both exception types derive from `SubjectTransactionException`, so catch them before the base type:

```csharp
using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);

motor.MaxAllowedSpeed = 200;
motor.MotorSpeed = 150; // validated: must not exceed MaxAllowedSpeed

try
{
    await transaction.CommitAsync(cancellationToken);
}
catch (SubjectTransactionConflictException)
{
    // Nothing was written: re-read the current state, adjust, and retry.
    motor.MaxAllowedSpeed = 250;
    await transaction.CommitAsync(cancellationToken);
}
catch (SubjectTransactionValidationException)
{
    // Nothing was written: fix the invalid values and retry.
    motor.MotorSpeed = 90;
    await transaction.CommitAsync(cancellationToken);
}
catch (SubjectTransactionException exception)
{
    // Changes may have reached a source or the local model: inspect exception.FailedChanges
    // and exception.Errors, dispose, and start a new transaction.
}
```

## Transaction Handlers

An `ITransactionHandler` is called for each write a transaction captures and checks the final set at commit. Property validation is one.

**Resolution.**
- A handler takes part when it is registered on the transaction's context, or on the context of a subject written in the transaction, for example a subject's own context that reaches the transaction's context through a fallback.
- Handlers of the transaction's context are resolved when the transaction begins, those of a subject's context when a subject of it is first written. A handler registered later does not take part when registered on the transaction's context, and is not guaranteed to when registered on a subject context that was already included.
- A resolved handler takes part for the rest of the transaction: `OnCapturing` is called for later writes to any subject, and `OnCommitting` receives the whole final set. To handle only some subjects, filter by `property.Subject` (`change.Property.Subject` in `OnCommitting`).
- Handlers are called in the order they were resolved, each context's in its service order. A handler that `Equals` one already taking part is not added again. `ValidationInterceptor` equals any instance of its type, so validation registered on several contexts of a fallback chain runs once.

**Capture.** `OnCapturing` is called before a write is captured or cancelled. Throwing rejects the write: nothing is captured and the exception propagates to the setter.

**Checks.** `OnCommitting` is called in up to two checks, both on the committing thread while the transaction is committing. The first handler that throws stops the rest. Do not assume a single call per commit.

| | Commit check | Pre-apply check |
|-|--------------|-----------------|
| Runs | Before conflict detection, any source write and the local apply | Only with source transactions, after the source writes and right before the local apply |
| Receives | The final set, in commit order | Only the changes that will be applied, with their captured origins |
| Skipped | When nothing is pending | When nothing will be applied, when the transaction was disposed, and in `Rollback` mode after a source write failure, which reverts every source anyway |
| Throwing | Aborts the commit before anything is written. The exception propagates unchanged, and the transaction stays retryable with its pending changes unless it was disposed. | Rejects the whole set in both failure handling modes: every written source is reverted, nothing from the set lands, every change is reported as failed in a `SubjectTransactionException`, and the transaction is terminal. |

The pre-apply check exists because inbound updates can change what a handler reads while the sources are written. It rejects the whole set because the other changes were checked against the rejected values. The exception's `Errors` hold the errors of a thrown `SubjectTransactionException` that has any, otherwise the thrown exception itself, after any source write errors and before any revert errors.

**Reads and writes during a check.** Handlers must not write properties.
- On the committing thread, a read returns the checked value: the pending value of a changed property, and the landed value otherwise. A cancelled change reads the landed value, and in the pre-apply check so does a property whose source write failed.
- On the committing thread, a write throws `InvalidOperationException`, including a write to a settable derived property. A write of a property's landed value is dropped by the equality check, same as outside a transaction.
- On other threads that carry the transaction, reads and writes throw `InvalidOperationException` because a commit is in progress, so handlers must read synchronously on the calling thread. This includes a write back to the landed value of a pending property; a write of the landed value of a property without a pending change is dropped as unchanged instead.
- Such a write from another thread, for example a child task, can still call `OnCapturing` while `OnCommitting` runs, before it is rejected, so handlers must tolerate concurrent calls.
- A write to a subject whose context does not route through the transaction interceptor is not intercepted and lands immediately, even during a check.

**Applies and rollbacks.** A handler that also intercepts writes can call `SubjectTransaction.TryConsumeCommitWrite` to recognize the apply of a value it already checked, or a rollback restoring one, and skip checking it again. Its limits:
- Reference-type values match by identity and value-type values by equality, so a struct with a loose `Equals` can pass an equal, unchecked value as the checked one.
- Only the first handler to call it for a write recognizes it; another handler that intercepts the same write checks it again.
- It does not recognize any apply after the transaction was disposed during the commit.

With handlers, a failed apply invalidates the checked set: in both failure handling modes the commit applies no further changes and reverts the applied values and every written source, see the [Failure Table](#failure-table). Writes outside transactions are not blocked by a commit, so a property a handler reads can change between its check and the local apply; the pre-apply check narrows that window to the synchronous apply itself.

## Derived Properties

Derived properties (marked with `[Derived]`) are handled specially. The pending read view belongs to each execution flow carrying a live ambient transaction, while derived tracking is shared across flows and therefore uses only values that have landed in the model:

- **Direct reads**: Getters called by application code in a flow carrying a live transaction use that transaction's pending values. A flow without a live transaction reads the landed model.
- **Shared tracking**: Cached derived values, dependencies, timestamps, and notifications use values that have landed in the model.
- **Captured writes**: Non-derived writes remain silent until commit replay applies them.
- **Landed writes**: Writes to settable derived properties are not captured. They land, recalculate, and notify immediately while a transaction is open, and survive transaction disposal or rollback.
- **Derived chains**: Derived-of-derived tracking uses the landed model view and retains flattened source dependencies.

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
    // FullName tracking is updated when the captured writes land during commit.
}
```

The same landed-model rule applies when tracking evaluates a getter during initial attachment, automatic recalculation, manual `RecalculateDerivedProperty()`, or a stabilization retry. It does not clear or replace the ambient transaction, so non-derived writes performed as getter side effects are still captured and direct reads resume the pending view as soon as the tracking evaluation finishes.

Notification boundaries follow the evaluation nesting. A top-level derived recalculation publishes after its tracking evaluation has ended, so a synchronous subscriber reads pending values normally. If a getter causes a reentrant derived recalculation or a landed side-effect notification, that nested publication occurs while the outer getter is still being evaluated. Synchronous subscribers in that nested path read landed model values until the outer evaluation finishes.

## Thread Safety

- `BeginTransactionAsync()` uses `AsyncLocal<T>` to store the current transaction
- Exclusive transactions use a per-context semaphore
- Each async execution context has its own transaction scope
- `Dispose()` clears the transaction only for the disposing flow. A flow that captured its execution context earlier can retain the disposed transaction in its slot; reads and writes on that flow treat it as inactive.
- After `Dispose()`, ambient access is ordinary raw model access even while an already-started external-writer commit completes. Such a raw write is not isolated from the commit and can be overwritten when its frozen snapshot replays.
- A successful or terminally failed commit is inactive for property interception even before disposal. Later reads and writes use the landed model normally; retryable failures remain active.
- While a live ambient transaction is committing, property reads and writes on a flow carrying it throw `InvalidOperationException`. This includes custom writer callbacks. Getter reads are also rejected because sibling or landed-model state is outside the frozen snapshot and can make the source payload inconsistent.
- Commit-owned synchronous local replay remains allowed, including property hooks, change handlers, and derived cascades initiated by that replay. This authorization does not extend into custom writer callbacks or child tasks.
- During the handler checks, reads on the committing thread return the checked values and writes throw `InvalidOperationException`, see [Transaction Handlers](#transaction-handlers).
- A transaction must be begun, used, committed, and disposed within the same async flow; committing from another flow throws
- Concurrent `CommitAsync` calls on the same transaction instance are rejected

## Best Practices

1. **Always use `using` blocks** - Ensures proper disposal even on exceptions
2. **Keep transactions short** - Long transactions hold pending changes in memory
3. **Handle exceptions from CommitAsync** - Commits can fail partially
4. **Don't share transactions across threads** - Each async context should have its own transaction

## Implementing a Custom Transaction Writer

Most applications use the built-in `SourceTransactionWriter` registered by `WithSourceTransactions()` and never implement `ITransactionWriter`. A custom writer replaces stage 3 of the [commit flow](#commit-flow) (registered as an `ITransactionWriter` service on the context) and is only needed when source writes require protocol-specific orchestration the built-in writer cannot provide. The writer is also responsible for enforcing the `SingleWrite` requirement.

**No property access.** Both writer callbacks execute while the caller's live ambient transaction is committing. Do not read or write subject properties from either callback, and do not suppress ambient execution-context flow to bypass this boundary. This includes getters: sibling or landed-model state is not represented by the supplied snapshot and can make source operations inconsistent with it. Construct writes from `changes` and `requirement`, and reverts from `written` and `revertState`, together with writer-owned configuration and any required subject state captured before `CommitAsync()`.

**Report, never throw.** Report failures in the returned `SourceWriteResult` and `SourceRevertResult`. The built-in writer never throws: a source that fails or throws is reported as a failed write.
- A throw from `WriteToSourcesAsync` fails the commit terminally with every change reported as failed and nothing applied. Its sources are not reverted, because the writer never returned the written set and revert state that reverting requires, so they may be left in an undefined state. The commit timeout's `OperationCanceledException` is the exception: it propagates and leaves the transaction retryable, so report a write timeout as a failed write, as the built-in writer does.
- A throw from `RevertSourceWritesAsync` counts as every requested revert failing, and the commit fails terminally.
- `SourceRevertResult.Failed` must contain only changes from `written`.

**In-place marking.** A custom writer must follow the in-place marking contract documented in the XML documentation of `ITransactionWriter.WriteToSourcesAsync`. Moving or replacing a snapshot slot silently corrupts the local apply. Not marking an accepted slot is allowed, but its apply notification publishes without a `Confirmed` origin and the outbound queue pushes the committed value to the source again. While developing a writer, enable the runtime contract validation:

```csharp
SubjectTransaction.ValidateWriterContract = true;
```

The commit then fails terminally with a `SubjectTransactionException` if a slot was moved or replaced (sources may already have been written, so no retry). The check covers slot identity only: missing or wrong-source marks are not detected. Leave it disabled in production (one array allocation and sweep per commit); the built-in `SourceTransactionWriter` does not need it.

## API Reference

### BeginTransactionAsync

```csharp
TransactionAwaitable BeginTransactionAsync(
    TransactionFailureHandling failureHandling,
    TransactionLocking locking = TransactionLocking.Exclusive,
    TransactionRequirement requirement = TransactionRequirement.None,
    TransactionConflictBehavior conflictBehavior = TransactionConflictBehavior.FailOnConflict,
    TransactionValidationMode validationMode = TransactionValidationMode.Immediate,
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
| `ValidationMode` | When transaction handlers validate the written values |
| `Current` (static) | The transaction of the current async flow, or null |
| `TryConsumeCommitWrite(...)` (static) | Lets a handler recognize a value it already checked, see [Transaction Handlers](#transaction-handlers) |
| `ValidateWriterContract` (static) | Enables the writer contract check, see [Implementing a Custom Transaction Writer](#implementing-a-custom-transaction-writer) |

### Enums

**TransactionFailureHandling:**
- `BestEffort` - Keep successful changes, revert the source write of a failed apply (per-property consistency); with transaction handlers, a failed apply reverts everything
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

**TransactionValidationMode:**
- `Immediate` - Validate each write and the final set at commit
- `Deferred` - Validate only the final set at commit

## Edge Cases and Limitations

- **Single context binding**: a transaction is bound to the context it begins on. Writing a subject throws `InvalidOperationException` (`Cannot modify property 'FirstName': Transaction is bound to a different context.`) unless the subject's context shares the transaction's interceptor, for example through a fallback to the transaction's context.
- **No nesting**: beginning a transaction while one is active in the same execution context throws `InvalidOperationException`: `Nested transactions are not supported.`
- **ABA**: `FailOnConflict` compares values. A property that changes A to B and back to A between the first write and the commit is not a conflict. Use external versioning for strict version-based detection.
- **Writes outside transactions**: conflict detection is not atomic with respect to raw property writes and external source callbacks. Transactions are serialized against each other, but such a write can interleave between conflict detection, the handler checks and the apply.
- **Multiple sources**: source writes run in parallel and reverts are best-effort, so sources can briefly disagree and atomicity across sources is not guaranteed. For strict atomicity, use a single source per transaction or application-level compensation.
- **Changing a source binding during a commit**: the source a property writes to is decided when the transaction commits, not when the value is set. Calling `SetSource` or `RemoveSource` on a property while a commit runs leaves it undefined whether that commit uses the old or the new binding. Reverts always target the sources that were actually written, even if the binding changed in the meantime.
- **Value-transforming write interceptors** are not supported with source transactions: the source would receive the stage 3 value while the local model applies the transformed one.
- **Validation caveats**, such as derived properties and subjects created without a context, are listed in [Writing Validators for Transactions](validation.md#writing-validators-for-transactions).
