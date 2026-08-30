# Inbound Batch Continuation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One property that fails to apply must no longer discard every other property in the same inbound `SubjectUpdate`.

**Architecture:** A single try/catch around the per-property body of `SubjectUpdateApplier.ApplyPropertyUpdate`, accumulating failures on the already-pooled `SubjectUpdateApplyContext` and rethrowing once at the end of `ApplyUpdate`. Because `ApplyPropertyUpdate` is reached recursively for nested subjects and for collection and dictionary items, the rule holds at every depth with no per-kind or per-depth special case. Callers keep seeing an exception, so no error handling anywhere changes; only what lands in the model changes.

**Tech Stack:** C# 13, .NET 9, xUnit, Verify. Design doc: `docs/superpowers/specs/2026-08-31-inbound-batch-continuation-design.md`.

---

## Context for the implementer

Read this before starting; it is the reasoning the code cannot carry.

**Why the applier is the odd one out.** Every other inbound path already catches per property and continues: OPC UA subscription (`SubscriptionManager.cs:252`), polling (`PollingManager.cs:416-424`), OPC UA server (`OpcUaSubjectServer.cs:432-439`), MQTT per message. Only `SubjectUpdateApplier.ApplyPropertyUpdates` (`:42-68`) has no per-property catch, and it is the path WebSocket uses for everything.

**Why every kind is caught, not just `Value`.** An earlier draft restricted this to scalars, arguing composites must abort because they mutate child subjects before writing the parent. That is wrong: the mutation has already happened when the exception fires, so aborting leaves the same partial state and additionally drops unrelated siblings.

**Why cancellation is excluded.** An `OperationCanceledException` during shutdown must unwind immediately, not be reported at the end of the batch as though a property were bad.

**Do not "fix" the initial-load path.** `SubjectPropertyWriter.cs:143` invokes the load's apply action with no try/catch on purpose: the throw is what drives the reconnect-and-reload retry. Catching it would mean a transient load failure is never retried. A reconnect loop on a persistent failure is a loud, visible alarm; a silently missing property is not.

**Ordering is not guaranteed.** `ApplyPropertyUpdates` iterates a `Dictionary<string, SubjectPropertyUpdate>`, so "the properties after the failing one" is meaningless. Assert that every non-failing property applied.

---

## File Structure

- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplyContext.cs` — gains a list of (property, exception) pairs, cleared with the rest of the pooled state.
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs` — the catch, and the rethrow at the end of `ApplyUpdate`.
- Create: `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateApplyFailureTests.cs` — all tests for this behaviour.
- Modify: `docs/connectors.md` — clarify the "Inbound Update Error Handling" section.

No public API changes. No signature changes. `ApplySubjectUpdate` stays `void` and still throws on failure.

---

## Task 1: Collect failures instead of aborting

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplyContext.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateApplyFailureTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateApplyFailureTests.cs`:

```csharp
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class SubjectUpdateApplyFailureTests
{
    [Fact]
    public void WhenOnePropertyThrows_ThenTheOthersStillApply()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new ThrowingDevice(context) { PropertyA = true, PropertyB = true };
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        var target = new ThrowingDevice(context)
        {
            ThrowingEnabled = true,
            ShouldThrow = name => name == nameof(ThrowingDevice.PropertyA)
        };

        // Act
        var exception = Assert.ThrowsAny<Exception>(
            () => target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));

        // Assert
        Assert.False(target.PropertyA);
        Assert.True(target.PropertyB);
        Assert.Contains(nameof(ThrowingDevice.PropertyA), exception.ToString());
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectUpdateApplyFailureTests"`

Expected: FAIL. `PropertyB` is `false`, because the throw on `PropertyA` aborted the batch. (If the dictionary happens to order `PropertyB` first it will pass by luck; Task 3 removes that dependence, and Step 5 below re-runs it.)

- [ ] **Step 3: Add failure collection to the pooled context**

