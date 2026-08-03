# Connector connect/reconnect write reconciliation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture local writes continuously across the whole source lifetime and reconcile every not-fully-connected write through one policy, so writes made during the reconnect delay are never lost and the connect and delay windows behave identically.

**Architecture:** Move the change subscription from per-connection (inside `ChangeQueueProcessor`) to source-lifetime (owned by `SubjectSourceBase`). Each connection attempt synchronously drains owned writes into the existing bounded `WriteRetryQueue` (after the retry delay and after the initial-state load), then runs one 3-way reconcile (restore / send / drop) before the connected phase. No background task and no new concurrency. `ChangeQueueProcessor.IsSuperseded` is left in place; it becomes inert on the source path because window writes are drained before `ProcessAsync`.

**Tech Stack:** C# 13 / .NET 9, xUnit, `Namotion.Interceptor.Connectors`, `Namotion.Interceptor.Tracking`.

Spec: `docs/superpowers/specs/2026-07-08-connector-write-reconciliation-design.md`

---

## File structure

- `src/Namotion.Interceptor.Tracking/Change/PropertyChangeQueueSubscription.cs` — add a non-blocking drain method.
- `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs` — add an internal constructor overload that accepts an externally owned subscription; gate subscription disposal.
- `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs` — source-lifetime subscription, synchronous drain helper, 3-way reconcile, restructured `ExecuteAsync`.
- Tests: `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs`, `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseTests.cs`.

Servers (`MqttSubjectServer`, `OpcUaSubjectServer`) keep the existing public constructor and are not touched.

---

### Task 1: Non-blocking drain on the subscription

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeQueueSubscription.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `ChangeQueueProcessorTests` (it already has `using Namotion.Interceptor.Tracking;` and access to `Person`):

```csharp
[Fact]
public void WhenDrainingImmediately_ThenReturnsQueuedItemsThenFalse()
{
    // Arrange
    var context = new InterceptorSubjectContext();
    context.WithRegistry();
    context.WithPropertyChangeQueue();
    var subject = new Person(context);
    using var subscription = context.CreatePropertyChangeQueueSubscription();

    subject.FirstName = "A";
    subject.FirstName = "B";

    // Act
    var drained = new List<string?>();
    while (subscription.TryDequeueImmediate(out var change))
    {
        drained.Add(change.GetNewValue<string?>());
    }

    // Assert
    Assert.Equal(["A", "B"], drained);
    Assert.False(subscription.TryDequeueImmediate(out _));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~WhenDrainingImmediately_ThenReturnsQueuedItemsThenFalse"`
Expected: FAIL to compile with "PropertyChangeQueueSubscription does not contain a definition for TryDequeueImmediate".

- [ ] **Step 3: Add the method**

In `PropertyChangeQueueSubscription.cs`, immediately after the `Count` property (which ends at the line `internal int Count => _queue.Count;`), add:

```csharp
    /// <summary>
    /// Dequeues one currently-available change without waiting; returns false when the queue is
    /// momentarily empty. Single-consumer only, like <see cref="TryDequeue"/>. Does not touch the
    /// wake-up signal, so it must not run concurrently with <see cref="TryDequeue"/>.
    /// </summary>
    internal bool TryDequeueImmediate(out SubjectPropertyChange item) => _queue.TryDequeue(out item);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~WhenDrainingImmediately_ThenReturnsQueuedItemsThenFalse"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Change/PropertyChangeQueueSubscription.cs src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs
git commit -m "feat: add non-blocking drain to PropertyChangeQueueSubscription"
```

---

### Task 2: Processor constructor overload for an externally owned subscription

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `ChangeQueueProcessorTests`:

