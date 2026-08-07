# Subject bound hosted service ownership: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `Namotion.Interceptor.Hosting` have exactly one owner for the start, stop and disposal of a hosted service bound to a subject, so a subject that leaves and re-enters the object graph gets working services again and nothing leaks.

**Architecture:** The global `BufferBlock` action loop in `HostedServiceHandler` is replaced by one serialized transition chain per managed target, where a target is either a subject implementing `IHostedService` or a factory attachment. Lifecycle events append transitions at event time under the lifecycle lock; execution is per target so unrelated services never block each other. Attachment becomes factory based so the handler creates and therefore disposes every instance, and `AddHostedSubject<T>` becomes `AddSubject<T>` which attaches the context unconditionally rather than depending on constructor shape.

**Tech Stack:** .NET 9, C# 13 preview, xUnit, Verify, PublicApiGenerator, `Microsoft.Extensions.Hosting.Abstractions`.

**Spec:** `docs/superpowers/specs/2026-08-07-hosting-lifecycle-design.md`. Read it before starting. Every design decision below is justified there and several have non obvious reasons backed by measured experiments.

## Global Constraints

- Working directory is the worktree `/Users/ricosuter/Projects/GitHub/Namotion.Interceptor-hosting-lifecycle`, branch `docs/hosting-lifecycle`. Do not `cd` to the main checkout.
- Target framework `net9.0` for `Namotion.Interceptor.Hosting`; the core `Namotion.Interceptor` is `netstandard2.0` and is not modified.
- `Directory.Build.props` sets `Nullable=enable` and warnings as errors. A warning fails the build.
- Test naming: `When<Condition>_Then<ExpectedBehavior>`. Test bodies use explicit `// Arrange`, `// Act`, `// Assert` comments (`// Act & Assert` for exception tests).
- **No `Task.Delay` or `Thread.Sleep` as synchronization in tests.** Use `AsyncTestHelpers.WaitUntilAsync(Func<bool>, TimeSpan?, TimeSpan?, string?)` from `Namotion.Interceptor.Testing`, or `ManualResetEventSlim` / `TaskCompletionSource`.
- No em dashes in any documentation, README or PR description.
- No AI attribution in commit messages: no agent names, no `Co-Authored-By`, no "Generated with" footers.
- Avoid abbreviations in identifiers. `attribute`, not `attr`.
- Comments are minimal and explain only the non obvious.
- Build: `dotnet build src/Namotion.Interceptor.slnx`. Unit tests: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`. Targeted: `dotnet test src/Namotion.Interceptor.Hosting.Tests`.
- Snapshot loops: prefix with `DiffEngine_Disabled=true` so Verify does not try to open a diff tool.
- Single pull request. Commit after every task.

---

## File Structure

**`src/Namotion.Interceptor.Hosting/`**

| File | Responsibility |
|---|---|
| `HostedServiceGate.cs` | *New.* The four state startup and shutdown gate. Owns the state machine and the parked transition release. Nothing else. |
| `HostedServiceTarget.cs` | *New.* One managed thing: its factory or subject, its current instance, its fault, its owner and its serialized transition chain. Knows nothing about lifecycle events. |
| `HostedServiceAttachment.cs` | *New.* The public `IHostedServiceAttachment` / `IHostedServiceAttachment<T>` interfaces and the internal implementation that wraps a `HostedServiceTarget`. |
| `HostedServiceHandler.cs` | *Rewritten.* Translates lifecycle events into transitions, holds ownership and the running set, runs the drain. |
| `InterceptorHostingExtensions.cs` | *Rewritten.* The factory based attach and detach surface plus target record storage in `subject.Data`. |
| `SubjectServiceCollectionExtensions.cs` | *Renamed from `HostedSubjectServiceCollectionExtensions.cs`.* `AddSubject<T>`. |
| `SubjectActivation.cs` | *New.* The per type hosted service that resolves the singleton and hands start ownership to the handler. |
| `InterceptorSubjectContextExtensions.cs` | *Modified.* Handler registered with `AddSingleton<IHostedService>`. |

The split matters: the gate and the target are the two pieces with real concurrency, and both are independently testable without a host. Keeping them out of `HostedServiceHandler` is what makes Task 3's tests possible.

---

## Task 1: Track the public API before changing it

Establishes the snapshot so every later task's API change shows up as a reviewable diff rather than being discovered at the end.

**Files:**
- Modify: `src/Namotion.Interceptor.Hosting.Tests/Namotion.Interceptor.Hosting.Tests.csproj`
- Modify: `src/Namotion.Interceptor.Hosting.Tests/VerifyTests.cs`
- Create: `src/Namotion.Interceptor.Hosting.Tests/VerifyChecksTests.PublicApi.verified.txt`

**Interfaces:**
- Consumes: nothing.
- Produces: a `PublicApi()` test that later tasks re-accept.

- [ ] **Step 1: Add the PublicApiGenerator package reference**

In `Namotion.Interceptor.Hosting.Tests.csproj`, inside the existing first `<ItemGroup>` that holds `PackageReference` entries, add:

```xml
<PackageReference Include="PublicApiGenerator" Version="11.5.4" />
```

- [ ] **Step 2: Add the PublicApi test**

Replace the whole of `src/Namotion.Interceptor.Hosting.Tests/VerifyTests.cs` with:

```csharp
using PublicApiGenerator;

namespace Namotion.Interceptor.Hosting.Tests
{
    public class VerifyChecksTests
    {
        [Fact]
        public Task Run() => VerifyChecks.Run();

        /// <summary>
        /// Snapshot of the assembly's public API. When this fails after an intentional API change,
        /// review the diff and accept by replacing the .verified.txt file with the test's .received.txt.
        /// </summary>
        [Fact]
        public Task PublicApi() => Verify(typeof(InterceptorHostingExtensions).Assembly.GeneratePublicApi(new ApiGeneratorOptions
        {
            DenyNamespacePrefixes = ["System", "XamlGeneratedNamespace"]
        }));
    }
}
```

- [ ] **Step 3: Run the test to produce the first snapshot**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Hosting.Tests --filter "FullyQualifiedName~PublicApi"`

Expected: FAIL, because no `.verified.txt` exists yet. Verify writes `VerifyChecksTests.PublicApi.received.txt` next to the test file.

- [ ] **Step 4: Accept the snapshot**

```bash
cd /Users/ricosuter/Projects/GitHub/Namotion.Interceptor-hosting-lifecycle
mv src/Namotion.Interceptor.Hosting.Tests/VerifyChecksTests.PublicApi.received.txt \
   src/Namotion.Interceptor.Hosting.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Open the `.verified.txt` and confirm it contains `AddHostedSubject`, `AttachHostedService`, `DetachHostedService` and `GetAttachedHostedServices`. That is the surface this plan replaces.

- [ ] **Step 5: Run to verify it passes**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Hosting.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Hosting.Tests
git commit -m "test: snapshot the Namotion.Interceptor.Hosting public API"
```

---

## Task 2: Two contexts on one service collection each get a running handler

Spec defect 4. Independent of everything else, so it lands first and stays small.

**Files:**
- Modify: `src/Namotion.Interceptor.Hosting/InterceptorSubjectContextExtensions.cs:16`
- Create: `src/Namotion.Interceptor.Hosting.Tests/WithHostedServicesTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: no API change.

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Hosting.Tests/WithHostedServicesTests.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

public class WithHostedServicesTests
{
    [Fact]
    public async Task WhenTwoContextsShareOneServiceCollection_ThenBothHandlersRun()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();

        var firstContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var secondContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            // Act
            var firstPerson = new Person(firstContext);
            var secondPerson = new Person(secondContext);
            firstPerson.AttachHostedService(new PersonBackgroundService(firstPerson));
            secondPerson.AttachHostedService(new PersonBackgroundService(secondPerson));

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => firstPerson.FirstName == "John");
            await AsyncTestHelpers.WaitUntilAsync(() => secondPerson.FirstName == "John",
                message: "The second context's handler was dropped by TryAddEnumerable dedupe.");
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
```

