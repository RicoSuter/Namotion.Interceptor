# Transaction follow-ups 1 and 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a throwing `ITransactionWriter` fail the commit terminally (as a full failure) instead of leaving it retryable, and surface cross-async-flow transaction misuse by failing fast at commit and guarding `Dispose`.

**Architecture:** Three small, localized changes in `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs`, each reusing existing machinery (the `FinishCommit`/`EndCommit` terminal path, `ValidateCanCommit`, the `AsyncLocal` slot). No new types, no new public API members, no new fields. Plus XML-doc and markdown documentation. Driven test-first.

**Tech Stack:** C# 13, .NET (Tracking on .NET Standard 2.0; tests on .NET 9.0), xUnit, Moq.

**Spec:** `docs/superpowers/specs/2026-06-09-transaction-followups-1-2-design.md`

**Branch:** `transaction-followups-1-2` (already checked out, spec committed).

---

## Background the implementer needs

- `SubjectTransaction.CommitAsync` validates via `ValidateCanCommit`, then for a source-bound commit calls `CommitWithWriterAsync` → `ReconcileWithWriterAsync`, which awaits `writer.WriteToSourcesAsync(...)` **before** applying anything to the local model. So if the writer throws there, the local model is untouched.
- A *reported* failure returns a `SubjectTransactionException` from `ReconcileWithWriterAsync`; the caller then runs `FinishCommit()` (sets `_isCommitted = true`) and throws it. Because `FinishCommit` ran, `EndCommit` does **not** reset `_commitStarted`, so a reported failure is already terminal/non-retryable.
- A *thrown* writer exception currently propagates raw, skips `FinishCommit`, and `EndCommit` resets `_commitStarted`, making the transaction retryable (the bug for item 1).
- The current transaction is stored in `static readonly AsyncLocal<SubjectTransaction?> CurrentTransaction`. `AsyncLocal` values flow into awaited children but not back out. `TransactionAwaiter.GetResult()` calls `SetCurrent(transaction)` in the awaiting flow. The write path (`SubjectTransactionInterceptor.WriteProperty`) routes on `SubjectTransaction.Current`; when it is `null` it writes the model directly (this is also the legitimate non-transactional write path, which is why a write-time check is impossible).
- `ITransactionWriter` is registered through `WithSourceTransactions()` (the built-in `SourceTransactionWriter`) or directly via `context.AddService<ITransactionWriter>(...)`.

## Files touched

- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs` (items 1, 2a, 2b)
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransactionExtensions.cs` (XML doc)
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/ITransactionWriter.cs` (XML doc)
- Modify: `docs/tracking-transactions.md` (docs)
- Test: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionFailureHandlingTests.cs` (item 1)
- Test: `src/Namotion.Interceptor.Tracking.Tests/Transactions/SubjectTransactionTests.cs` (items 2a, 2b)

No new public API members are added, so no `*.PublicApi.verified.txt` snapshots should change.

---

## Task 1: A throwing `ITransactionWriter` becomes a terminal full failure

**Files:**
- Test: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionFailureHandlingTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs` (`ReconcileWithWriterAsync` ~lines 384-387; `EndCommit` comment ~lines 497-505)

- [ ] **Step 1: Add usings and the throwing fake writer + failing test**

Add these usings to the top of `SubjectTransactionFailureHandlingTests.cs` (after the existing `using` lines):

```csharp
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
```

Inside the `SubjectTransactionFailureHandlingTests` class, add the nested fake writer and the test:

```csharp
    private sealed class ThrowingTransactionWriter : ITransactionWriter
    {
        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            ReadOnlyMemory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Writer boom");

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));
    }

    [Fact]
    public async Task WhenWriterThrows_ThenAllChangesFailAndTransactionIsTerminal()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();
        context.AddService<ITransactionWriter>(new ThrowingTransactionWriter());

        var person = new Person(context);

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(2, exception.FailedChanges.Count);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal("Writer boom", exception.InnerException!.Message);
        Assert.Null(person.FirstName); // local model untouched (writer threw before apply)
        Assert.Null(person.LastName);

        // A second commit is rejected: the transaction is terminal, not retryable.
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.Contains("already been committed", second.Message);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~WhenWriterThrows_ThenAllChangesFailAndTransactionIsTerminal"`

