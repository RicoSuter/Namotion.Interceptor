# Source Write Lock Ownership Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the per-source write serialization lock from a static `ConditionalWeakTable` onto the source instance as a nullable `SemaphoreSlim` property, and delete the `ISupportsConcurrentWrites` marker interface.

**Architecture:** `ISubjectSource` gains `SemaphoreSlim? WriteLock { get; }` where `null` means "source handles its own write concurrency" (replacing the marker interface). `SubjectSourceBase` implements it as a virtual property initialized to `new(1, 1)`. `SubjectSourceExtensions.WriteChangesInBatchesAsync` reads the property instead of consulting the weak table. The semaphore is intentionally never disposed (no wait handle is ever created; disposing would race in-flight writes).

**Tech Stack:** C# / .NET 9.0, xUnit + Moq, Verify + PublicApiGenerator for API snapshots.

**Spec:** `docs/superpowers/specs/2026-07-13-source-write-lock-ownership-design.md`

## Global Constraints

- Warnings are errors (`Directory.Build.props`); the build must be clean.
- Priorities when tradeoffs conflict: correctness, then performance (allocations first), then style.
- Test naming: `When<Condition>_Then<ExpectedBehavior>`; explicit `// Arrange`, `// Act`, `// Assert` comments.
- No hardcoded waits for synchronization; event-based coordination (`TaskCompletionSource`) as in the existing tests of the same file.
- No AI attribution anywhere in commits (no "Claude", no `Co-Authored-By`, no "Generated with" footers).
- Subagents must NOT commit. All changes are committed once, at the end, by the main session (Task 5).
- Do NOT commit anything under `docs/superpowers/` (spec and this plan stay local and uncommitted).
- The semaphore is never disposed. Do not "fix" this by adding a `Dispose` call; the spec documents why.

---

### Task 1: Core production change

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ISubjectSource.cs`
- Delete: `src/Namotion.Interceptor.Connectors/ISupportsConcurrentWrites.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceExtensions.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: `SemaphoreSlim? ISubjectSource.WriteLock { get; }` (null = source handles its own concurrency) and `public virtual SemaphoreSlim? SubjectSourceBase.WriteLock { get; } = new(1, 1);`. Tasks 2 and 3 implement/override this member in fakes.

Note on ordering: this is a compile-breaking interface change, so the usual test-first cycle is not possible in isolation. The behavioral safety net is the existing serialization/concurrency tests in `SubjectSourceExtensionsTests`, which run unchanged in Task 2.

- [ ] **Step 1: Create the work branch off master**

The working tree may be shared with another active session (it was on `feature/change-origin-corrections` at planning time). Confirm with the user before switching branches if `git status` shows unexpected in-progress work, or use a git worktree for isolation.

```bash
git checkout master && git pull && git checkout -b feature/source-write-lock-ownership
```

Expected: new branch `feature/source-write-lock-ownership` at origin/master. Untracked files under `docs/superpowers/` ride along; leave them untracked.

- [ ] **Step 2: Add `WriteLock` to `ISubjectSource` and update the `WriteChangesAsync` remarks**

In `src/Namotion.Interceptor.Connectors/ISubjectSource.cs`, insert the new property after `WriteBatchSize` (line 15) and replace the marker-interface sentence in the `WriteChangesAsync` remarks:

```csharp
    /// <summary>
    /// Gets the maximum number of property changes that can be applied in a single batch (0 = no limit).
    /// </summary>
    public int WriteBatchSize { get; }

    /// <summary>
    /// Gets the semaphore used by <see cref="SubjectSourceExtensions.WriteChangesInBatchesAsync"/>
    /// to serialize writes to this source. Return <c>null</c> when the source handles concurrent
    /// writes itself and needs no external synchronization. The semaphore must be created with
    /// initial and maximum count 1, is reserved for the write pipeline and must not be waited on
    /// or released by other code, and is owned by the source for its entire lifetime.
    /// Implementers must never dispose the semaphore, even when the source itself is disposable:
    /// no wait handle is ever created, so there is nothing to release, and disposing would
    /// race in-flight writes.
    /// </summary>
    public SemaphoreSlim? WriteLock { get; }
```

In the `WriteChangesAsync` remarks, replace this line:

```csharp
    /// Implement <see cref="ISupportsConcurrentWrites"/> to opt-out of automatic synchronization.
```

with:

```csharp
    /// Return <c>null</c> from <see cref="WriteLock"/> to opt out of automatic synchronization.
```

- [ ] **Step 3: Delete the marker interface**

```bash
rm src/Namotion.Interceptor.Connectors/ISupportsConcurrentWrites.cs
```

- [ ] **Step 4: Rewrite the lock acquisition in `SubjectSourceExtensions`**

In `src/Namotion.Interceptor.Connectors/SubjectSourceExtensions.cs`:

Remove these members entirely:
- The `// TODO: ConditionalWeakTable pattern...` comment block (lines 9 to 11).
- The `private static readonly ConditionalWeakTable<ISubjectSource, SourceWriteLock> WriteLocks = new();` field (line 12).
- The `internal sealed class SourceWriteLock` nested class at the bottom of the file (lines 130 to 136).

Update the `<remarks>` of `WriteChangesInBatchesAsync` from:

```csharp
    /// This method automatically synchronizes write operations unless the source implements
    /// <see cref="ISupportsConcurrentWrites"/>. Callers should always use this method
    /// instead of calling <see cref="ISubjectSource.WriteChangesAsync"/> directly.
```

to:

```csharp
    /// This method automatically serializes write operations via <see cref="ISubjectSource.WriteLock"/>
    /// unless the source returns <c>null</c> from it. Callers should always use this method
    /// instead of calling <see cref="ISubjectSource.WriteChangesAsync"/> directly.
```

Replace the body between the empty-changes check and the first `try` (currently the `ISupportsConcurrentWrites` check plus `WriteLocks.GetValue`):

```csharp
        // Skip synchronization for sources that handle their own concurrency
        if (source is ISupportsConcurrentWrites)
        {
            return await WriteChangesInBatchesCoreAsync(source, changes, cancellationToken).ConfigureAwait(false);
        }

        var writeLock = WriteLocks.GetValue(source, static _ => new SourceWriteLock());
        try
        {
            await writeLock.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
```

with:

```csharp
        // Sources that handle their own concurrency expose no lock
        var writeLock = source.WriteLock;
        if (writeLock is null)
        {
            return await WriteChangesInBatchesCoreAsync(source, changes, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
```

And in the `finally` block change `writeLock.Semaphore.Release();` to `writeLock.Release();`.

The `using System.Runtime.CompilerServices;` directive stays (still used by `AsyncMethodBuilder`); verify `System.Runtime.InteropServices` is still needed (it is, for `ImmutableCollectionsMarshal` in the core method).

- [ ] **Step 5: Implement the property in `SubjectSourceBase`**

In `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`, insert after the `WriteBatchSize` property (line 54):

```csharp
    /// <inheritdoc cref="ISubjectSource.WriteLock" />
    /// <remarks>
    /// Intentionally never disposed: the semaphore's <c>AvailableWaitHandle</c> is never created,
    /// so there is nothing to release, and disposing would race in-flight writes
    /// (for example a transaction commit awaiting the lock during host shutdown).
    /// Override to return <c>null</c> when the derived source supports concurrent writes.
    /// </remarks>
    public virtual SemaphoreSlim? WriteLock { get; } = new(1, 1);
```

Do not touch `Dispose()`.

- [ ] **Step 6: Build the Connectors project**

Run: `dotnet build src/Namotion.Interceptor.Connectors`
Expected: build succeeds with 0 warnings.

- [ ] **Step 7: Build the full solution to enumerate the breaks**

Run: `dotnet build src/Namotion.Interceptor.slnx 2>&1 | grep -E "error|Error" | head -20`
Expected: compile errors ONLY for the three direct `ISubjectSource` implementers fixed in Task 2:
- `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceExtensionsTests.cs` (`ConcurrentTestSource` references deleted `ISupportsConcurrentWrites`; it and `BlockingTestSource` lack `WriteLock`)
- `src/Namotion.Interceptor.Benchmark/SubjectTransactionBenchmark.cs` (`BenchmarkSource` lacks `WriteLock`)

If any OTHER project fails with "does not implement interface member ... WriteLock", that is an implementer the spec missed: add `public SemaphoreSlim? WriteLock { get; } = new(1, 1);` to it (preserving the old serialized behavior) and report it in the task summary.

