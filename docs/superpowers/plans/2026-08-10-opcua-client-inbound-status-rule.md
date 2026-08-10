# OPC UA client: inbound status rule Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** make all four inbound OPC UA client paths agree that Good and Uncertain values are applied and Bad values are not, and stop a single bad value taking anything else down with it.

**Architecture:** no new abstraction. `StatusCode.IsNotBad(status)` is exactly Good plus Uncertain, so the rule is one SDK call at each of the four sites. Polling additionally gains the conversion it never had, placed before its change-detection cache update so a converter throw cannot lose the change permanently.

**Tech Stack:** .NET 9, OPCFoundation.NetStandard.Opc.Ua 1.5.378.145, xUnit, the repo's `OpcUaTestServer` / `OpcUaTestPortPool` harness.

**Scope:** this is commit 1 of three in the client PR (spec: `docs/superpowers/specs/2026-08-10-opcua-client-status-conformance-design.md`). It stands alone: it ships, tests and reverts independently. The retry-queue work and the read-after-write work get their own plans, written after this lands, because both touch shared connector code whose behaviour is easier to reason about once this is exercised.

**Branch:** `fix/opcua-client-status-conformance`, already created off master at `f561d196`.

---

## File structure

| File | Responsibility | Change |
|---|---|---|
| `src/Namotion.Interceptor.OpcUa.Tests/Integration/Testing/OpcUaNodeStatusDriver.cs` | Test-only helper that makes a running server emit a chosen `StatusCode` for a property | Create |
| `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaInboundStatusTests.cs` | All tests for this commit | Create |
| `src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs` | Subscription notifications | Modify `:196-207` |
| `src/Namotion.Interceptor.OpcUa/Client/Polling/PollingManager.cs` | Polled reads | Modify `:363-374`, `:387-414` |
| `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs` | Initial and reconnect state load | Modify `:236-254` |
| `src/Namotion.Interceptor.OpcUa/Client/ReadAfterWrite/ReadAfterWriteManager.cs` | Read-after-write verification | Modify `:322-326` |

---

## Before you start

**The OPC UA test suite binds a fixed port.** It cannot run concurrently with another instance of itself, with the connector tester, or with anything else holding 4840. Run it alone:

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaInboundStatusTests"
```

**Nothing has been built in this session.** The first build will be cold and slow. That is expected.

---

## Task 1: A test driver that makes the server emit a chosen status

The server never emits a non-Good status on its own, so every test below needs a way to produce one. The pattern is the one `Integration/OpcUaCrossStoreConvergenceTests.cs:44-60` already uses for values: reach the node through `TryGetVariableNode`, mutate it under `NodeManagerLock`, and flush.

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Integration/Testing/OpcUaNodeStatusDriver.cs`

- [ ] **Step 1: Write the helper**

```csharp
using Namotion.Interceptor.OpcUa.Server;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Drives a running server's node state directly, so a test can produce inbound values the server
/// would never generate on its own. Mutates under <c>NodeManagerLock</c> and flushes in the same
/// hold, which is how the SDK's own write service reaches a node.
/// </summary>
internal static class OpcUaNodeStatusDriver
{
    /// <summary>
    /// Sets a property's node value and status code, then flushes so subscribers and pollers observe it.
    /// </summary>
    public static void Publish(
        IOpcUaSubjectServer server,
        PropertyReference property,
        object? value,
        StatusCode statusCode)
    {
        if (!server.TryGetVariableNode(property, out var node))
        {
            throw new InvalidOperationException(
                $"No variable node exists for '{property.Name}'. Wait for it with TryGetVariableNode first.");
        }

        var standardServer = (OpcUaStandardServer)((OpcUaSubjectServer)server).CurrentServer!;
        var systemContext = standardServer.CurrentInstance.DefaultSystemContext;

        lock (standardServer.NodeManagerLock!)
        {
            node.Value = value;
            node.StatusCode = statusCode;
            node.Timestamp = DateTime.UtcNow;
            node.ClearChangeMasks(systemContext, false);
        }
    }
}
```

- [ ] **Step 2: Build to confirm the accessors are reachable**

Run: `dotnet build src/Namotion.Interceptor.OpcUa.Tests`
Expected: success. If `CurrentServer` or `NodeManagerLock` is inaccessible from the test assembly, add the test project to `InternalsVisibleTo` in `src/Namotion.Interceptor.OpcUa/Namotion.Interceptor.OpcUa.csproj` rather than widening either member's visibility.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Integration/Testing/OpcUaNodeStatusDriver.cs
git commit -m "test: driver to emit a chosen node status from a running server"
```

---

## Task 2: Subscriptions stop applying Bad values

`SubscriptionManager.cs:200-207` never inspects the status, so a Bad value is applied as though it were a reading. A server may omit the value entirely for a bad status, so this can write a null or a default into the model on a sensor fault.

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs:196-207`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaInboundStatusTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Trait("Category", "Integration")]
public class OpcUaInboundStatusTests
{
    private readonly ITestOutputHelper _output;