Expected: FAIL. Currently the writer's exception propagates raw, so `Assert.ThrowsAsync<SubjectTransactionException>` fails (it sees an `InvalidOperationException` instead).

- [ ] **Step 3: Convert a writer throw into a full-failure return in `ReconcileWithWriterAsync`**

In `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs`, in `ReconcileWithWriterAsync`, replace this block:

```csharp
        // The writer is contractually expected to report failures via SourceWriteResult, not throw.
        // A throwing writer propagates here and bypasses source-revert (matches prior behavior).
        var (written, failedSource, sourceErrors, revertState) = await writer
            .WriteToSourcesAsync(changes, _requirement, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
        // The writer is contractually expected to report failures via SourceWriteResult, not throw.
        // If it throws anyway (and this is not our commit timeout), there is no SourceWriteResult, so we
        // have neither the written set nor the revertState needed to revert; sources cannot be reverted.
        // The local model is untouched (apply runs only after this returns), so report every change as
        // failed. Returning a failure routes through FinishCommit() in the caller, making the transaction
        // terminal (non-retryable) and consistent with a reported full source failure.
        SourceWriteResult writeResult;
        try
        {
            writeResult = await writer
                .WriteToSourcesAsync(changes, _requirement, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new SubjectTransactionException(
                "The transaction writer threw an exception during commit. Sources may be in an undefined, " +
                "un-reverted state; the transaction is terminal and must be disposed, not retried.",
                appliedChanges: [],
                failedChanges: changes.ToArray(),
                errors: [exception]);
        }

        var (written, failedSource, sourceErrors, revertState) = writeResult;
```

- [ ] **Step 4: Fix the now-stale `EndCommit` comment**

In the same file, in `EndCommit`, replace this comment:

```csharp
            // Reset so CommitAsync can be called again, but only for failures that occur BEFORE any
            // change is applied to the local model (conflict detected, optimistic lock acquisition failed,
            // commit timeout, or the writer threw). Once the apply pass runs, FinishCommit has marked
            // the transaction committed and this branch is skipped, so an apply/validation failure is
            // terminal and cannot be retried.
```

with:

```csharp
            // Reset so CommitAsync can be called again, but only for failures that occur BEFORE any
            // change is applied to the local model (conflict detected, optimistic lock acquisition failed,
            // or the commit timed out). A writer that throws is converted to a full failure that runs
            // through FinishCommit, so it is terminal and not retried. Once the apply pass runs,
            // FinishCommit has marked the transaction committed and this branch is skipped, so an
            // apply/validation failure is terminal and cannot be retried.
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~WhenWriterThrows_ThenAllChangesFailAndTransactionIsTerminal"`

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs \
        src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionFailureHandlingTests.cs
git commit -m "Treat a throwing transaction writer as a terminal full failure (#338)"
```

---

## Task 2: Fail fast when committing from a different async flow

**Files:**
- Test: `src/Namotion.Interceptor.Tracking.Tests/Transactions/SubjectTransactionTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs` (`ValidateCanCommit` ~lines 508-518)

- [ ] **Step 1: Write the failing test**

Add to the `SubjectTransactionTests` class in `src/Namotion.Interceptor.Tracking.Tests/Transactions/SubjectTransactionTests.cs`:

```csharp
    [Fact]
    public async Task WhenCommittingFromDifferentAsyncFlow_ThenThrows()
    {
        // Arrange: begin in a separate flow so its AsyncLocal current-transaction does not reach here.
        var context = CreateTransactionContext();
        var transaction = await Task.Run(async () =>
            await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort));

        // Act & Assert
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
            Assert.Contains("async flow", exception.Message);
        }
        finally
        {
            transaction.Dispose();
        }
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~WhenCommittingFromDifferentAsyncFlow_ThenThrows"`