Note this test uses the *current* instance based `AttachHostedService`. Task 4 rewrites it to the factory form.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Hosting.Tests --filter "FullyQualifiedName~WhenTwoContextsShareOneServiceCollection"`

Expected: FAIL, timing out on the second assertion with the message above. The second handler exists in its context but was never registered as a hosted service, so its action loop never started.

- [ ] **Step 3: Change the registration**

In `src/Namotion.Interceptor.Hosting/InterceptorSubjectContextExtensions.cs`, replace `serviceCollection.AddHostedService(sp => {...})` with a plain singleton registration. The method becomes:

```csharp
public static IInterceptorSubjectContext WithHostedServices(this IInterceptorSubjectContext context, IServiceCollection serviceCollection)
{
    context
        .TryAddService(() =>
        {
            ILogger? logger = null;
            var handler = new HostedServiceHandler(() => logger);

            // A plain Add, not AddHostedService: AddHostedService routes through TryAddEnumerable,
            // which dedupes on the implementation type, so a second context on the same collection
            // would silently lose its handler and never start any of its subjects.
            serviceCollection.AddSingleton<IHostedService>(sp =>
            {
                logger = sp.GetRequiredService<ILogger<HostedServiceHandler>>();
                return handler;
            });

            return handler;
        }, _ => true);

    return context
        .WithLifecycle();
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Hosting.Tests --filter "FullyQualifiedName~WhenTwoContextsShareOneServiceCollection"`
Expected: PASS.

- [ ] **Step 5: Run the whole hosting suite for regressions**

Run: `dotnet test src/Namotion.Interceptor.Hosting.Tests`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Hosting src/Namotion.Interceptor.Hosting.Tests
git commit -m "fix: give each context its own hosted service handler registration

AddHostedService routes through TryAddEnumerable and dedupes on the
implementation type, so a second context calling WithHostedServices on the
same IServiceCollection had its handler registration discarded. Its action
loop never started and every subject in that context silently never ran."
```

---

## Task 3: The gate and the target

The two concurrency primitives, built and tested without a host. Everything later depends on these being right, and they are the pieces two design reviews found easiest to get subtly wrong.

**Files:**
- Create: `src/Namotion.Interceptor.Hosting/HostedServiceGate.cs`
- Create: `src/Namotion.Interceptor.Hosting/HostedServiceTarget.cs`
- Create: `src/Namotion.Interceptor.Hosting.Tests/HostedServiceGateTests.cs`
- Create: `src/Namotion.Interceptor.Hosting.Tests/HostedServiceTargetTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `internal enum HostedServiceGateState { NotStarted, Running, Draining, Drained }`
  - `internal sealed class HostedServiceGate` with `HostedServiceGateState State { get; }`, `void EnsureStarted()`, `void BeginDraining()`, `void CompleteDraining()`, `Task WaitForOpenAsync()`
  - `internal sealed class HostedServiceTarget` with `Func<IHostedService>? Factory`, `IHostedService? Current`, `Exception? Fault`, `HostedServiceHandler? Owner`, `bool TryTakeOwnership(HostedServiceHandler)`, `void ReleaseOwnership(HostedServiceHandler)`, `Task AppendAsync(Func<CancellationToken, Task>, CancellationToken)`

- [ ] **Step 1: Write the failing gate tests**

Create `src/Namotion.Interceptor.Hosting.Tests/HostedServiceGateTests.cs`:

```csharp
namespace Namotion.Interceptor.Hosting.Tests;

public class HostedServiceGateTests
{
    [Fact]
    public void WhenEnsureStartedIsCalledTwice_ThenStateIsRunningOnce()
    {
        // Arrange
        var gate = new HostedServiceGate();

        // Act
        gate.EnsureStarted();
        gate.EnsureStarted();

        // Assert
        Assert.Equal(HostedServiceGateState.Running, gate.State);
    }

    [Fact]
    public void WhenEnsureStartedIsCalledWhileDraining_ThenStateStaysDraining()
    {
        // Arrange
        var gate = new HostedServiceGate();
        gate.EnsureStarted();
        gate.BeginDraining();

        // Act
        gate.EnsureStarted();

        // Assert - a plain assignment here would reopen the shutdown race the fourth state exists to close
        Assert.Equal(HostedServiceGateState.Draining, gate.State);
    }

    [Fact]
    public async Task WhenGateIsNotStarted_ThenWaitDoesNotComplete()
    {
        // Arrange
        var gate = new HostedServiceGate();

        // Act
        var wait = gate.WaitForOpenAsync();

        // Assert
        Assert.False(wait.IsCompleted);
        gate.EnsureStarted();
        await wait;
    }

    [Fact]
    public async Task WhenDrainingStartsFromNotStarted_ThenParkedWaitersAreReleasedAtDrained()
    {
        // Arrange - a host that aborts startup never opens the gate; parked transitions must not hang
        var gate = new HostedServiceGate();
        var wait = gate.WaitForOpenAsync();
        Assert.False(wait.IsCompleted);

        // Act
        gate.BeginDraining();
        gate.CompleteDraining();

        // Assert
        await wait;
        Assert.Equal(HostedServiceGateState.Drained, gate.State);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Hosting.Tests --filter "FullyQualifiedName~HostedServiceGateTests"`
Expected: FAIL to compile with `CS0246: The type or namespace name 'HostedServiceGate' could not be found`.

- [ ] **Step 3: Implement the gate**

Create `src/Namotion.Interceptor.Hosting/HostedServiceGate.cs`:

```csharp
namespace Namotion.Interceptor.Hosting;

internal enum HostedServiceGateState
{
    NotStarted,
    Running,
    Draining,
    Drained
}

/// <summary>
/// Startup and shutdown gate for hosted service transitions. The state only ever moves forward:
/// NotStarted to Running to Draining to Drained, or NotStarted straight to Draining when a host
/// is stopped without having started.
/// </summary>
internal sealed class HostedServiceGate
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HostedServiceGateState _state = HostedServiceGateState.NotStarted;

    public HostedServiceGateState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Advances NotStarted to Running. A one way ratchet: calling this during shutdown must not
    /// reopen the gate, or a detach arriving mid drain would let queued starts run again.
    /// </summary>
    public void EnsureStarted()
    {
        var opened = false;
        lock (_sync)
        {
            if (_state == HostedServiceGateState.NotStarted)
            {
                _state = HostedServiceGateState.Running;
                opened = true;
            }
        }

        if (opened)
        {
            _opened.TrySetResult();
        }
    }

    public void BeginDraining()
    {
        lock (_sync)
        {
            if (_state is HostedServiceGateState.NotStarted or HostedServiceGateState.Running)
            {
                _state = HostedServiceGateState.Draining;
            }
        }

        // Releases anything parked on a gate that was never opened, so a host that aborts
        // startup does not leave transitions and their awaiters hanging forever.
        _opened.TrySetResult();
    }

    public void CompleteDraining()
    {
        lock (_sync)
        {
            _state = HostedServiceGateState.Drained;
        }

        _opened.TrySetResult();
    }

    /// <summary>
    /// Completes once the gate has left <see cref="HostedServiceGateState.NotStarted"/>. Callers
    /// must then read <see cref="State"/> and decide what to do; the wait itself carries no verdict.
    /// </summary>
    public Task WaitForOpenAsync() => _opened.Task;
}
```

- [ ] **Step 4: Run to verify the gate tests pass**

Run: `dotnet test src/Namotion.Interceptor.Hosting.Tests --filter "FullyQualifiedName~HostedServiceGateTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Write the failing target tests**

Create `src/Namotion.Interceptor.Hosting.Tests/HostedServiceTargetTests.cs`:

```csharp
namespace Namotion.Interceptor.Hosting.Tests;

public class HostedServiceTargetTests
{
    [Fact]
    public async Task WhenTransitionsAreAppendedConcurrently_ThenTheyNeverOverlap()
    {
        // Arrange - an unsynchronised "_tail = _tail.ContinueWith(...)" is a read-modify-write and
        // loses an assignment under contention, running several transitions on one target at once.
        var target = new HostedServiceTarget(factory: null, subject: null);
        var head = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent = 0;
        var maximumConcurrent = 0;
        var sync = new object();

        var stall = target.AppendAsync(async _ => await head.Task, CancellationToken.None);

        // Act
        var appended = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            target.AppendAsync(async _ =>
            {
                lock (sync)
                {
                    concurrent++;
                    maximumConcurrent = Math.Max(maximumConcurrent, concurrent);
                }

                await Task.Yield();

                lock (sync)
                {
                    concurrent--;
                }
            }, CancellationToken.None))).ToArray();

        var transitions = await Task.WhenAll(appended);
        head.SetResult();
        await stall;
        await Task.WhenAll(transitions);

        // Assert
        Assert.Equal(1, maximumConcurrent);
    }

    [Fact]
    public async Task WhenATransitionThrows_ThenTheFaultIsRecordedAndTheChainContinues()
    {
        // Arrange - a faulted tail that propagated would raise UnobservedTaskException for every
        // dropped fire and forget transition, and would surface nowhere at all.
        var target = new HostedServiceTarget(factory: null, subject: null);
        var secondRan = false;

        // Act
        await target.AppendAsync(_ => throw new InvalidOperationException("boom"), CancellationToken.None);
        await target.AppendAsync(_ =>
        {
            secondRan = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Assert
        Assert.True(secondRan);
    }

    [Fact]
    public async Task WhenATransitionIsAppended_ThenItDoesNotRunOnTheAppendingThread()
    {
        // Arrange - appends happen while LifecycleInterceptor holds its lock, so an inline
        // continuation would run user code under that lock.
        var target = new HostedServiceTarget(factory: null, subject: null);
        var appendingThread = Environment.CurrentManagedThreadId;
        var ranInline = false;

        // Act
        await target.AppendAsync(_ =>
        {
            ranInline = Environment.CurrentManagedThreadId == appendingThread;
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Assert
        Assert.False(ranInline);
    }

    [Fact]
    public void WhenOwnershipIsTakenTwiceByTheSameHandler_ThenItSucceeds()
    {
        // Arrange - a re-attach arriving before the release must not be read as "lost to another handler"
        var target = new HostedServiceTarget(factory: null, subject: null);
        var handler = new HostedServiceHandler(() => null);

        // Act
        var first = target.TryTakeOwnership(handler);
        var second = target.TryTakeOwnership(handler);

        // Assert
        Assert.True(first);
        Assert.True(second);
    }

    [Fact]
    public void WhenASecondHandlerTakesOwnership_ThenItFails()
    {
        // Arrange
        var target = new HostedServiceTarget(factory: null, subject: null);
        var first = new HostedServiceHandler(() => null);
        var second = new HostedServiceHandler(() => null);
        target.TryTakeOwnership(first);

        // Act
        var taken = target.TryTakeOwnership(second);

        // Assert
        Assert.False(taken);
        Assert.Same(first, target.Owner);
    }
}
```

- [ ] **Step 6: Run to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Hosting.Tests --filter "FullyQualifiedName~HostedServiceTargetTests"`
Expected: FAIL to compile, `HostedServiceTarget` does not exist.

- [ ] **Step 7: Implement the target**

Create `src/Namotion.Interceptor.Hosting/HostedServiceTarget.cs`:

```csharp
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// One managed thing: either a subject that implements <see cref="IHostedService"/>, or a factory
/// attachment. Owns a serialized transition chain so start, stop and dispose for this target never
/// interleave, while transitions for unrelated targets run concurrently.
/// </summary>
internal sealed class HostedServiceTarget
{
    private readonly object _sync = new();

    private Task _tail = Task.CompletedTask;
    private IHostedService? _current;
    private Exception? _fault;
    private HostedServiceHandler? _owner;

    public HostedServiceTarget(Func<IHostedService>? factory, IHostedService? subject)
    {
        Factory = factory;
        Subject = subject;
    }

    /// <summary>The factory for an attachment, or null when this target is a subject.</summary>
    public Func<IHostedService>? Factory { get; }

    /// <summary>The subject when this target is a subject, or null when it is an attachment.</summary>
    public IHostedService? Subject { get; }

    /// <summary>True when the handler created the current instance and must therefore dispose it.</summary>
    public bool IsHandlerOwnedInstance => Factory is not null;

    public IHostedService? Current => Volatile.Read(ref _current);

    public Exception? Fault => Volatile.Read(ref _fault);

    public HostedServiceHandler? Owner => Volatile.Read(ref _owner);

    public void SetCurrent(IHostedService? instance) => Volatile.Write(ref _current, instance);

    public void SetFault(Exception? fault) => Volatile.Write(ref _fault, fault);

    /// <summary>
    /// Takes ownership for the given handler. Finding this handler already installed counts as
    /// success; only losing to a different handler returns false.
    /// </summary>
    public bool TryTakeOwnership(HostedServiceHandler handler)
    {
        var previous = Interlocked.CompareExchange(ref _owner, handler, null);
        return previous is null || ReferenceEquals(previous, handler);
    }

    public void ReleaseOwnership(HostedServiceHandler handler)
        => Interlocked.CompareExchange(ref _owner, null, handler);

    /// <summary>
    /// Appends a transition to this target's chain and returns a task that completes when it has run.
    /// Appending never blocks and never runs the body, so callers may append while holding a lock.
    /// </summary>
    public Task AppendAsync(Func<CancellationToken, Task> body, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            // The lock is required: "_tail = _tail.ContinueWith(...)" is a read-modify-write and
            // two racing appenders lose an assignment, running both transitions concurrently.
            // TaskScheduler.Default is required: ContinueWith otherwise captures TaskScheduler.Current,
            // which can be a scheduler the appending task is itself occupying.
            _tail = _tail
                .ContinueWith(
                    _ => RunAsync(body, cancellationToken),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();

            return _tail;
        }
    }

    private static async Task RunAsync(Func<CancellationToken, Task> body, CancellationToken cancellationToken)
    {
        // Bodies never throw. A faulted tail would raise UnobservedTaskException for every dropped
        // fire and forget transition and would be retained until the target transitions again.
        try
        {
            await body(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Handled by the body itself, which records into Fault and logs. This catch only
            // guarantees the chain stays unfaulted.
        }
    }
}
```

- [ ] **Step 8: Run to verify the target tests pass**

Run: `dotnet test src/Namotion.Interceptor.Hosting.Tests --filter "FullyQualifiedName~HostedServiceTargetTests"`
Expected: PASS, 5 tests. If `WhenTransitionsAreAppendedConcurrently` fails with `maximumConcurrent > 1`, the lock or the scheduler argument was dropped.

- [ ] **Step 9: Commit**

```bash
git add src/Namotion.Interceptor.Hosting src/Namotion.Interceptor.Hosting.Tests
git commit -m "feat: add the hosted service gate and per target transition chain

Two primitives the handler rewrite needs: a four state startup and shutdown
gate that only ratchets forward, and a target that serializes its own start,
stop and dispose transitions without coupling unrelated services."
```

---

## Task 4: Rewrite the handler and the attachment API

The core of the change. The handler and the extension methods are rewritten together because the extension methods are the handler's only entry point, so they cannot compile apart.

**Files:**
- Create: `src/Namotion.Interceptor.Hosting/HostedServiceAttachment.cs`
- Rewrite: `src/Namotion.Interceptor.Hosting/HostedServiceHandler.cs`
- Rewrite: `src/Namotion.Interceptor.Hosting/InterceptorHostingExtensions.cs`
- Rewrite: `src/Namotion.Interceptor.Hosting.Tests/HostedServiceHandlerTests.cs`
- Modify: `src/Namotion.Interceptor.Hosting.Tests/WithHostedServicesTests.cs` (follow the API change)

**Interfaces:**
- Consumes: `HostedServiceGate`, `HostedServiceTarget` from Task 3.
- Produces:
  - `public interface IHostedServiceAttachment { IHostedService? Current { get; } Exception? Fault { get; } }`
  - `public interface IHostedServiceAttachment<out T> : IHostedServiceAttachment where T : class, IHostedService { new T? Current { get; } }`
  - `IHostedServiceAttachment<T> AttachHostedService<T>(this IInterceptorSubject, Func<T>) where T : class, IHostedService`
  - `Task<IHostedServiceAttachment<T>> AttachHostedServiceAsync<T>(this IInterceptorSubject, Func<T>, CancellationToken) where T : class, IHostedService`
  - `bool DetachHostedService(this IInterceptorSubject, IHostedServiceAttachment)`
  - `Task<bool> DetachHostedServiceAsync(this IInterceptorSubject, IHostedServiceAttachment, CancellationToken)`
  - `ImmutableArray<IHostedServiceAttachment> GetHostedServiceAttachments(this IInterceptorSubject)`
  - On `HostedServiceHandler`: `internal Task EnsureStartedAsync()`, `internal Task WaitForStartAsync(IInterceptorSubject, IHostedService, CancellationToken)`

Read the spec's "Concurrency: per target serialization" section in full before writing any code in this task. Five decisions in it are counter intuitive and each has a measured failure behind it.

- [ ] **Step 1: Write the failing attachment lifecycle tests**

Replace the whole of `src/Namotion.Interceptor.Hosting.Tests/HostedServiceHandlerTests.cs` with the tests below. They encode the design's non obvious guarantees; the wording of each comment is the reason the assertion exists.

```csharp
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

public class HostedServiceHandlerTests
{
    [Fact]
    public async Task WhenSubjectImplementsIHostedService_ThenItIsStartedAndStopped()
    {
        // Arrange
        PersonWithBackgroundService person = null!;

        // Act
        await RunWithAppLifecycleAsync(async context =>
        {
            person = new PersonWithBackgroundService(context);
            await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "John");

            // Assert
            Assert.Equal("John", person.FirstName);
        });

        await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "Disposed");
    }

    [Fact]
    public async Task WhenAttachmentIsCreated_ThenTheFactoryProducesTheRunningInstance()
    {
        // Arrange
        await RunWithAppLifecycleAsync(async context =>
        {
            var person = new Person(context);

            // Act
            var attachment = await person.AttachHostedServiceAsync(
                () => new PersonBackgroundService(person), CancellationToken.None);

            // Assert
            Assert.NotNull(attachment.Current);
            Assert.Equal("John", person.FirstName);
        });
    }

    [Fact]
    public async Task WhenSubjectIsDetachedAndReattached_ThenAFreshInstanceRuns()
    {
        // Arrange - this is the whole point of the factory API. The pre-detach instance must be
        // disposed and a NEW one created; restarting the old one is impossible because a disposed
        // connector cannot restart.
        await RunWithAppLifecycleAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var created = new List<TrackedBackgroundService>();

            child.AttachHostedService(() =>
            {
                var instance = new TrackedBackgroundService();
                created.Add(instance);
                return instance;
            });

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => created.Count == 1 && created[0].IsStarted);

            // Act - detach and reattach with no quiescing in between
            parent.Child = null;
            parent.Child = child;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => created.Count == 2 && created[1].IsStarted,
                message: "The re-attach did not create a second instance.");
            await AsyncTestHelpers.WaitUntilAsync(() => created[0].IsDisposed,
                message: "The pre-detach instance was never disposed.");
            Assert.False(created[1].IsDisposed);
        });
    }

    [Fact]
    public async Task WhenSubjectIsDetached_ThenTheInstanceIsDisposedExactlyOnce()
    {
        // Arrange
        await RunWithAppLifecycleAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var instance = new TrackedBackgroundService();
            child.AttachHostedService(() => instance);
            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => instance.IsStarted);

            // Act
            parent.Child = null;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => instance.IsDisposed);
            Assert.Equal(1, instance.DisposeCount);
        });
    }

    [Fact]
    public async Task WhenAttachmentIsDetachedExplicitly_ThenALaterContextAttachStartsNothing()
    {
        // Arrange
        await RunWithAppLifecycleAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var created = 0;
            var attachment = child.AttachHostedService(() =>
            {
                created++;
                return new TrackedBackgroundService();
            });

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => created == 1);

            // Act
            await child.DetachHostedServiceAsync(attachment, CancellationToken.None);
            parent.Child = null;
            parent.Child = child;

            // Assert
            Assert.Empty(child.GetHostedServiceAttachments());
            await Task.Yield();
            Assert.Equal(1, created);
        });
    }

    [Fact]
    public async Task WhenTheFactoryThrows_ThenTheFaultIsRecordedAndCurrentStaysNull()
    {
        // Arrange
        await RunWithAppLifecycleAsync(async context =>
        {
            var person = new Person(context);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                person.AttachHostedServiceAsync<TrackedBackgroundService>(
                    () => throw new InvalidOperationException("factory failed"), CancellationToken.None));

            Assert.Equal("factory failed", exception.Message);

            // The transactional guarantee: a caller's catch is never left owning an invisible attachment
            Assert.Empty(person.GetHostedServiceAttachments());
        });
    }

    [Fact]
    public async Task WhenAStartFaults_ThenTheInstanceIsDisposed()
    {
        // Arrange - leaving a half started connector undisposed is the leak this design exists to fix
        await RunWithAppLifecycleAsync(async context =>
        {
            var person = new Person(context);
            var instance = new TrackedBackgroundService { ThrowOnStart = true };

            // Act
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                person.AttachHostedServiceAsync(() => instance, CancellationToken.None));

            // Assert
            Assert.True(instance.IsDisposed);
        });
    }

    [Fact]
    public async Task WhenATransitionFaultedEarlier_ThenTheNextSuccessfulOneClearsTheFault()
    {
        // Arrange - a stale Fault would make a later successful attach throw, and the OPC UA wrappers
        // would turn that into Status = Error with a stale message.
        await RunWithAppLifecycleAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var shouldThrow = true;

            var attachment = child.AttachHostedService(() =>
            {
                if (shouldThrow)
                {
                    shouldThrow = false;
                    throw new InvalidOperationException("first attempt fails");
                }

                return new TrackedBackgroundService();
            });

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => attachment.Fault is not null);

            // Act
            parent.Child = null;
            parent.Child = child;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => attachment.Current is not null);
            Assert.Null(attachment.Fault);
        });
    }

    [Fact]
    public async Task WhenHostStops_ThenHandlerCreatedInstancesAreDisposedAndSubjectsAreNot()
    {
        // Arrange
        var instance = new TrackedBackgroundService();
        PersonWithBackgroundService person = null!;

        await RunWithAppLifecycleAsync(async context =>
        {
            person = new PersonWithBackgroundService(context);
            await person.AttachHostedServiceAsync(() => instance, CancellationToken.None);
        });

        // Assert - the container disposes AddSubject singletons, so the claim under test is
        // specifically that the HANDLER did not dispose the subject.
        Assert.True(instance.IsDisposed);
        Assert.False(person.WasDisposedByHandler);
    }

    [Fact]
    public async Task WhenReparentedWithoutReachingZeroReferences_ThenNothingRestarts()
    {
        // Arrange - add-then-remove keeps the reference count above zero, so isLastDetach never fires
        await RunWithAppLifecycleAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var created = 0;
            child.AttachHostedService(() =>
            {
                created++;
                return new TrackedBackgroundService();
            });

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => created == 1);

            // Act
            parent.SecondChild = child;
            parent.Child = null;

            // Assert
            await Task.Yield();
            Assert.Equal(1, created);
        });
    }

    private static async Task RunWithAppLifecycleAsync(Func<IInterceptorSubjectContext, Task> action)
    {
        var builder = Host.CreateApplicationBuilder();

        // WithContextInheritance, not just WithLifecycle: without it a child subject's Context never
        // resolves the handler and every child scenario below is silently unreachable.
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var host = builder.Build();
        await host.StartAsync();
        try
        {
            await action(context);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
```

- [ ] **Step 2: Add the test models the tests need**

Create `src/Namotion.Interceptor.Hosting.Tests/Models/TrackedBackgroundService.cs`:

```csharp
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting.Tests.Models;

public sealed class TrackedBackgroundService : IHostedService, IAsyncDisposable
{
    private int _disposeCount;

    public bool ThrowOnStart { get; init; }

    public bool IsStarted { get; private set; }

    public bool IsStopped { get; private set; }

    public bool IsDisposed => Volatile.Read(ref _disposeCount) > 0;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (ThrowOnStart)
        {
            throw new InvalidOperationException("start failed");
        }

        IsStarted = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        IsStopped = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return ValueTask.CompletedTask;
    }
}
```

Create `src/Namotion.Interceptor.Hosting.Tests/Models/Parent.cs`:

```csharp
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

[InterceptorSubject]
public partial class Parent
{
    public partial Person? Child { get; set; }

    public partial Person? SecondChild { get; set; }
}
```

Add the disposal probe to `src/Namotion.Interceptor.Hosting.Tests/PersonWithBackgroundService.cs` by adding this member inside the class:

```csharp
public bool WasDisposedByHandler { get; private set; }

public override void Dispose()
{
    WasDisposedByHandler = true;
    base.Dispose();
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Hosting.Tests --filter "FullyQualifiedName~HostedServiceHandlerTests"`
Expected: FAIL to compile. `AttachHostedService(Func<T>)`, `GetHostedServiceAttachments` and `IHostedServiceAttachment` do not exist yet.

- [ ] **Step 4: Add the attachment handle**

Create `src/Namotion.Interceptor.Hosting/HostedServiceAttachment.cs`:

```csharp
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// A hosted service bound to a subject. The handler creates the instance when the subject enters the
/// graph and disposes it when the subject leaves, so the same attachment yields a fresh instance on
/// every re-attach.
/// </summary>
public interface IHostedServiceAttachment
{
    /// <summary>The running instance, or null when nothing is running.</summary>
    IHostedService? Current { get; }

    /// <summary>The exception from the last failed transition, or null.</summary>
    Exception? Fault { get; }
}

/// <inheritdoc />
public interface IHostedServiceAttachment<out T> : IHostedServiceAttachment
    where T : class, IHostedService
{
    /// <inheritdoc cref="IHostedServiceAttachment.Current" />
    new T? Current { get; }
}

/// <summary>
/// Lets the handler reach the target from a non generic attachment. An abstract base class cannot
/// serve here: the generic and non generic <c>Current</c> differ only by return type, so declaring
/// both on one class is CS0102. The non generic one is implemented explicitly instead.
/// </summary>
internal interface IHostedServiceAttachmentTarget
{
    HostedServiceTarget Target { get; }
}

internal sealed class HostedServiceAttachment<T> : IHostedServiceAttachment<T>, IHostedServiceAttachmentTarget
    where T : class, IHostedService
{
    public HostedServiceAttachment(HostedServiceTarget target)
    {
        Target = target;
    }

    public HostedServiceTarget Target { get; }

    public T? Current => (T?)Target.Current;

    public Exception? Fault => Target.Fault;

    IHostedService? IHostedServiceAttachment.Current => Target.Current;
}
```

- [ ] **Step 5: Rewrite the handler**

Replace the whole of `src/Namotion.Interceptor.Hosting/HostedServiceHandler.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting;

internal sealed class HostedServiceHandler : IHostedService, ILifecycleHandler, IDisposable
{
    private const int StartDelayMilliseconds = 50;

    private readonly Func<ILogger?> _loggerResolver;
    private readonly HostedServiceGate _gate = new();
    private readonly ConcurrentDictionary<HostedServiceTarget, IInterceptorSubject> _running = new();
    private readonly ConcurrentDictionary<IInterceptorSubject, byte> _liveSubjects = new();

    private ILogger? _logger;

    public HostedServiceHandler(Func<ILogger?> loggerResolver)
    {
        _loggerResolver = loggerResolver;
    }

    private ILogger? Logger => _logger ??= _loggerResolver();

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        // Invoked from inside LifecycleInterceptor's lock (_attachedSubjects). Everything here must
        // only append; appending never blocks and never runs user code.
        if (change.IsContextAttach)
        {
            AttachSubject(change.Subject);
        }
        else if (change.IsContextDetach)
        {
            DetachSubject(change.Subject);
        }
    }

    private void AttachSubject(IInterceptorSubject subject)
    {
        _liveSubjects[subject] = 0;

        if (subject is IHostedService hostedService)
        {
            var target = subject.GetOrAddSubjectTarget(hostedService);
            if (target.TryTakeOwnership(this))
            {
                AppendStart(subject, target, CancellationToken.None);
            }
        }

        foreach (var attachment in subject.GetHostedServiceAttachments())
        {
            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            if (target.TryTakeOwnership(this))
            {
                AppendStart(subject, target, CancellationToken.None);
            }
        }
    }

    private void DetachSubject(IInterceptorSubject subject)
    {
        // Liveness is per subject and cleared here, under the lifecycle lock. It cannot be per target,
        // because the attaching path takes target ownership itself and would pass its own check.
        _liveSubjects.TryRemove(subject, out _);

        var subjectStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subjectTarget = subject.TryGetSubjectTarget();

        // Stops are appended NOW, not issued later from inside another transition. Deferring them
        // lets a re-attach's create land first on the attachment chain, after which the deferred stop
        // disposes the NEW instance and leaks the old one.
        if (subjectTarget is not null)
        {
            AppendStop(subject, subjectTarget, subjectStopped, waitFor: null, CancellationToken.None);
        }
        else
        {
            subjectStopped.TrySetResult();
        }

        foreach (var attachment in subject.GetHostedServiceAttachments())
        {
            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            AppendStop(subject, target, signal: null, waitFor: subjectStopped.Task, CancellationToken.None);
        }

        // Released after the stops are appended, and never from inside a transition body: releasing
        // from the body would clobber ownership a re-attach has already retaken, and the re-attach's
        // start would then no-op itself.
        subjectTarget?.ReleaseOwnership(this);
        foreach (var attachment in subject.GetHostedServiceAttachments())
        {
            ((IHostedServiceAttachmentTarget)attachment).Target.ReleaseOwnership(this);
        }
    }

    internal Task AppendStart(IInterceptorSubject subject, HostedServiceTarget target, CancellationToken cancellationToken)
    {
        _running[target] = subject;

        return target.AppendAsync(async _ =>
        {
            target.SetFault(null);

            await _gate.WaitForOpenAsync().ConfigureAwait(false);
            if (_gate.State != HostedServiceGateState.Running)
            {
                // Read inside the body, never at append time: a start already queued when shutdown
                // begins must re-read the state, and a body skipped at append time would never run
                // its signalling.
                return;
            }

            if (!_liveSubjects.ContainsKey(subject) || !ReferenceEquals(target.Owner, this))
            {
                return;
            }

            try
            {
                await Task.Delay(StartDelayMilliseconds, cancellationToken).ConfigureAwait(false);

                var instance = target.Subject ?? target.Factory!();
                try
                {
                    await instance.StartAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    if (target.IsHandlerOwnedInstance)
                    {
                        await DisposeInstanceAsync(instance).ConfigureAwait(false);
                    }

                    throw;
                }

                target.SetCurrent(instance);
            }
            catch (Exception exception)
            {
                target.SetFault(exception);
                Logger?.LogError(exception, "Failed to start hosted service for subject {Subject}.", subject);
            }
        }, cancellationToken);
    }

    internal Task AppendStop(
        IInterceptorSubject subject,
        HostedServiceTarget target,
        TaskCompletionSource? signal,
        Task? waitFor,
        CancellationToken cancellationToken)
    {
        _running.TryRemove(target, out _);

        return target.AppendAsync(async _ =>
        {
            try
            {
                if (waitFor is not null)
                {
                    // Orders a subject's stop ahead of its attachments. Acyclic: the subject's chain
                    // waits on nothing. A hosted service must therefore not detach an attachment from
                    // inside its own stop path, or this becomes a cycle.
                    await waitFor.ConfigureAwait(false);
                }

                await _gate.WaitForOpenAsync().ConfigureAwait(false);
                if (_gate.State == HostedServiceGateState.Drained)
                {
                    return;
                }

                var instance = target.Current;
                if (instance is null)
                {
                    return;
                }

                target.SetCurrent(null);

                await Task.Delay(StartDelayMilliseconds, CancellationToken.None).ConfigureAwait(false);

                try
                {
                    await instance.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    target.SetFault(exception);
                    Logger?.LogError(exception, "Failed to stop hosted service for subject {Subject}.", subject);
                }

                if (target.IsHandlerOwnedInstance)
                {
                    await DisposeInstanceAsync(instance).ConfigureAwait(false);
                }
            }
            finally
            {
                // Always signals, including on the gated-out and cancelled paths, or a paired
                // attachment stop parks forever on a signal that is never set.
                signal?.TrySetResult();
            }
        }, cancellationToken);
    }

    private async Task DisposeInstanceAsync(IHostedService instance)
    {
        try
        {
            switch (instance)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception exception)
        {
            // Detach runs inside a property write, so throwing here would surface at an unrelated assignment.
            Logger?.LogError(exception, "Failed to dispose hosted service {Service}.", instance.ToString());
        }
    }

    internal Task EnsureStartedAsync()
    {
        _gate.EnsureStarted();
        return Task.CompletedTask;
    }

    internal async Task WaitForStartAsync(IInterceptorSubject subject, IHostedService hostedService, CancellationToken cancellationToken)
    {
        var target = subject.GetOrAddSubjectTarget(hostedService);
        target.TryTakeOwnership(this);

        await target.AppendAsync(_ => Task.CompletedTask, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

        if (target.Fault is { } fault)
        {
            throw fault;
        }
    }

    internal bool IsLive(IInterceptorSubject subject) => _liveSubjects.ContainsKey(subject);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger ??= _loggerResolver();
        return EnsureStartedAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _gate.BeginDraining();

        var snapshot = _running.ToArray();
        var stops = new List<Task>(snapshot.Length);

        foreach (var (target, subject) in snapshot)
        {
            stops.Add(AppendStop(subject, target, signal: null, waitFor: null, cancellationToken));
        }

        await Task.WhenAll(stops).ConfigureAwait(false);

        foreach (var (target, _) in snapshot)
        {
            // Released after the stops so a second host cannot start ahead of this host's stop,
            // and released at all so a second host over the same subjects is not blocked forever.
            target.ReleaseOwnership(this);
        }

        _gate.CompleteDraining();
    }

    public void Dispose()
    {
    }
}
```

- [ ] **Step 6: Rewrite the extension methods**

Replace the whole of `src/Namotion.Interceptor.Hosting/InterceptorHostingExtensions.cs`:

```csharp
using System.Collections.Immutable;
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Extension methods for attaching and detaching hosted services to and from interceptor subjects.
/// </summary>
public static class InterceptorHostingExtensions
{
    private const string AttachmentsKey = "Namotion.Hosting.HostedServiceAttachments";
    private const string SubjectTargetKey = "Namotion.Hosting.SubjectTarget";

    /// <summary>
    /// Gets an immutable snapshot of the hosted service attachments on the subject.
    /// </summary>
    public static ImmutableArray<IHostedServiceAttachment> GetHostedServiceAttachments(this IInterceptorSubject subject)
    {
        var value = subject.Data.GetOrAdd((null, AttachmentsKey), _ => null);
        return value is ImmutableArray<IHostedServiceAttachment> attachments ? attachments : [];
    }

    /// <summary>
    /// Attaches a hosted service factory to the subject. The handler invokes the factory when the
    /// subject enters the graph and disposes the instance when it leaves, so a re-attach yields a
    /// fresh instance. The factory must construct: returning an existing instance breaks the design,
    /// because a re-attach would start an instance the handler has already disposed.
    /// </summary>
    public static IHostedServiceAttachment<T> AttachHostedService<T>(
        this IInterceptorSubject subject, Func<T> factory)
        where T : class, IHostedService
    {
        var attachment = AddAttachment(subject, factory);

        var handler = subject.Context.TryGetService<HostedServiceHandler>();
        if (handler is not null && handler.IsLive(subject) && attachment.Target.TryTakeOwnership(handler))
        {
            handler.AppendStart(subject, attachment.Target, CancellationToken.None);
        }

        return attachment;
    }

    /// <summary>
    /// Attaches a hosted service factory and waits for the instance to start. Transactional: when the
    /// start faults, the attachment is removed before the exception propagates.
    /// </summary>
    public static async Task<IHostedServiceAttachment<T>> AttachHostedServiceAsync<T>(
        this IInterceptorSubject subject, Func<T> factory, CancellationToken cancellationToken)
        where T : class, IHostedService
    {
        var attachment = AddAttachment(subject, factory);

        var handler = subject.Context.TryGetService<HostedServiceHandler>();
        if (handler is null)
        {
            // No handler means no context to bound the lifetime, so the factory is stored and nothing runs.
            return attachment;
        }

        await handler.EnsureStartedAsync().ConfigureAwait(false);

        if (handler.IsLive(subject) && attachment.Target.TryTakeOwnership(handler))
        {
            await handler
                .AppendStart(subject, attachment.Target, cancellationToken)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (attachment.Fault is { } fault)
        {
            RemoveAttachment(subject, attachment);
            throw fault;
        }

        return attachment;
    }

    /// <summary>
    /// Detaches a hosted service attachment. The instance is stopped, disposed and forgotten, and the
    /// factory is removed, so a later context attach starts nothing.
    /// </summary>
    public static bool DetachHostedService(this IInterceptorSubject subject, IHostedServiceAttachment attachment)
    {
        if (!RemoveAttachment(subject, attachment))
        {
            return false;
        }

        var target = ((IHostedServiceAttachmentTarget)attachment).Target;
        var handler = subject.Context.TryGetService<HostedServiceHandler>();
        handler?.AppendStop(subject, target, signal: null, waitFor: null, CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Detaches a hosted service attachment and waits for the instance to stop and be disposed.
    /// </summary>
    public static async Task<bool> DetachHostedServiceAsync(
        this IInterceptorSubject subject, IHostedServiceAttachment attachment, CancellationToken cancellationToken)
    {
        if (!RemoveAttachment(subject, attachment))
        {
            return false;
        }

        var target = ((IHostedServiceAttachmentTarget)attachment).Target;
        var handler = subject.Context.TryGetService<HostedServiceHandler>();
        if (handler is null)
        {
            return true;
        }

        await handler.EnsureStartedAsync().ConfigureAwait(false);
        await handler
            .AppendStop(subject, target, signal: null, waitFor: null, cancellationToken)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    private static HostedServiceAttachment<T> AddAttachment<T>(IInterceptorSubject subject, Func<T> factory)
        where T : class, IHostedService
    {
        // Built outside the update delegate: ConcurrentDictionary may invoke that delegate more than
        // once and does not roll back its side effects, so constructing the record inside it could
        // register a target that loses the compare-and-swap and is never seen again.
        var attachment = new HostedServiceAttachment<T>(new HostedServiceTarget(factory, subject: null));

        subject.Data.AddOrUpdate((null, AttachmentsKey),
            _ => ImmutableArray.Create<IHostedServiceAttachment>(attachment),
            (_, value) => value is ImmutableArray<IHostedServiceAttachment> attachments
                ? attachments.Add(attachment)
                : ImmutableArray.Create<IHostedServiceAttachment>(attachment));

        return attachment;
    }

    private static bool RemoveAttachment(IInterceptorSubject subject, IHostedServiceAttachment attachment)
    {
        var removed = false;

        subject.Data.AddOrUpdate((null, AttachmentsKey),
            _ => null,
            (_, value) =>
            {
                if (value is not ImmutableArray<IHostedServiceAttachment> attachments || !attachments.Contains(attachment))
                {
                    return value;
                }

                removed = true;
                var updated = attachments.Remove(attachment);
                return updated.Length > 0 ? updated : null;
            });

        return removed;
    }

    internal static HostedServiceTarget GetOrAddSubjectTarget(this IInterceptorSubject subject, IHostedService hostedService)
    {
        var target = new HostedServiceTarget(factory: null, subject: hostedService);
        var stored = subject.Data.GetOrAdd((null, SubjectTargetKey), _ => target);
        return stored as HostedServiceTarget ?? target;
    }

    internal static HostedServiceTarget? TryGetSubjectTarget(this IInterceptorSubject subject)
        => subject.Data.GetOrAdd((null, SubjectTargetKey), _ => null) as HostedServiceTarget;
}
```

- [ ] **Step 7: Update the Task 2 test to the new API**

In `WithHostedServicesTests.cs`, replace the two `AttachHostedService(new PersonBackgroundService(...))` calls with factory form:

```csharp
firstPerson.AttachHostedService(() => new PersonBackgroundService(firstPerson));
secondPerson.AttachHostedService(() => new PersonBackgroundService(secondPerson));
```

- [ ] **Step 8: Build**

Run: `dotnet build src/Namotion.Interceptor.Hosting.Tests`
Expected: succeeds. Fix any nullability or unused-using warnings; warnings are errors here.

- [ ] **Step 9: Run the hosting tests**

Run: `dotnet test src/Namotion.Interceptor.Hosting.Tests`
Expected: all pass. If `WhenSubjectIsDetachedAndReattached_ThenAFreshInstanceRuns` fails with one instance created, the detach path is issuing its stops lazily rather than appending them at event time.

- [ ] **Step 10: Accept the API snapshot**

```bash
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Hosting.Tests --filter "FullyQualifiedName~PublicApi"
mv src/Namotion.Interceptor.Hosting.Tests/VerifyChecksTests.PublicApi.received.txt \
   src/Namotion.Interceptor.Hosting.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Review the diff before accepting. It must show the four instance based methods and `GetAttachedHostedServices` removed, and the factory based methods plus `IHostedServiceAttachment` added.

- [ ] **Step 11: Commit**

```bash
git add src/Namotion.Interceptor.Hosting src/Namotion.Interceptor.Hosting.Tests
git commit -m "feat: give the hosted service handler a single owner per target

Replaces the global action loop with one serialized transition chain per
managed target, and makes attachment factory based so the handler creates
and therefore disposes every instance it starts.

Stops are appended when the lifecycle event fires rather than issued from
inside another transition, which is what lets a detach immediately followed
by an attach produce one fresh running instance instead of disposing the new
one and leaking the old."
```

---

## Task 5: `AddSubject<T>` replaces `AddHostedSubject<T>`

**Files:**
- Delete: `src/Namotion.Interceptor.Hosting/HostedSubjectServiceCollectionExtensions.cs`
- Create: `src/Namotion.Interceptor.Hosting/SubjectServiceCollectionExtensions.cs`
- Create: `src/Namotion.Interceptor.Hosting/SubjectActivation.cs`
- Create: `src/Namotion.Interceptor.Hosting.Tests/AddSubjectTests.cs`
- Create: `src/Namotion.Interceptor.Hosting.Tests/Models/SubjectWithDependencies.cs`

**Interfaces:**
- Consumes: `HostedServiceHandler.EnsureStartedAsync`, `HostedServiceHandler.WaitForStartAsync` from Task 4.
- Produces: `public static IServiceCollection AddSubject<T>(this IServiceCollection services, Action<T>? configure = null, Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null) where T : class, IInterceptorSubject`

- [ ] **Step 1: Write the failing tests**

Create `src/Namotion.Interceptor.Hosting.Tests/Models/SubjectWithDependencies.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A subject whose only declared constructor takes dependencies, so the generator emits no
/// (IInterceptorSubjectContext) constructor. This is the shape every HomeBlaze device has.
/// </summary>
[InterceptorSubject]
public partial class SubjectWithDependencies : BackgroundService
{
    private readonly ILogger<SubjectWithDependencies> _logger;

    public partial string? Name { get; set; }

    public int StartCount;

    public SubjectWithDependencies(ILogger<SubjectWithDependencies> logger)
    {
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref StartCount);
        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
```

Create `src/Namotion.Interceptor.Hosting.Tests/AddSubjectTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

public class AddSubjectTests
{
    [Fact]
    public async Task WhenSubjectHasGeneratedContextConstructor_ThenItStartsExactlyOnce()
    {
        // Arrange - the context attach starts it and AddHostedService started it again: two starts,
        // a second execute task and an orphaned token source.
        var builder = Host.CreateApplicationBuilder();
        var context = CreateContext(builder);
        builder.Services.AddSingleton(context);
        builder.Services.AddSubject<PersonWithBackgroundService>();

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            // Act
            var subject = host.Services.GetRequiredService<PersonWithBackgroundService>();

            // Assert
            Assert.Equal(1, subject.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenSubjectHasDependencyInjectedConstructor_ThenItIsStillAttachedToTheContext()
    {
        // Arrange - the generator emits the (IInterceptorSubjectContext) constructor only when the
        // first declared constructor is parameterless, so this shape used to get no context at all.
        var builder = Host.CreateApplicationBuilder();
        var context = CreateContext(builder);
        builder.Services.AddSingleton(context);
        builder.Services.AddSubject<SubjectWithDependencies>();

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            // Act
            var subject = host.Services.GetRequiredService<SubjectWithDependencies>();

            // Assert - registry membership is the observable for "attached to the context"
            var registry = context.GetService<ISubjectRegistry>();
            Assert.Contains(registry.KnownSubjects, known => ReferenceEquals(known.Key, subject));
            Assert.Equal(1, subject.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenThereIsNoHostingHandler_ThenTheActivatorStartsTheSubjectItself()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        var context = InterceptorSubjectContext.Create().WithContextInheritance();
        builder.Services.AddSingleton(context);
        builder.Services.AddSubject<SubjectWithDependencies>();

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            // Act
            var subject = host.Services.GetRequiredService<SubjectWithDependencies>();

            // Assert
            Assert.Equal(1, subject.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAddSubjectIsCalledTwice_ThenOnlyOneActivationIsRegistered()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        var context = CreateContext(builder);
        builder.Services.AddSingleton(context);
        builder.Services.AddSubject<SubjectWithDependencies>();
        builder.Services.AddSubject<SubjectWithDependencies>();

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            // Act
            var subject = host.Services.GetRequiredService<SubjectWithDependencies>();

            // Assert
            Assert.Equal(1, subject.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAddSubjectIsRegisteredBeforeWithHostedServices_ThenStartupDoesNotHang()
    {
        // Arrange - the activator awaits a transition gated on the handler having started. Without
        // EnsureStartedAsync opening the gate, host startup would deadlock on registration order.
        var builder = Host.CreateApplicationBuilder();

        var contextHolder = new IInterceptorSubjectContext[1];
        builder.Services.AddSingleton(_ => contextHolder[0]!);
        builder.Services.AddSubject<SubjectWithDependencies>();

        contextHolder[0] = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithRegistry()
            .WithHostedServices(builder.Services);

        var host = builder.Build();

        // Act
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            // Assert
            var subject = host.Services.GetRequiredService<SubjectWithDependencies>();
            Assert.Equal(1, subject.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IInterceptorSubjectContext CreateContext(HostApplicationBuilder builder)
        => InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithRegistry()
            .WithHostedServices(builder.Services);
}
```

Add a `StartCount` probe to `PersonWithBackgroundService` so the first test can assert it. Inside the class add:

```csharp
public int StartCount;

public override Task StartAsync(CancellationToken cancellationToken)
{
    Interlocked.Increment(ref StartCount);
    return base.StartAsync(cancellationToken);
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Hosting.Tests --filter "FullyQualifiedName~AddSubjectTests"`
Expected: FAIL to compile, `AddSubject` does not exist.

- [ ] **Step 3: Add the activator**

Create `src/Namotion.Interceptor.Hosting/SubjectActivation.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Forces construction of a DI registered subject at host start. A singleton nobody resolves is never
/// built, never attached to its context and never started, and <see cref="IHostedService"/> is the
/// only hook the generic host offers for forcing that construction.
/// </summary>
internal sealed class SubjectActivation<T> : IHostedService
    where T : class, IInterceptorSubject
{
    private readonly IServiceProvider _serviceProvider;

    private T? _subject;

    public SubjectActivation(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolving constructs the subject, which attaches it to the context, which makes the
        // handler append its start. Start ownership stays with the handler.
        _subject = _serviceProvider.GetRequiredService<T>();

        if (_subject is not IHostedService hostedService)
        {
            return;
        }

        var handler = _subject.Context.TryGetService<HostedServiceHandler>();
        if (handler is null)
        {
            await hostedService.StartAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Opens the gate before awaiting, so a handler registered after this activator cannot deadlock
        // host startup, and awaits the start so a failing subject still aborts host startup the way
        // AddHostedService does.
        await handler.EnsureStartedAsync().ConfigureAwait(false);
        await handler.WaitForStartAsync(_subject, hostedService, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subject is IHostedService hostedService &&
            _subject.Context.TryGetService<HostedServiceHandler>() is null)
        {
            await hostedService.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 4: Replace the registration extension**

```bash
git rm src/Namotion.Interceptor.Hosting/HostedSubjectServiceCollectionExtensions.cs
```

Create `src/Namotion.Interceptor.Hosting/SubjectServiceCollectionExtensions.cs`:

```csharp
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Extension methods for registering subjects with dependency injection.
/// </summary>
public static class SubjectServiceCollectionExtensions
{
    /// <summary>
    /// Registers the subject as a singleton and constructs it at host start, attaching it to the
    /// context. When the subject is an <see cref="Microsoft.Extensions.Hosting.IHostedService"/> and
    /// the context has hosting enabled, the context starts it.
    /// </summary>
    /// <remarks>
    /// Registration is idempotent, which has a sharp edge worth knowing: a second call for the same
    /// type silently drops its <paramref name="configure"/> and <paramref name="contextResolver"/>,
    /// and if the caller already registered <typeparamref name="T"/> themselves, neither the context
    /// nor <paramref name="configure"/> is applied.
    /// </remarks>
    /// <typeparam name="T">The subject type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback applied to the instance after construction.</param>
    /// <param name="contextResolver">
    /// Optional resolver for the context. When null, the context is resolved from DI; when it returns
    /// null, the subject is registered without a context.
    /// </param>
    public static IServiceCollection AddSubject<T>(
        this IServiceCollection services,
        Action<T>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        where T : class, IInterceptorSubject
    {
        services.TryAddSingleton<T>(serviceProvider =>
        {
            var context = contextResolver is not null
                ? contextResolver(serviceProvider)
                : serviceProvider.GetService<IInterceptorSubjectContext>();

            // The constructor branch exists only because ActivatorUtilities throws when no constructor
            // can consume the extra argument. It confers no attachment advantage: the generated
            // constructor is "C(IInterceptorSubjectContext context) : this()", so the attach happens
            // after the parameterless constructor body either way.
            var instance = context is not null && HasContextConstructor<T>()
                ? ActivatorUtilities.CreateInstance<T>(serviceProvider, context)
                : ActivatorUtilities.CreateInstance<T>(serviceProvider);

            if (context is not null)
            {
                // Unconditional and idempotent. Applying it only when there is no context constructor
                // would leave the documented "MySubject(IInterceptorSubjectContext? context = null)"
                // shape unattached, because that constructor takes the context and never uses it.
                instance.Context.AddFallbackContext(context);
            }

            configure?.Invoke(instance);
            return instance;
        });

        services.AddHostedService<SubjectActivation<T>>();

        return services;
    }

    private static bool HasContextConstructor<T>()
    {
        return typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Any(constructor => constructor.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(IInterceptorSubjectContext)));
    }
}
```

- [ ] **Step 5: Add the Registry project reference the tests need**

In `src/Namotion.Interceptor.Hosting.Tests/Namotion.Interceptor.Hosting.Tests.csproj`, add to the `ProjectReference` item group:

```xml
<ProjectReference Include="..\Namotion.Interceptor.Registry\Namotion.Interceptor.Registry.csproj" />
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test src/Namotion.Interceptor.Hosting.Tests --filter "FullyQualifiedName~AddSubjectTests"`
Expected: PASS, 5 tests. `WhenSubjectHasGeneratedContextConstructor_ThenItStartsExactlyOnce` asserting 2 means the activator is still starting the subject itself when a handler exists.

- [ ] **Step 7: Run the whole hosting suite and accept the snapshot**

```bash
dotnet test src/Namotion.Interceptor.Hosting.Tests
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Hosting.Tests --filter "FullyQualifiedName~PublicApi"
mv src/Namotion.Interceptor.Hosting.Tests/VerifyChecksTests.PublicApi.received.txt \
   src/Namotion.Interceptor.Hosting.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Confirm the diff shows `AddHostedSubject` gone and `AddSubject` present.

- [ ] **Step 8: Commit**

```bash
git add -A src/Namotion.Interceptor.Hosting src/Namotion.Interceptor.Hosting.Tests
git commit -m "feat: replace AddHostedSubject with AddSubject

AddHostedSubject registered the subject as a hosted service in addition to
the context starting it, so a subject with the generated context constructor
started twice. It also passed the context only when a constructor accepted
one, which the generator emits only for parameterless shapes, so every
subject with injected constructor dependencies silently got no context.

AddSubject attaches the context unconditionally after construction and
leaves start ownership with the handler, using a per type activator to force
construction at host start."
```

---

## Task 6: Rename in the six device extensions

Mechanical, but it is the change that makes those six subjects participate in tracking for the first time.

**Files:**
- Modify: `src/HomeBlaze/Namotion.Devices.Shelly/ShellyServiceCollectionExtensions.cs:13`
- Modify: `src/HomeBlaze/Namotion.Devices.MyStrom/MyStromServiceCollectionExtensions.cs:13`
- Modify: `src/HomeBlaze/Namotion.Devices.Wallbox/WallboxServiceCollectionExtensions.cs:13`
- Modify: `src/HomeBlaze/Namotion.Devices.Gpio/GpioServiceCollectionExtensions.cs:27`
- Modify: `src/HomeBlaze/Namotion.Devices.Ecowitt/EcowittServiceCollectionExtensions.cs:13`
- Modify: `src/HomeBlaze/Namotion.Devices.Philips.Hue/HueServiceCollectionExtensions.cs:13`

**Interfaces:**
- Consumes: `AddSubject<T>` from Task 5.
- Produces: no public signature change. The `Add*Device` methods keep their exact signatures.

- [ ] **Step 1: Replace the call in each file**

In each of the six files, change `=> services.AddHostedSubject(configure, contextResolver);` to:

```csharp
=> services.AddSubject(configure, contextResolver);
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: succeeds.

- [ ] **Step 3: Run the device tests**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Gpio.Tests src/HomeBlaze/Namotion.Devices.MyStrom.Tests`

Expected: PASS. Note these two suites build a bare `BuildServiceProvider()` and assert property values, so they provide no regression coverage for the double start or the missing context and would pass unchanged even if Task 5 were reverted. The coverage lives in `AddSubjectTests`.

- [ ] **Step 4: Commit**

```bash
git add src/HomeBlaze
git commit -m "refactor: point the device extensions at AddSubject

Their signatures are unchanged, so consumers see no compile error, but the
behaviour is corrective: these subjects declare parameterised constructors,
so the generator emits no context constructor and they previously ran with
no context at all. They now participate in tracking and the registry, and
the configure callback now runs against an attached subject."
```

---

## Task 7: Migrate `HomeBlaze.OpcUa.OpcUaClient`

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaClient.cs`

**Interfaces:**
- Consumes: `AttachHostedServiceAsync`, `DetachHostedServiceAsync`, `IHostedServiceAttachment<T>` from Task 4.
- Produces: nothing consumed by later tasks.

Read the spec's "The two HomeBlaze wrappers" section first. The single most important change is that `ExecuteAsync`'s unwind must stop detaching, and the reason is a deadlock, not tidiness.

- [ ] **Step 1: Replace the field**

At `OpcUaClient.cs:27`, replace:

```csharp
private IOpcUaSubjectClientSource? _clientSource;
```

with:

```csharp
private IHostedServiceAttachment<IOpcUaSubjectClientSource>? _attachment;
```

- [ ] **Step 2: Rewrite the start path**

Replace the body of `StartClientAsync` from the `var rootPathSegments` line through the `await this.AttachHostedServiceAsync(...)` line (currently `:245-263`) with:

```csharp
if (_attachment is null)
{
    _attachment = await this.AttachHostedServiceAsync(() =>
    {
        var rootPathSegments = RootPath?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var root = new OpcUaDynamicSubject(rootPathSegments is { Length: > 0 } ? rootPathSegments[^1] : "Root");
        Root = root;

        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = ServerUrl,
            RootPath = rootPathSegments,
            DefaultSamplingInterval = SamplingInterval,
            TypeResolver = new HomeBlazeOpcUaTypeResolver(_logger),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new HomeBlazeOpcUaSubjectFactory(),
            CreateUserIdentity = !string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password)
                ? _ => Task.FromResult(new UserIdentity(Username, Encoding.UTF8.GetBytes(Password)))
                : null,
        };

        return root.CreateOpcUaClientSource(configuration, _logger);
    }, cancellationToken);
}
```

The factory reads `RootPath`, `ServerUrl`, `SamplingInterval`, `Username` and `Password` when it is invoked rather than capturing a snapshot, so a re-attach builds a source from the configuration that is current then. The guard is what keeps a restarted `ExecuteAsync` from creating a second source alongside the one the handler re-created from the surviving attachment.

- [ ] **Step 3: Rewrite the stop path**

Replace `StopClientAsync` (currently `:276-321`) with:

```csharp
private async Task StopClientAsync(CancellationToken cancellationToken)
{
    if (_attachment is null)
    {
        return;
    }

    try
    {
        Status = ServiceStatus.Stopping;
        await this.DetachHostedServiceAsync(_attachment, cancellationToken);
        _attachment = null;
        _logger.LogInformation("OPC UA client stopped");
    }
    catch (Exception ex)
    {
        // The field is deliberately left set when the detach failed: nulling it would leave the
        // attachment on the subject while the guard believes there is none, and the next start
        // would attach a second source.
        _logger.LogError(ex, "Failed to stop OPC UA client");
    }
    finally
    {
        Root = null;
        Status = ServiceStatus.Stopped;
        IsConnected = null;
        MonitoredItemCount = null;
        PollingItemCount = null;
        PendingWriteCount = null;
        TotalReconnections = null;
        IncomingChangesPerSecond = null;
        OutgoingChangesPerSecond = null;
    }
}
```

The manual `DisposeAsync` block and its explanatory comment are gone: the handler disposes what it created.

- [ ] **Step 4: Stop detaching from the unwind**

At `OpcUaClient.cs:207`, replace `await StopClientAsync(CancellationToken.None);` at the end of `ExecuteAsync` with:

```csharp
// Deliberately does NOT detach. ExecuteAsync unwinds inside the handler's own stop transition for
// this subject, and detaching from there waits on the attachment chain, whose head is waiting on
// this subject's stop to complete. The handler owns the detach on graph events; the explicit
// detach lives on the Stop operation and ApplyConfigurationAsync, neither of which is reached
// through StopAsync.
Status = ServiceStatus.Stopped;
IsConnected = null;
```

- [ ] **Step 5: Update the diagnostics poll**

In `UpdateDiagnostics` (currently `:216-229`), replace `if (_clientSource is { } source)` with:

```csharp
if (_attachment?.Current is { } source)
```

- [ ] **Step 6: Build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.OpcUa`
Expected: succeeds. Add `using Namotion.Interceptor.Hosting;` if the attachment type does not resolve.

- [ ] **Step 7: Run the OPC UA unit tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category!=Integration"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.OpcUa/OpcUaClient.cs
git commit -m "refactor: move the OPC UA client wrapper to factory attachment

The handler now creates and disposes the client source, so the manual
dispose block goes away, and the factory reads the current configuration on
each invocation so a re-attach does not rebuild a stale source.

ExecuteAsync's unwind no longer detaches. It runs inside the handler's stop
transition for this subject, so detaching there waits on the attachment
chain whose head is waiting on this subject's stop, which deadlocks."
```

---

## Task 8: Migrate `HomeBlaze.OpcUa.OpcUaServer`

Same three changes, with one real difference: its target subject comes from a path lookup rather than a construction.

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaServer.cs`

**Interfaces:**
- Consumes: the same API as Task 7.
- Produces: nothing.

- [ ] **Step 1: Replace the field**

Replace `private IOpcUaSubjectServer? _serverService;` with:

```csharp
private IHostedServiceAttachment<IOpcUaSubjectServer>? _attachment;
```

- [ ] **Step 2: Rewrite the start path**

`StartServerAsync` keeps its `while (!_rootManager.IsLoaded)` wait at `:227-230` outside the factory, because a synchronous `Func<T>` cannot await it. Replace the block from `var targetSubject = ...` (`:239`) through the attach (`:268`) with:

```csharp
if (_attachment is null)
{
    _attachment = await this.AttachHostedServiceAsync(() =>
    {
        // Re-resolved on every invocation, not captured: the target subject belongs to the graph and
        // may have been replaced between a detach and a re-attach.
        var targetSubject = _pathResolver.ResolveSubject(Path, PathStyle.Canonical)
            ?? throw new InvalidOperationException($"Could not resolve subject at path: {Path}");

        var defaults = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter()
        };

        var configuration = new OpcUaServerConfiguration
        {
            ValueConverter = new OpcUaValueConverter(),
            Mapper = new OpcUaCompositeMapper(
                new OpcUaPathProviderMapper(new StateAttributeOpcUaPathProvider()),
                new OpcUaAttributeMapper()),
            ApplicationName = ApplicationName ?? defaults.ApplicationName,
            NamespaceUri = NamespaceUri ?? defaults.NamespaceUri,
            RootName = RootName,
            BaseAddress = BaseAddress ?? defaults.BaseAddress,
            CleanCertificateStore = CleanCertificateStore ?? defaults.CleanCertificateStore,
            BufferTime = BufferTimeMs.HasValue ? TimeSpan.FromMilliseconds(BufferTimeMs.Value) : defaults.BufferTime,
        };

        return targetSubject.CreateOpcUaServer(configuration, _logger);
    }, cancellationToken);
}
```

The throw replaces the old null check at `:240-245`, which set `StatusMessage = "Could not resolve subject at path: {Path}"` inline. The existing `catch` in `StartServerAsync` already sets `Status = ServiceStatus.Error` and `StatusMessage = ex.Message`, so the same message still reaches the UI.

- [ ] **Step 3: Rewrite the stop path**

Replace `StopServerAsync` (`:281-304`) with:

```csharp
private async Task StopServerAsync(CancellationToken cancellationToken)
{
    if (_attachment is null)
    {
        return;
    }

    try
    {
        Status = ServiceStatus.Stopping;
        await this.DetachHostedServiceAsync(_attachment, cancellationToken);
        _attachment = null;
        _logger.LogInformation("OPC UA server stopped");
    }
    catch (Exception ex)
    {
        // Left set on failure, so the guard cannot attach a second server over a live attachment.
        _logger.LogError(ex, "Failed to stop OPC UA server");
    }
    finally
    {
        Status = ServiceStatus.Stopped;
        IncomingChangesPerSecond = null;
        OutgoingChangesPerSecond = null;
        ActiveSessionCount = null;
    }
}
```

- [ ] **Step 4: Stop detaching from the unwind**

At `OpcUaServer.cs:203`, replace the `await StopServerAsync(CancellationToken.None);` at the end of `ExecuteAsync` with:

```csharp
// See OpcUaClient: detaching from the unwind deadlocks against this subject's own stop transition.
Status = ServiceStatus.Stopped;
```

- [ ] **Step 5: Update the inline diagnostics poll**

In `ExecuteAsync` (`:188-194`), replace `if (_serverService is { } service)` with:

```csharp
if (_attachment?.Current is { } service)
```

- [ ] **Step 6: Build and test**

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category!=Integration"
```

Expected: build succeeds, tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.OpcUa/OpcUaServer.cs
git commit -m "refactor: move the OPC UA server wrapper to factory attachment

Mirrors the client, with the path resolved inside the factory on every
invocation so a re-attach cannot bind the server to a subject the graph has
since replaced. The wait on the root manager stays outside the factory,
which is synchronous by design."
```

---

## Task 9: Documentation and the command files

**Files:**
- Rewrite: `docs/hosting.md`
- Modify: `docs/subject-guidelines.md:442,444,448,455,480,510`
- Modify: `.claude/commands/create-homeblaze-library.md:113`
- Modify: `.claude/commands/migrate-homeblaze-library.md:166`

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

- [ ] **Step 1: Rewrite `docs/hosting.md`**

Restructure around the ownership rule rather than the API surface. It must contain, in this order:

1. **The rule**, stated once: a hosted service runs exactly while its subject is in the graph; the handler on the subject's context is the only thing that starts or stops it, and it disposes exactly what it created.
2. **Setup**, unchanged in substance from the current `## Setup` section.
3. **Which pattern when**, the section whose absence caused this whole investigation:
   - *Construct and register directly* (`var car = new Car(context); services.AddSingleton(car);`) when the subject needs no DI constructor dependencies. This gives you the instance at configuration time, which is what the connector samples need for `context.AddService(root)`. Note that the container does **not** dispose an instance registered this way.
   - *`AddSubject<T>()`* when the subject has constructor dependencies that only exist after `builder.Build()`, with the two sharp edges: a second `AddSubject<T>` drops its `configure`, and a pre-existing registration of `T` means neither the context nor `configure` is applied.
   - *Factory attachment* when a service should run for as long as a subject is in the graph.
   - *Subject implements `BackgroundService`* when the subject's own purpose is a background loop.
4. **Factory attachment**, replacing the current `## Attaching Hosted Services` section, including:
   - the factory must construct; `() => existingInstance` is wrong, because a re-attach would start an instance the handler has already disposed;
   - a hosted service must not detach an attachment from inside its own stop path;
   - a connector's dispose path must not enter the lifecycle lock, directly or transitively, and must not block on a lock its own `SubjectDetaching` handler acquires. The handler now disposes from a transition that can run while a detach cascade still holds `lock (_attachedSubjects)`, whereas previously the wrapper disposed and the two never interleaved. Writing a scalar property is safe; writing a subject or collection typed property is not, and attaching or detaching a subject enters the same lock without being a property write at all. Nothing enforces this and no test covers it, which is exactly why it has to be written down;
   - `Current` and `Fault` on the handle, and that the synchronous overloads mean "accepted", not "started".
5. **Subject as hosted service**, including the restart contract (`ExecuteAsync` must tolerate being run again) and that a hand written `IHostedService` must honour `StopAsync` rather than relying on the token passed to `StartAsync`.

Delete the current `### Automatic Cleanup` block at `:91-102`. It says attached services are "stopped and removed"; they are now stopped, disposed and kept, which is what makes a re-attach work. Replace it with a short subsection saying exactly that.

Update the `## For Library Authors` link at `:145` to name `AddSubject<T>()` and keep the anchor pointing at whatever `docs/subject-guidelines.md` heading survives Step 2.

- [ ] **Step 2: Update `docs/subject-guidelines.md`**

- `:442,444,455,480,510`: replace every `AddHostedSubject` with `AddSubject`, and rewrite the "Interaction with AddHostedSubject" paragraph. It currently says the method detects a context accepting constructor; it now says the context is applied unconditionally after construction, so the subject is attached regardless of constructor shape.
- `:448`: fix the unrelated error that says `HueBridge` injects `IHttpClientFactory`. `HueBridge.cs:115` takes only `ILogger<HueBridge>`.
- Add one paragraph to the hosted subject section stating the restart contract: a subject implementing `IHostedService` is restarted when it re-enters the graph, so `ExecuteAsync` must tolerate running more than once.

- [ ] **Step 3: Update the two command files**

`.claude/commands/create-homeblaze-library.md:113` and `.claude/commands/migrate-homeblaze-library.md:166` both describe the service extension as "using `AddHostedSubject` pattern". Change both to `AddSubject`.

- [ ] **Step 4: Check for stale references**

```bash
grep -rn "AddHostedSubject\|GetAttachedHostedServices" --include="*.md" --include="*.cs" . \
  | grep -v "/obj/" | grep -v "/bin/" | grep -v "docs/superpowers/"
```

Expected: no output. The spec and this plan under `docs/superpowers/` legitimately still name the old API when describing what was replaced.

- [ ] **Step 5: Full build and test**

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: build succeeds, all unit tests pass.

- [ ] **Step 6: Commit**

```bash
git add docs .claude src
git commit -m "docs: restructure the hosting documentation around service ownership

Adds the which-pattern-when section whose absence made it unclear whether a
connector could be attached to a subject at all, documents that detach now
disposes and keeps the attachment so a re-attach re-creates, and states the
two rules that are easy to get wrong: the factory must construct, and a
hosted service must not detach an attachment from inside its own stop path."
```

---

## Deferred test coverage

The spec lists 29 test scenarios. Tasks 1 to 9 implement the ones that pin behaviour a reviewer can check without new infrastructure. The following need a **stall seam**, one internal hook that lets a test hold a named transition or hold the drain in `Draining`. Design that seam once, then write these together as a final task rather than four ad hoc delays, and do not use timing:

- A target attached during the drain is not left running (measured at 3 in 400 without the fourth gate state).
- An attachment added while a detach is in flight is not left running (329 in 400 with target level liveness instead of subject level).
- A context detach immediately followed by a re-attach where the re-attach provably lands mid stop.
- Concurrent appends to one target, driven by an explicit detach racing a context detach.

Two more are worth writing but are not blocking:

- Stopping the host while a wrapper's `ExecuteAsync` is unwinding completes well inside a shortened `HostOptions.ShutdownTimeout`. This is the regression guard for the deadlock Task 7 removes.
- A dropped fire and forget faulted transition raises no `UnobservedTaskException`. Vacuous while transition bodies never throw, so it needs a positive control proving the assertion can fail, `GC.Collect` plus `WaitForPendingFinalizers`, and a non parallel collection because `TaskScheduler.UnobservedTaskException` is process global.

## Verification before opening the pull request

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
dotnet test src/Namotion.Interceptor.OpcUa.Tests
```

The OPC UA integration tests need port 4840 free. A locally running Demo.Host will collide with them.

Confirm `git log --oneline master..HEAD` shows the spec commits plus one commit per task, and that no commit message mentions an agent or carries a `Co-Authored-By` trailer.