In `SubjectUpdateApplyContext.cs`, add the field beside `_processedSubjectIds`:

```csharp
    private readonly HashSet<string> _processedSubjectIds = [];
    private List<(RegisteredSubjectProperty Property, Exception Exception)>? _failures;
```

The property is recorded beside the exception for two reasons: it lets the thrown message name what failed, which an update of twenty properties badly needs, and it is the hook the follow-up divergence work attaches to. That work does not read the exception to learn the property; it runs in this same catch, where the property is already in scope.

Add the accessor next to `TryMarkAsProcessed`:

```csharp
    /// <summary>
    /// Records a property that could not be applied. The batch continues; the collected failures are
    /// thrown once by the caller when the whole update has been walked.
    /// </summary>
    public void RecordFailure(RegisteredSubjectProperty property, Exception exception)
        => (_failures ??= []).Add((property, exception));

    /// <summary>The failures recorded so far, or <c>null</c> when every property applied.</summary>
    public List<(RegisteredSubjectProperty Property, Exception Exception)>? Failures => _failures;
```

Extend `Clear`, which runs before the context returns to the pool:

```csharp
    public void Clear()
    {
        _processedSubjectIds.Clear();
        _failures = null;
        Subjects = null!;
        SubjectFactory = null!;
        Origin = default;
        TransformValueBeforeApply = null;
    }
```

- [ ] **Step 4: Catch per property in `ApplyPropertyUpdate`**

In `SubjectUpdateApplier.cs`, wrap the existing `switch` in `ApplyPropertyUpdate`. Leave the `registeredProperty is null` guard above the `try`, so an unknown property is still a silent skip rather than a recorded failure:

```csharp
        var registeredProperty = subject.TryGetRegisteredProperty(propertyName, registry);
        if (registeredProperty is null)
            return;

        try
        {
            switch (propertyUpdate.Kind)
            {
                // ...existing arms, unchanged...
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not an apply failure. Let a shutdown unwind now rather than surfacing
            // at the end of the batch as though this property were bad.
            throw;
        }
        catch (Exception exception)
        {
            // One property must not cost its siblings. Every other inbound path already catches per
            // property; this applier was the only one that abandoned the rest of the batch.
            context.RecordFailure(registeredProperty, exception);
        }
```

- [ ] **Step 5: Throw once at the end of `ApplyUpdate`**

Replace the body of `ApplyUpdate` after the guards:

```csharp
        var context = ContextPool.Rent();
        List<(RegisteredSubjectProperty Property, Exception Exception)>? failures = null;
        try
        {
            context.Initialize(update.Subjects, subjectFactory, origin, transformValueBeforeApply);
            context.TryMarkAsProcessed(update.Root);
            ApplyPropertyUpdates(subject, rootProperties, context);
            failures = context.Failures;
        }
        finally
        {
            context.Clear();
            ContextPool.Return(context);
        }

        if (failures is null)
        {
            return;
        }

        // A single failure is rethrown as itself, with its original stack, so a caller that catches a
        // specific exception type keeps working exactly as before this change.
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0].Exception).Throw();
        }

        throw new AggregateException(
            $"{failures.Count} property updates could not be applied: " +
            string.Join(", ", failures.Select(failure => failure.Property.Name)),
            failures.Select(failure => failure.Exception));
```

Add the using at the top of the file:

```csharp
using System.Runtime.ExceptionServices;
```

Note: `failures` holds the list reference, so `context.Clear()` nulling the field does not affect it.