```csharp
[Fact]
public void WhenConstructedWithExternalSubscription_ThenDisposeDoesNotDisposeIt()
{
    // Arrange
    var context = new InterceptorSubjectContext();
    context.WithRegistry();
    context.WithPropertyChangeQueue();
    var subject = new Person(context);
    using var subscription = context.CreatePropertyChangeQueueSubscription();

    var processor = new ChangeQueueProcessor(
        source: null,
        subscription: subscription,
        propertyFilter: _ => true,
        writeHandler: (_, _) => ValueTask.CompletedTask,
        bufferTime: TimeSpan.FromMilliseconds(50),
        maxQueueDepth: null,
        logger: NullLogger.Instance);

    // Act
    processor.Dispose();

    // Assert - the externally owned subscription is still capturing (not disposed/completed)
    subject.FirstName = "still-capturing";
    Assert.True(subscription.TryDequeueImmediate(out var change));
    Assert.Equal("still-capturing", change.GetNewValue<string?>());
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~WhenConstructedWithExternalSubscription_ThenDisposeDoesNotDisposeIt"`
Expected: FAIL to compile (no constructor overload taking a `PropertyChangeQueueSubscription`).

- [ ] **Step 3: Add the ownership field**

In `ChangeQueueProcessor.cs`, find:

```csharp
    private readonly PropertyChangeQueueSubscription _subscription;
```

Replace with:

```csharp
    private readonly PropertyChangeQueueSubscription _subscription;
    private readonly bool _ownsSubscription;
```

- [ ] **Step 4: Mark the existing constructor as owning its subscription**

In the existing public constructor, find:

```csharp
        try
        {
            _subscription = context.CreatePropertyChangeQueueSubscription();
        }
        catch
```

Replace with:

```csharp
        try
        {
            _subscription = context.CreatePropertyChangeQueueSubscription();
            _ownsSubscription = true;
        }
        catch
```

- [ ] **Step 5: Add the internal overload**

Immediately after the closing brace of the existing public constructor (the one whose `try/catch` creates the subscription), add:

```csharp
    /// <summary>
    /// Initializes the processor with an externally owned subscription. The caller keeps ownership:
    /// <see cref="Dispose"/> does not dispose the subscription. Use this when the subscription must
    /// outlive the processor, for example a source-lifetime subscription reused across reconnects.
    /// </summary>
    internal ChangeQueueProcessor(
        object? source,
        PropertyChangeQueueSubscription subscription,
        Func<PropertyReference, bool> propertyFilter,
        Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> writeHandler,
        TimeSpan? bufferTime,
        int? maxQueueDepth,
        ILogger logger)
    {
        _source = source;
        _propertyFilter = propertyFilter;
        _writeHandler = writeHandler;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _maxQueueDepth = maxQueueDepth;
        _subscription = subscription;
        _ownsSubscription = false;
    }
```

- [ ] **Step 6: Gate subscription disposal**

In `Dispose()`, find:

```csharp
        _subscription.Dispose();
```

Replace with:

```csharp
        if (_ownsSubscription)
        {
            _subscription.Dispose();
        }
```

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~WhenConstructedWithExternalSubscription_ThenDisposeDoesNotDisposeIt"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs
git commit -m "feat: allow ChangeQueueProcessor to use an externally owned subscription"
```

---

### Task 3: Replace 2-way re-apply with 3-way reconcile

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `SubjectSourceBaseTests` (it already has the `CreateSourceWithRetryQueue` and `EnqueueRetryChange` helpers):

```csharp
[Fact]
public async Task WhenRetryChangeMatchesCurrentModelValue_ThenItIsSentToSource()
{
    // Arrange: a retry-queued change whose new value is already the current model value
    // (the write survived the load) must be sent, not dropped. The 2-way re-apply dropped it.
    var context = InterceptorSubjectContext.Create()
        .WithFullPropertyTracking()
        .WithRegistry();
    var subject = new Person(context) { FirstName = "ClientValue" }; // model already holds the new value

    var (source, writtenChanges, writeTcs) = CreateSourceWithRetryQueue(subject, context,
        initialStateAction: s => { }); // load leaves FirstName alone

    // Retry queue: Original -> ClientValue (new value already in the model)
    EnqueueRetryChange(source, subject, nameof(Person.FirstName), "Original", "ClientValue");

    // Act
    await source.StartAsync(CancellationToken.None);
    await writeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await source.StopAsync(CancellationToken.None);

    // Assert - the change was sent to the source (flush branch), not dropped
    Assert.Contains(writtenChanges, c =>
        c.Property.Name == nameof(Person.FirstName) &&
        c.GetNewValue<string?>() == "ClientValue");
    Assert.Equal("ClientValue", subject.FirstName);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~WhenRetryChangeMatchesCurrentModelValue_ThenItIsSentToSource"`
Expected: FAIL (the 2-way `ReapplyRetryQueue` drops this change because `current != old`, so `writeTcs` never completes and the wait times out).

- [ ] **Step 3: Replace the method**

In `SubjectSourceBase.cs`, delete the entire `private void ReapplyRetryQueue()` method (from its doc/signature through its closing brace) and replace it with:

```csharp
    private async Task ReconcileRetryQueueAsync(CancellationToken cancellationToken)
    {
        var retryChanges = WriteRetryQueue?.DrainForLocalReapply();
        if (retryChanges is null || retryChanges.Length == 0)
        {
            return;
        }

        var restored = 0;
        var sent = 0;
        var dropped = 0;
        var failed = 0;
        List<SubjectPropertyChange>? toSend = null;

        foreach (var change in retryChanges)
        {
            try
            {
                var property = change.Property;
                var currentValue = property.Metadata.GetValue?.Invoke(property.Subject);

                if (Equals(currentValue, change.GetNewValue<object?>()))
                {
                    // Already the current model value: the source has not received it, so send it.
                    (toSend ??= []).Add(change);
                    sent++;
                }
                else if (Equals(currentValue, change.GetOldValue<object?>()))
                {
                    // Source still at the baseline the write was based on: restore locally. The
                    // connected phase captures and sends the re-applied write.
                    property.Metadata.SetValue?.Invoke(property.Subject, change.GetNewValue<object>());
                    restored++;
                }
                else
                {
                    // Source diverged from the baseline: source wins.
                    dropped++;
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "Failed to reconcile retry queue change for property '{PropertyName}', dropping.",
                    change.Property.Name);
                failed++;
            }
        }

        if (toSend is not null)
        {
            WriteRetryQueue!.Enqueue(toSend.ToArray());
            await WriteRetryQueue.FlushAsync(this, cancellationToken).ConfigureAwait(false);
        }

        if (dropped > 0 || failed > 0)
        {
            _logger.LogWarning(
                "Retry queue reconcile: {Restored} restored, {Sent} sent, {Dropped} dropped (source wins), {Failed} failed.",
                restored, sent, dropped, failed);
        }
        else if (restored > 0 || sent > 0)
        {
            _logger.LogInformation(
                "Retry queue reconcile: {Restored} restored, {Sent} sent.", restored, sent);
        }
    }
```

- [ ] **Step 4: Update the call site**

In `ExecuteAsync`, find:

```csharp
                // Optimistic retry re-apply: after initial state load + ChangeQueueProcessor creation,
                // re-apply queued changes locally if the source hasn't changed the property.
                // ChangeQueueProcessor picks up re-applied changes and sends them to the source as fresh writes.
                ReapplyRetryQueue();
```

Replace with:

```csharp
                // Single reconcile point after initial state load: restore (source unchanged),
                // send (already current), or drop (source diverged). See ReconcileRetryQueueAsync.
                await ReconcileRetryQueueAsync(stoppingToken).ConfigureAwait(false);
```

- [ ] **Step 5: Run the new test and the existing retry-queue tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~WhenRetryChangeMatchesCurrentModelValue_ThenItIsSentToSource|FullyQualifiedName~WhenRetryQueueHas|FullyQualifiedName~WhenAllRetryChangesConflict|FullyQualifiedName~WhenRetryChangeThrowsDuringReapply"`
Expected: PASS (new flush test passes; existing restore cases still restore, conflict cases still drop).

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseTests.cs
git commit -m "feat: reconcile retry queue with 3-way restore/send/drop"
```

---

### Task 4: Source-lifetime subscription, synchronous drains, restructured pump

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `SubjectSourceBaseTests`:

```csharp
[Fact]
public async Task WhenPropertyIsWrittenWhileNotConnected_ThenChangeReachesSourceOnReconnect()
{
    // Arrange: the first connection attempt writes a property and then fails. Under the old
    // per-connection subscription that write was lost (no subscription across the retry gap).
    // The source-lifetime subscription must capture it and deliver it on the next attempt.
    var context = InterceptorSubjectContext.Create()
        .WithFullPropertyTracking()
        .WithRegistry();
    var subject = new Person(context);

    var receivedChanges = new ConcurrentQueue<SubjectPropertyChange>();
    var attempts = 0;
    var source = new TestSubjectSource(subject, context, NullLogger.Instance,
        retryTime: TimeSpan.FromMilliseconds(50))
    {
        StartListeningOverride = (_, _) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1)
            {
                // Write while connecting, then fail the attempt. Captured, then the pump retries.
                subject.FirstName = "written-while-not-connected";
                throw new InvalidOperationException("first attempt fails");
            }
            return Task.FromResult<IAsyncDisposable?>(null);
        },
        LoadInitialStateOverride = _ => Task.FromResult<Action?>(null), // load leaves FirstName alone
        WriteChangesOverride = (changes, _) =>
        {
            foreach (var change in changes.ToArray())
            {
                receivedChanges.Enqueue(change);
            }
            return ValueTask.FromResult(WriteResult.Success);
        },
    };
    new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

    // Act
    await source.StartAsync(CancellationToken.None);
    await AsyncTestHelpers.WaitUntilAsync(
        () => receivedChanges.Any(c => c.Property.Name == nameof(Person.FirstName)),
        message: "Expected the write made while not connected to reach the source on reconnect");
    await source.StopAsync(CancellationToken.None);

    // Assert
    var received = receivedChanges.First(c => c.Property.Name == nameof(Person.FirstName));
    Assert.Equal("written-while-not-connected", received.GetNewValue<string?>());
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~WhenPropertyIsWrittenWhileNotConnected_ThenChangeReachesSourceOnReconnect"`
Expected: FAIL (the write from the failed first attempt is lost; the wait times out).