Expected: FAIL. Without the check, the commit sees no pending changes in this flow and completes silently, so `Assert.ThrowsAsync<InvalidOperationException>` fails (nothing is thrown).

- [ ] **Step 3: Add the commit-time flow check**

In `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs`, in `ValidateCanCommit`, insert the flow check immediately after the disposed check:

```csharp
    private void ValidateCanCommit()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
            throw new ObjectDisposedException(nameof(SubjectTransaction));

        if (!ReferenceEquals(CurrentTransaction.Value, this))
            throw new InvalidOperationException(
                "Transaction is being committed from a different async flow than the one it is active in. " +
                "Begin, use, commit, and dispose a transaction within the same async flow.");

        if (_isCommitted)
            throw new InvalidOperationException("Transaction has already been committed.");

        if (Interlocked.CompareExchange(ref _commitStarted, 1, 0) != 0)
            throw new InvalidOperationException("CommitAsync is already in progress.");
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~WhenCommittingFromDifferentAsyncFlow_ThenThrows"`

Expected: PASS.

- [ ] **Step 5: Run the full Tracking transaction tests to confirm no regression**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~Transactions"`

Expected: PASS (all existing transaction tests begin and commit within the same flow, so `Current == this` holds).

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs \
        src/Namotion.Interceptor.Tracking.Tests/Transactions/SubjectTransactionTests.cs
git commit -m "Fail fast when committing a transaction from a different async flow (#338)"
```

---

## Task 3: Guard `Dispose` against clobbering another flow's current transaction

**Files:**
- Test: `src/Namotion.Interceptor.Tracking.Tests/Transactions/SubjectTransactionTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs` (`Dispose` ~line 615)

- [ ] **Step 1: Write the failing test**

Add to the `SubjectTransactionTests` class:

```csharp
    [Fact]
    public async Task WhenDisposingFromDifferentAsyncFlow_ThenOtherTransactionSlotIsNotCleared()
    {
        // Arrange
        var context = CreateTransactionContext();

        // transaction1 is begun in a separate flow, so its SetCurrent does not reach this flow.
        // Optimistic locking means neither transaction takes the lock at begin, so they can coexist.
        var transaction1 = await Task.Run(async () =>
            await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort, TransactionLocking.Optimistic));

        using var transaction2 = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, TransactionLocking.Optimistic);
        Assert.Same(transaction2, SubjectTransaction.Current);

        // Act: disposing transaction1 from transaction2's flow must not clear transaction2's slot.
        transaction1.Dispose();

        // Assert
        Assert.Same(transaction2, SubjectTransaction.Current);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~WhenDisposingFromDifferentAsyncFlow_ThenOtherTransactionSlotIsNotCleared"`

Expected: FAIL. Without the guard, `transaction1.Dispose()` nulls the current flow's slot, so `SubjectTransaction.Current` becomes `null` and `Assert.Same(transaction2, ...)` fails.

- [ ] **Step 3: Guard the slot clear in `Dispose`**

In `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs`, in `Dispose`, replace:

```csharp
            CurrentTransaction.Value = null;
```

with:

```csharp
            // Only clear the slot if this transaction is the one active in the disposing flow, so disposing
            // in a different flow cannot clobber an unrelated transaction's current-transaction slot.
            if (CurrentTransaction.Value == this)
            {
                CurrentTransaction.Value = null;
            }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~WhenDisposingFromDifferentAsyncFlow_ThenOtherTransactionSlotIsNotCleared"`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs \
        src/Namotion.Interceptor.Tracking.Tests/Transactions/SubjectTransactionTests.cs
git commit -m "Guard transaction Dispose against clobbering another flow's current transaction (#338)"
```

---

## Task 4: Documentation (XML docs + transactions guide)

No tests. The verification is a clean build (the repo treats warnings as errors, so malformed XML doc crefs would fail).

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransactionExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs` (`CommitAsync` XML doc ~lines 249-251)
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/ITransactionWriter.cs`
- Modify: `docs/tracking-transactions.md`

- [ ] **Step 1: Document the single-flow requirement on `BeginTransactionAsync`**

In `SubjectTransactionExtensions.cs`, replace:

```csharp
    /// <summary>
    /// Begins a new transaction bound to this context.
    /// </summary>