    public OpcUaInboundStatusTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task WhenASubscriptionNotificationIsBad_ThenTheValueIsNotApplied()
    {
        // Arrange
        await using var fixture = await InboundStatusFixture.StartAsync(_output);
        await fixture.WaitForClientValueAsync("initial");

        // Act
        OpcUaNodeStatusDriver.Publish(
            fixture.ServerService, fixture.ServerProperty, "from-faulted-sensor", StatusCodes.BadDeviceFailure);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => fixture.ServerRoot.Child!.Value == "from-faulted-sensor",
            message: "the server's own model should hold the driven value");

        Assert.Equal("initial", fixture.ClientRoot.Child!.Value);
    }
}
```

`InboundStatusFixture` is a small `IAsyncDisposable` in the same file that starts an `OpcUaTestServer` plus a connected client against an `OpcUaTestPortPool` port, following `OpcUaCrossStoreConvergenceTests`. Write it once here; every later task reuses it.

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~WhenASubscriptionNotificationIsBad"`
Expected: FAIL. The client holds `"from-faulted-sensor"`, because the status is never checked.

- [ ] **Step 3: Add the guard**

In `SubscriptionManager.cs`, replace the body of the build loop at `:196-207`:

```csharp
for (var i = 0; i < monitoredItemsCount; i++)
{
    var item = notification.MonitoredItems[i];
    if (!_monitoredItems.TryGetValue(item.ClientHandle, out var property))
    {
        continue;
    }

    // Good and Uncertain are both usable: Uncertain means the server doubts the quality, not that
    // there is no reading. Bad means the value is not usable and may not even be present.
    if (!StatusCode.IsNotBad(item.Value.StatusCode))
    {
        continue;
    }

    changes.Add(new PropertyUpdate
    {
        Property = property,
        Value = _configuration.ValueConverter.ConvertToPropertyValue(item.Value.Value, property),
        Timestamp = item.Value.SourceTimestamp
    });
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~WhenASubscriptionNotificationIsBad"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs \
        src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaInboundStatusTests.cs
git commit -m "fix: stop applying values the server marked Bad on the subscription path"
```

---

## Task 3: One bad conversion stops discarding the whole notification

The catch at `SubscriptionManager.cs:208-216` returns the pooled list and **rethrows**, so a single property whose converter throws discards every other value in the same notification.

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs:196-207`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaInboundStatusTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task WhenOneValuesConversionThrows_ThenTheRestOfTheNotificationIsStillApplied()
{
    // Arrange
    await using var fixture = await InboundStatusFixture.StartAsync(
        _output, valueConverter: new ThrowOnSentinelConverter("poison"));
    await fixture.WaitForClientValueAsync("initial");

    // Act: drive both properties in one flush so they arrive in one notification.
    fixture.PublishPair(first: "poison", second: "survivor");

    // Assert
    await AsyncTestHelpers.WaitUntilAsync(
        () => fixture.ClientRoot.Child!.Other == "survivor",
        message: "a sibling property must survive another property's converter throw");
}
```

`ThrowOnSentinelConverter` is a test double deriving from `OpcUaValueConverter` that throws from `ConvertToPropertyValue` when the incoming value equals its sentinel. Put it in the test file.

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~WhenOneValuesConversionThrows"`
Expected: FAIL by timeout. `Other` is never applied, because the throw discards the batch.

- [ ] **Step 3: Guard the conversion per item**

Wrap only the conversion, inside the loop written in Task 2:

```csharp
    object? converted;
    try
    {
        converted = _configuration.ValueConverter.ConvertToPropertyValue(item.Value.Value, property);
    }
    catch (Exception e)
    {
        _logger.LogError(e, "Failed to convert an inbound value for {PropertyName}.", property.Name);
        continue;
    }

    changes.Add(new PropertyUpdate
    {
        Property = property,
        Value = converted,
        Timestamp = item.Value.SourceTimestamp
    });
```

Keep the outer `catch` at `:208-216` exactly as it is. It exists to return the pooled list on a genuinely unexpected failure and must keep rethrowing.

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~WhenOneValuesConversionThrows"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs \
        src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaInboundStatusTests.cs
