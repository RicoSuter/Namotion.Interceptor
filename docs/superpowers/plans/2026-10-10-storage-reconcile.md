# HomeBlaze Storage Reconcile Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the per-event handling of file system changes in `FluentStorageContainer` with one debounced, sequential pass that compares the subject tree with a listing of the storage and applies the difference.

**Architecture:** A `StorageIndex` remembers what was applied last (exact path, version, subject, hash). A `StorageReconciler` lists the storage once per pass, compares it with the index, loads what is new or changed and assigns each changed folder once. A `StorageWorker` runs passes and UI operations one at a time, so the hierarchy lock disappears. A `ReconcileTrigger` turns watcher events, watcher errors and a periodic timer into passes.

**Tech Stack:** .NET 10, C# 14, FluentStorage 5.6.0 (`IBlobStorage`), xUnit 2.9, Moq, `TimeProvider` with the existing `ManualTimeProvider` test double.

**Spec:** `docs/superpowers/specs/2026-10-10-storage-reconcile-design.md`. Read it first.

## Global Constraints

- Read `AGENTS.md` in the repository root before starting. Its rules apply to every task.
- Priorities, in this order: correctness (thread safety, the tree agrees with the disk once writes settle), then performance (allocations weigh most), then style.
- Tests are named `When<Condition>_Then<ExpectedBehavior>` and use `// Arrange`, `// Act`, `// Assert` comments (`// Act & Assert` for exception tests).
- No hardcoded waits in tests (`Task.Delay`, `Thread.Sleep`). Use `AsyncTestHelpers.WaitUntilAsync`, a `TaskCompletionSource`, `GatedFile` or `ManualTimeProvider`.
- No abbreviations in names (`exception`, not `ex`; `cancellationToken`, not `ct`), unless the name is very long.
- No em dashes and no hard wrapping in markdown.
- Inline comments only for a why the reader cannot derive. XML docs state the contract.
- No AI attribution in commit messages or pull request text: no agent names, no `Co-Authored-By`, no "Generated with".
- Commit titles start with `fix:`, `feat:`, `perf:`, `docs:`, `refactor:`, `test:` or `chore:`.
- All new production types are `internal` and live in `src/HomeBlaze/HomeBlaze.Storage/Internal`. The test project already has `InternalsVisibleTo`.
- `StorageReconciler`, `StorageWorker` and `StorageIndex` use `IBlobStorage` only. No `System.IO.File`, `Directory` or `FileInfo` in them.
- Paths in the index are exact on-disk names, compared with `StringComparison.Ordinal`, with forward slashes and no leading or trailing slash.
- Build with warnings as errors. Sonar diagnostics can fail the build; do not add complexity to satisfy one, raise it instead.
- Run from the repository root. Storage tests: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`. Full unit suite: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`.

## Deviations from the spec, decided while planning

- `JsonSubjectSynchronizer` is removed, not shrunk. What remains of it is two lines in the reconciler.
- Hashes are computed from the file's bytes for every content file, JSON included, so a write and a later read produce the same hash without depending on text decoding.
- A file that was blocked because its folder was not placed is retried in the pass after the one that places the folder, not in the same pass.
- A subject that is moved is applied in two steps (add everywhere, then remove everywhere). The lifecycle interceptor detaches before it attaches within one assignment, so a single step would stop and restart a moved device.
- The index finds a subject's path by scanning its entries. That runs once per configuration save over a few thousand entries, and avoids a second dictionary that every state change would have to keep in step.

## File map

Created in `src/HomeBlaze/HomeBlaze.Storage/Internal`:

| File | Responsibility |
|---|---|
| `StoragePath.cs` | Normalize a path, parent, name, depth |
| `StorageHash.cs` | SHA256 of bytes as hex |
| `StorageEntry.cs` | `StorageVersion`, `StorageEntryState`, `StorageEntry` |
| `StorageIndex.cs` | Entries by exact path |
| `StorageWorker.cs` | Sequential queue and the inline rule |
| `StorageReconciler.cs` | One pass, applying the tree, key rules |
| `StorageCallTimeout.cs` | The 30 second limit for storage calls |
| `ReconcileTrigger.cs` | Quiet period, maximum delay, periodic pass |

Modified: `FluentStorageContainer.cs`, `Internal/StorageFileWatcher.cs`, `Internal/StoragePathFilter.cs`, `Internal/FileSubjectFactory.cs`, `Files/GenericFile.cs`, `Files/JsonFile.cs`.

Deleted: `Internal/FileEventCoalescer.cs`, `Internal/StoragePathRegistry.cs`, `Internal/StorageHierarchyManager.cs`, `Internal/JsonSubjectSynchronizer.cs`, and the tests `FileEventCoalescerTests.cs`, `StoragePathRegistryTests.cs`, `StorageHierarchyManagerTests.cs`.

Tests created in `src/HomeBlaze/HomeBlaze.Storage.Tests`: `StorageIndexTests.cs`, `StorageWorkerTests.cs`, `GenericFileTests.cs`, `StorageTestBase.cs`, `StorageReconcilerTests.cs`, `PausableBlobStorage.cs`, `StorageTimeoutTests.cs`, `ReconcileTriggerTests.cs`.

## Branch

The work happens on `refactor/storage-reconcile`. It currently sits on top of the branch of pull request #672. When #672 is merged, rebase before opening the pull request (Task 7).

---

### Task 1: Paths, hash and index

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Internal/StoragePath.cs`
- Create: `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageHash.cs`
- Create: `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageEntry.cs`
- Create: `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageIndex.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/StoragePathFilter.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/StorageIndexTests.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/StoragePathFilterTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `StoragePath.Root` (`""`), `StoragePath.Normalize(string)`, `GetParent(string)`, `GetName(string)`, `GetDepth(string)`
  - `StorageHash.Compute(ReadOnlySpan<byte>) : string`
  - `readonly record struct StorageVersion(long Size, DateTimeOffset? Modified)`
  - `enum StorageEntryState { New, Placed, KeyTaken, Failed }`
  - `sealed class StorageEntry { required string Path; required bool IsFolder; StorageVersion Version; IInterceptorSubject? Subject; string? Hash; string? Key; StorageEntryState State; }`
  - `sealed class StorageIndex { int Count; IReadOnlyCollection<StorageEntry> Entries; bool TryGet(string, out StorageEntry); bool TryGetPath(IInterceptorSubject, out string); void Set(StorageEntry); bool Remove(string); void EnsureFolders(string); }`
  - `StoragePathFilter.IsIgnored(ReadOnlySpan<char>) : bool`