- [ ] **Step 6: Run the test**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectUpdateApplyFailureTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplyContext.cs src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateApplyFailureTests.cs
git commit -m "fix: apply the rest of an inbound update when one property fails"
```

---

## Task 2: Pin the exception contract

**Files:**
- Test: `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateApplyFailureTests.cs`

- [ ] **Step 1: Write the tests**

Add to `SubjectUpdateApplyFailureTests`:

```csharp
    [Fact]
    public void WhenOnePropertyFails_ThenItsOwnExceptionIsThrownUnwrapped()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new ThrowingDevice(context) { PropertyA = true, PropertyB = true };
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        var target = new ThrowingDevice(context)
        {
            ThrowingEnabled = true,
            ShouldThrow = name => name == nameof(ThrowingDevice.PropertyA)
        };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));
    }

    [Fact]
    public void WhenSeveralPropertiesFail_ThenAnAggregateCarriesThemAll()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new ThrowingDevice(context) { PropertyA = true, PropertyB = true };
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        var target = new ThrowingDevice(context)
        {
            ThrowingEnabled = true,
            ShouldThrow = _ => true
        };

        // Act
        var exception = Assert.Throws<AggregateException>(
            () => target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));

        // Assert
        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.Contains(nameof(ThrowingDevice.PropertyA), exception.Message);
        Assert.Contains(nameof(ThrowingDevice.PropertyB), exception.Message);
    }

    [Fact]
    public void WhenCancellationIsThrown_ThenItPropagatesImmediatelyAndIsNotCollected()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new ThrowingDevice(context) { PropertyA = true, PropertyB = true };
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        var target = new ThrowingDevice(context);

        // Act & Assert
        Assert.Throws<OperationCanceledException>(
            () => target.ApplySubjectUpdate(
                update,
                DefaultSubjectFactory.Instance,
                ChangeOrigin.Local,
                (_, _) => throw new OperationCanceledException()));
    }
```

- [ ] **Step 2: Run them**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectUpdateApplyFailureTests"`
Expected: PASS, 4 tests.

If `WhenCancellationIsThrown_...` fails with `AggregateException`, the `OperationCanceledException` arm in Step 4 of Task 1 is missing or is ordered after the general `catch (Exception)`. It must come first.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateApplyFailureTests.cs
git commit -m "test: pin the applier's exception contract for partial failures"
```

---

## Task 3: Cover nesting and the deliberate non-goals

**Files:**
- Test: `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateApplyFailureTests.cs`

- [ ] **Step 1: Write a test for a failure nested inside a collection item**

Uses `Person`, whose `FirstName` carries `[MaxLength(4)]`, with data annotation validation enabled so a too-long value is refused. `Children` is a `List<Person>`, so the refusal happens inside a collection rebuild.

```csharp
    [Fact]
    public void WhenAPropertyInsideACollectionItemFails_ThenTheRestOfTheUpdateStillApplies()
    {
        // Arrange
        var sourceContext = InterceptorSubjectContext.Create().WithRegistry();
        var source = new Person(sourceContext)
        {
            LastName = "Doe",
            Children = [new Person(sourceContext) { FirstName = "TooLongName", LastName = "Child" }]
        };
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        var targetContext = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithDataAnnotationValidation();
        var target = new Person(targetContext);

        // Act
        Assert.ThrowsAny<Exception>(
            () => target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));

        // Assert
        Assert.Equal("Doe", target.LastName);
        Assert.Single(target.Children);
        Assert.Equal("Child", target.Children[0].LastName);
        Assert.Null(target.Children[0].FirstName);
    }
```

- [ ] **Step 2: Write a test pinning that the initial-load retry is untouched**

This is a non-goal guard: it must stay true that a failing apply action still throws out of the applier, because that throw is what drives the reconnect-and-reload retry.

```csharp
    [Fact]
    public void WhenEveryPropertyFails_ThenTheApplierStillThrowsSoCallersCanRetry()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new ThrowingDevice(context) { PropertyA = true, PropertyB = true };
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        var target = new ThrowingDevice(context)
        {
            ThrowingEnabled = true,
            ShouldThrow = _ => true
        };

        // Act & Assert
        Assert.ThrowsAny<Exception>(
            () => target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));
    }
```

- [ ] **Step 3: Run the file**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectUpdateApplyFailureTests"`
Expected: PASS, 6 tests.