git commit -m "fix: one bad conversion no longer discards a whole subscription notification"
```

---

## Task 4: Polling applies Uncertain values, and converts them

Two defects in one path. `PollingManager.cs:363-374` drops Uncertain with no log and no metric, and `ProcessValueChange` applies `dataValue.Value` **raw**, so a `decimal`, `decimal[]` or `Uuid`-delivered `Guid` property throws in the setter, is swallowed at `:424-427`, and never updates via polling at all.

The conversion must sit **between** the equality check and `_pollingItems.TryUpdate`. After the cache update, a throw loses the change permanently, because the next poll sees no difference.

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Polling/PollingManager.cs:363-374`, `:387-414`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaInboundStatusTests.cs`

- [ ] **Step 1: Write the two failing tests**

```csharp
[Fact]
public async Task WhenAPolledValueIsUncertain_ThenItIsApplied()
{
    // Arrange
    await using var fixture = await InboundStatusFixture.StartAsync(_output, pollingOnly: true);
    await fixture.WaitForClientValueAsync("initial");

    // Act
    OpcUaNodeStatusDriver.Publish(
        fixture.ServerService, fixture.ServerProperty, "held-over", StatusCodes.UncertainLastUsableValue);

    // Assert
    await AsyncTestHelpers.WaitUntilAsync(
        () => fixture.ClientRoot.Child!.Value == "held-over",
        message: "an Uncertain value is usable and must be applied");
}

[Fact]
public async Task WhenAPolledPropertyNeedsConversion_ThenItIsApplied()
{
    // Arrange
    await using var fixture = await InboundStatusFixture.StartAsync(_output, pollingOnly: true);

    // Act: Decimal maps to Double on the wire, so this only lands if the poll path converts.
    OpcUaNodeStatusDriver.Publish(
        fixture.ServerService, fixture.DecimalProperty, 12.5d, StatusCodes.Good);

    // Assert
    await AsyncTestHelpers.WaitUntilAsync(
        () => fixture.ClientRoot.Child!.DecimalValue == 12.5m,
        message: "the polling path must convert like every other inbound path");
}
```

- [ ] **Step 2: Run both and watch them fail**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~WhenAPolled"`
Expected: both FAIL by timeout. The first is dropped for being Uncertain; the second throws in the setter and is swallowed.

- [ ] **Step 3: Apply non-Bad, and record Uncertain**

Replace the status branch at `:363-374`:

```csharp
if (StatusCode.IsNotBad(dataValue.StatusCode))
{
    _metrics.RecordRead();
    ProcessValueChange(pollingItem, dataValue, DateTimeOffset.UtcNow);
}
else
{
    _metrics.RecordFailedRead();
    _logger.LogWarning("Polling read failed for {NodeId}: {Status}",
        pollingItem.NodeId, dataValue.StatusCode);
}
```

- [ ] **Step 4: Convert before the cache update**

In `ProcessValueChange`, between the equality check and `TryUpdate`:

```csharp
if (!ValuesAreEqual(newValue, oldValue))
{
    object? converted;
    try
    {
        converted = _configuration.ValueConverter.ConvertToPropertyValue(newValue, pollingItem.Property);
    }
    catch (Exception e)
    {
        // Before the cache update on purpose: updating first would make the next poll see no change
        // and lose this value permanently.
        _logger.LogError(e, "Failed to convert a polled value for {NodeId}.", pollingItem.NodeId);
        return;
    }

    var key = pollingItem.NodeId.ToString();
    var updatedItem = pollingItem with { LastValue = newValue };   // cache stays raw
    ...
    var update = new PropertyUpdate
    {
        Property = pollingItem.Property,
        Value = converted,
        Timestamp = dataValue.SourceTimestamp
    };
```

`LastValue` keeps the **raw** value so change detection continues to compare like with like.

`pollingItem.Property` is a `PropertyReference`; `ConvertToPropertyValue` takes a `RegisteredSubjectProperty`. Resolve it with `TryGetRegisteredProperty()` and skip with a log if it does not resolve, matching what the other three paths do.

- [ ] **Step 5: Run both and watch them pass**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~WhenAPolled"`
Expected: both PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/Polling/PollingManager.cs \
        src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaInboundStatusTests.cs