- [ ] **Step 1: Write the failing tests**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/StorageIndexTests.cs`:

```csharp
using HomeBlaze.Storage.Internal;
using Moq;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class StorageIndexTests
{
    [Theory]
    [InlineData("/Docs/Readme.md", "Docs/Readme.md")]
    [InlineData("Docs\\Readme.md", "Docs/Readme.md")]
    [InlineData("/Docs/", "Docs")]
    [InlineData("Readme.md", "Readme.md")]
    public void WhenPathIsNormalized_ThenItHasForwardSlashesAndNoOuterSlash(string path, string expected)
    {
        // Act
        var normalized = StoragePath.Normalize(path);

        // Assert
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("Docs/Guides/Setup.md", "Docs/Guides", "Setup.md", 2)]
    [InlineData("Readme.md", "", "Readme.md", 0)]
    public void WhenPathIsSplit_ThenParentNameAndDepthMatch(string path, string parent, string name, int depth)
    {
        // Act & Assert
        Assert.Equal(parent, StoragePath.GetParent(path));
        Assert.Equal(name, StoragePath.GetName(path));
        Assert.Equal(depth, StoragePath.GetDepth(path));
    }

    [Fact]
    public void WhenPathsDifferOnlyInCasing_ThenIndexKeepsBoth()
    {
        // Arrange
        var index = new StorageIndex();

        // Act
        index.Set(new StorageEntry { Path = "README.md", IsFolder = false });
        index.Set(new StorageEntry { Path = "readme.md", IsFolder = false });

        // Assert
        Assert.Equal(2, index.Count);
        Assert.True(index.TryGet("README.md", out _));
        Assert.False(index.TryGet("Readme.md", out _));
    }

    [Fact]
    public void WhenSubjectIsInIndex_ThenItsPathIsFound()
    {
        // Arrange
        var index = new StorageIndex();
        var subject = new Mock<IInterceptorSubject>().Object;
        index.Set(new StorageEntry { Path = "Docs/Motor.json", IsFolder = false, Subject = subject });

        // Act
        var found = index.TryGetPath(subject, out var path);

        // Assert
        Assert.True(found);
        Assert.Equal("Docs/Motor.json", path);
        Assert.False(index.TryGetPath(new Mock<IInterceptorSubject>().Object, out _));
    }

    [Fact]
    public void WhenFoldersAreEnsured_ThenMissingAncestorsAreAddedAndExistingOnesKept()
    {
        // Arrange
        var index = new StorageIndex();
        var docs = new StorageEntry { Path = "Docs", IsFolder = true, State = StorageEntryState.Placed };
        index.Set(docs);

        // Act
        index.EnsureFolders("Docs/Guides/Setup.md");

        // Assert
        Assert.True(index.TryGet("Docs", out var keptDocs));
        Assert.Same(docs, keptDocs);
        Assert.True(index.TryGet("Docs/Guides", out var guides));
        Assert.True(guides.IsFolder);
        Assert.False(index.TryGet("Docs/Guides/Setup.md", out _));
    }

    [Fact]
    public void WhenSameBytesAreHashed_ThenHashIsEqual()
    {
        // Act
        var first = StorageHash.Compute("content"u8);
        var second = StorageHash.Compute("content"u8);
        var other = StorageHash.Compute("Content"u8);

        // Assert
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }
}
```

Append to the class in `src/HomeBlaze/HomeBlaze.Storage.Tests/StoragePathFilterTests.cs`, before its closing brace:

```csharp
    [Theory]
    [InlineData(".idea/workspace.xml", true)]
    [InlineData("Docs/Notes.md.tmp", true)]
    [InlineData("Build.tmp/Output.md", true)]
    [InlineData("Docs/Readme.md", false)]
    public void WhenPathIsHiddenOrTemporary_ThenItIsIgnored(string path, bool expected)
    {
        // Act
        var isIgnored = StoragePathFilter.IsIgnored(path);

        // Assert
        Assert.Equal(expected, isIgnored);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~StorageIndexTests|FullyQualifiedName~StoragePathFilterTests"`
Expected: build errors, `StoragePath`, `StorageIndex`, `StorageEntry`, `StorageHash` and `StoragePathFilter.IsIgnored` do not exist.

- [ ] **Step 3: Implement**

Create `src/HomeBlaze/HomeBlaze.Storage/Internal/StoragePath.cs`:

```csharp
namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Paths within a storage as the index holds them: forward slashes, no leading or trailing slash, exact casing.
/// </summary>
internal static class StoragePath
{
    /// <summary>The path of the storage root, which is the parent of every top-level entry.</summary>
    public const string Root = "";

    public static string Normalize(string path)
        => path.Replace('\\', '/').Trim('/');

    public static string GetParent(string path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? Root : path[..separatorIndex];
    }

    public static string GetName(string path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? path : path[(separatorIndex + 1)..];
    }

    public static int GetDepth(string path)
        => path.AsSpan().Count('/');
}
```

Create `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageHash.cs`:

```csharp
using System.Security.Cryptography;

namespace HomeBlaze.Storage.Internal;

internal static class StorageHash
{
    /// <summary>
    /// Hashes the bytes of a file. Always computed from bytes, so a write and a later read of the same file agree.
    /// </summary>
    public static string Compute(ReadOnlySpan<byte> content)
        => Convert.ToHexString(SHA256.HashData(content));
}
```

Create `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageEntry.cs`:

```csharp
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// What a listing reports about the content of a file. Only compared for equality: a backend with a stronger
/// token than size and time can supply that instead.
/// </summary>
internal readonly record struct StorageVersion(long Size, DateTimeOffset? Modified);

internal enum StorageEntryState
{
    /// <summary>Loaded in this pass and not applied to the tree yet.</summary>
    New,

    /// <summary>The subject is in the tree under <see cref="StorageEntry.Key"/>.</summary>
    Placed,

    /// <summary>Not in the tree: its key is held by another entry, or its folder is not placed.</summary>
    KeyTaken,

    /// <summary>The file could not be loaded.</summary>
    Failed
}

/// <summary>
/// What was applied last for one path.
/// </summary>
internal sealed class StorageEntry
{
    public required string Path { get; init; }

    public required bool IsFolder { get; init; }

    public StorageVersion Version { get; set; }

    public IInterceptorSubject? Subject { get; set; }

    /// <summary>The content hash, for subjects that hold content. Null for folders and plain files.</summary>
    public string? Hash { get; set; }

    /// <summary>The key in the parent folder that the entry holds or asked for.</summary>
    public string? Key { get; set; }

    public StorageEntryState State { get; set; }
}
```

Create `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageIndex.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The entries of a storage by exact path.
/// </summary>
/// <remarks>Not thread-safe. Only the <see cref="StorageWorker"/> reads or writes it.</remarks>
internal sealed class StorageIndex
{
    private readonly Dictionary<string, StorageEntry> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public IReadOnlyCollection<StorageEntry> Entries => _entries.Values;

    public bool TryGet(string path, [MaybeNullWhen(false)] out StorageEntry entry)
        => _entries.TryGetValue(path, out entry);

    public bool TryGetPath(IInterceptorSubject subject, [MaybeNullWhen(false)] out string path)
    {
        // Scanned on purpose: a second dictionary would have to follow every change of an entry's subject.
        foreach (var entry in _entries.Values)
        {
            if (ReferenceEquals(entry.Subject, subject))
            {
                path = entry.Path;
                return true;
            }
        }

        path = null;
        return false;
    }

    public void Set(StorageEntry entry)
        => _entries[entry.Path] = entry;

    public bool Remove(string path)
        => _entries.Remove(path);

    /// <summary>
    /// Adds a folder entry for every ancestor of the path that has none.
    /// </summary>
    public void EnsureFolders(string path)
    {
        for (var parent = StoragePath.GetParent(path);
             parent.Length > 0 && !_entries.ContainsKey(parent);
             parent = StoragePath.GetParent(parent))
        {
            Set(new StorageEntry { Path = parent, IsFolder = true });
        }
    }
}
```

In `src/HomeBlaze/HomeBlaze.Storage/Internal/StoragePathFilter.cs`, add this method after `IsHidden`:

```csharp
    /// <summary>
    /// Checks whether the path never becomes a subject: it is hidden or has a temporary segment.
    /// </summary>
    /// <remarks>Only for paths within the storage, see <see cref="HasTemporarySegment"/>.</remarks>
    public static bool IsIgnored(ReadOnlySpan<char> path)
        => IsHidden(path) || HasTemporarySegment(path);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~StorageIndexTests|FullyQualifiedName~StoragePathFilterTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage/Internal src/HomeBlaze/HomeBlaze.Storage.Tests
git commit -m "refactor: add the index of applied storage entries"
```

---

### Task 2: The sequential worker

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageWorker.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/StorageWorkerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `sealed class StorageWorker : IDisposable`
  - `StorageWorker(ILogger? logger = null)`
  - `Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)`
  - `Task<TResult> RunAsync<TResult>(Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken)`
  - After `Dispose`, `RunAsync` returns a cancelled task and does not throw.

- [ ] **Step 1: Write the failing tests**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/StorageWorkerTests.cs`:

```csharp
using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class StorageWorkerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WhenItemIsRunning_ThenNextItemWaitsForIt()
    {
        // Arrange
        using var worker = new StorageWorker();
        var firstStarted = NewSignal();
        var firstRelease = NewSignal();
        var order = new List<string>();

        var first = worker.RunAsync(async _ =>
        {
            order.Add("first started");
            firstStarted.SetResult();
            await firstRelease.Task;
            order.Add("first finished");
        }, CancellationToken.None);

        var second = worker.RunAsync(_ =>
        {
            order.Add("second");
            return Task.CompletedTask;
        }, CancellationToken.None);

        await firstStarted.Task.WaitAsync(Timeout);

        // Act
        var secondWasWaiting = !second.IsCompleted;
        firstRelease.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Timeout);

        // Assert
        Assert.True(secondWasWaiting);
        Assert.Equal(["first started", "first finished", "second"], order);
    }

    [Fact]
    public async Task WhenItemThrows_ThenCallerGetsExceptionAndLaterItemsRun()
    {
        // Arrange
        using var worker = new StorageWorker();

        // Act
        var failing = worker.RunAsync(_ => throw new InvalidOperationException("failed"), CancellationToken.None);
        var result = await worker.RunAsync(_ => Task.FromResult(42), CancellationToken.None).WaitAsync(Timeout);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task WhenItemCallsWorkerAgain_ThenInnerWorkRunsInline()
    {
        // Arrange
        using var worker = new StorageWorker();

        // Act
        var result = await worker.RunAsync(
            async token => await worker.RunAsync(_ => Task.FromResult("inner"), token),
            CancellationToken.None).WaitAsync(Timeout);

        // Assert
        Assert.Equal("inner", result);
    }

    [Fact]
    public async Task WhenFlowOfFinishedItemCallsWorker_ThenWorkIsQueuedBehindRunningItem()
    {
        // Arrange
        using var worker = new StorageWorker();
        ExecutionContext? flowOfFinishedItem = null;
        await worker.RunAsync(_ =>
        {
            flowOfFinishedItem = ExecutionContext.Capture();
            return Task.CompletedTask;
        }, CancellationToken.None).WaitAsync(Timeout);

        var runningStarted = NewSignal();
        var runningRelease = NewSignal();
        var running = worker.RunAsync(async _ =>
        {
            runningStarted.SetResult();
            await runningRelease.Task;
        }, CancellationToken.None);
        await runningStarted.Task.WaitAsync(Timeout);

        // Act: a task started by the finished item still carries its flow and calls in while another item runs.
        var lateWorkRan = false;
        Task? lateWork = null;
        ExecutionContext.Run(flowOfFinishedItem!, _ =>
        {
            lateWork = worker.RunAsync(_ =>
            {
                lateWorkRan = true;
                return Task.CompletedTask;
            }, CancellationToken.None);
        }, null);

        var ranInline = lateWorkRan;
        runningRelease.SetResult();
        await Task.WhenAll(running, lateWork!).WaitAsync(Timeout);

        // Assert
        Assert.False(ranInline);
        Assert.True(lateWorkRan);
    }

    [Fact]
    public async Task WhenWorkerIsDisposed_ThenRunningItemFinishesAndQueuedItemIsCancelled()
    {
        // Arrange
        var worker = new StorageWorker();
        var runningStarted = NewSignal();
        var runningRelease = NewSignal();
        var running = worker.RunAsync(async _ =>
        {
            runningStarted.SetResult();
            await runningRelease.Task;
            return "finished";
        }, CancellationToken.None);
        var queued = worker.RunAsync(_ => Task.CompletedTask, CancellationToken.None);
        await runningStarted.Task.WaitAsync(Timeout);

        // Act
        worker.Dispose();
        runningRelease.SetResult();
        var afterDispose = worker.RunAsync(_ => Task.CompletedTask, CancellationToken.None);

        // Assert
        Assert.Equal("finished", await running.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => afterDispose.WaitAsync(Timeout));
    }

    [Fact]
    public async Task WhenCallerTokenIsCancelledBeforeItemStarts_ThenItemIsNotRun()
    {
        // Arrange
        using var worker = new StorageWorker();
        using var cancellation = new CancellationTokenSource();
        var runningStarted = NewSignal();
        var runningRelease = NewSignal();
        var running = worker.RunAsync(async _ =>
        {
            runningStarted.SetResult();
            await runningRelease.Task;
        }, CancellationToken.None);
        await runningStarted.Task.WaitAsync(Timeout);

        var wasRun = false;
        var queued = worker.RunAsync(_ =>
        {
            wasRun = true;
            return Task.CompletedTask;
        }, cancellation.Token);

        // Act
        cancellation.Cancel();
        runningRelease.SetResult();
        await running.WaitAsync(Timeout);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
        Assert.False(wasRun);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~StorageWorkerTests"`
Expected: build error, `StorageWorker` does not exist.

- [ ] **Step 3: Implement**

Create `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageWorker.cs`:

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Runs work items one at a time, in the order they were handed in. Everything that changes the subject tree
/// or the index of a storage goes through it, which is why neither needs a lock.
/// </summary>
internal sealed class StorageWorker : IDisposable
{
    private readonly Channel<WorkItem> _queue =
        Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _disposalSource = new();
    private readonly AsyncLocal<WorkItem?> _itemOfFlow = new();
    private readonly ILogger? _logger;

    private volatile WorkItem? _runningItem;

    public StorageWorker(ILogger? logger = null)
    {
        _logger = logger;
        _ = Task.Run(ProcessQueueAsync);
    }

    /// <summary>
    /// Runs the work after everything handed in before it. Work handed in from within a running item runs
    /// inline. After disposal the returned task is cancelled.
    /// </summary>
    public Task<TResult> RunAsync<TResult>(Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken)
    {
        // Queued, a call from within the running item would wait for the item that waits for it. The item is
        // compared, not just a flag: a task that an item started keeps the flow of that item after it has
        // finished, and must not run next to a later item.
        var itemOfFlow = _itemOfFlow.Value;
        if (itemOfFlow != null && ReferenceEquals(itemOfFlow, _runningItem))
        {
            return work(cancellationToken);
        }

        var item = new WorkItem<TResult>(work, cancellationToken);
        if (!_queue.Writer.TryWrite(item))
        {
            item.Cancel();
        }

        return item.Completion;
    }

    /// <inheritdoc cref="RunAsync{TResult}"/>
    public Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        => RunAsync(async token =>
        {
            await work(token);
            return true;
        }, cancellationToken);

    private async Task ProcessQueueAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync())
        {
            _runningItem = item;
            _itemOfFlow.Value = item;
            try
            {
                await item.ExecuteAsync(_disposalSource.Token);
            }
            catch (Exception exception)
            {
                // An item reports its own outcome to its caller. Reaching this means that reporting failed.
                _logger?.LogError(exception, "Storage work item failed outside of its own error handling");
            }
            finally
            {
                _runningItem = null;
            }
        }
    }

    /// <summary>
    /// Lets the running item finish and cancels the items that are still queued.
    /// </summary>
    public void Dispose()
    {
        _disposalSource.Cancel();
        _queue.Writer.TryComplete();
    }

    private abstract class WorkItem
    {
        public abstract Task ExecuteAsync(CancellationToken disposalToken);
    }

    private sealed class WorkItem<TResult>(
        Func<CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken) : WorkItem
    {
        // Asynchronous continuations keep the caller's code off the worker loop.
        private readonly TaskCompletionSource<TResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TResult> Completion => _completion.Task;

        public void Cancel() => _completion.TrySetCanceled();

        public override async Task ExecuteAsync(CancellationToken disposalToken)
        {
            if (cancellationToken.IsCancellationRequested || disposalToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled();
                return;
            }

            try
            {
                _completion.TrySetResult(await work(cancellationToken));
            }
            catch (OperationCanceledException)
            {
                _completion.TrySetCanceled();
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~StorageWorkerTests"`
Expected: 6 passed. Run it five times in a row; all runs pass.

- [ ] **Step 5: Verify the inline rule is tested by the stale-flow test**

Temporarily replace the condition `itemOfFlow != null && ReferenceEquals(itemOfFlow, _runningItem)` with `itemOfFlow != null`. Run the tests.
Expected: `WhenFlowOfFinishedItemCallsWorker_ThenWorkIsQueuedBehindRunningItem` fails. Restore the condition.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage/Internal/StorageWorker.cs src/HomeBlaze/HomeBlaze.Storage.Tests/StorageWorkerTests.cs
git commit -m "refactor: add the sequential worker for storage changes"
```

---

### Task 3: File subjects and factory without System.IO

`GenericFile` and `JsonFile` read their size and time through `FileInfo` and the concrete container type. They switch to `IStorageContainer.GetBlobMetadataAsync`, which `MarkdownFile` already uses. The factory gets a method that builds a subject from JSON text that was already read, so the reconciler can hash and deserialize from one read.

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/GenericFile.cs` (method `OnFileChangedAsync`)
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Files/JsonFile.cs` (method `OnFileChangedAsync`)
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/FileSubjectFactory.cs` (method `CreateFromJsonBlobAsync`)
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/GenericFileTests.cs`

**Interfaces:**
- Consumes: `IStorageContainer.GetBlobMetadataAsync(string path, CancellationToken) : Task<BlobMetadata?>`, `record BlobMetadata(long Size, DateTime? LastModifiedUtc)`.
- Produces: `FileSubjectFactory.CreateFromJson(IStorageContainer storage, string path, string json) : IInterceptorSubject`. It returns the deserialized configurable subject, or a `JsonFile` for plain or invalid JSON. It never returns null and does not call `OnFileChangedAsync`.

- [ ] **Step 1: Write the failing tests**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/GenericFileTests.cs`:

```csharp
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using Moq;

namespace HomeBlaze.Storage.Tests;

public class GenericFileTests
{
    private static readonly DateTime Modified = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task WhenGenericFileChanges_ThenSizeAndTimeComeFromTheStorage()
    {
        // Arrange
        var storage = new Mock<IStorageContainer>();
        storage
            .Setup(s => s.GetBlobMetadataAsync("/Data/Report.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BlobMetadata(1234, Modified));
        var file = new GenericFile(storage.Object, "/Data/Report.pdf");

        // Act
        await file.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1234, file.FileSize);
        Assert.Equal(Modified, file.LastModified);
    }

    [Fact]
    public async Task WhenJsonFileChanges_ThenSizeAndTimeComeFromTheStorage()
    {
        // Arrange
        var storage = new Mock<IStorageContainer>();
        storage
            .Setup(s => s.GetBlobMetadataAsync("/Data/Values.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BlobMetadata(56, Modified));
        var file = new JsonFile(storage.Object, "/Data/Values.json");

        // Act
        await file.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.Equal(56, file.FileSize);
        Assert.Equal(Modified, file.LastModified);
    }

    [Fact]
    public async Task WhenStorageHasNoMetadata_ThenFileKeepsItsValues()
    {
        // Arrange
        var storage = new Mock<IStorageContainer>();
        var file = new GenericFile(storage.Object, "/Data/Gone.pdf") { FileSize = 7 };

        // Act
        await file.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.Equal(7, file.FileSize);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~GenericFileTests"`
Expected: the first two fail with size 0, because the storage is a mock and not a `FluentStorageContainer`.

- [ ] **Step 3: Implement**

In `src/HomeBlaze/HomeBlaze.Storage/Files/GenericFile.cs` and in `src/HomeBlaze/HomeBlaze.Storage/Files/JsonFile.cs`, replace the whole `OnFileChangedAsync` method with:

```csharp
    public async Task OnFileChangedAsync(CancellationToken cancellationToken)
    {
        var metadata = await Storage.GetBlobMetadataAsync(FullPath, cancellationToken);
        if (metadata != null)
        {
            FileSize = metadata.Size;
            LastModified = metadata.LastModifiedUtc ?? LastModified;
        }
    }
```

In `src/HomeBlaze/HomeBlaze.Storage/Internal/FileSubjectFactory.cs`, replace the whole `CreateFromJsonBlobAsync` method with these two methods:

```csharp
    private async Task<IInterceptorSubject?> CreateFromJsonBlobAsync(
        IBlobStorage client,
        IStorageContainer storage,
        Blob blob,
        CancellationToken cancellationToken)
    {
        var json = await client.ReadTextAsync(blob.FullPath, cancellationToken: cancellationToken);
        return CreateFromJson(storage, blob.FullPath, json);
    }

    /// <summary>
    /// Creates the subject of a JSON file from its text: the configurable subject it describes,
    /// or a <see cref="JsonFile"/> when the text is plain or invalid JSON.
    /// </summary>
    public IInterceptorSubject CreateFromJson(IStorageContainer storage, string path, string json)
    {
        try
        {
            var subject = _serializer.Deserialize(json);
            if (subject != null)
            {
                // All IConfigurable implementations are also IInterceptorSubject (via [InterceptorSubject] attribute)
                return (IInterceptorSubject)subject;
            }
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Failed to deserialize JSON subject from: {Path}", path);
        }

        return new JsonFile(storage, path);
    }
```

- [ ] **Step 4: Run the storage tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: all pass, including the existing file event tests.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Storage src/HomeBlaze/HomeBlaze.Storage.Tests/GenericFileTests.cs
git commit -m "refactor: read file subject metadata through the storage interface"
```

---

### Task 4: The reconciler, and the container on top of it

This is the core task. After it, the container has one way to change the tree: a pass, or a UI operation, both on the worker. The old watcher and coalescer stay for now and act as the trigger: each coalesced event runs a pass that names the event's paths. Task 6 replaces them.

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageReconciler.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/FluentStorageContainer.cs`
- Delete: `src/HomeBlaze/HomeBlaze.Storage/Internal/StoragePathRegistry.cs`
- Delete: `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageHierarchyManager.cs`
- Delete: `src/HomeBlaze/HomeBlaze.Storage/Internal/JsonSubjectSynchronizer.cs`
- Delete: `src/HomeBlaze/HomeBlaze.Storage.Tests/StoragePathRegistryTests.cs`
- Delete: `src/HomeBlaze/HomeBlaze.Storage.Tests/StorageHierarchyManagerTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.Storage.Tests/StorageTestBase.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Tests/GatedFile.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Tests/FluentStorageContainerFileEventTests.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/StorageReconcilerTests.cs`

**Interfaces:**
- Consumes: everything Task 1 to 3 produce.
- Produces:
  - `StorageReconciler(IBlobStorage client, FluentStorageContainer storage, FileSubjectFactory subjectFactory, ConfigurableSubjectSerializer serializer, StorageIndex index, ILogger? logger)`
  - `Task StorageReconciler.ReconcileAsync(IReadOnlySet<string> namedPaths, bool allNamed, CancellationToken cancellationToken)`
  - `void StorageReconciler.Apply(bool keepRemovedUntilAdded = false)`
  - `bool StorageReconciler.IsKeyFree(string path, IInterceptorSubject subject)`
  - `Task<StorageVersion> StorageReconciler.GetVersionAsync(string path, CancellationToken cancellationToken)`
  - `Task<string> StorageReconciler.ComputeHashAsync(string path, CancellationToken cancellationToken)`
  - `internal Task FluentStorageContainer.ReconcileAsync(IReadOnlySet<string>? namedPaths = null, bool allNamed = false)`. It never throws for a failed pass; it sets `Status` to `Error`.
  - `internal Task FluentStorageContainer.ProcessFileEventAsync(FileSystemEventArgs e)` stays until Task 6 and now runs a pass naming the event's paths.
  - Test base: `StorageTestBase` with `StorageDirectory`, `Context`, `ConnectAsync(bool enableFileWatching = false, Action<FluentStorageContainer>? configure = null)`, `WriteFile`, `GetFullPath`, `SerializeMotor`, `CreateMotor`, `Named(params string[])`.

- [ ] **Step 1: Extract the test base class**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/StorageTestBase.cs`:

```csharp
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A storage container on a temporary directory, wired with the application's interceptor context.
/// </summary>
public abstract class StorageTestBase : IDisposable
{
    protected static readonly TimeSpan WatcherTimeout = TimeSpan.FromSeconds(20);

    private readonly List<FluentStorageContainer> _storages = [];
    private ServiceProvider? _serviceProvider;

    protected StorageTestBase()
    {
        GatedFile.Reset();
    }

    protected DirectoryInfo StorageDirectory { get; } = Directory.CreateTempSubdirectory("homeblaze-storage-");

    /// <summary>The context of the storage connected last.</summary>
    protected IInterceptorSubjectContext? Context { get; private set; }

    protected async Task<FluentStorageContainer> ConnectAsync(
        bool enableFileWatching = false,
        Action<FluentStorageContainer>? configure = null)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(FluentStorageContainer).Assembly);
        typeProvider.AddAssembly(typeof(Samples.Motor).Assembly);
        typeProvider.AddAssembly(typeof(GatedFile).Assembly);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        // The application's context, so that the Children setters run change tracking, registry and lifecycle code.
        var services = new ServiceCollection();
        var context = SubjectContextFactory.Create(services);
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        services.AddSingleton(sp => new SubjectPathResolver(() => sp.GetRequiredService<RootManager>().Root));
        services.AddSingleton<MarkdownContentParser>();
        var serviceProvider = services.BuildServiceProvider();
        _serviceProvider = serviceProvider;
        Context = context;

        var storage = new FluentStorageContainer(
            typeRegistry,
            serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>(),
            serviceProvider)
        {
            ConnectionString = StorageDirectory.FullName,
            EnableFileWatching = enableFileWatching
        };

        ((IInterceptorSubject)storage).Context.AddFallbackContext(context);
        configure?.Invoke(storage);
        _storages.Add(storage);
        await storage.ConnectAsync(CancellationToken.None);
        return storage;
    }

    protected Samples.Motor CreateMotor(string name = "Motor")
        => new(_serviceProvider!.GetRequiredService<IInterceptorSubjectContext>()) { Name = name };

    protected static string SerializeMotor(string name)
    {
        // Serialized with services of its own, so the subject is not part of the storage under test.
        var typeProvider = new TypeProvider();
        typeProvider.AddAssembly(typeof(Samples.Motor).Assembly);

        var services = new ServiceCollection();
        services.AddSingleton(typeProvider);
        services.AddSingleton(new SubjectTypeRegistry(typeProvider));
        services.AddSingleton<IInterceptorSubjectContext>(InterceptorSubjectContext.Create());
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();

        using var serviceProvider = services.BuildServiceProvider();
        var motor = new Samples.Motor(serviceProvider.GetRequiredService<IInterceptorSubjectContext>()) { Name = name };
        return serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>().Serialize(motor);
    }

    protected string GetFullPath(string relativePath)
        => Path.Combine(StorageDirectory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

    protected void WriteFile(string relativePath, string content = "content")
    {
        var fullPath = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    protected static IReadOnlySet<string> Named(params string[] relativePaths)
        => relativePaths.ToHashSet(StringComparer.Ordinal);

    public void Dispose()
    {
        foreach (var storage in _storages)
        {
            storage.Dispose();
        }

        _serviceProvider?.Dispose();
        StorageDirectory.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }
}
```

In `src/HomeBlaze/HomeBlaze.Storage.Tests/FluentStorageContainerFileEventTests.cs`:

- Change the class declaration to `public class FluentStorageContainerFileEventTests : StorageTestBase`.
- Delete the members that the base class now has: the fields `WatcherTimeout`, `_directory`, `_storages`, `_serviceProvider`, the constructor, and the methods `ConnectAsync`, `SerializeMotor`, `GetFullPath`, `WriteFile` and `Dispose`.
- Keep the helpers `Renamed` and `Event`. In `Renamed`, replace `_directory.FullName` with `StorageDirectory.FullName`.
- Replace every `new Samples.Motor(_serviceProvider!.GetRequiredService<IInterceptorSubjectContext>()) { Name = "Added" }` with `CreateMotor("Added")`, and every `new Samples.Motor(_serviceProvider!.GetRequiredService<IInterceptorSubjectContext>())` with `CreateMotor()`.

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: all pass, nothing else changed yet.

Commit:

```bash
git add src/HomeBlaze/HomeBlaze.Storage.Tests
git commit -m "test: share the storage container test setup"
```

- [ ] **Step 2: Extend the pausable test file**

In `src/HomeBlaze/HomeBlaze.Storage.Tests/GatedFile.cs`, add a load counter and a way to fail a load. Replace the `PauseNextLoad`, `Reset` and `OnFileChangedAsync` members with:

```csharp
    private static int _loadCount;
    private static int _failNextLoad;

    /// <summary>The number of loads since the last <see cref="Reset"/>.</summary>
    public static int LoadCount => Volatile.Read(ref _loadCount);

    /// <summary>
    /// Makes the next load pause until the returned gate is released. Loads without a gate run through.
    /// </summary>
    public static Gate PauseNextLoad()
    {
        var gate = new Gate();
        Gates.Enqueue(gate);
        return gate;
    }

    /// <summary>
    /// Makes the next load throw.
    /// </summary>
    public static void FailNextLoad() => Volatile.Write(ref _failNextLoad, 1);

    public static void Reset()
    {
        Gates.Clear();
        Volatile.Write(ref _loadCount, 0);
        Volatile.Write(ref _failNextLoad, 0);
    }

    public async Task OnFileChangedAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCount);
        if (Interlocked.Exchange(ref _failNextLoad, 0) == 1)
        {
            throw new InvalidOperationException("The load was made to fail.");
        }

        await using var stream = await ReadAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(cancellationToken);

        if (Gates.TryDequeue(out var gate))
        {
            await gate.PauseAsync();
        }

        Content = content;
    }
```

- [ ] **Step 3: Write the failing reconciler tests**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/StorageReconcilerTests.cs`:

```csharp
using HomeBlaze.Storage.Files;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Storage.Tests;

public class StorageReconcilerTests : StorageTestBase
{
    [Fact]
    public async Task WhenNothingChanged_ThenPassLoadsNoFile()
    {
        // Arrange
        WriteFile("Data.gated");
        var storage = await ConnectAsync();
        var loadCountAfterStartup = GatedFile.LoadCount;

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(1, loadCountAfterStartup);
        Assert.Equal(1, GatedFile.LoadCount);
    }

    [Fact]
    public async Task WhenFileIsNamedButUnchanged_ThenItIsNotReloaded()
    {
        // Arrange
        WriteFile("Data.gated");
        var storage = await ConnectAsync();

        // Act
        await storage.ReconcileAsync(Named("Data.gated"));

        // Assert
        Assert.Equal(1, GatedFile.LoadCount);
    }

    [Fact]
    public async Task WhenFileChangesSize_ThenUnnamedPassReloadsIt()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectAsync();
        var file = Assert.IsType<GatedFile>(storage.Children["Data.gated"]);
        WriteFile("Data.gated", "second version");

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Same(file, storage.Children["Data.gated"]);
        Assert.Equal("second version", file.Content);
    }

    [Fact]
    public async Task WhenContentChangesWithSameSizeAndTime_ThenOnlyNamedPassReloadsIt()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectAsync();
        var file = Assert.IsType<GatedFile>(storage.Children["Data.gated"]);
        var originalTime = File.GetLastWriteTimeUtc(GetFullPath("Data.gated"));
        WriteFile("Data.gated", "other");
        File.SetLastWriteTimeUtc(GetFullPath("Data.gated"), originalTime);

        // Act
        await storage.ReconcileAsync();
        var contentAfterUnnamedPass = file.Content;
        await storage.ReconcileAsync(Named("Data.gated"));

        // Assert
        Assert.Equal("first", contentAfterUnnamedPass);
        Assert.Equal("other", file.Content);
    }

    [Fact]
    public async Task WhenEveryFileIsNamed_ThenChangedContentIsReloaded()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectAsync();
        var file = Assert.IsType<GatedFile>(storage.Children["Data.gated"]);
        var originalTime = File.GetLastWriteTimeUtc(GetFullPath("Data.gated"));
        WriteFile("Data.gated", "other");
        File.SetLastWriteTimeUtc(GetFullPath("Data.gated"), originalTime);

        // Act
        await storage.ReconcileAsync(allNamed: true);

        // Assert
        Assert.Equal("other", file.Content);
    }

    [Fact]
    public async Task WhenJsonSubjectFileIsRenamed_ThenInstanceIsKeptAndNeverDetached()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        var detaches = CountDetachesOf(motor);
        File.Move(GetFullPath("Motor.json"), GetFullPath("Engine.json"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["Engine"], storage.Children.Keys);
        Assert.Same(motor, storage.Children["Engine"]);
        Assert.Equal(0, detaches.Count);
    }

    [Fact]
    public async Task WhenFolderOfJsonSubjectIsRenamed_ThenInstanceIsKeptAndNeverDetached()
    {
        // Arrange
        WriteFile("Devices/Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(((VirtualFolder)storage.Children["Devices"]).Children["Motor"]);
        var detaches = CountDetachesOf(motor);
        Directory.Move(GetFullPath("Devices"), GetFullPath("Machines"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["Machines"], storage.Children.Keys);
        Assert.Same(motor, ((VirtualFolder)storage.Children["Machines"]).Children["Motor"]);
        Assert.Equal(0, detaches.Count);
    }

    [Fact]
    public async Task WhenTwoJsonSubjectsWithSameContentAreRenamed_ThenNewInstancesAreCreated()
    {
        // Arrange
        WriteFile("First.json", SerializeMotor("Same"));
        WriteFile("Second.json", SerializeMotor("Same"));
        var storage = await ConnectAsync();
        var first = storage.Children["First"];
        var second = storage.Children["Second"];
        File.Move(GetFullPath("First.json"), GetFullPath("Third.json"));
        File.Move(GetFullPath("Second.json"), GetFullPath("Fourth.json"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["Fourth", "Third"], storage.Children.Keys.Order());
        Assert.DoesNotContain(storage.Children.Values, subject => ReferenceEquals(subject, first) || ReferenceEquals(subject, second));
    }

    [Fact]
    public async Task WhenJsonSubjectIsRenamedAndEdited_ThenNewInstanceIsCreated()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var motor = storage.Children["Motor"];
        File.Delete(GetFullPath("Motor.json"));
        WriteFile("Engine.json", SerializeMotor("Edited"));

        // Act
        await storage.ReconcileAsync();

        // Assert
        var engine = Assert.IsType<Samples.Motor>(storage.Children["Engine"]);
        Assert.NotSame(motor, engine);
        Assert.Equal("Edited", engine.Name);
    }

    [Fact]
    public async Task WhenKeyBecomesFree_ThenBlockedFileIsPlaced()
    {
        // Arrange
        WriteFile("Docs/Readme.md");
        var storage = await ConnectAsync();
        WriteFile("Docs.json", SerializeMotor("Blocked"));
        await storage.ReconcileAsync();
        var folderWhileBlocked = storage.Children["Docs"];
        Directory.Delete(GetFullPath("Docs"), recursive: true);

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.IsType<VirtualFolder>(folderWhileBlocked);
        Assert.Equal(["Docs"], storage.Children.Keys);
        Assert.IsType<Samples.Motor>(storage.Children["Docs"]);
    }

    [Fact]
    public async Task WhenNewEntryClashesWithPlacedOne_ThenPlacedOneKeepsItsKey()
    {
        // Arrange
        WriteFile("Docs.json", SerializeMotor("First"));
        var storage = await ConnectAsync();
        var motor = storage.Children["Docs"];
        WriteFile("Docs/Readme.md");

        // Act
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(["Docs"], storage.Children.Keys);
        Assert.Same(motor, storage.Children["Docs"]);
    }

    [Fact]
    public async Task WhenFileFailsToLoad_ThenItIsRetriedOnlyWhenItChanges()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        GatedFile.FailNextLoad();
        var storage = await ConnectAsync();
        var childrenAfterFailure = storage.Children.Keys.ToList();

        // Act
        await storage.ReconcileAsync();
        var loadCountAfterIdlePass = GatedFile.LoadCount;
        WriteFile("Data.gated", "second version");
        await storage.ReconcileAsync();

        // Assert
        Assert.Empty(childrenAfterFailure);
        Assert.Equal(1, loadCountAfterIdlePass);
        var file = Assert.IsType<GatedFile>(storage.Children["Data.gated"]);
        Assert.Equal("second version", file.Content);
    }

    [Fact]
    public async Task WhenFileIsRenamedToIgnoredName_ThenItsSubjectIsRemoved()
    {
        // Arrange
        WriteFile("Home.md");
        var storage = await ConnectAsync();
        File.Move(GetFullPath("Home.md"), GetFullPath("Home.md~"));

        // Act
        await storage.ReconcileAsync(Named("Home.md", "Home.md~"));

        // Assert
        Assert.Empty(storage.Children);
    }

    [Fact]
    public async Task WhenFilesDifferOnlyInCasing_ThenBothArePlaced()
    {
        // Arrange
        WriteFile("readme.md", "lower");
        if (File.Exists(GetFullPath("README.md")))
        {
            // The volume ignores case, so the second file cannot exist next to the first one.
            return;
        }

        WriteFile("README.md", "upper");

        // Act
        var storage = await ConnectAsync();

        // Assert
        Assert.Equal(["README.md", "readme.md"], storage.Children.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WhenUiOperationArrivesDuringPass_ThenItRunsAfterThePass()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Slow.gated");
        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();

        // Act
        var add = storage.AddSubjectAsync("Motor.json", CreateMotor(), CancellationToken.None);
        var addWasWaiting = !add.IsCompleted;
        gate.Release();
        await Task.WhenAll(pass, add);

        // Assert
        Assert.True(addWasWaiting);
        Assert.Equal(["Motor", "Slow.gated"], storage.Children.Keys.Order());
    }

    [Fact]
    public async Task WhenConfigurationIsWritten_ThenNamedPassDoesNotReloadTheSubject()
    {
        // Arrange
        WriteFile("Motor.json", SerializeMotor("From file"));
        var storage = await ConnectAsync();
        var motor = Assert.IsType<Samples.Motor>(storage.Children["Motor"]);
        motor.Name = "Saved";
        await storage.WriteConfigurationAsync(motor, CancellationToken.None);
        motor.Name = "Only in memory";

        // Act
        await storage.ReconcileAsync(Named("Motor.json"));

        // Assert
        Assert.Contains("Saved", File.ReadAllText(GetFullPath("Motor.json")));
        Assert.Equal("Only in memory", motor.Name);
    }

    [Fact]
    public async Task WhenBlobIsWritten_ThenSubjectReloadsOnceAndNamedPassDoesNotReloadIt()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectAsync();
        var file = Assert.IsType<GatedFile>(storage.Children["Data.gated"]);

        // Act
        using (var content = new MemoryStream("second version"u8.ToArray()))
        {
            await storage.WriteBlobAsync("Data.gated", content, CancellationToken.None);
        }

        var loadCountAfterWrite = GatedFile.LoadCount;
        await storage.ReconcileAsync(Named("Data.gated"));

        // Assert
        Assert.Equal("second version", file.Content);
        Assert.Equal(2, loadCountAfterWrite);
        Assert.Equal(2, GatedFile.LoadCount);
    }

    [Fact]
    public async Task WhenSubjectIsDeleted_ThenFileAndSubjectAreGone()
    {
        // Arrange
        WriteFile("Docs/Motor.json", SerializeMotor("Pump"));
        var storage = await ConnectAsync();
        var docs = Assert.IsType<VirtualFolder>(storage.Children["Docs"]);

        // Act
        await storage.DeleteSubjectAsync(docs.Children["Motor"], CancellationToken.None);

        // Assert
        Assert.Empty(docs.Children);
        Assert.False(File.Exists(GetFullPath("Docs/Motor.json")));
    }

    private DetachCounter CountDetachesOf(IInterceptorSubject subject)
    {
        var counter = new DetachCounter(subject);
        Context!.AddService<ILifecycleHandler>(counter);
        return counter;
    }

    private sealed class DetachCounter(IInterceptorSubject subject) : ILifecycleHandler
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextDetach && ReferenceEquals(change.Subject, subject))
            {
                Interlocked.Increment(ref _count);
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~StorageReconcilerTests"`
Expected: build error, `FluentStorageContainer.ReconcileAsync` does not exist.

- [ ] **Step 5: Implement the reconciler**

Create `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageReconciler.cs`:

```csharp
using System.Text;
using FluentStorage.Blobs;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Brings the subject tree of a storage in line with the storage itself. A pass lists the storage once,
/// compares the listing with the index of what was applied last, loads what is new or changed, and assigns
/// the children of each changed folder once.
/// </summary>
/// <remarks>Not thread-safe. Every member runs on the <see cref="StorageWorker"/>.</remarks>
internal sealed class StorageReconciler
{
    private readonly IBlobStorage _client;
    private readonly FluentStorageContainer _storage;
    private readonly FileSubjectFactory _subjectFactory;
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly StorageIndex _index;
    private readonly ILogger? _logger;

    public StorageReconciler(
        IBlobStorage client,
        FluentStorageContainer storage,
        FileSubjectFactory subjectFactory,
        ConfigurableSubjectSerializer serializer,
        StorageIndex index,
        ILogger? logger)
    {
        _client = client;
        _storage = storage;
        _subjectFactory = subjectFactory;
        _serializer = serializer;
        _index = index;
        _logger = logger;
    }

    /// <summary>
    /// Runs one pass.
    /// </summary>
    /// <param name="namedPaths">Paths an event named. They are compared by content even when their version is unchanged.</param>
    /// <param name="allNamed">Treats every file as named, for when events were lost.</param>
    /// <param name="cancellationToken">Cancels the pass between two files.</param>
    public async Task ReconcileAsync(IReadOnlySet<string> namedPaths, bool allNamed, CancellationToken cancellationToken)
    {
        var listing = await ListAsync(cancellationToken);
        var movableSubjects = RemoveMissingEntries(listing);

        var hasMovedSubjects = false;
        foreach (var listed in listing.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (listed.IsFolder)
            {
                if (!_index.TryGet(listed.Path, out _))
                {
                    _index.Set(new StorageEntry { Path = listed.Path, IsFolder = true });
                }

                continue;
            }

            try
            {
                var isNamed = allNamed || namedPaths.Contains(listed.Path);
                if (!_index.TryGet(listed.Path, out var entry) || NeedsLoad(entry, listed, isNamed))
                {
                    hasMovedSubjects |= await AddAsync(listed, movableSubjects, cancellationToken);
                }
                else if (entry.Subject != null && (entry.Version != listed.Version || isNamed))
                {
                    await RefreshAsync(entry, listed, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger?.LogWarning(exception, "Failed to load: {Path}", listed.Path);
                RecordFailure(listed);
            }
        }

        Apply(hasMovedSubjects);
    }

    /// <summary>
    /// Builds the children of every folder from the index and assigns those that changed.
    /// </summary>
    /// <param name="keepRemovedUntilAdded">
    /// Assigns in two steps: first everything that is added, then everything that is removed. Needed when a
    /// subject moved, because an assignment detaches before it attaches and would stop and restart the subject.
    /// </param>
    public void Apply(bool keepRemovedUntilAdded = false)
    {
        var childrenByFolder = new Dictionary<string, Dictionary<string, IInterceptorSubject>>(StringComparer.Ordinal)
        {
            [StoragePath.Root] = new()
        };

        // Placed entries go first, so one that is in the tree never loses its key to a newcomer.
        // Within each group a folder comes before what is inside it.
        var candidates = _index.Entries
            .Where(entry => entry.IsFolder || entry.Subject != null)
            .OrderBy(entry => entry.State != StorageEntryState.Placed)
            .ThenBy(entry => StoragePath.GetDepth(entry.Path))
            .ThenBy(entry => !entry.IsFolder)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .ToList();

        foreach (var entry in candidates)
        {
            var key = entry.IsFolder ? StoragePath.GetName(entry.Path) : GetChildKey(entry.Path, entry.Subject!);
            if (!childrenByFolder.TryGetValue(StoragePath.GetParent(entry.Path), out var siblings) || siblings.ContainsKey(key))
            {
                if (entry.State != StorageEntryState.KeyTaken)
                {
                    _logger?.LogWarning("Skipping '{Path}': key \"{Key}\" is taken or its folder is not placed", entry.Path, key);
                }

                entry.State = StorageEntryState.KeyTaken;
                entry.Key = key;
                entry.Subject = null;
                continue;
            }

            if (entry.IsFolder)
            {
                entry.Subject ??= new VirtualFolder(_storage, entry.Path + "/");
                childrenByFolder[entry.Path] = new Dictionary<string, IInterceptorSubject>();
            }

            siblings.Add(key, entry.Subject!);
            entry.State = StorageEntryState.Placed;
            entry.Key = key;
        }

        // Deepest first, the root last: a new folder is attached with its children already in place.
        var folders = childrenByFolder.Keys
            .OrderByDescending(path => path.Length == 0 ? -1 : StoragePath.GetDepth(path))
            .ToList();

        if (keepRemovedUntilAdded)
        {
            foreach (var folder in folders)
            {
                var union = new Dictionary<string, IInterceptorSubject>(GetChildren(folder));
                foreach (var (key, subject) in childrenByFolder[folder])
                {
                    union[key] = subject;
                }

                AssignChildren(folder, union);
            }
        }

        foreach (var folder in folders)
        {
            AssignChildren(folder, childrenByFolder[folder]);
        }
    }

    /// <summary>
    /// Checks whether a new subject at the path could be placed: its folder is placed or can be created, and
    /// its key in that folder is free.
    /// </summary>
    public bool IsKeyFree(string path, IInterceptorSubject subject)
    {
        var key = GetChildKey(path, subject);
        var parent = StoragePath.GetParent(path);

        // A folder that does not exist yet is created with the file, so its own name has to be free where it starts.
        while (parent.Length > 0 && !_index.TryGet(parent, out _))
        {
            key = StoragePath.GetName(parent);
            parent = StoragePath.GetParent(parent);
        }

        return IsKeyFree(parent, key);
    }

    public async Task<StorageVersion> GetVersionAsync(string path, CancellationToken cancellationToken)
    {
        var blobs = await _client.GetBlobsAsync([path], cancellationToken);
        var blob = blobs.FirstOrDefault();
        return blob == null ? default : new StorageVersion(blob.Size ?? 0, blob.LastModificationTime);
    }

    public async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
        => StorageHash.Compute(await ReadAsync(path, cancellationToken));

    private static string GetChildKey(string path, IInterceptorSubject subject)
        => subject is IConfigurable && IsJson(path)
            ? Path.GetFileNameWithoutExtension(path)
            : StoragePath.GetName(path);

    private static bool IsJson(string path)
        => Path.GetExtension(path).Equals(FileExtensions.Json, StringComparison.OrdinalIgnoreCase);

    private bool IsKeyFree(string parent, string key)
    {
        if (parent.Length > 0 &&
            !(_index.TryGet(parent, out var folder) && folder is { IsFolder: true, State: StorageEntryState.Placed }))
        {
            return false;
        }

        foreach (var entry in _index.Entries)
        {
            if (entry.State == StorageEntryState.Placed && entry.Key == key && StoragePath.GetParent(entry.Path) == parent)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<Dictionary<string, StorageListing>> ListAsync(CancellationToken cancellationToken)
    {
        var blobs = await _client.ListAsync(recurse: true, cancellationToken: cancellationToken);

        var listing = new Dictionary<string, StorageListing>(StringComparer.Ordinal);
        foreach (var blob in blobs)
        {
            var path = StoragePath.Normalize(blob.FullPath);
            if (path.Length == 0 || StoragePathFilter.IsIgnored(path))
            {
                continue;
            }

            var version = blob.IsFolder ? default : new StorageVersion(blob.Size ?? 0, blob.LastModificationTime);
            listing[path] = new StorageListing(path, blob.IsFolder, version);
        }

        // Not every backend lists the folders of its files.
        foreach (var path in listing.Keys.ToList())
        {
            for (var parent = StoragePath.GetParent(path);
                 parent.Length > 0 && !listing.ContainsKey(parent);
                 parent = StoragePath.GetParent(parent))
            {
                listing[parent] = new StorageListing(parent, true, default);
            }
        }

        return listing;
    }

    /// <summary>
    /// Removes the entries that are gone or changed kind, and returns the configurable subjects among them
    /// by content hash: a new file with the same content is the same subject under a new path.
    /// </summary>
    private Dictionary<string, List<IInterceptorSubject>> RemoveMissingEntries(Dictionary<string, StorageListing> listing)
    {
        var movableSubjects = new Dictionary<string, List<IInterceptorSubject>>(StringComparer.Ordinal);
        foreach (var entry in _index.Entries.ToList())
        {
            if (listing.TryGetValue(entry.Path, out var listed) && listed.IsFolder == entry.IsFolder)
            {
                continue;
            }

            _index.Remove(entry.Path);

            if (entry is { IsFolder: false, Subject: IConfigurable, Hash: not null })
            {
                if (!movableSubjects.TryGetValue(entry.Hash, out var subjects))
                {
                    movableSubjects[entry.Hash] = subjects = [];
                }

                subjects.Add(entry.Subject);
            }
        }

        return movableSubjects;
    }

    /// <summary>
    /// Decides whether an entry without a subject is loaded again.
    /// </summary>
    private bool NeedsLoad(StorageEntry entry, StorageListing listed, bool isNamed)
    {
        if (entry.Subject != null)
        {
            return false;
        }

        if (entry.Version != listed.Version || isNamed)
        {
            return true;
        }

        return entry is { State: StorageEntryState.KeyTaken, Key: not null } &&
               IsKeyFree(StoragePath.GetParent(entry.Path), entry.Key);
    }

    /// <returns>True when an existing subject was moved to the path instead of creating one.</returns>
    private async Task<bool> AddAsync(
        StorageListing listed,
        Dictionary<string, List<IInterceptorSubject>> movableSubjects,
        CancellationToken cancellationToken)
    {
        var blob = new Blob(listed.Path) { Size = listed.Version.Size, LastModificationTime = listed.Version.Modified };
        var entry = new StorageEntry { Path = listed.Path, IsFolder = false, Version = listed.Version };
        var isMoved = false;

        if (IsJson(listed.Path))
        {
            var content = await ReadAsync(listed.Path, cancellationToken);
            entry.Hash = StorageHash.Compute(content);

            // More than one candidate cannot be told apart, so none of them is moved.
            if (movableSubjects.Remove(entry.Hash, out var candidates) && candidates.Count == 1)
            {
                entry.Subject = candidates[0];
                isMoved = true;
            }
            else
            {
                entry.Subject = _subjectFactory.CreateFromJson(_storage, blob.FullPath, DecodeText(content));
                if (entry.Subject is IStorageFile file)
                {
                    await file.OnFileChangedAsync(cancellationToken);
                }
            }
        }
        else
        {
            entry.Subject = await _subjectFactory.CreateFromBlobAsync(_client, _storage, blob, cancellationToken)
                ?? throw new InvalidOperationException($"No subject could be created for '{listed.Path}'.");

            if (entry.Subject is not GenericFile)
            {
                entry.Hash = StorageHash.Compute(await ReadAsync(listed.Path, cancellationToken));
            }
        }

        _index.Set(entry);
        return isMoved;
    }

    private async Task RefreshAsync(StorageEntry entry, StorageListing listed, CancellationToken cancellationToken)
    {
        if (entry.Subject is GenericFile genericFile)
        {
            // Holds nothing but size and time, so there is no content to compare.
            if (entry.Version != listed.Version)
            {
                await genericFile.OnFileChangedAsync(cancellationToken);
                entry.Version = listed.Version;
            }

            return;
        }

        var content = await ReadAsync(listed.Path, cancellationToken);
        var hash = StorageHash.Compute(content);
        if (hash != entry.Hash)
        {
            if (entry.Subject is IStorageFile file)
            {
                await file.OnFileChangedAsync(cancellationToken);
            }
            else if (entry.Subject is IConfigurable configurable)
            {
                _serializer.UpdateConfiguration(entry.Subject, DecodeText(content));
                await configurable.ApplyConfigurationAsync(cancellationToken);
            }

            _logger?.LogInformation("Reloaded: {Path}", listed.Path);
        }
        else if (entry.Subject is IStorageFile unchangedFile)
        {
            unchangedFile.FileSize = listed.Version.Size;
            unchangedFile.LastModified = listed.Version.Modified?.UtcDateTime ?? unchangedFile.LastModified;
        }

        entry.Hash = hash;
        entry.Version = listed.Version;
    }

    private void RecordFailure(StorageListing listed)
    {
        // A subject that is in the tree stays there with what it has. Recording the version makes the next
        // pass wait for another change instead of failing again on every pass.
        if (_index.TryGet(listed.Path, out var entry) && entry.Subject != null)
        {
            entry.Version = listed.Version;
            return;
        }

        _index.Set(new StorageEntry
        {
            Path = listed.Path,
            IsFolder = false,
            Version = listed.Version,
            State = StorageEntryState.Failed
        });
    }

    private Dictionary<string, IInterceptorSubject> GetChildren(string folder)
        => folder.Length == 0
            ? _storage.Children
            : ((VirtualFolder)GetEntry(folder).Subject!).Children;

    private void AssignChildren(string folder, Dictionary<string, IInterceptorSubject> children)
    {
        var current = GetChildren(folder);
        if (current.Count == children.Count &&
            children.All(pair => current.TryGetValue(pair.Key, out var subject) && ReferenceEquals(subject, pair.Value)))
        {
            return;
        }

        // A copy, because the union of the first step is not what the folder holds after the second.
        var assigned = new Dictionary<string, IInterceptorSubject>(children);
        if (folder.Length == 0)
        {
            _storage.Children = assigned;
        }
        else
        {
            ((VirtualFolder)GetEntry(folder).Subject!).Children = assigned;
        }
    }

    private StorageEntry GetEntry(string path)
        => _index.TryGet(path, out var entry)
            ? entry
            : throw new InvalidOperationException($"No entry for '{path}'.");

    private async Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = await _client.OpenReadAsync(path, cancellationToken)
            ?? throw new FileNotFoundException($"'{path}' is not in the storage.", path);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static string DecodeText(byte[] content)
    {
        // A reader, not Encoding.GetString: it drops a byte order mark, which the JSON parser rejects.
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private readonly record struct StorageListing(string Path, bool IsFolder, StorageVersion Version);
}
```

- [ ] **Step 6: Rewrite the container onto the reconciler**

In `src/HomeBlaze/HomeBlaze.Storage/FluentStorageContainer.cs`:

Add `using System.Text;` to the usings.

Replace the fields from `private readonly StoragePathRegistry _pathRegistry` down to `private JsonSubjectSynchronizer? _jsonSyncHelper;` (this includes the comment block and the `_hierarchyLock` field) with:

```csharp
    private static readonly IReadOnlySet<string> NoPaths = new HashSet<string>();

    private readonly FileSubjectFactory _subjectFactory;
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly ILogger<FluentStorageContainer>? _logger;

    // The index, the reconciler and the tree below Children are only touched on the worker.
    private StorageIndex _index = new();
    private StorageReconciler? _reconciler;
    private StorageWorker? _worker;

    private StorageFileWatcher? _fileWatcher;
    private string? _storageDirectory;

    private StorageWorker Worker => _worker
        ?? throw new InvalidOperationException("Storage not connected");
```

In the constructor, delete the line `_hierarchyManager = new StorageHierarchyManager(logger);`.

Replace the whole `ConnectAsync` method with:

```csharp
    /// <summary>
    /// Initializes the storage client based on configuration and loads the subject tree.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var isInMemory = StorageType == "inmemory";
        if (!isInMemory && string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("ConnectionString is not configured");

        Status = StorageStatus.Initializing;
        try
        {
            // A reconnect starts from scratch. A watcher or worker left running would keep changing the tree.
            _fileWatcher?.Dispose();
            _fileWatcher = null;
            _worker?.Dispose();
            var previousClient = _client;

            _storageDirectory = isInMemory ? null : ResolveStorageDirectory();
            _client = StorageType switch
            {
                "disk" or "filesystem" => StorageFactory.Blobs.DirectoryFiles(_storageDirectory!),
                "inmemory" => StorageFactory.Blobs.InMemory(),
                _ => throw new NotSupportedException($"Storage type '{StorageType}' is not supported")
            };
            previousClient?.Dispose();

            _index = new StorageIndex();
            var reconciler = new StorageReconciler(_client, this, _subjectFactory, _serializer, _index, _logger);
            _reconciler = reconciler;
            _worker = new StorageWorker(_logger);

            _logger?.LogInformation("Connected to storage: {Type} at {Path}", StorageType,
                isInMemory ? "(in-memory)" : _storageDirectory);

            // Started before the first pass, so a change during startup leads to another pass.
            if (EnableFileWatching && !isInMemory)
            {
                StartFileWatching();
            }

            await _worker.RunAsync(token => reconciler.ReconcileAsync(NoPaths, allNamed: false, token), cancellationToken);

            Status = StorageStatus.Connected;
            _logger?.LogInformation("Storage loaded: {Count} entries.", _index.Count);
        }
        catch (Exception ex)
        {
            Status = StorageStatus.Error;
            _logger?.LogError(ex, "Failed to connect to storage");
            throw;
        }
    }
```

Delete these members completely: `ScanAsync`, `ResyncAsync`, `IsIgnored`, `SyncPathAsync`, `SyncDirectoryAsync`, `SyncBlobsAsync`, `EnsureFolder`, `RemoveMissingChildren`, `AddFileAsync`, `TryComputeJsonHashAsync`, `NotifyFileChangedAsync`, `RemoveIfMissing`, `Remove`, `AddToHierarchy`, `RemoveFromHierarchy`, `RemoveFolder`.

Replace `StartFileWatching` and `ProcessFileEventAsync` with:

```csharp
    private void StartFileWatching()
    {
        _fileWatcher = new StorageFileWatcher(
            _storageDirectory!,
            ProcessFileEventAsync,
            () => ReconcileAsync(allNamed: true),
            _logger);

        _fileWatcher.Start();
    }

    internal Task ProcessFileEventAsync(FileSystemEventArgs e)
    {
        var namedPaths = new HashSet<string>(StringComparer.Ordinal) { GetRelativePath(e.FullPath) };
        if (e is RenamedEventArgs renamed)
        {
            namedPaths.Add(GetRelativePath(renamed.OldFullPath));
        }

        return ReconcileAsync(namedPaths);
    }

    private string GetRelativePath(string fileSystemPath)
        => Path.GetRelativePath(_storageDirectory!, fileSystemPath).Replace('\\', '/');

    /// <summary>
    /// Runs one pass on the worker. A pass that fails is logged and sets <see cref="Status"/> to
    /// <see cref="StorageStatus.Error"/>, and the next pass tries again.
    /// </summary>
    internal Task ReconcileAsync(IReadOnlySet<string>? namedPaths = null, bool allNamed = false)
    {
        var worker = _worker;
        var reconciler = _reconciler;
        if (worker == null || reconciler == null)
        {
            return Task.CompletedTask;
        }

        return worker.RunAsync(async token =>
        {
            try
            {
                await reconciler.ReconcileAsync(namedPaths ?? NoPaths, allNamed, token);
                if (Status != StorageStatus.Connected)
                {
                    Status = StorageStatus.Connected;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Status = StorageStatus.Error;
                _logger?.LogError(exception, "Failed to reconcile the storage, the next pass tries again");
            }
        }, CancellationToken.None);
    }
```

Replace `WriteConfigurationAsync` and `AddSubjectAsync` with:

```csharp
    /// <summary>
    /// IConfigurationWriter - called by ConfigurationManager background thread.
    /// </summary>
    public Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var worker = _worker;
        if (worker == null)
            return Task.FromResult(false);

        return worker.RunAsync(async token =>
        {
            if (!_index.TryGetPath(subject, out var path) || !_index.TryGet(path, out var entry))
                return false;

            await WriteAndRecordAsync(entry, Encoding.UTF8.GetBytes(_subjectFactory.Serialize(subject)), token);

            _logger?.LogDebug("Saved subject to storage: {Path}", path);
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Adds a new subject to storage at the specified path.
    /// </summary>
    /// <exception cref="ArgumentException">The path is hidden or temporary and would not be loaded again.</exception>
    /// <exception cref="InvalidOperationException">A file exists at the path, or its key in the hierarchy is taken.</exception>
    public async Task AddSubjectAsync(string path, IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var relativePath = StoragePath.Normalize(path);
        if (StoragePathFilter.IsIgnored(relativePath))
            throw new ArgumentException($"A subject at '{path}' would not be loaded again: the path is hidden or temporary.", nameof(path));

        await Worker.RunAsync(async token =>
        {
            if (_index.TryGet(relativePath, out _) || await Client.ExistsAsync(relativePath, token))
                throw new InvalidOperationException($"A file already exists at '{path}'.");

            if (!_reconciler!.IsKeyFree(relativePath, subject))
                throw new InvalidOperationException($"A subject already exists at '{path}'.");

            var entry = new StorageEntry { Path = relativePath, IsFolder = false, Subject = subject };
            await WriteAndRecordAsync(entry, Encoding.UTF8.GetBytes(_subjectFactory.Serialize(subject)), token);

            _index.EnsureFolders(relativePath);
            _index.Set(entry);
            _reconciler.Apply();

            _logger?.LogInformation("Added subject to storage: {Path}", relativePath);
        }, cancellationToken);
    }

    // Recording version and hash is what keeps the event that this write raises from reloading the subject.
    private async Task WriteAndRecordAsync(StorageEntry entry, byte[] content, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content);
        await Client.WriteAsync(entry.Path, stream, append: false, cancellationToken: cancellationToken);

        entry.Hash = StorageHash.Compute(content);
        entry.Version = await _reconciler!.GetVersionAsync(entry.Path, cancellationToken);
    }
```

Replace `WriteBlobAsync`, `DeleteBlobAsync` and `DeleteSubjectAsync` with:

```csharp
    /// <summary>
    /// IStorageContainer - Writes a blob to storage.
    /// </summary>
    public Task WriteBlobAsync(string path, Stream content, CancellationToken cancellationToken)
        => Worker.RunAsync(async token =>
        {
            var relativePath = StoragePath.Normalize(path);
            await Client.WriteAsync(relativePath, content, append: false, cancellationToken: token);
            _logger?.LogDebug("Wrote blob to storage: {Path}", relativePath);

            // A file without a subject is picked up by the pass that its event triggers.
            if (!_index.TryGet(relativePath, out var entry) || entry.Subject == null)
                return;

            if (entry.Subject is IStorageFile file)
            {
                await file.OnFileChangedAsync(token);
            }

            entry.Version = await _reconciler!.GetVersionAsync(relativePath, token);
            entry.Hash = entry.Subject is GenericFile ? null : await _reconciler.ComputeHashAsync(relativePath, token);
        }, cancellationToken);

    /// <summary>
    /// IStorageContainer - Deletes a blob from storage and removes from Children.
    /// </summary>
    public Task DeleteBlobAsync(string path, CancellationToken cancellationToken)
        => Worker.RunAsync(token => DeleteEntryAsync(StoragePath.Normalize(path), token), cancellationToken);

    /// <summary>
    /// IStorageContainer - Deletes a subject by finding its path in the index.
    /// </summary>
    public Task DeleteSubjectAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
        => Worker.RunAsync(token => _index.TryGetPath(subject, out var path)
            ? DeleteEntryAsync(path, token)
            : throw new InvalidOperationException("Subject not found in storage registry"), cancellationToken);

    private async Task DeleteEntryAsync(string relativePath, CancellationToken cancellationToken)
    {
        if (!_index.TryGet(relativePath, out var entry) || entry.IsFolder || entry.Subject == null)
        {
            _logger?.LogWarning("Cannot delete blob - no subject at: {Path}", relativePath);
            return;
        }

        await Client.DeleteAsync(relativePath, cancellationToken: cancellationToken);
        _index.Remove(relativePath);
        _reconciler!.Apply();

        _logger?.LogDebug("Deleted blob from storage: {Path}", relativePath);
    }
```

In `Dispose`, add `_worker?.Dispose();` after `_fileWatcher?.Dispose();`.

Add `using HomeBlaze.Storage.Files;` if `GenericFile` does not resolve. Remove usings that are no longer needed, the build fails on them otherwise.

Delete the files `Internal/StoragePathRegistry.cs`, `Internal/StorageHierarchyManager.cs`, `Internal/JsonSubjectSynchronizer.cs`, and the tests `StoragePathRegistryTests.cs` and `StorageHierarchyManagerTests.cs`:

```bash
git rm src/HomeBlaze/HomeBlaze.Storage/Internal/StoragePathRegistry.cs \
       src/HomeBlaze/HomeBlaze.Storage/Internal/StorageHierarchyManager.cs \
       src/HomeBlaze/HomeBlaze.Storage/Internal/JsonSubjectSynchronizer.cs \
       src/HomeBlaze/HomeBlaze.Storage.Tests/StoragePathRegistryTests.cs \
       src/HomeBlaze/HomeBlaze.Storage.Tests/StorageHierarchyManagerTests.cs
```

- [ ] **Step 7: Adapt the existing file event tests**

Events are no longer handled concurrently, so three tests in `src/HomeBlaze/HomeBlaze.Storage.Tests/FluentStorageContainerFileEventTests.cs` change.

Delete the test `WhenTwoEventsLoadSameNewFile_ThenSubjectHasContentOfLaterLoad`. Two loads of the same file can no longer overlap.

Replace `WhenFileIsDeletedWhileItsSubjectLoads_ThenNoSubjectIsAdded` with:

```csharp
    [Fact]
    public async Task WhenFileIsDeletedWhileItsSubjectLoads_ThenNextPassRemovesIt()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Data.gated");

        var gate = GatedFile.PauseNextLoad();
        var add = storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Data.gated"));
        await gate.WhenReachedAsync();

        File.Delete(GetFullPath("Data.gated"));
        var delete = storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Deleted, "Data.gated"));

        // Act
        gate.Release();
        await Task.WhenAll(add, delete);

        // Assert
        Assert.Empty(storage.Children);
    }
```

Replace `WhenFileIsAddedWhileStorageIsResynchronized_ThenFileIsKept` with:

```csharp
    [Fact]
    public async Task WhenFileIsAddedWhilePassRuns_ThenFileIsKept()
    {
        // Arrange
        var storage = await ConnectAsync();
        WriteFile("Slow.gated");

        var gate = GatedFile.PauseNextLoad();
        var pass = storage.ReconcileAsync();
        await gate.WhenReachedAsync();

        WriteFile("Added.md");
        var added = storage.ProcessFileEventAsync(Event(WatcherChangeTypes.Created, "Added.md"));

        // Act
        gate.Release();
        await Task.WhenAll(pass, added);

        // Assert
        Assert.Equal(["Added.md", "Slow.gated"], storage.Children.Keys.Order());
    }
```

Replace every remaining `storage.ResyncAsync()` with `storage.ReconcileAsync(allNamed: true)`.

- [ ] **Step 8: Run the storage tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: all pass. If a test from before this task fails, it shows a behaviour the rework changed: stop and compare with the spec before changing the test.

Run the storage tests ten times in a row. Expected: ten passes.

- [ ] **Step 9: Run the full unit suite and the HomeBlaze end-to-end tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: no failures.

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: no failures. The end-to-end tests create, edit and delete subjects through the UI, which now goes through the worker.

- [ ] **Step 10: Commit**

```bash
git add -A src/HomeBlaze/HomeBlaze.Storage src/HomeBlaze/HomeBlaze.Storage.Tests
git commit -m "refactor: reconcile the HomeBlaze storage tree in sequential passes

The container no longer handles each file event on its own. A pass lists
the storage once, compares it with the index of what was applied last and
applies the difference. Passes and UI operations run one at a time on a
worker, which replaces the hierarchy lock. The startup scan is the first
pass.

Paths are exact on-disk names. A renamed configurable JSON subject keeps
its instance."
```

---

### Task 5: Storage calls cannot block the worker without a limit

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageCallTimeout.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageReconciler.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/FluentStorageContainer.cs`
- Create: `src/HomeBlaze/HomeBlaze.Storage.Tests/PausableBlobStorage.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Tests/StorageTestBase.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/StorageTimeoutTests.cs`

**Interfaces:**
- Consumes: `StorageReconciler`, `FluentStorageContainer` from Task 4, `ManualTimeProvider` (exists in the test project, `Advance(TimeSpan)`).
- Produces:
  - `StorageCallTimeout.Limit` (`TimeSpan`, 30 seconds)
  - Extension methods `Task<T> WithStorageTimeoutAsync<T>(this Task<T> call, TimeProvider timeProvider, CancellationToken cancellationToken)` and the non-generic one. They throw `TimeoutException`.
  - `internal TimeProvider FluentStorageContainer.TimeProvider { get; set; }`, default `TimeProvider.System`. Read once in `ConnectAsync`.
  - `internal Func<IBlobStorage, IBlobStorage>? FluentStorageContainer.ClientDecorator { get; set; }`, applied to the client in `ConnectAsync`.
  - `StorageReconciler` constructor gains a `TimeProvider timeProvider` parameter before `ILogger? logger`.

- [ ] **Step 1: Write the storage double**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/PausableBlobStorage.cs`:

```csharp
using FluentStorage.Blobs;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A storage whose next call of one operation can be made to hang, to test what a stalled source does.
/// </summary>
internal sealed class PausableBlobStorage(IBlobStorage inner) : IBlobStorage
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _reached;
    private string? _pausedOperation;

    /// <summary>
    /// Makes the next call of the operation hang. The returned task completes when that call has arrived.
    /// </summary>
    public Task PauseNext(string operation)
    {
        _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pausedOperation = operation;
        return _reached.Task.WaitAsync(Timeout);
    }

    public async Task<IReadOnlyCollection<Blob>> ListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
    {
        await PauseAsync(nameof(ListAsync));
        return await inner.ListAsync(options, cancellationToken);
    }

    public async Task WriteAsync(string fullPath, Stream dataStream, bool append = false, CancellationToken cancellationToken = default)
    {
        await PauseAsync(nameof(WriteAsync));
        await inner.WriteAsync(fullPath, dataStream, append, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        await PauseAsync(nameof(OpenReadAsync));
        return await inner.OpenReadAsync(fullPath, cancellationToken);
    }

    public Task DeleteAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        => inner.DeleteAsync(fullPaths, cancellationToken);

    public Task<IReadOnlyCollection<bool>> ExistsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        => inner.ExistsAsync(fullPaths, cancellationToken);

    public Task<IReadOnlyCollection<Blob>> GetBlobsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
        => inner.GetBlobsAsync(fullPaths, cancellationToken);

    public Task SetBlobsAsync(IEnumerable<Blob> blobs, CancellationToken cancellationToken = default)
        => inner.SetBlobsAsync(blobs, cancellationToken);

    public Task<ITransaction> OpenTransactionAsync()
        => inner.OpenTransactionAsync();

    public void Dispose()
    {
        // Lets a call that is still hanging end, so that no task outlives the test.
        _released.TrySetResult();
        inner.Dispose();
    }

    private async Task PauseAsync(string operation)
    {
        if (_pausedOperation == operation)
        {
            _pausedOperation = null;
            _reached!.TrySetResult();
            await _released.Task;
        }
    }
}
```

- [ ] **Step 2: Write the failing tests**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/StorageTimeoutTests.cs`:

```csharp
using FluentStorage.Blobs;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class StorageTimeoutTests : StorageTestBase
{
    private readonly ManualTimeProvider _timeProvider = new();
    private PausableBlobStorage? _client;

    [Fact]
    public async Task WhenListingHangs_ThenPassFailsAndNextPassSucceeds()
    {
        // Arrange
        WriteFile("Home.md");
        var storage = await ConnectPausableAsync();
        WriteFile("Added.md");
        var listingReached = _client!.PauseNext(nameof(IBlobStorage.ListAsync));
        var pass = storage.ReconcileAsync();
        await listingReached;

        // Act
        _timeProvider.Advance(StorageCallTimeout.Limit);
        await pass;
        var statusAfterFailedPass = storage.Status;
        await storage.ReconcileAsync();

        // Assert
        Assert.Equal(StorageStatus.Error, statusAfterFailedPass);
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Added.md", "Home.md"], storage.Children.Keys.Order());
    }

    [Fact]
    public async Task WhenReadOfOneFileHangs_ThenOnlyThatFileIsMissing()
    {
        // Arrange
        var storage = await ConnectPausableAsync();
        WriteFile("First.md");
        WriteFile("Second.md");
        var readReached = _client!.PauseNext(nameof(IBlobStorage.OpenReadAsync));
        var pass = storage.ReconcileAsync();
        await readReached;

        // Act
        _timeProvider.Advance(StorageCallTimeout.Limit);
        await pass;

        // Assert
        Assert.Equal(["Second.md"], storage.Children.Keys);
        Assert.Equal(StorageStatus.Connected, storage.Status);
    }

    [Fact]
    public async Task WhenWriteHangs_ThenCallerGetsTimeoutAndLaterWorkRuns()
    {
        // Arrange
        var storage = await ConnectPausableAsync();
        var writeReached = _client!.PauseNext(nameof(IBlobStorage.WriteAsync));
        var add = storage.AddSubjectAsync("Motor.json", CreateMotor(), CancellationToken.None);
        await writeReached;

        // Act
        _timeProvider.Advance(StorageCallTimeout.Limit);

        // Assert
        await Assert.ThrowsAsync<TimeoutException>(() => add);
        await storage.ReconcileAsync();
        Assert.Empty(storage.Children);
    }

    private Task<FluentStorageContainer> ConnectPausableAsync()
        => ConnectAsync(configure: storage =>
        {
            storage.TimeProvider = _timeProvider;
            storage.ClientDecorator = client => _client = new PausableBlobStorage(client);
        });
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~StorageTimeoutTests"`
Expected: build errors, `StorageCallTimeout`, `TimeProvider` and `ClientDecorator` on the container do not exist.

- [ ] **Step 4: Implement**

Create `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageCallTimeout.cs`:

```csharp
namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The limit for a single call into the storage. Without it a stalled source would block the worker,
/// and with it every operation that waits behind the call.
/// </summary>
internal static class StorageCallTimeout
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <exception cref="TimeoutException">The call did not complete within <see cref="Limit"/>.</exception>
    public static Task<TResult> WithStorageTimeoutAsync<TResult>(
        this Task<TResult> call, TimeProvider timeProvider, CancellationToken cancellationToken)
        => call.WaitAsync(Limit, timeProvider, cancellationToken);

    /// <exception cref="TimeoutException">The call did not complete within <see cref="Limit"/>.</exception>
    public static Task WithStorageTimeoutAsync(
        this Task call, TimeProvider timeProvider, CancellationToken cancellationToken)
        => call.WaitAsync(Limit, timeProvider, cancellationToken);
}
```

The call itself is not cancelled by the timeout: a backend that ignores its token would not stop anyway. It is left to finish or fail on its own, and only the wait ends.

In `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageReconciler.cs`:

- Add a field `private readonly TimeProvider _timeProvider;` and a constructor parameter `TimeProvider timeProvider` before `ILogger? logger`. Assign it.
- In `GetVersionAsync`, change the call to `await _client.GetBlobsAsync([path], cancellationToken).WithStorageTimeoutAsync(_timeProvider, cancellationToken);`
- In `ListAsync`, change the call to `await _client.ListAsync(recurse: true, cancellationToken: cancellationToken).WithStorageTimeoutAsync(_timeProvider, cancellationToken);`
- In `ReadAsync`, change the open call to `await _client.OpenReadAsync(path, cancellationToken).WithStorageTimeoutAsync(_timeProvider, cancellationToken)` and the copy to `await stream.CopyToAsync(buffer, cancellationToken).WithStorageTimeoutAsync(_timeProvider, cancellationToken);`

In `src/HomeBlaze/HomeBlaze.Storage/FluentStorageContainer.cs`:

Add next to the other fields:

```csharp
    private TimeProvider _timeProvider = TimeProvider.System;

    /// <summary>The clock for timeouts and timers. Read when the storage connects.</summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Wraps the storage client when the storage connects.</summary>
    internal Func<IBlobStorage, IBlobStorage>? ClientDecorator { get; set; }
```

In `ConnectAsync`, after the `_client = StorageType switch { ... };` statement, add:

```csharp
            _client = ClientDecorator?.Invoke(_client) ?? _client;
            _timeProvider = TimeProvider;
```

and pass `_timeProvider` to the reconciler: `new StorageReconciler(_client, this, _subjectFactory, _serializer, _index, _timeProvider, _logger)`.

Add `.WithStorageTimeoutAsync(_timeProvider, <token>)` to every remaining call on `Client`, using the token of the surrounding method:

- `AddSubjectAsync`: `await Client.ExistsAsync(relativePath, token).WithStorageTimeoutAsync(_timeProvider, token)`
- `WriteAndRecordAsync`: `await Client.WriteAsync(entry.Path, stream, append: false, cancellationToken: cancellationToken).WithStorageTimeoutAsync(_timeProvider, cancellationToken);`
- `WriteBlobAsync`: `await Client.WriteAsync(relativePath, content, append: false, cancellationToken: token).WithStorageTimeoutAsync(_timeProvider, token);`
- `DeleteEntryAsync`: `await Client.DeleteAsync(relativePath, cancellationToken: cancellationToken).WithStorageTimeoutAsync(_timeProvider, cancellationToken);`
- `ReadBlobAsync`: `return await Client.OpenReadAsync(path, cancellationToken: cancellationToken).WithStorageTimeoutAsync(_timeProvider, cancellationToken);`
- `GetBlobMetadataAsync`, the listing in the branch for backends without a directory: `await Client.ListAsync(folderPath: ..., recurse: false, cancellationToken: cancellationToken).WithStorageTimeoutAsync(_timeProvider, cancellationToken);`

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: all pass.

In `WhenReadOfOneFileHangs_ThenOnlyThatFileIsMissing` the first read of the pass belongs to `First.md`, because a pass handles paths in ordinal order. If the assertion sees `First.md` and not `Second.md`, the order of a pass changed: fix the order, not the test.

- [ ] **Step 6: Commit**

```bash
git add -A src/HomeBlaze/HomeBlaze.Storage src/HomeBlaze/HomeBlaze.Storage.Tests
git commit -m "fix: limit every storage call of the HomeBlaze storage worker to 30 seconds"
```

---

### Task 6: The trigger, the periodic pass, and the small watcher

After this task the watcher only reports paths. `FileEventCoalescer`, `CoalesceEvents` and the own-write marks are gone.

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Storage/Internal/ReconcileTrigger.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageFileWatcher.cs` (rewritten)
- Modify: `src/HomeBlaze/HomeBlaze.Storage/FluentStorageContainer.cs`
- Delete: `src/HomeBlaze/HomeBlaze.Storage/Internal/FileEventCoalescer.cs`
- Delete: `src/HomeBlaze/HomeBlaze.Storage.Tests/FileEventCoalescerTests.cs`
- Test: `src/HomeBlaze/HomeBlaze.Storage.Tests/ReconcileTriggerTests.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Tests/StorageFileWatcherTests.cs` (rewritten)
- Modify: `src/HomeBlaze/HomeBlaze.Storage.Tests/FluentStorageContainerFileEventTests.cs`

**Interfaces:**
- Consumes: `FluentStorageContainer.ReconcileAsync(IReadOnlySet<string>? namedPaths, bool allNamed)`, `FluentStorageContainer.TimeProvider`, `StoragePathFilter.IsIgnored`, `ManualTimeProvider`.
- Produces:
  - `ReconcileTrigger(Func<IReadOnlySet<string>, bool, Task> runPass, TimeSpan periodicInterval, TimeProvider? timeProvider = null, ILogger? logger = null)`
  - `void ReconcileTrigger.NotifyChanged(string path, string? otherPath = null)`
  - `void ReconcileTrigger.NotifyEventsLost()`
  - `ReconcileTrigger.QuietPeriod` (1 second) and `ReconcileTrigger.MaximumDelay` (5 seconds), both `internal static readonly TimeSpan`
  - `StorageFileWatcher(string basePath, Action<string, string?> onChanged, Action onEventsLost, ILogger? logger = null)` with `Start()`, `SimulateFileEvent(FileSystemEventArgs)`, `Dispose()`
  - `[Configuration] public partial int ReconcileIntervalSeconds { get; set; }` on `FluentStorageContainer`, default 300, 0 switches the periodic pass off.

- [ ] **Step 1: Write the failing trigger tests**

Create `src/HomeBlaze/HomeBlaze.Storage.Tests/ReconcileTriggerTests.cs`:

```csharp
using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class ReconcileTriggerTests
{
    private readonly ManualTimeProvider _timeProvider = new();
    private readonly List<(IReadOnlySet<string> NamedPaths, bool AllNamed)> _passes = [];
    private TaskCompletionSource? _runningPass;

    [Fact]
    public void WhenPathChanges_ThenPassRunsAfterQuietPeriod()
    {
        // Arrange
        using var trigger = CreateTrigger();

        // Act
        trigger.NotifyChanged("Home.md");
        _timeProvider.Advance(ReconcileTrigger.QuietPeriod - TimeSpan.FromMilliseconds(1));
        var passesBeforeQuietPeriodEnds = _passes.Count;
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal(0, passesBeforeQuietPeriodEnds);
        var pass = Assert.Single(_passes);
        Assert.Equal(["Home.md"], pass.NamedPaths);
        Assert.False(pass.AllNamed);
    }

    [Fact]
    public void WhenPathsKeepChanging_ThenQuietPeriodRestartsAndPassNamesAllOfThem()
    {
        // Arrange
        using var trigger = CreateTrigger();

        // Act
        trigger.NotifyChanged("First.md");
        _timeProvider.Advance(TimeSpan.FromMilliseconds(800));
        trigger.NotifyChanged("Second.md", "Old.md");
        _timeProvider.Advance(TimeSpan.FromMilliseconds(800));
        var passesBeforeQuietPeriodEnds = _passes.Count;
        _timeProvider.Advance(TimeSpan.FromMilliseconds(200));

        // Assert
        Assert.Equal(0, passesBeforeQuietPeriodEnds);
        var pass = Assert.Single(_passes);
        Assert.Equal(["First.md", "Old.md", "Second.md"], pass.NamedPaths.Order());
    }

    [Fact]
    public void WhenPathsNeverStopChanging_ThenPassRunsAfterMaximumDelay()
    {
        // Arrange
        using var trigger = CreateTrigger();

        // Act
        for (var elapsed = TimeSpan.Zero; elapsed < ReconcileTrigger.MaximumDelay; elapsed += TimeSpan.FromMilliseconds(500))
        {
            trigger.NotifyChanged("Busy.md");
            _timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        }

        // Assert
        Assert.Single(_passes);
    }

    [Fact]
    public void WhenEventsAreLost_ThenPassRunsAtOnceWithEveryFileNamed()
    {
        // Arrange
        using var trigger = CreateTrigger();

        // Act
        trigger.NotifyEventsLost();
        _timeProvider.Advance(TimeSpan.Zero);

        // Assert
        var pass = Assert.Single(_passes);
        Assert.True(pass.AllNamed);
    }

    [Fact]
    public void WhenPeriodicIntervalElapses_ThenPassRunsWithoutNamedPaths()
    {
        // Arrange
        using var trigger = CreateTrigger(periodicInterval: TimeSpan.FromMinutes(5));

        // Act
        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        var passesAfterFirstInterval = _passes.Count;
        _timeProvider.Advance(TimeSpan.FromMinutes(5));

        // Assert
        Assert.Equal(1, passesAfterFirstInterval);
        Assert.Equal(2, _passes.Count);
        Assert.All(_passes, pass =>
        {
            Assert.Empty(pass.NamedPaths);
            Assert.False(pass.AllNamed);
        });
    }

    [Fact]
    public void WhenPeriodicIntervalIsZero_ThenNoPeriodicPassRuns()
    {
        // Arrange
        using var trigger = CreateTrigger(periodicInterval: TimeSpan.Zero);

        // Act
        _timeProvider.Advance(TimeSpan.FromHours(1));

        // Assert
        Assert.Empty(_passes);
    }

    [Fact]
    public void WhenPathsChangeDuringPass_ThenExactlyOnePassFollows()
    {
        // Arrange
        using var trigger = CreateTrigger();
        _runningPass = new TaskCompletionSource();
        trigger.NotifyChanged("First.md");
        _timeProvider.Advance(ReconcileTrigger.QuietPeriod);

        // Act
        trigger.NotifyChanged("Second.md");
        trigger.NotifyChanged("Third.md");
        _timeProvider.Advance(ReconcileTrigger.MaximumDelay);
        var passesWhileFirstRuns = _passes.Count;

        var firstPass = _runningPass;
        _runningPass = null;
        firstPass.SetResult();
        _timeProvider.Advance(ReconcileTrigger.MaximumDelay);

        // Assert
        Assert.Equal(1, passesWhileFirstRuns);
        Assert.Equal(2, _passes.Count);
        Assert.Equal(["Second.md", "Third.md"], _passes[1].NamedPaths.Order());
    }

    [Fact]
    public void WhenTriggerIsDisposed_ThenNoPassRuns()
    {
        // Arrange
        var trigger = CreateTrigger(periodicInterval: TimeSpan.FromMinutes(5));
        trigger.NotifyChanged("Home.md");

        // Act
        trigger.Dispose();
        trigger.NotifyChanged("Other.md");
        _timeProvider.Advance(TimeSpan.FromMinutes(10));

        // Assert
        Assert.Empty(_passes);
    }

    private ReconcileTrigger CreateTrigger(TimeSpan? periodicInterval = null)
        => new(
            (namedPaths, allNamed) =>
            {
                _passes.Add((namedPaths, allNamed));
                return _runningPass?.Task ?? Task.CompletedTask;
            },
            periodicInterval ?? TimeSpan.Zero,
            _timeProvider);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~ReconcileTriggerTests"`
Expected: build error, `ReconcileTrigger` does not exist.

- [ ] **Step 3: Implement the trigger**

Create `src/HomeBlaze/HomeBlaze.Storage/Internal/ReconcileTrigger.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Decides when a reconcile pass runs. Changes are collected until the storage has been quiet for
/// <see cref="QuietPeriod"/>, but no longer than <see cref="MaximumDelay"/>. A periodic pass covers changes
/// that no event reported. Only one pass runs at a time, and whatever arrives during it leads to one more.
/// </summary>
internal sealed class ReconcileTrigger : IDisposable
{
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1);

    // Without it a folder that is written to more often than once per quiet period would never get a pass.
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(5);

    private readonly Func<IReadOnlySet<string>, bool, Task> _runPass;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;
    private readonly ITimer _timer;
    private readonly ITimer? _periodicTimer;
    private readonly Lock _lock = new();

    private HashSet<string> _namedPaths = new(StringComparer.Ordinal);
    private bool _allNamed;
    private bool _isPending;
    private long _firstPendingTimestamp;
    private bool _isRunning;
    private bool _isDisposed;

    /// <param name="runPass">Runs a pass with the named paths and whether every file counts as named. Must not throw.</param>
    /// <param name="periodicInterval">How often a pass runs without any change. Zero switches it off.</param>
    /// <param name="timeProvider">The clock and timer source, the system one by default.</param>
    /// <param name="logger">Receives a pass that threw despite the contract.</param>
    public ReconcileTrigger(
        Func<IReadOnlySet<string>, bool, Task> runPass,
        TimeSpan periodicInterval,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        _runPass = runPass;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;

        _timer = _timeProvider.CreateTimer(
            static state => ((ReconcileTrigger)state!).OnTimer(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        if (periodicInterval > TimeSpan.Zero)
        {
            _periodicTimer = _timeProvider.CreateTimer(
                static state => ((ReconcileTrigger)state!).Request(null, null, allNamed: false, atOnce: true),
                this, periodicInterval, periodicInterval);
        }
    }

    /// <summary>
    /// Reports that an event named the path, and for a rename the other path as well.
    /// </summary>
    public void NotifyChanged(string path, string? otherPath = null)
        => Request(path, otherPath, allNamed: false, atOnce: false);

    /// <summary>
    /// Reports that events were lost, so the next pass cannot rely on the named paths.
    /// </summary>
    public void NotifyEventsLost()
        => Request(null, null, allNamed: true, atOnce: true);

    private void Request(string? path, string? otherPath, bool allNamed, bool atOnce)
    {
        lock (_lock)
        {
            if (_isDisposed)
                return;

            if (path != null)
            {
                _namedPaths.Add(path);
            }

            if (otherPath != null)
            {
                _namedPaths.Add(otherPath);
            }

            _allNamed |= allNamed;

            if (!_isPending)
            {
                _isPending = true;
                _firstPendingTimestamp = _timeProvider.GetTimestamp();
            }

            // The running pass arms the timer when it ends.
            if (!_isRunning)
            {
                ArmTimer(atOnce);
            }
        }
    }

    private void ArmTimer(bool atOnce)
    {
        var untilMaximum = MaximumDelay - _timeProvider.GetElapsedTime(_firstPendingTimestamp);
        var delay = atOnce || untilMaximum <= TimeSpan.Zero
            ? TimeSpan.Zero
            : untilMaximum < QuietPeriod ? untilMaximum : QuietPeriod;

        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        IReadOnlySet<string> namedPaths;
        bool allNamed;

        lock (_lock)
        {
            if (_isDisposed || _isRunning || !_isPending)
                return;

            namedPaths = _namedPaths;
            allNamed = _allNamed;

            _namedPaths = new HashSet<string>(StringComparer.Ordinal);
            _allNamed = false;
            _isPending = false;
            _isRunning = true;
        }

        _ = RunPassAsync(namedPaths, allNamed);
    }

    private async Task RunPassAsync(IReadOnlySet<string> namedPaths, bool allNamed)
    {
        try
        {
            await _runPass(namedPaths, allNamed);
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Reconcile pass failed");
        }
        finally
        {
            lock (_lock)
            {
                _isRunning = false;
                if (_isPending && !_isDisposed)
                {
                    ArmTimer(atOnce: false);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _isDisposed = true;
        }

        _timer.Dispose();
        _periodicTimer?.Dispose();
    }
}
```

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~ReconcileTriggerTests"`
Expected: 8 passed.

- [ ] **Step 4: Write the failing watcher tests**

Replace the whole content of `src/HomeBlaze/HomeBlaze.Storage.Tests/StorageFileWatcherTests.cs` with:

```csharp
using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class StorageFileWatcherTests
{
    private static readonly string BasePath = Path.Combine(Path.GetTempPath(), "homeblaze-watcher");

    private readonly List<(string Path, string? OtherPath)> _changes = [];

    [Fact]
    public void WhenFileChanges_ThenItsRelativePathIsReported()
    {
        // Arrange
        using var watcher = CreateWatcher();

        // Act
        watcher.SimulateFileEvent(new FileSystemEventArgs(
            WatcherChangeTypes.Changed, Path.Combine(BasePath, "Docs"), "Readme.md"));

        // Assert
        Assert.Equal([("Docs/Readme.md", null)], _changes);
    }

    [Theory]
    [InlineData(".DS_Store")]
    [InlineData("Home.md~")]
    public void WhenIgnoredPathChanges_ThenNothingIsReported(string name)
    {
        // Arrange
        using var watcher = CreateWatcher();

        // Act
        watcher.SimulateFileEvent(new FileSystemEventArgs(WatcherChangeTypes.Changed, BasePath, name));

        // Assert
        Assert.Empty(_changes);
    }

    [Fact]
    public void WhenFileIsRenamed_ThenBothPathsAreReported()
    {
        // Arrange
        using var watcher = CreateWatcher();

        // Act
        watcher.SimulateFileEvent(new RenamedEventArgs(WatcherChangeTypes.Renamed, BasePath, "New.md", "Old.md"));

        // Assert
        Assert.Equal([("New.md", "Old.md")], _changes);
    }

    [Theory]
    [InlineData("Home.md", "Home.md.tmp")]
    [InlineData("Home.md~", "Home.md")]
    public void WhenRenameTouchesOneIgnoredPath_ThenItIsStillReported(string newName, string oldName)
    {
        // Arrange
        using var watcher = CreateWatcher();

        // Act
        watcher.SimulateFileEvent(new RenamedEventArgs(WatcherChangeTypes.Renamed, BasePath, newName, oldName));

        // Assert
        Assert.Equal([(newName, oldName)], _changes);
    }

    [Fact]
    public void WhenRenameTouchesOnlyIgnoredPaths_ThenNothingIsReported()
    {
        // Arrange
        using var watcher = CreateWatcher();

        // Act
        watcher.SimulateFileEvent(new RenamedEventArgs(WatcherChangeTypes.Renamed, BasePath, "~b.tmp", "~a.tmp"));

        // Assert
        Assert.Empty(_changes);
    }

    // Not started: the events are simulated, so no file system watcher is needed.
    private StorageFileWatcher CreateWatcher()
        => new(BasePath, (path, otherPath) => _changes.Add((path, otherPath)), () => { });
}
```

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~StorageFileWatcherTests"`
Expected: build error, the constructor of `StorageFileWatcher` has other parameters.

- [ ] **Step 5: Rewrite the watcher**

Replace the whole content of `src/HomeBlaze/HomeBlaze.Storage/Internal/StorageFileWatcher.cs` with:

```csharp
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Reports which paths of a directory the file system changed. It does not interpret the events:
/// what happened to a path is decided by looking at the storage.
/// </summary>
internal sealed class StorageFileWatcher : IDisposable
{
    private readonly string _basePath;
    private readonly Action<string, string?> _onChanged;
    private readonly Action _onEventsLost;
    private readonly ILogger? _logger;

    private FileSystemWatcher? _watcher;

    /// <param name="basePath">The watched directory.</param>
    /// <param name="onChanged">Receives the path within the directory, and for a rename the old path as well.</param>
    /// <param name="onEventsLost">Called when the watcher failed and events may be missing.</param>
    /// <param name="logger">The logger.</param>
    public StorageFileWatcher(
        string basePath,
        Action<string, string?> onChanged,
        Action onEventsLost,
        ILogger? logger = null)
    {
        _basePath = Path.GetFullPath(basePath);
        _onChanged = onChanged;
        _onEventsLost = onEventsLost;
        _logger = logger;
    }

    public void Start()
    {
        _watcher = new FileSystemWatcher(_basePath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024, // 64KB buffer to reduce overflow risk
            EnableRaisingEvents = true
        };

        _watcher.Created += OnWatcherEvent;
        _watcher.Changed += OnWatcherEvent;
        _watcher.Deleted += OnWatcherEvent;
        _watcher.Renamed += OnWatcherEvent;
        _watcher.Error += OnWatcherError;

        _logger?.LogInformation("File watching enabled for: {Path}", _basePath);
    }

    /// <summary>
    /// Handles a file system event as if the watcher had raised it.
    /// </summary>
    internal void SimulateFileEvent(FileSystemEventArgs e) => OnWatcherEvent(this, e);

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        var path = GetRelativePath(e.FullPath);
        var oldPath = e is RenamedEventArgs { OldFullPath: { } oldFullPath } ? GetRelativePath(oldFullPath) : null;

        // A rename between a tracked name and an ignored one changes the tree, so it counts when either side is tracked.
        if (StoragePathFilter.IsIgnored(path) && (oldPath == null || StoragePathFilter.IsIgnored(oldPath)))
            return;

        _onChanged(path, oldPath);
    }

    private string GetRelativePath(string fullPath)
        => Path.GetRelativePath(_basePath, fullPath).Replace('\\', '/');

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger?.LogError(e.GetException(), "FileSystemWatcher error (buffer overflow?), events may be lost");

        try
        {
            _watcher?.Dispose();
            Start();
        }
        catch (Exception exception)
        {
            // The periodic pass still follows the storage without a watcher.
            _logger?.LogError(exception, "Failed to restart the file watcher for: {Path}", _basePath);
        }

        _onEventsLost();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
```

Delete the coalescer and its tests:

```bash
git rm src/HomeBlaze/HomeBlaze.Storage/Internal/FileEventCoalescer.cs \
       src/HomeBlaze/HomeBlaze.Storage.Tests/FileEventCoalescerTests.cs
```

Keep `src/HomeBlaze/HomeBlaze.Storage.Tests/ManualTimeProvider.cs`. The trigger and timeout tests use it.

- [ ] **Step 6: Wire the trigger into the container**

In `src/HomeBlaze/HomeBlaze.Storage/FluentStorageContainer.cs`:

Add the setting after the `EnableFileWatching` property:

```csharp
    /// <summary>
    /// How often the storage is compared with the subject tree without any file event, in seconds.
    /// Covers changes the file watcher never reports. Default is 300, and 0 switches it off.
    /// </summary>
    [Configuration]
    public partial int ReconcileIntervalSeconds { get; set; }
```

In the constructor, after `EnableFileWatching = true;`, add `ReconcileIntervalSeconds = 300;`.

Add a field next to `_fileWatcher`: `private ReconcileTrigger? _trigger;`

In `ConnectAsync`, where the watcher and worker of a previous connection are disposed, also dispose the trigger, before the worker:

```csharp
            _fileWatcher?.Dispose();
            _fileWatcher = null;
            _trigger?.Dispose();
            _worker?.Dispose();
```

In `ConnectAsync`, replace the block that starts the watcher with:

```csharp
            // Both exist before the first pass, so a change during startup leads to another pass.
            var trigger = new ReconcileTrigger(
                (namedPaths, allNamed) => ReconcileAsync(namedPaths, allNamed),
                TimeSpan.FromSeconds(ReconcileIntervalSeconds),
                _timeProvider,
                _logger);
            _trigger = trigger;

            if (EnableFileWatching && !isInMemory)
            {
                _fileWatcher = new StorageFileWatcher(_storageDirectory!, trigger.NotifyChanged, trigger.NotifyEventsLost, _logger);
                _fileWatcher.Start();
            }
```

Delete the methods `StartFileWatching`, `ProcessFileEventAsync` and `GetRelativePath`.

In `Dispose`, add `_trigger?.Dispose();` after `_fileWatcher?.Dispose();`.

- [ ] **Step 7: Convert the file event tests from events to passes**

`ProcessFileEventAsync` is gone. In `src/HomeBlaze/HomeBlaze.Storage.Tests/FluentStorageContainerFileEventTests.cs`:

- Replace every `storage.ProcessFileEventAsync(Event(<type>, <path>))` with `storage.ReconcileAsync(Named(<path>))`.
- Replace every `storage.ProcessFileEventAsync(Renamed(<old>, <new>))` with `storage.ReconcileAsync(Named(<old>, <new>))`.
- Delete the helpers `Event` and `Renamed`.
- The three theories over `WatcherChangeTypes` no longer vary anything. Turn each into a fact and drop the parameter and the `InlineData` lines: `WhenEventArrivesForUnregisteredFileOnDisk_ThenFileIsAdded` becomes `WhenFileAppears_ThenPassAddsIt`, `WhenEventArrivesForRegisteredFileMissingOnDisk_ThenSubjectIsRemoved` becomes `WhenFileIsGone_ThenPassRemovesItsSubject`.
- Rename `WhenCreatedEventArrivesForMissingFile_ThenNoSubjectIsAdded` to `WhenNamedPathDoesNotExist_ThenNoSubjectIsAdded`.
- Rename the test class and its file to `FluentStorageContainerReconcileTests`, since no test in it handles an event any more:

```bash
git mv src/HomeBlaze/HomeBlaze.Storage.Tests/FluentStorageContainerFileEventTests.cs \
       src/HomeBlaze/HomeBlaze.Storage.Tests/FluentStorageContainerReconcileTests.cs
```

The three tests that use the real watcher (`WhenNewFileIsSavedViaTempFileAndRename_ThenWatcherAddsFile`, `WhenRegisteredFileIsRecreatedAndDeletedAtOnce_ThenWatcherRemovesFile`, `WhenDirectoryWithFileIsCreated_ThenWatcherAddsFolderAndFile`) and `WhenFileWatchingIsDisabledByReconfiguration_ThenPreviousWatcherStops` stay as they are.

Add one test for the periodic pass to the class:

```csharp
    [Fact]
    public async Task WhenNoEventArrives_ThenPeriodicPassPicksUpTheChange()
    {
        // Arrange
        var timeProvider = new ManualTimeProvider();
        var storage = await ConnectAsync(configure: container =>
        {
            container.TimeProvider = timeProvider;
            container.ReconcileIntervalSeconds = 60;
        });
        WriteFile("Added.md");

        // Act
        timeProvider.Advance(TimeSpan.FromSeconds(60));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => storage.Children.ContainsKey("Added.md"), WatcherTimeout);
    }
```

- [ ] **Step 8: Run all storage tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests`
Expected: all pass.

Run it ten times in a row. Expected: ten passes. The three real-watcher tests now wait for the quiet period of one second.

- [ ] **Step 9: Run the full unit suite and the end-to-end tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: no failures.

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: no failures.

- [ ] **Step 10: Commit**

```bash
git add -A src/HomeBlaze/HomeBlaze.Storage src/HomeBlaze/HomeBlaze.Storage.Tests
git commit -m "refactor: trigger HomeBlaze storage passes from a debounce and a periodic timer

The file watcher only reports which paths changed. A pass runs after one
second without a change, after five seconds at most, and every
ReconcileIntervalSeconds without any change. Event coalescing, rename
handling and own-write marks are removed: a pass compares the tree with
the storage, so none of them is needed."
```

---

### Task 7: Measurements, documentation and the pull request

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/Data/Files/Docs/architecture/design/storage.md` (sections "File Watching and Change Detection", "Known Limitations and Follow-ups", "File Hierarchy")
- Modify: any page under `src/HomeBlaze/HomeBlaze/Data/Files/Docs` that documents `EnableFileWatching`
- Delete: `docs/superpowers/`

**Interfaces:**
- Consumes: the finished implementation.
- Produces: nothing for other tasks.

- [ ] **Step 1: Measure the two numbers the spec asks for**

Create a temporary test `src/HomeBlaze/HomeBlaze.Storage.Tests/TempMeasurements.cs`. It is not committed.

```csharp
using System.Diagnostics;
using Xunit.Abstractions;

namespace HomeBlaze.Storage.Tests;

public class TempMeasurements(ITestOutputHelper output) : StorageTestBase
{
    [Fact]
    public async Task Measure()
    {
        // 5,000 plain files in 50 folders.
        for (var index = 0; index < 5000; index++)
        {
            WriteFile($"Folder{index % 50}/File{index}.bin", "x");
        }

        var storage = await ConnectAsync();

        var stopwatch = Stopwatch.StartNew();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        await storage.ReconcileAsync();
        var allocatedAfter = GC.GetTotalAllocatedBytes(true);
        output.WriteLine($"MEASURE idle pass over 5000 files: {stopwatch.ElapsedMilliseconds} ms, {(allocatedAfter - allocatedBefore) / 1024.0 / 1024.0:F1} MB");

        // 4,000 files added to one folder in one pass.
        for (var index = 0; index < 4000; index++)
        {
            WriteFile($"Many/File{index}.bin", "x");
        }

        stopwatch.Restart();
        allocatedBefore = GC.GetTotalAllocatedBytes(true);
        await storage.ReconcileAsync();
        allocatedAfter = GC.GetTotalAllocatedBytes(true);
        output.WriteLine($"MEASURE adding 4000 files to one folder: {stopwatch.ElapsedMilliseconds} ms, {(allocatedAfter - allocatedBefore) / 1024.0 / 1024.0:F1} MB");
    }
}
```

Run: `dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests --filter "FullyQualifiedName~TempMeasurements" --logger "console;verbosity=detailed" | grep MEASURE`

Write both lines down for the pull request. The second number was 270 MB when the files were added one event at a time. Then delete the file:

```bash
rm src/HomeBlaze/HomeBlaze.Storage.Tests/TempMeasurements.cs
```

If the idle pass takes more than about 100 ms or the second number is not far below 270 MB, stop and report: the assumption behind "walk the whole tree on every pass" does not hold.

- [ ] **Step 2: Rewrite the file watching section of the storage design page**

In `src/HomeBlaze/HomeBlaze/Data/Files/Docs/architecture/design/storage.md`, replace everything from the heading `### File Watching and Change Detection` up to, but not including, the heading `### File Types` with:

```markdown
### Following Changes in the Storage

The subject tree follows the storage through reconcile passes. A pass lists the storage once, compares the listing with what was applied in the last pass, and applies the difference. The startup scan is simply the first pass.

**When a pass runs**

- After a file system event, once the storage has been quiet for 1 second, and after 5 seconds at the latest. The event only names the path. What happened to it is decided by the listing.
- After the file watcher reported an error, such as a buffer overflow.
- Every `ReconcileIntervalSeconds` (default 300, 0 switches it off), which covers changes that no event reported.

**What a pass does with a path**

| In the storage | Applied before | Action |
|---|---|---|
| present | no | The subject is loaded and added. A folder becomes a `VirtualFolder`. |
| gone | yes | The subject or folder is removed. |
| size or modification time differs | yes | The subject is refreshed. |
| same size and time, named by an event | yes | The file is hashed and the subject refreshed only if the hash differs. |
| same size and time, not named | yes | Nothing. The file is not opened. |

- **Exact names.** Paths are compared exactly as the storage reports them. A rename is the old path gone and the new one present, which includes a rename that only changes the casing.
- **Renamed JSON subjects keep their instance.** A new `.json` file with the content of a subject whose file is gone in the same pass is that subject under a new path. It is neither stopped nor recreated, and its history follows it.
- **Ignored paths.** A path with a segment that starts with a dot (`.DS_Store`, `.idea`, `._*`) or has a temp name (`~` prefix or suffix, `.tmp` suffix, `.tmp.` in the name) never becomes a subject. That includes everything below a folder with such a name.
- **Keys.** A subject that is in the tree keeps its key. A file whose key is taken (`Docs.json` next to a `Docs` folder) is left out with a warning and placed in the pass where the key is free.
- **Failed loads.** A file that cannot be loaded is retried when it changes.
- **Folders.** The children of a folder are assigned once per pass, and only when they changed.

**One thing at a time**

Passes and the operations of the UI (create, delete, write, save configuration) run one after another on a single worker. Only the worker changes the tree, so there is no lock. An operation that arrives during a pass waits for it. After a write, the worker records the new size, time and hash, so the event that the write raises reloads nothing.

Every call into the storage is limited to 30 seconds. A stalled source fails the pass or the operation, sets the status of the storage to `Error`, and the next pass tries again.

#### Known Limitations and Follow-ups

- **Samba.** The tests run on macOS and on Linux in CI. A data folder edited over an SMB share is the setup this was built for, and it has only been observed, not tested.
- **Coarse timestamps.** On a file system with timestamps of one or two seconds, an edit that keeps the size and falls into the same tick is only seen when its event arrives. The periodic pass alone misses it.
- **A long pass delays the UI.** An operation waits for the running pass. That is short on a local disk and long only at startup.
- **Subject code on the worker.** A subject whose `ApplyConfigurationAsync` hangs blocks the worker. The 30 second limit covers storage calls, not subject code.
- **Renamed file subjects are recreated.** Only configurable JSON subjects keep their instance. A renamed markdown or other file becomes a new subject.
- **Blocked folders.** The files of a folder that was left out because its key was taken are placed one pass after the folder itself.

### File Hierarchy

Files are organized into a `VirtualFolder` tree. Each folder delegates storage operations to its parent `IStorageContainer`. A subject is in the index under its path if and only if it is placed in the tree. `AddSubjectAsync` writes the file and places the subject as one step on the worker, and throws without writing when the path is ignored, a file already exists there, or the key is taken.

```

- [ ] **Step 3: Document the new setting where the old one is documented**

Run: `grep -rn "EnableFileWatching\|enableFileWatching" src/HomeBlaze/HomeBlaze/Data/Files/Docs src/HomeBlaze/HomeBlaze/Seed src/HomeBlaze/HomeBlaze/Data`

For every documentation page in the result that lists the storage settings, add a row or line for `reconcileIntervalSeconds` next to it: "Seconds between passes without a file event. Default 300, 0 switches the periodic pass off." Do not add the setting to JSON configuration files; the default applies.

If nothing documents `EnableFileWatching`, skip this step.

- [ ] **Step 4: Remove the working documents**

```bash
git rm -r docs/superpowers
```

- [ ] **Step 5: Run everything once more**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: no failures.

Run: `dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests`
Expected: no failures.

- [ ] **Step 6: Commit**

```bash
git add -A src/HomeBlaze/HomeBlaze/Data/Files/Docs docs
git commit -m "docs: describe how the HomeBlaze storage follows changes through passes"
```

- [ ] **Step 7: Rebase onto master and open the pull request**

Pull request #672 must be merged first. If it is not, stop and ask.

```bash
git fetch origin
git rebase --onto origin/master 0c31a1074
```

`0c31a1074` is the last commit of the #672 branch that this branch was started from. If #672 gained commits after that, use its last commit instead (`git log origin/fix/storage-watcher-review-followup -1 --format=%h`). Resolve conflicts in favour of this branch for files under `src/HomeBlaze/HomeBlaze.Storage`, then run the storage tests again.

Open `.github/pull_request_template.md` and fill in every section. Use:

- Title: `refactor: follow HomeBlaze storage changes through sequential reconcile passes`
- Labels: `area: homeblaze` and `type: enhancement`
- Breaking changes: none in API. Behaviour: a change shows up after about one second instead of half a second; paths are case-sensitive on every platform; a new setting `ReconcileIntervalSeconds`.
- Performance: the two measured lines from Step 1, stated as one-off measurements, not as a BenchmarkDotNet run.
- Diff composition: the output of `pwsh scripts/diff-composition.ps1`.
- Verification: unit tests with the count for `HomeBlaze.Storage.Tests`, the end-to-end tests, and that nothing was run against a Samba share.

Write the filled-in template to a file outside the repository, for example `$TMPDIR/storage-reconcile-pr.md`, then:

```bash
git push -u origin refactor/storage-reconcile
gh pr create --base master --title "refactor: follow HomeBlaze storage changes through sequential reconcile passes" --body-file "$TMPDIR/storage-reconcile-pr.md" --label "area: homeblaze" --label "type: enhancement"
```

- [ ] **Step 8: After the merge**

Deploy to the server whose data folder is edited over an SMB share and repeat the four scenarios from the original report: a file saved through a temp file and a rename, a file created and deleted at once, hidden files, and a new directory with a file. This is the one check no test covers.