If the collection test fails because `Children[0].LastName` is null, the failure is aborting the item rebuild rather than being contained at the property. Check that the catch in `ApplyPropertyUpdate` is around the whole `switch`, not one arm.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateApplyFailureTests.cs
git commit -m "test: cover nested failures and the retained retry behaviour"
```

---

## Task 4: Update the documentation

**Files:**
- Modify: `docs/connectors.md`

- [ ] **Step 1: Rewrite the "Inbound Update Error Handling" section**

Find the section at roughly line 221. Replace its body with:

```markdown
When applying inbound updates (writing data from the external system to the local subject model), a property that fails to apply is logged and dropped. There is no retry mechanism for inbound updates.

This is by design:
- A failed property does not block the other properties in the same update, at any nesting depth
- Write failures to internal models are treated as non-transient because property writes are deterministic: they either succeed or fail consistently, so retrying would not help (this includes custom validation failures)
- Monitor logs for `Failed to apply subject update` errors to detect issues

The whole update still reports failure to its caller once every property has been attempted: a single failure is rethrown as itself, several are wrapped in an `AggregateException`. Callers that retry, such as a source's initial state load, therefore still retry.

Note that a failed update to an object, collection or dictionary property can leave child subjects partially updated while the parent still references its previous value, because children are updated in place before the parent is written.

This differs from outbound changes (writing from local model to external system), which use a retry queue to handle transient failures.
```

- [ ] **Step 2: Check no other doc contradicts it**

Run: `grep -rn "block other updates\|update is dropped" docs/`
Expected: only the section just edited.

- [ ] **Step 3: Commit**

Stage the file by name. Never `git add docs/`, which would sweep in the untracked `docs/superpowers/` scaffolding.

```bash
git add docs/connectors.md
git commit -m "docs: describe inbound update failures as per property, not per batch"
```

---

## Task 5: Full verification

- [ ] **Step 1: Build with warnings as errors**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Unit tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: all green. Parse results by matching each assembly's summary line rather than by line position, because parallel projects interleave stdout and two summaries can share a line.

- [ ] **Step 3: Connector integration suites**

The applier is shared by all three connectors and CI path filters skip these for shared-library changes, so they must run locally. Check the port is free first; a leftover ConnectorTester on 4840 makes the OPC UA suite fail in varied, misleading ways.

```bash
ss -tlnp | grep 4840 || echo "port 4840 free"
dotnet test src/Namotion.Interceptor.OpcUa.Tests
dotnet test src/Namotion.Interceptor.Mqtt.Tests
dotnet test src/Namotion.Interceptor.WebSocket.Tests
```

Expected: all green. Do not run these concurrently with each other or with any review agent; the OPC UA suite uses a hardcoded port.

- [ ] **Step 4: Confirm the public API is unchanged**

Run: `git diff --stat -- '*.verified.txt'`
Expected: empty. This change adds no public API; a snapshot diff means something leaked and needs explaining before it is accepted.

- [ ] **Step 5: Confirm no scaffolding was committed**

Run: `git diff origin/master..HEAD --name-only | grep superpowers || echo "clean"`
Expected: `clean`. `git status` will still show `docs/superpowers/` as untracked, which is expected and not evidence either way.

- [ ] **Step 6: Final commit if anything remains**

```bash
git status --short
```

---

## Notes for the pull request

Title: `fix: apply the rest of an inbound update when one property fails`

Points the body should make:
- The defect, with the observation that every other inbound path already catches per property and only the batch applier did not.
- That the error surface is unchanged for every caller: it still throws, so the WebSocket server still sends its error frame and a source's initial load still retries.
- The one real behaviour change: on the paths that never retry, being the WebSocket server applying a client update and the client's partial updates, the surviving properties now land instead of being lost.
- The known limitation, unchanged by this work: a failed composite can leave a child subtree partially updated.
- No public API change, no hot path touched, so no benchmark. Connector suites were run locally; state which and their results.