git commit -m "fix: polling applies Uncertain values and converts them like every other path"
```

---

## Task 5: The initial state load applies Uncertain, and survives a throwing apply

Two defects. `OpcUaSubjectClientSource.cs:237` keeps only Good results, so an Uncertain property is never initialised. And the apply loop at `:247-251` has no `try`, so one property whose apply throws aborts the rest of the load, propagates to `SubjectSourceBase.cs:215`, is caught at `:249`, and the connect is retried. A deterministic throw means the source **never reaches `Synchronized`**.

Do **not** wrap `SubjectPropertyWriter.cs:111` instead. The WebSocket load closure calls `ClaimPropertyOwnership()` after its apply and that is its only call site, so swallowing there would leave that connector owning nothing and silently mute.

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs:236-254`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaInboundStatusTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task WhenOnePropertysApplyThrowsDuringInitialLoad_ThenTheSourceStillReachesSynchronized()
{
    // Arrange: a validating interceptor that rejects one property's loaded value.
    await using var fixture = await InboundStatusFixture.StartAsync(
        _output, clientInterceptor: new ThrowOnValueInterceptor("initial"));

    // Act & Assert
    await AsyncTestHelpers.WaitUntilAsync(
        () => fixture.ClientSource.State == SourceState.Synchronized,
        message: $"one throwing property must not wedge the connect; state is {fixture.ClientSource.State}");
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~WhenOnePropertysApplyThrows"`
Expected: FAIL by timeout, with the state cycling rather than settling.

- [ ] **Step 3: Keep non-Bad results, guard each apply, and report honestly**

```csharp
for (var i = 0; i < resultCount; i++)
{
    if (StatusCode.IsNotBad(readResponse.Results[i].StatusCode))
    {
        result[ownedProperties[offset + i].Property] = readResponse.Results[i];
    }
}
```

and the returned closure:

```csharp
_logger.LogInformation("Read {Count} of {Requested} OPC UA nodes from server.", result.Count, itemCount);
return () =>
{
    var applied = 0;
    foreach (var (property, dataValue) in result)
    {
        try
        {
            var value = _configuration.ValueConverter.ConvertToPropertyValue(dataValue.Value, property);
            property.SetValueFromSource(this, dataValue.SourceTimestamp, null, value);
            applied++;
        }
        catch (Exception e)
        {
            // Per property, not per load: one rejected value must not stop the source reaching
            // Synchronized, because the connect would then retry forever.
            _logger.LogError(e, "Failed to apply the loaded value for {PropertyName}.", property.Name);
        }
    }

    _logger.LogInformation("Updated {Count} properties with OPC UA node values.", applied);
};
```

Both logs now report what happened rather than what was requested.

- [ ] **Step 4: Run it and watch it pass**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~WhenOnePropertysApplyThrows"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs \
        src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaInboundStatusTests.cs
git commit -m "fix: a throwing apply no longer wedges the initial state load"
```

---

## Task 6: Read-after-write applies Uncertain values

`ReadAfterWriteManager.cs:322-326` skips everything that is not Good, silently.

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/ReadAfterWrite/ReadAfterWriteManager.cs:322-326`

- [ ] **Step 1: Make the change**

```csharp
var result = response.Results[i];
if (!StatusCode.IsNotBad(result.StatusCode))
{
    continue;
}
```

No new test. Read-after-write is inert against a timestamp-preserving server (the guard compares a value to itself), so no integration test can observe this until the read-after-write commit lands and changes that. The behaviour is covered there, and this one-line change keeps the four sites consistent rather than leaving one on the old rule.

- [ ] **Step 2: Run the whole OPC UA suite**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests`
Expected: PASS, including the existing integration tests. Nothing else may regress.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/ReadAfterWrite/ReadAfterWriteManager.cs
git commit -m "fix: read-after-write applies Uncertain values like every other inbound path"
```

---

## Task 7: Verify the whole commit

- [ ] **Step 1: Run the unit suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS, no regressions.

- [ ] **Step 2: Run the OPC UA integration suite alone**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests`
Expected: PASS. Nothing else may be using port 4840.

- [ ] **Step 3: Confirm the public API did not move**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: PASS with no `.received.txt` produced. This commit is entirely internal; if the snapshot moves, something was made public that should not have been.

- [ ] **Step 4: Run the MQTT and WebSocket integration suites**

Run them one at a time, since each binds ports:

```bash
dotnet test src/Namotion.Interceptor.Mqtt.Tests
dotnet test src/Namotion.Interceptor.WebSocket.Tests
```

Expected: PASS. Neither connector is touched by this commit, so a failure means something shared moved unexpectedly.

---

## Notes carried from review

- `StatusCode.IsNotBad` is verified to be exactly Good plus Uncertain: `IsNotBad` is `(code & 0x80000000) == 0`, and the reserved severity falls to Bad, which is the side we want.
- Bad is sticky, so a faulted sensor reports it every sample. The `LogWarning` in Task 4 fires at the polling interval for such a property. If that proves noisy in the soak, log the transition rather than the value, but do not add a second dictionary lookup to the Good path to do it.
- `OpcUaClientDiagnostics.IncomingChangesPerSecond` will shift against a server that emits Bad, because those changes are no longer counted as applied. Expected, and it belongs in the release notes.

---

## What this commit deliberately does not do

The retry-queue collapse, continuing past a failing batch, and the read-after-write revision guard. Those are commits 2 and 3 of the same PR and get their own plans. They touch `Namotion.Interceptor.Connectors`, which every connector shares, and one of them changes an ordering guarantee documented at `docs/connectors.md:791`, so they should be planned with the same fidelity rather than folded in here.
