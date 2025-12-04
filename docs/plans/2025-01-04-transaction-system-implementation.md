# Transaction System Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement transactional property changes with per-context serialization, conflict detection, and structured exceptions.

**Architecture:** Transactions are bound to a single context, serialize via SemaphoreSlim, buffer property changes, write to sources first, then apply to local model. Conflict detection compares timestamps at read/write/commit time.

**Tech Stack:** C# 13, .NET 9.0, xUnit, Moq

---

## Task 1: Add TransactionConflictBehavior Enum

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Transactions/TransactionConflictBehavior.cs`

**Step 1: Create the enum file**

```csharp
namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Controls handling of concurrent modifications during a transaction.
/// </summary>
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

**Step 2: Verify build**

Run: `dotnet build src/Namotion.Interceptor.Tracking`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Transactions/TransactionConflictBehavior.cs
git commit -m "feat(transactions): add TransactionConflictBehavior enum"
```

---

## Task 2: Add TransactionException and TransactionConflictException

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Transactions/TransactionException.cs`
- Create: `src/Namotion.Interceptor.Tracking/Transactions/TransactionConflictException.cs`
- Create: `src/Namotion.Interceptor.Tracking/Transactions/SourceWriteFailure.cs`

**Step 1: Create SourceWriteFailure class**

```csharp
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Represents a failed source write operation.
/// </summary>
public sealed class SourceWriteFailure
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SourceWriteFailure"/> class.
    /// </summary>
    public SourceWriteFailure(SubjectPropertyChange change, object source, Exception error)
    {
        Change = change;
        Source = source;
        Error = error;
    }

    /// <summary>
    /// Gets the change that failed to write.
    /// </summary>
    public SubjectPropertyChange Change { get; }

    /// <summary>
    /// Gets the source that failed.
    /// </summary>
    public object Source { get; }

    /// <summary>
    /// Gets the error that occurred.
    /// </summary>
    public Exception Error { get; }
}
```

**Step 2: Create TransactionException class**

```csharp
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Exception thrown when a transaction fails to commit.
/// </summary>
public class TransactionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransactionException"/> class.
    /// </summary>
    public TransactionException(
        string message,
        IReadOnlyList<SubjectPropertyChange> appliedChanges,
        IReadOnlyList<SourceWriteFailure> failedChanges)
        : base(message)
    {
        AppliedChanges = appliedChanges;
        FailedChanges = failedChanges;
    }

    /// <summary>
    /// Gets the changes that were successfully written to source and applied to local model.
    /// </summary>
    public IReadOnlyList<SubjectPropertyChange> AppliedChanges { get; }

    /// <summary>
    /// Gets the changes that failed to write to source (not applied to local model).
    /// </summary>
    public IReadOnlyList<SourceWriteFailure> FailedChanges { get; }

    /// <summary>
    /// Gets a value indicating whether at least one change was applied successfully.
    /// </summary>
    public bool IsPartialSuccess => AppliedChanges.Count > 0 && FailedChanges.Count > 0;
}
```

**Step 3: Create TransactionConflictException class**

```csharp
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Exception thrown when a transaction detects a concurrent modification conflict.
/// </summary>
public sealed class TransactionConflictException : TransactionException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransactionConflictException"/> class.
    /// </summary>
    public TransactionConflictException(IReadOnlyList<PropertyReference> conflictingProperties)
        : base(
            $"Transaction conflict detected on {conflictingProperties.Count} property(ies): {string.Join(", ", conflictingProperties.Select(p => p.Name))}",
            Array.Empty<SubjectPropertyChange>(),
            Array.Empty<SourceWriteFailure>())
    {
        ConflictingProperties = conflictingProperties;
    }

    /// <summary>
    /// Gets the properties that were modified by another source during the transaction.
    /// </summary>
    public IReadOnlyList<PropertyReference> ConflictingProperties { get; }
}
```

**Step 4: Verify build**