```

with:

```csharp
    /// <summary>
    /// Begins a new transaction bound to this context.
    /// </summary>
    /// <remarks>
    /// A transaction must be begun, used, committed, and disposed within the same async flow. The current
    /// transaction is tracked in an async-local slot that flows into awaited calls but not back out of
    /// them, so beginning a transaction inside a helper that returns it to the caller leaves the caller's
    /// flow without it: property writes then bypass the transaction, and <c>CommitAsync</c> throws an
    /// <see cref="InvalidOperationException"/>.
    /// </remarks>
```

- [ ] **Step 2: Document the new exceptions on `CommitAsync`**

In `SubjectTransaction.cs`, replace:

```csharp
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the transaction has been disposed.</exception>
    /// <exception cref="SubjectTransactionException">Thrown when one or more changes failed to commit.</exception>
    public ValueTask CommitAsync(CancellationToken cancellationToken)
```

with:

```csharp
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the transaction has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when commit is called from a different async flow than the one the transaction is active in,
    /// when the transaction was already committed, or when another commit is already in progress.
    /// </exception>
    /// <exception cref="SubjectTransactionException">
    /// Thrown when one or more changes failed to commit. If a registered <see cref="ITransactionWriter"/>
    /// throws instead of reporting failures, all changes are reported as failed and the transaction becomes
    /// terminal (it cannot be retried and must be disposed); its sources may be left un-reverted.
    /// </exception>
    public ValueTask CommitAsync(CancellationToken cancellationToken)
```

- [ ] **Step 3: Tighten the `ITransactionWriter.WriteToSourcesAsync` contract remarks**

In `ITransactionWriter.cs`, replace:

```csharp
    /// <remarks>
    /// Implementations must REPORT per-source failures via <see cref="SourceWriteResult"/> rather than
    /// throwing. This contract is load-bearing: a thrown exception propagates past the transaction's
    /// reconcile logic and bypasses source revert, leaving already-succeeded writes from other sources
    /// applied at their sources.
    /// </remarks>
```

with:

```csharp
    /// <remarks>
    /// Implementations must REPORT per-source failures via <see cref="SourceWriteResult"/> rather than
    /// throwing. This contract is load-bearing: if an implementation throws, the transaction has no
    /// <see cref="SourceWriteResult"/> to revert with, so it treats the commit as a full failure (every
    /// change reported failed, nothing applied to the local model) and becomes terminal; any writes that
    /// already reached other sources are left applied and un-reverted.
    /// </remarks>
```

- [ ] **Step 4: Update the transactions guide — Thread Safety and Best Practices**

In `docs/tracking-transactions.md`, in the `### Thread Safety` list, replace the line:

```markdown
- The transaction is automatically cleared on `Dispose()`
```

with:

```markdown
- The transaction is automatically cleared on `Dispose()` (only if it is the current transaction in the disposing flow)
- A transaction must be begun, used, committed, and disposed within the same async flow; committing from another flow throws (see [Single Async Flow](#single-async-flow))
```

In the `## Best Practices` list, replace:

```markdown
5. **Don't share transactions across threads** - Each async context should have its own transaction
```

with:

```markdown
5. **Use a transaction within a single async flow** - Begin, use, commit, and dispose it in the same flow; don't begin it in a helper and return it, or commit/dispose it from another flow (committing from a different flow throws)
```

- [ ] **Step 5: Update the transactions guide — Error Handling**