---

### Task 2: Fix test fakes, mocks, and the benchmark implementer

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceExtensionsTests.cs:357-394` (serialization test), `:494-527` (fake sources)
- Modify: `src/Namotion.Interceptor.Benchmark/SubjectTransactionBenchmark.cs:108-130`

**Interfaces:**
- Consumes: `SemaphoreSlim? ISubjectSource.WriteLock { get; }` from Task 1.
- Produces: compiling test fakes; the full solution builds again.

Background: Moq mocks of `ISubjectSource` return `null` for the new `WriteLock` getter by default, which now means "concurrent writes allowed". That is harmless for the many tests that await writes sequentially, but any test asserting serialization must set up a real semaphore explicitly.

- [ ] **Step 1: Give the serialization test's mock a real lock and make the test deterministic**

Replace `WriteChangesInBatchesAsync_RegularSource_SerializesWrites` (lines 357 to 394 of `SubjectSourceExtensionsTests.cs`) entirely. The old body used a 50 ms overlap window (`Task.Delay(50, ct)`), which can only miss a broken lock: a stalled runner serializes the calls naturally and the assertion passes even with no lock. The replacement is deterministic with no timing at all. It relies on async methods executing synchronously until their first incomplete await: with no lock, all three calls enter `WriteChangesAsync` during task creation (max reaches 3 before the gate opens); with a working lock, calls two and three park on the semaphore instead.

```csharp
    [Fact]
    public async Task WriteChangesInBatchesAsync_RegularSource_SerializesWrites()
    {
        // Arrange
        var concurrentCalls = 0;
        var maxConcurrentCalls = 0;
        var canContinue = new TaskCompletionSource();

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.SetupGet(s => s.WriteLock).Returns(new SemaphoreSlim(1, 1));
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                var current = Interlocked.Increment(ref concurrentCalls);
                maxConcurrentCalls = Math.Max(maxConcurrentCalls, current);

                await canContinue.Task;

                Interlocked.Decrement(ref concurrentCalls);
                return WriteResult.Success;
            });

        var changes = CreateChanges(1);

        // Act - Without a lock all three calls enter the callback synchronously during
        // task creation; with the lock the second and third park on the semaphore
        var tasks = new[]
        {
            sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask(),
            sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask(),
            sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask()
        };

        canContinue.SetResult();
        await Task.WhenAll(tasks);

        // Assert - Regular source should serialize writes (max 1 concurrent)
        Assert.Equal(1, maxConcurrentCalls);
    }
```

- [ ] **Step 2: Update `ConcurrentTestSource` to the null-lock contract**

Replace the class (lines 494 to 504) with:

```csharp
    /// <summary>
    /// Test source that returns a null write lock to opt out of automatic synchronization.
    /// </summary>
    private sealed class ConcurrentTestSource(Func<Task<WriteResult>> writeCallback) : ISubjectSource
    {
        public IInterceptorSubject RootSubject => throw new NotSupportedException();
        public int WriteBatchSize => 0;
        public SemaphoreSlim? WriteLock => null;
        public async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
            => await writeCallback();
        public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken) => Task.FromResult<Action?>(null);
    }
```

- [ ] **Step 3: Add a lock to `BlockingTestSource`**

It is used by `WriteChangesInBatchesAsync_CancellationDuringSemaphoreWait_ReturnsFailure`, which needs serialized writes. After `public int WriteBatchSize => 0;` (line 515) add:

```csharp
        public SemaphoreSlim? WriteLock { get; } = new(1, 1);
```

- [ ] **Step 4: Add a lock to `BenchmarkSource`**

In `src/Namotion.Interceptor.Benchmark/SubjectTransactionBenchmark.cs`, after `public int WriteBatchSize => 0;` (line 117) add the same member. It gets a real semaphore (not null) so benchmark numbers stay comparable with runs made under the old weak-table behavior, where every source was serialized:

```csharp
        public SemaphoreSlim? WriteLock { get; } = new(1, 1);