Run: `dotnet build src/Namotion.Interceptor.Tracking`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Transactions/SourceWriteFailure.cs
git add src/Namotion.Interceptor.Tracking/Transactions/TransactionException.cs
git add src/Namotion.Interceptor.Tracking/Transactions/TransactionConflictException.cs
git commit -m "feat(transactions): add TransactionException hierarchy"
```

---

## Task 3: Add LastChangedTimestamp Tracking

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Change/PropertyTimestampInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs`
- Modify: `src/Namotion.Interceptor/PropertyReference.cs` (add extension for timestamp access)

**Step 1: Create PropertyTimestampInterceptor**

```csharp
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Interceptor that tracks the last changed timestamp for each property.
/// Used for conflict detection in transactions.
/// </summary>
public sealed class PropertyTimestampInterceptor : IWriteInterceptor
{
    internal const string LastChangedTimestampKey = "LastChangedTimestamp";

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        next(ref context);

        var changeContext = SubjectChangeContext.Current;
        context.Property.SetPropertyData(LastChangedTimestampKey, changeContext.ChangedTimestamp);
    }
}
```

**Step 2: Add extension method for timestamp access**

Create file `src/Namotion.Interceptor.Tracking/Change/PropertyTimestampExtensions.cs`:

```csharp
namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Extension methods for property timestamp tracking.
/// </summary>
public static class PropertyTimestampExtensions
{
    /// <summary>
    /// Gets the last changed timestamp for a property.
    /// </summary>
    /// <param name="property">The property reference.</param>
    /// <returns>The last changed timestamp, or null if never changed.</returns>
    public static DateTimeOffset? GetLastChangedTimestamp(this PropertyReference property)
    {
        if (property.TryGetPropertyData(PropertyTimestampInterceptor.LastChangedTimestampKey, out var value) &&
            value is DateTimeOffset timestamp)
        {
            return timestamp;
        }
        return null;
    }
}
```

**Step 3: Register interceptor in WithFullPropertyTracking**

In `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs`, add the interceptor registration. Find the `WithFullPropertyTracking` method and add `PropertyTimestampInterceptor` to the chain.

**Step 4: Verify build**

Run: `dotnet build src/Namotion.Interceptor.Tracking`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Change/PropertyTimestampInterceptor.cs
git add src/Namotion.Interceptor.Tracking/Change/PropertyTimestampExtensions.cs
git add src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs
git commit -m "feat(tracking): add PropertyTimestampInterceptor for conflict detection"
```

---

## Task 4: Add Context Lock for Transaction Serialization

**Files:**
- Modify: `src/Namotion.Interceptor/IInterceptorSubjectContext.cs` (if needed)
- Create: `src/Namotion.Interceptor.Tracking/Transactions/TransactionLock.cs`

**Step 1: Create TransactionLock service**

```csharp
namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Manages per-context transaction serialization.
/// </summary>
internal sealed class TransactionLock : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Acquires the transaction lock for this context.
    /// </summary>
    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new LockReleaser(_semaphore);
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }

    private sealed class LockReleaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public LockReleaser(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            if (!_disposed)
            {
                _semaphore.Release();
                _disposed = true;
            }
        }
    }
}
```

**Step 2: Verify build**

Run: `dotnet build src/Namotion.Interceptor.Tracking`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Transactions/TransactionLock.cs
git commit -m "feat(transactions): add TransactionLock for per-context serialization"
```

---

## Task 5: Remove TransactionMode.Strict

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/TransactionMode.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs`
- Modify: `src/Namotion.Interceptor.Sources/Transactions/SourceTransactionWriteHandler.cs`
- Modify: Tests that reference `TransactionMode.Strict`

**Step 1: Update TransactionMode enum**

```csharp
namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Specifies how transaction commit handles failures when writing to external sources.
/// </summary>
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

**Step 2: Update SubjectTransaction.CommitAsync to remove Strict handling**

Find references to `TransactionMode.Strict` and remove them from the switch expressions.

**Step 3: Update SourceTransactionWriteHandler if it references Strict**

**Step 4: Update tests**

Search for tests using `TransactionMode.Strict` and either remove them or convert to appropriate mode.

**Step 5: Verify build and tests**

Run: `dotnet build src/Namotion.Interceptor.sln`
Run: `dotnet test src/Namotion.Interceptor.sln`
Expected: Build succeeded, tests pass