- [ ] **Step 3: Add the drain helper**

In `SubjectSourceBase.cs`, add this method directly above `ReconcileRetryQueueAsync`:

```csharp
    private void DrainOwnedWritesToRetryQueue(PropertyChangeQueueSubscription subscription)
    {
        List<SubjectPropertyChange>? owned = null;
        while (subscription.TryDequeueImmediate(out var change))
        {
            if (WriteRetryQueue is null)
            {
                continue; // drain-and-discard: without a retry queue there is nothing to reconcile
            }

            if (change.Source == this)
            {
                continue; // this source's own applies (inbound / source-tagged)
            }

            if (!(change.Property.TryGetSource(out var source) && source == this))
            {
                continue; // not owned by this source
            }

            (owned ??= []).Add(change);
        }

        if (owned is not null)
        {
            WriteRetryQueue!.Enqueue(owned.ToArray());
        }
    }
```

- [ ] **Step 4: Restructure `ExecuteAsync`**

Replace the entire body of `ExecuteAsync` (the method whose signature is `protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)`) with:

```csharp
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Source-lifetime capture: one subscription for the whole source, so writes are captured
        // continuously (including during the retry delay) and never fall into a no-subscription gap.
        using var subscription = _context.CreatePropertyChangeQueueSubscription();

        var firstAttempt = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!firstAttempt)
                {
                    await Task.Delay(_retryTime, stoppingToken).ConfigureAwait(false);
                }
                firstAttempt = false;

                // Park writes captured since the previous attempt (retry delay + any failed attempt).
                // This also caps memory across repeated failed attempts.
                DrainOwnedWritesToRetryQueue(subscription);

                _propertyWriter.StartBuffering();
                await using var listenLifetime = await StartListeningAsync(_propertyWriter, stoppingToken).ConfigureAwait(false);

                await _propertyWriter.LoadInitialStateAndResumeAsync(stoppingToken).ConfigureAwait(false);

                // Park connect-window writes captured during listen/load.
                DrainOwnedWritesToRetryQueue(subscription);

                // Single reconcile point: restore (source unchanged), send (already current), drop (diverged).
                await ReconcileRetryQueueAsync(stoppingToken).ConfigureAwait(false);

                // Connected phase reuses the source-lifetime subscription and does not own it.
                using var processor = new ChangeQueueProcessor(
                    this,
                    subscription,
                    propertyReference => propertyReference.TryGetSource(out var source) && source == this,
                    WriteChangesViaRetryQueueAsync,
                    _bufferTime,
                    maxQueueDepth: null,
                    logger: _logger);

                await processor.ProcessAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to listen for changes in source.");
                // The next iteration delays before reconnecting, with the subscription still capturing.
            }
        }
    }
```