```

- [ ] **Step 5: Build the full solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: build succeeds with 0 warnings.

- [ ] **Step 6: Run the extension tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectSourceExtensionsTests"`
Expected: all pass, including `WriteChangesInBatchesAsync_RegularSource_SerializesWrites` (mock with explicit lock, max 1 concurrent) and `WriteChangesInBatchesAsync_ConcurrentSource_AllowsConcurrentWrites` (null lock, max 3 concurrent).

---

### Task 3: New coverage for `SubjectSourceBase` defaults and the null override

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/TestSubjectSource.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceExtensionsTests.cs` (add two tests before the `CreateChange` helper at line 472; add usings)

**Interfaces:**
- Consumes: `public virtual SemaphoreSlim? WriteLock` from Task 1; `TestSubjectSource(IInterceptorSubject subject, IInterceptorSubjectContext context, ILogger logger, ...)` and its `WriteChangesOverride` init property (both exist already).
- Produces: `public bool SupportsConcurrentWrites { get; init; }` on `TestSubjectSource`.

- [ ] **Step 1: Add the override knob to `TestSubjectSource`**

In `src/Namotion.Interceptor.Connectors.Tests/TestSubjectSource.cs`, after `public override int WriteBatchSize => WriteBatchSizeOverride;` (line 29) add:

```csharp
    public bool SupportsConcurrentWrites { get; init; }

    public override SemaphoreSlim? WriteLock => SupportsConcurrentWrites ? null : base.WriteLock;
```

- [ ] **Step 2: Write the two tests**

In `SubjectSourceExtensionsTests.cs`, extend the usings at the top of the file to:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Tracking.Change;
```

Insert before the `private static SubjectPropertyChange CreateChange(int id)` helper:

```csharp
    [Fact]
    public async Task WhenSourceBaseHasDefaultWriteLock_ThenWritesAreSerialized()
    {
        // Arrange
        var concurrentCalls = 0;
        var maxConcurrentCalls = 0;

        var source = new TestSubjectSource(
            new Mock<IInterceptorSubject>().Object,
            new Mock<IInterceptorSubjectContext>().Object,
            NullLogger.Instance)
        {
            WriteChangesOverride = async (_, _) =>
            {
                var current = Interlocked.Increment(ref concurrentCalls);
                maxConcurrentCalls = Math.Max(maxConcurrentCalls, current);

                await canContinue.Task;

                Interlocked.Decrement(ref concurrentCalls);
                return WriteResult.Success;
            }
        };

        var changes = CreateChanges(1);

        // Act - Without a lock all three calls enter the callback synchronously during
        // task creation; with the lock the second and third park on the semaphore
        var tasks = new[]
        {
            source.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask(),
            source.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask(),
            source.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask()
        };

        canContinue.SetResult();
        await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1, maxConcurrentCalls);
    }

    [Fact]
    public async Task WhenSourceBaseWriteLockIsNull_ThenWritesAreConcurrent()
    {
        // Arrange
        var concurrentCalls = 0;
        var maxConcurrentCalls = 0;
        var canContinue = new TaskCompletionSource();

        var source = new TestSubjectSource(
            new Mock<IInterceptorSubject>().Object,
            new Mock<IInterceptorSubjectContext>().Object,
            NullLogger.Instance)
        {
            SupportsConcurrentWrites = true,
            WriteChangesOverride = async (_, _) =>
            {
                var current = Interlocked.Increment(ref concurrentCalls);
                maxConcurrentCalls = Math.Max(maxConcurrentCalls, current);

                await canContinue.Task;

                Interlocked.Decrement(ref concurrentCalls);
                return WriteResult.Success;
            }
        };

        var changes = CreateChanges(1);

        // Act - With a null lock all three calls enter the callback synchronously
        // during task creation; a regression would park them on a semaphore instead
        var tasks = new[]
        {
            source.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask(),
            source.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask(),
            source.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask()
        };

        canContinue.SetResult();
        await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(3, maxConcurrentCalls);
    }
```

Notes: both tests are fully deterministic with no delays or polling. Async methods execute synchronously until their first incomplete await, so at the moment the three tasks have been created, either all three calls sit inside the callback awaiting the gate (no lock) or exactly one does while the other two are parked on the semaphore (lock present). Opening the gate then lets everything drain; the recorded maximum distinguishes the two cases exactly. The source is never started as a hosted service, so plain mocks for subject and context are sufficient.