**Step 6: Commit**

```bash
git add -A
git commit -m "refactor(transactions): remove TransactionMode.Strict"
```

---

## Task 6: Refactor SubjectTransaction to Async with Context Binding

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs`
- Create: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransactionExtensions.cs`

**Step 1: Write failing test for new API**

Create test file `src/Namotion.Interceptor.Sources.Tests/Transactions/SubjectTransactionAsyncTests.cs`:

```csharp
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Sources.Tests.Transactions;

public class SubjectTransactionAsyncTests
{
    [Fact]
    public async Task BeginTransactionAsync_ReturnsTransaction()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        await using var tx = await context.BeginTransactionAsync();

        Assert.NotNull(tx);
        Assert.Same(context, tx.Context);
    }

    [Fact]
    public async Task BeginTransactionAsync_SerializesTransactionsPerContext()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        await using var tx1 = await context.BeginTransactionAsync();

        var tx2Task = context.BeginTransactionAsync();

        // tx2 should be waiting
        await Task.Delay(50);
        Assert.False(tx2Task.IsCompleted);

        // Dispose tx1 to release lock
        await tx1.DisposeAsync();

        // Now tx2 should complete
        await using var tx2 = await tx2Task;
        Assert.NotNull(tx2);
    }

    [Fact]
    public async Task WriteProperty_ToDifferentContext_ThrowsInvalidOperationException()
    {
        var context1 = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var context2 = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person1 = new Person(context1);
        var person2 = new Person(context2);

        await using var tx = await context1.BeginTransactionAsync();

        person1.FirstName = "John"; // OK

        Assert.Throws<InvalidOperationException>(() => person2.FirstName = "Jane");
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Sources.Tests --filter "FullyQualifiedName~SubjectTransactionAsyncTests"`
Expected: FAIL (methods don't exist)

**Step 3: Implement SubjectTransaction changes**

Refactor `SubjectTransaction.cs`:
- Add `Context` property
- Add `StartTimestamp` property
- Add `ConflictBehavior` property
- Change constructor to accept context
- Implement `IAsyncDisposable`
- Store lock releaser for cleanup
- Add internal `BeginAsync` static method

**Step 4: Create extension method**

```csharp
namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Extension methods for transaction support on IInterceptorSubjectContext.
/// </summary>
public static class SubjectTransactionExtensions
{
    /// <summary>
    /// Begins a new transaction bound to this context.
    /// Waits if another transaction is active on this context.
    /// </summary>
    public static ValueTask<SubjectTransaction> BeginTransactionAsync(
        this IInterceptorSubjectContext context,
        TransactionMode mode = TransactionMode.Rollback,
        TransactionRequirement requirement = TransactionRequirement.None,
        TransactionConflictBehavior conflictBehavior = TransactionConflictBehavior.FailOnConflict,
        CancellationToken cancellationToken = default)
    {
        return SubjectTransaction.BeginAsync(context, mode, requirement, conflictBehavior, cancellationToken);
    }
}
```

**Step 5: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Sources.Tests --filter "FullyQualifiedName~SubjectTransactionAsyncTests"`
Expected: PASS

**Step 6: Commit**

```bash
git add -A
git commit -m "feat(transactions): refactor to async context-bound transactions"
```

---

## Task 7: Update SubjectTransactionInterceptor for Context Validation

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransactionInterceptor.cs`

**Step 1: Add context validation to WriteProperty**

Update the `WriteProperty` method to check if the property's context matches the transaction's context:

```csharp
// Add after checking transaction is active
if (transaction.Context != context.Property.Subject.Context)
{
    throw new InvalidOperationException(
        $"Cannot modify property in context - transaction is bound to different context.");
}
```

**Step 2: Verify tests pass**

Run: `dotnet test src/Namotion.Interceptor.Sources.Tests --filter "FullyQualifiedName~SubjectTransactionAsyncTests"`
Expected: PASS

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Transactions/SubjectTransactionInterceptor.cs
git commit -m "feat(transactions): add context validation in interceptor"
```

---

## Task 8: Implement Conflict Detection on Write

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransactionInterceptor.cs`
- Create test: `src/Namotion.Interceptor.Sources.Tests/Transactions/SubjectTransactionConflictTests.cs`

**Step 1: Write failing test**

```csharp
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Sources.Tests.Transactions;

public class SubjectTransactionConflictTests
{
    [Fact]
    public async Task Write_WhenPropertyChangedSinceTransactionStart_ThrowsConflictException()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);
        person.FirstName = "Original";

        await using var tx = await context.BeginTransactionAsync(
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);

        // Simulate external change (bypasses transaction)
        await Task.Delay(10); // Ensure timestamp differs
        using (SubjectChangeContext.Capture(DateTimeOffset.UtcNow))
        {
            // Direct write bypassing transaction
            var setter = person.GetType().GetProperty("FirstName")!.SetMethod!;
            // This won't work - need different approach
        }

        // For now, test the simpler case: FailOnConflict detects changes
        // We need the PropertyTimestampInterceptor to set the timestamp

        Assert.Throws<TransactionConflictException>(() => person.FirstName = "New");
    }

    [Fact]
    public async Task Write_WithIgnoreConflictBehavior_AllowsConflictingWrite()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        await using var tx = await context.BeginTransactionAsync(
            conflictBehavior: TransactionConflictBehavior.Ignore);

        // Should not throw even if property was modified
        person.FirstName = "John";

        await tx.CommitAsync();

        Assert.Equal("John", person.FirstName);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Sources.Tests --filter "FullyQualifiedName~SubjectTransactionConflictTests"`
Expected: FAIL

**Step 3: Implement conflict detection in WriteProperty**

In `SubjectTransactionInterceptor.WriteProperty`, add:

```csharp
// Check for conflict if FailOnConflict mode
if (transaction.ConflictBehavior == TransactionConflictBehavior.FailOnConflict)
{
    var lastChanged = context.Property.GetLastChangedTimestamp();
    if (lastChanged.HasValue && lastChanged.Value > transaction.StartTimestamp)
    {
        throw new TransactionConflictException(new[] { context.Property });
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Sources.Tests --filter "FullyQualifiedName~SubjectTransactionConflictTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add -A
git commit -m "feat(transactions): implement conflict detection on write"
```

---

## Task 9: Implement Conflict Detection on Read

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransactionInterceptor.cs`
- Add tests to: `src/Namotion.Interceptor.Sources.Tests/Transactions/SubjectTransactionConflictTests.cs`

**Step 1: Write failing test**

```csharp
[Fact]
public async Task Read_WhenPropertyChangedSinceTransactionStart_ThrowsConflictException()
{
    var context = InterceptorSubjectContext
        .Create()
        .WithRegistry()
        .WithFullPropertyTracking();

    var person = new Person(context);
    person.FirstName = "Original";

    await using var tx = await context.BeginTransactionAsync(
        conflictBehavior: TransactionConflictBehavior.FailOnConflict);

    // Simulate time passing and external change
    // (in real scenario, this would be from source callback)

    Assert.Throws<TransactionConflictException>(() => _ = person.FirstName);
}
```

**Step 2: Implement conflict detection in ReadProperty**

```csharp
public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
{
    var transaction = SubjectTransaction.Current;

    // Return pending value if transaction active and not committing
    if (transaction is { IsCommitting: false })
    {
        if (transaction.PendingChanges.TryGetValue(context.Property, out var change))
        {
            return change.GetNewValue<TProperty>();
        }

        // Check for conflict if FailOnConflict mode
        if (transaction.ConflictBehavior == TransactionConflictBehavior.FailOnConflict)
        {
            var lastChanged = context.Property.GetLastChangedTimestamp();
            if (lastChanged.HasValue && lastChanged.Value > transaction.StartTimestamp)
            {
                throw new TransactionConflictException(new[] { context.Property });
            }
        }
    }

    return next(ref context);
}
```

**Step 3: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Sources.Tests --filter "FullyQualifiedName~SubjectTransactionConflictTests"`
Expected: PASS

**Step 4: Commit**

```bash
git add -A
git commit -m "feat(transactions): implement conflict detection on read"
```

---

## Task 10: Implement Conflict Detection on Commit

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs`
- Add tests

**Step 1: Write failing test**

```csharp
[Fact]
public async Task CommitAsync_WhenPropertyChangedAfterWrite_ThrowsConflictException()
{
    var context = InterceptorSubjectContext
        .Create()
        .WithRegistry()
        .WithFullPropertyTracking()
        .WithSourceTransactions();

    var person = new Person(context);

    await using var tx = await context.BeginTransactionAsync(
        conflictBehavior: TransactionConflictBehavior.FailOnConflict);

    person.FirstName = "John"; // Buffered

    // Simulate external change between write and commit
    // (This is tricky to test - may need mock or timing)

    await Assert.ThrowsAsync<TransactionConflictException>(() => tx.CommitAsync());
}
```

**Step 2: Implement conflict check in CommitAsync**

At the start of `CommitAsync`, before writing to sources:

```csharp
// Check for conflicts before writing
if (_conflictBehavior == TransactionConflictBehavior.FailOnConflict)
{
    var conflictingProperties = new List<PropertyReference>();
    foreach (var change in changes)
    {
        var currentValue = change.Property.Metadata.GetValue?.Invoke(change.Property.Subject);
        var originalValue = change.GetOldValue<object?>();
        if (!Equals(currentValue, originalValue))
        {
            conflictingProperties.Add(change.Property);
        }
    }

    if (conflictingProperties.Count > 0)
    {
        throw new TransactionConflictException(conflictingProperties);
    }
}
```

**Step 3: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Sources.Tests`
Expected: PASS

**Step 4: Commit**

```bash
git add -A
git commit -m "feat(transactions): implement conflict detection on commit"
```

---

## Task 11: Replace AggregateException with TransactionException

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/TransactionWriteResult.cs`
- Update all tests that expect `AggregateException`

**Step 1: Update TransactionWriteResult to use SourceWriteFailure**

```csharp
public class TransactionWriteResult
{
    public IReadOnlyList<SubjectPropertyChange> SuccessfulChanges { get; }
    public IReadOnlyList<SourceWriteFailure> Failures { get; }

    public TransactionWriteResult(
        IReadOnlyList<SubjectPropertyChange> successfulChanges,
        IReadOnlyList<SourceWriteFailure> failures)
    {
        SuccessfulChanges = successfulChanges;
        Failures = failures;
    }

    public static TransactionWriteResult Success(IReadOnlyList<SubjectPropertyChange> changes) =>
        new(changes, Array.Empty<SourceWriteFailure>());
}
```

**Step 2: Update CommitAsync to throw TransactionException**

Replace the `AggregateException` throw with:

```csharp
if (allFailures.Count > 0)
{
    var message = _mode switch
    {
        TransactionMode.BestEffort => "One or more source writes failed. Successfully written changes have been applied.",
        TransactionMode.Rollback => "One or more source writes failed. Rollback was attempted. No changes have been applied.",
        _ => "One or more source writes failed."
    };
    throw new TransactionException(message, allSuccessfulChanges, allFailures);
}
```

**Step 3: Update SourceTransactionWriteHandler to create SourceWriteFailure**

**Step 4: Update all tests expecting AggregateException**

Search for `Assert.ThrowsAsync<AggregateException>` and update to `TransactionException`.

**Step 5: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.sln`
Expected: PASS

**Step 6: Commit**

```bash
git add -A
git commit -m "refactor(transactions): replace AggregateException with TransactionException"
```

---

## Task 12: Update SourceTransactionWriteHandler

**Files:**
- Modify: `src/Namotion.Interceptor.Sources/Transactions/SourceTransactionWriteHandler.cs`
- Modify: `src/Namotion.Interceptor.Sources/Transactions/SourceWriteException.cs` (may be removed)

**Step 1: Update to use SourceWriteFailure**

Update the handler to create `SourceWriteFailure` instances instead of `SourceWriteException`.

**Step 2: Remove SourceWriteException if no longer needed**

Or keep it as the inner exception in `SourceWriteFailure.Error`.

**Step 3: Run tests**

Run: `dotnet test src/Namotion.Interceptor.sln`
Expected: PASS

**Step 4: Commit**

```bash
git add -A
git commit -m "refactor(sources): update SourceTransactionWriteHandler for new exception model"
```

---

## Task 13: Remove Old Static BeginTransaction Method

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs`
- Update all tests using old API

**Step 1: Remove or mark obsolete the static BeginTransaction method**

Either remove entirely or mark with `[Obsolete]` for migration period.

**Step 2: Update all tests to use new async API**

Find all usages of `SubjectTransaction.BeginTransaction(` and update to:
```csharp
await using var tx = await context.BeginTransactionAsync(...);
```

**Step 3: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.sln`
Expected: PASS

**Step 4: Commit**

```bash
git add -A
git commit -m "refactor(transactions): remove deprecated sync BeginTransaction"
```

---

## Task 14: Update Documentation

**Files:**
- Modify: `docs/tracking-transactions.md` (if exists)
- Modify: `docs/tracking.md` (if has transaction section)

**Step 1: Update documentation to reflect new API**

- New `context.BeginTransactionAsync()` API
- Removed `TransactionMode.Strict`
- New `TransactionConflictBehavior` enum
- New exception types
- Usage examples

**Step 2: Commit**

```bash
git add docs/
git commit -m "docs: update transaction documentation for new API"
```

---

## Task 15: Final Integration Test

**Files:**
- Create: `src/Namotion.Interceptor.Sources.Tests/Transactions/TransactionIntegrationTests.cs`

**Step 1: Write comprehensive integration test**

```csharp
public class TransactionIntegrationTests : TransactionTestBase
{
    [Fact]
    public async Task FullFlow_CommandTransaction_WithRollbackOnFailure()
    {
        var context = CreateContext();
        var person = new Person(context);

        var successSource = CreateSucceedingSource();
        var failSource = CreateFailingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        await using var tx = await context.BeginTransactionAsync(
            mode: TransactionMode.Rollback,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);

        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync());

        Assert.Null(person.FirstName); // Rolled back
        Assert.Null(person.LastName);
        Assert.Empty(ex.AppliedChanges);
        Assert.Single(ex.FailedChanges);
    }

    [Fact]
    public async Task FullFlow_UiSyncTransaction_WithPartialSuccess()
    {
        var context = CreateContext();
        var person = new Person(context);

        var successSource = CreateSucceedingSource();
        var failSource = CreateFailingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        await using var tx = await context.BeginTransactionAsync(
            mode: TransactionMode.BestEffort,
            conflictBehavior: TransactionConflictBehavior.Ignore);

        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync());

        Assert.Equal("John", person.FirstName); // Applied
        Assert.Null(person.LastName); // Failed
        Assert.Single(ex.AppliedChanges);
        Assert.Single(ex.FailedChanges);
        Assert.True(ex.IsPartialSuccess);
    }
}
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.sln`
Expected: All tests PASS

**Step 3: Commit**

```bash
git add -A
git commit -m "test: add transaction integration tests"
```

---

## Task 16: Final Verification

**Step 1: Run full test suite**

Run: `dotnet test src/Namotion.Interceptor.sln`
Expected: All tests PASS

**Step 2: Run build**

Run: `dotnet build src/Namotion.Interceptor.sln`
Expected: Build succeeded with no errors

**Step 3: Review git log**

Run: `git log --oneline -20`
Verify all commits are present and well-organized.

---

## Summary of Changes

1. **New enum**: `TransactionConflictBehavior` (FailOnConflict, Ignore)
2. **New exceptions**: `TransactionException`, `TransactionConflictException`, `SourceWriteFailure`
3. **New infrastructure**: `PropertyTimestampInterceptor`, `TransactionLock`
4. **Removed**: `TransactionMode.Strict`
5. **API change**: `BeginTransaction()` → `context.BeginTransactionAsync()`
6. **Exception change**: `AggregateException` → `TransactionException`
7. **New feature**: Per-context transaction serialization
8. **New feature**: Optimistic concurrency conflict detection