- [ ] **Step 5: Run the new test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~WhenPropertyIsWrittenWhileNotConnected_ThenChangeReachesSourceOnReconnect"`
Expected: PASS.

- [ ] **Step 6: Run the existing pump and retry tests to confirm no regression**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectSourceBaseTests"`
Expected: PASS (including `WhenPropertyIsWrittenWhileInitialStateLoads_ThenChangeIsStillWrittenToSource`, `WhenQueuedWriteIsSupersededByInitialState_ThenStaleWriteIsNotSent`, `WhenStartingSourceAndPushingChanges_ThenUpdatesAreInCorrectOrder`, and the retry-queue re-apply tests).

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseTests.cs
git commit -m "feat: capture writes across reconnects via a source-lifetime subscription"
```

---

### Task 5: Full verification

**Files:** none (verification only).

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded, 0 warnings (warnings are errors in this repo).

- [ ] **Step 2: Run the Connectors unit suite**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "Category!=Integration"`
Expected: PASS, 0 failed. All prior tests plus the three new ones.

- [ ] **Step 3: Confirm the public API snapshot is unchanged**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~VerifyChecksTests.PublicApi"`
Expected: PASS with no `.received.txt` produced (the new constructor is `internal`, so the public API is unchanged). If a `.received.txt` appears, the constructor was made public by mistake; make it `internal`.

- [ ] **Step 4: Run the OPC UA integration tests (connector-affecting change)**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests`
Expected: PASS. Occasional parallel-run load flakes may occur on a busy host; rerun the failing test in isolation to confirm it is a load artifact, not a regression.

- [ ] **Step 5: Commit any snapshot acceptance if needed**

If Step 3 legitimately required accepting a new snapshot (it should not), replace `.verified.txt` with `.received.txt` and commit:

```bash
git add src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt
git commit -m "test: accept public API snapshot"
```

Otherwise no commit for this task.

---

## Self-review notes

- Spec coverage: source-lifetime subscription (Task 4), synchronous drains after delay and after load (Task 4), 3-way reconcile restore/send/drop (Task 3), non-blocking drain primitive (Task 1), externally owned subscription without disposal (Task 2), supersede left in place (unchanged, verified inert by Task 4's passing pump tests), memory bound via per-attempt drain into the bounded retry queue (Task 4 helper), servers untouched (internal overload, existing constructor unchanged, Task 5 Step 3).
- Type consistency: `TryDequeueImmediate(out SubjectPropertyChange)` (Task 1) is used by `DrainOwnedWritesToRetryQueue` (Task 4); the internal `ChangeQueueProcessor(object?, PropertyChangeQueueSubscription, ...)` overload (Task 2) is called in `ExecuteAsync` (Task 4); `ReconcileRetryQueueAsync(CancellationToken)` (Task 3) is awaited in `ExecuteAsync` (Task 4).
- No placeholders: every code and command step is concrete.