In `docs/tracking-transactions.md`, under `### SubjectTransactionException`, after the code block (after the closing ``` on the line before `### SubjectTransactionConflictException`), add this paragraph:

```markdown
If a custom `ITransactionWriter` throws instead of reporting failures, the commit fails with a `SubjectTransactionException` that reports every change as failed; the transaction becomes terminal and must be disposed, not retried (its sources may be left un-reverted).
```

Then in the `### Other Exceptions` table, replace the `InvalidOperationException` row:

```markdown
| `InvalidOperationException` | Nested transactions, already committed, transactions not enabled |
```

with:

```markdown
| `InvalidOperationException` | Nested transactions, already committed, transactions not enabled, committing from a different async flow |
```

- [ ] **Step 6: Add the `Single Async Flow` limitation section**

In `docs/tracking-transactions.md`, under `## Limitations`, after the `### Nested Transactions` section (and its code block), add:

````markdown
### Single Async Flow

A transaction is tracked in the current async flow via `AsyncLocal<T>`, which flows into awaited calls but not back out of them. Begin, use, commit, and dispose a transaction within the same async flow. In particular, do not begin a transaction inside a helper that returns it to the caller: the caller's flow will not see it, property writes will silently bypass the transaction and hit the model directly, and the commit will capture nothing.

To catch this, committing a transaction from a different flow than the one it is active in throws `InvalidOperationException`:

```csharp
// WRONG: begin runs in the helper's flow, so the caller's flow never sees the transaction.
static async Task<SubjectTransaction> StartAsync(IInterceptorSubjectContext ctx)
    => await ctx.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

var tx = await StartAsync(context);
person.FirstName = "John";              // bypasses the transaction (writes the model directly)
await tx.CommitAsync(CancellationToken.None);
// THROWS InvalidOperationException:
// "Transaction is being committed from a different async flow than the one it is active in. ..."
```
````

- [ ] **Step 7: Build to verify docs are well-formed**

Run: `dotnet build src/Namotion.Interceptor.Tracking`

Expected: Build succeeds with no warnings/errors (warnings are errors; unresolved XML crefs would fail here).

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Transactions/SubjectTransactionExtensions.cs \
        src/Namotion.Interceptor.Tracking/Transactions/SubjectTransaction.cs \
        src/Namotion.Interceptor.Tracking/Transactions/ITransactionWriter.cs \
        docs/tracking-transactions.md
git commit -m "Document single-async-flow transaction usage and throwing-writer behavior (#338)"
```

---

## Task 5: Full verification

No code changes; this confirms the whole change set is green and nothing regressed (especially the commit-time flow check against the existing concurrency tests).

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`

Expected: Build succeeds, no warnings/errors.

- [ ] **Step 2: Run the full unit-test suite (excluding integration)**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`

Expected: All tests pass, including:
- `WhenWriterThrows_ThenAllChangesFailAndTransactionIsTerminal`
- `WhenCommittingFromDifferentAsyncFlow_ThenThrows`
- `WhenDisposingFromDifferentAsyncFlow_ThenOtherTransactionSlotIsNotCleared`
- All pre-existing `Transactions` tests in Tracking.Tests and Connectors.Tests (the concurrency tests that begin and commit inside `Task.Run` flows, and the `RunWithoutAsyncLocalFlowAsync` external-write tests).

- [ ] **Step 3: Confirm no public-API snapshot drift**

The full suite includes each library's `VerifyChecksTests.PublicApi` test. Confirm none reported a snapshot mismatch (no `.received.txt` produced). No new public members were added, so none should change. If any `.received.txt` appears, stop and investigate rather than accepting it.

---

## Self-review notes (already checked against the spec)

- **Item 1** (throwing writer → terminal full failure): Task 1. Production change + comment fix + test covering all-failed, untouched local model, inner exception, and terminal/non-retryable.
- **Item 2a** (cross-flow commit fails fast): Task 2. `ValidateCanCommit` flow check + test.
- **Item 2b** (Dispose guard): Task 3. `== this` guard + test.
- **Docs** (XML + markdown for both items): Task 4.
- **Timeout preserved:** the `when (!cancellationToken.IsCancellationRequested)` filter leaves the commit-timeout `OperationCanceledException` path untouched and still retryable; no test change needed there.
- **No public API additions:** verified by Task 5 Step 3.
- **Out of scope (per spec):** item 3 test cleanup, the isolation-window options, a scoped `RunInTransactionAsync` API, and any write-time bypass check.