- [ ] **Step 3: Run the two new tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectSourceExtensionsTests"`
Expected: PASS, including both `When...` tests.

- [ ] **Step 4: Sanity-check the null override is what makes the difference**

Temporarily flip `SupportsConcurrentWrites = true` to `false` in `WhenSourceBaseWriteLockIsNull_ThenWritesAreConcurrent`, rerun the filter, and confirm the test now FAILS with `maxConcurrentCalls == 1`. Revert the flip and rerun to green. This proves the test exercises the override rather than passing vacuously.

---

### Task 4: Accept the public API snapshot

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt`

**Interfaces:**
- Consumes: the Task 1 API surface.
- Produces: a passing `VerifyChecksTests.PublicApi` test.

- [ ] **Step 1: Run the snapshot test and expect a mismatch**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~VerifyChecksTests"`
Expected: FAIL with a Verify mismatch; a `VerifyChecksTests.PublicApi.received.txt` appears next to the verified file.

- [ ] **Step 2: Inspect the received diff, then accept it**

Run: `diff src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.received.txt`

Expected diff, exactly three changes:
1. `ISubjectSource` gains `System.Threading.SemaphoreSlim? WriteLock { get; }`.
2. The line `public interface ISupportsConcurrentWrites { }` is removed.
3. `SubjectSourceBase` gains `public virtual System.Threading.SemaphoreSlim? WriteLock { get; }`.

Anything else in the diff is an unintended API change: stop and investigate before accepting.

```bash
mv src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.received.txt src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt
```

- [ ] **Step 3: Rerun to green**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~VerifyChecksTests"`
Expected: PASS.

---

### Task 5: Full verification and single commit

**Files:**
- No new edits expected; fixes only if the full run surfaces stragglers.

**Interfaces:**
- Consumes: everything above.
- Produces: the final commit on `feature/source-write-lock-ownership`.

- [ ] **Step 1: Full build and unit test run**

Run: `dotnet build src/Namotion.Interceptor.slnx && dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: build clean, all tests pass.

If a test in another Connectors test file fails because its `Mock<ISubjectSource>` now takes the unsynchronized path: only tests asserting write serialization need fixing, using the same one-line pattern as Task 2 Step 1 (`sourceMock.SetupGet(s => s.WriteLock).Returns(new SemaphoreSlim(1, 1));`). Tests that merely await writes sequentially are unaffected; do not add locks to them.

- [ ] **Step 2: Confirm no references to the removed API remain**

Run: `grep -rn "ISupportsConcurrentWrites\|SourceWriteLock\|ConditionalWeakTable" src --include="*.cs" | grep -v "/obj/\|/bin/"`
Expected: no matches.

- [ ] **Step 3: Review the diff and commit everything at once**

Verify the staged set contains only intended files. Do NOT stage `docs/superpowers/`, `.claude/`, `graphify-out/`, or `opc-ua-loader-findings.md`.

```bash
git add src/Namotion.Interceptor.Connectors/ISubjectSource.cs \
        src/Namotion.Interceptor.Connectors/SubjectSourceExtensions.cs \
        src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs \
        src/Namotion.Interceptor.Connectors.Tests/SubjectSourceExtensionsTests.cs \
        src/Namotion.Interceptor.Connectors.Tests/TestSubjectSource.cs \
        src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt \
        src/Namotion.Interceptor.Benchmark/SubjectTransactionBenchmark.cs
git rm src/Namotion.Interceptor.Connectors/ISupportsConcurrentWrites.cs
git status --short
git commit -m "refactor: sources own their write lock instead of a static weak table

The write serialization lock moves from a ConditionalWeakTable in
SubjectSourceExtensions onto ISubjectSource as a nullable WriteLock
property, tying its lifetime to the source. A null lock replaces the
ISupportsConcurrentWrites marker interface, which is removed. The
semaphore is intentionally not disposed: no wait handle is ever
created and disposing would race in-flight writes."
```

Expected: one commit; `git status` afterwards shows only the pre-existing local noise (untracked `docs/superpowers/` files, `.claude/settings.local.json`).

- [ ] **Step 4: Report completion**

Summarize: files changed, test results (counts), the exact API diff accepted in Task 4, and any stragglers fixed in Step 1. Do not push or open a PR without the user's go-ahead.
