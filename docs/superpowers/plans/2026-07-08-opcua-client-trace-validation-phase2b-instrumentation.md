# OPC UA Client Trace Validation, Phase 2b: C# Instrumentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Instrument the OPC UA client so the existing integration tests emit abstract-state traces, and validate those real traces against `OpcUaClient.tla` with the Phase 2a checker, so a code regression in the modeled area is caught mechanically.

**Architecture:** A `[Conditional("MODELTRACE")]` static helper in core forwards per-dimension transition events to a test-side sink via an `AsyncLocal` hook. The client's subsystems call it at their mutation sites. The sink folds events into full-state snapshots and writes one newline-delimited JSON behavior per client per test. The Phase 2a `check-traces.sh` validates them. Production ships with the calls compiled out.

**Tech Stack:** C# 13, .NET 9, xUnit, `System.Text.Json`; the Phase 2a TLA+ trace-checking pipeline.

**Depends on:** PR #359 merged to master (this instruments the fixed client), and Phase 2a complete (`gen-trace-spec.sh`, `check-traces.sh`, Community Modules toolchain).

**Grounded sites (referenced by method; verified present on merged master):** buffering `SubjectPropertyWriter.StartBuffering` / resume (`LoadInitialStateAndResumeAsync`) in `Namotion.Interceptor.Connectors`; session `SessionManager.SetSession` and `AbandonCurrentSession`; coverage `SubscriptionManager` around `subscription.AddItem`, the `ClassifyFailedItem` disposition switch, and `EscalatePersistentlyFailedItemsAsync`. Note: #364 centralized the transient/permanent decision into `OpcUaStatusCodeClassifier` (used by both `ClassifyFailedItem` and `SubscriptionHealthMonitor.IsRetryable`); the disposition categories and the coverage-transition sites are unchanged, so the instrumentation points are the same.

**Three hard points, addressed as spikes (Tasks 1, 6, 7 handle them):**
1. `AsyncLocal` flows via `ExecutionContext` to the client's own `Task.Run` loops if the sink is set before `StartAsync`, but the OPC UA SDK's callback threads (`OnKeepAlive`, `OnReconnectComplete`) may not carry it, so SDK-path session events can be lost. Task 6 spikes the capture.
2. The model's `Items` is a fixed abstract set, but real traces have arbitrary node keys and counts. Phase 2a's generator derives `Items` per trace (`DOMAIN cover`); Task 1 confirms it on realistic keys.
3. The client's session state is implicit in flags, not an enum matching the model. Task 7 builds the mapping and validates it by running the generated full-state checker over the suite.

---

## File Structure

- Create: `src/Namotion.Interceptor/Diagnostics/ModelTrace.cs` — the `[Conditional]` helper plus `AsyncLocal` hook (core; inert in production).
- Modify: `Directory.Build.props` — `EnableModelTrace` to `MODELTRACE`.
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Formal/ModelTraceSink.cs` — the fold-and-flush sink (test infra).
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Integration/Testing/` base/fixture — install a per-test sink scope.
- Modify: `SubjectPropertyWriter.cs`, `SessionManager.cs`, `SubscriptionManager.cs`, `SubscriptionHealthMonitor.cs`, and the test server/harness — add `ModelTrace` calls.
- (No checker file to edit: Phase 2a's `gen-trace-spec.sh` generates the trace-check module from the model and derives `Items` per trace.)
- Create: `.github/workflows/` trace-validation job (or extend `build.yml`).

---

### Task 1: Confirm the generated checker on realistic keys

Phase 2a's generator already derives `Items` per trace (`DOMAIN cover`), so there is no per-model checker file to edit here. Confirm it validates a trace whose `cover` keys are realistic OPC UA node ids, so the emission side is built on a verified checker.

**Files:**
- Create: `docs/formal/opcua-client/samples/realistic-ok.ndjson`

- [ ] **Step 1: A conforming trace with node-id keys**

Create `docs/formal/opcua-client/samples/realistic-ok.ndjson` (one line): a `Connect` then `Activate` behavior like Phase 2a's `ok.ndjson`, but with `cover` keys `"ns=2;s=Temp"` and `"ns=2;s=Pressure"` (both `Retrying`, then `Subscribed` at `SessionActive`).

- [ ] **Step 2: Validate**

```bash
tools/tla/check-traces.sh docs/formal/opcua-client/samples/realistic-ok.ndjson docs/formal/opcua-client/OpcUaClient.tla; echo "exit: $?"
```

Expected: `exit: 0`. If `Items <- TraceItems` chokes on the keys, apply the `jq`-extracted `CONSTANT Items = {...}` fallback in `gen-trace-spec.sh` (documented in Phase 2a Task 3).

- [ ] **Step 3: Commit**

```bash
git add docs/formal/opcua-client/samples/realistic-ok.ndjson
git commit -m "Confirm generated checker validates realistic node-key traces"
```

---

### Task 2: The core `ModelTrace` helper

**Files:**
- Create: `src/Namotion.Interceptor/Diagnostics/ModelTrace.cs`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Formal/ModelTraceTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.OpcUa.Tests/Formal/ModelTraceTests.cs`:

```csharp
using Namotion.Interceptor.Diagnostics;
using Xunit;

public class ModelTraceTests
{
    [Fact]
    public void WhenSinkInstalled_ThenSetForwardsFieldAndValue()
    {
        // Arrange
        var captured = new List<(string field, string? key, string value)>();
        ModelTrace.Sink.Value = (f, k, v) => captured.Add((f, k, v));

        // Act
        ModelTrace.Set("state", "SessionActive");
        ModelTrace.SetItem("cover", "ns=2;s=A", "Subscribed");

        // Assert
        Assert.Equal(("state", null, "SessionActive"), captured[0]);
        Assert.Equal(("cover", "ns=2;s=A", "Subscribed"), captured[1]);
        ModelTrace.Sink.Value = null;
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests -p:EnableModelTrace=true --filter FullyQualifiedName~ModelTraceTests`
Expected: FAIL, `ModelTrace` does not exist.

- [ ] **Step 3: Write the helper**

Create `src/Namotion.Interceptor/Diagnostics/ModelTrace.cs`:

```csharp
using System.Diagnostics;

namespace Namotion.Interceptor.Diagnostics;

/// <summary>
/// Emits abstract-state transition events for formal trace validation. All calls
/// are compiled out unless the MODELTRACE symbol is defined, so production ships
/// with no instrumentation. The test infrastructure installs <see cref="Sink"/>.
/// </summary>
public static class ModelTrace
{
    /// <summary>Test-side receiver of (field, itemKey, value). Null in production.</summary>
    public static readonly AsyncLocal<Action<string, string?, string>?> Sink = new();

    [Conditional("MODELTRACE")]
    public static void Set(string field, string value) => Sink.Value?.Invoke(field, null, value);

    [Conditional("MODELTRACE")]
    public static void SetItem(string field, string key, string value) => Sink.Value?.Invoke(field, key, value);
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests -p:EnableModelTrace=true --filter FullyQualifiedName~ModelTraceTests`
Expected: PASS. (Without `-p:EnableModelTrace=true` the calls compile out and the test would trivially see nothing, which is why the formal tests always set the property.)

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor/Diagnostics/ModelTrace.cs src/Namotion.Interceptor.OpcUa.Tests/Formal/ModelTraceTests.cs
git commit -m "Add ModelTrace conditional trace helper in core"
```

---

### Task 3: Build wiring for MODELTRACE

**Files:**
- Modify: `Directory.Build.props`

- [ ] **Step 1: Add the conditional symbol**

In `Directory.Build.props`, inside the main `<PropertyGroup>`, add:

```xml
<DefineConstants Condition="'$(EnableModelTrace)' == 'true'">$(DefineConstants);MODELTRACE</DefineConstants>
```

- [ ] **Step 2: Verify it flows**

Run: `dotnet build src/Namotion.Interceptor -c Release -p:EnableModelTrace=true -v:n 2>&1 | grep -i modeltrace | head -2`
Expected: the build defines `MODELTRACE`. Then confirm a plain build does not:
Run: `dotnet build src/Namotion.Interceptor -c Release 2>&1 | grep -ci modeltrace`
Expected: `0`.

- [ ] **Step 3: Commit**

```bash
git add Directory.Build.props
git commit -m "Wire EnableModelTrace to the MODELTRACE define"
```

---

### Task 4: The fold-and-flush sink

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Formal/ModelTraceSink.cs`
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Formal/ModelTraceSinkTests.cs`

The sink installs itself as the `AsyncLocal` hook, folds each event into the running abstract state, snapshots after every event, and on dispose appends one ND-JSON behavior line to the shared trace file. `linkUp/buffering/stalled` serialize as JSON booleans, `state` and each `cover` value as strings.

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.OpcUa.Tests/Formal/ModelTraceSinkTests.cs`:

```csharp
using System.Text.Json;
using Namotion.Interceptor.Diagnostics;
using Xunit;

public class ModelTraceSinkTests
{
    [Fact]
    public void WhenEventsRecorded_ThenOneNdjsonBehaviorWithFoldedSnapshots()
    {
        // Arrange
        var file = Path.GetTempFileName();

        // Act
        using (new ModelTraceSink(file))
        {
            ModelTrace.Set("state", "Connecting");
            ModelTrace.Set("state", "SessionActive");
            ModelTrace.SetItem("cover", "A", "Subscribed");
        }

        // Assert
        var line = File.ReadAllText(file).TrimEnd('\n');
        using var doc = JsonDocument.Parse(line);
        var states = doc.RootElement;
        Assert.Equal(3, states.GetArrayLength());
        Assert.Equal("SessionActive", states[1].GetProperty("state").GetString());
        Assert.Equal("Subscribed", states[2].GetProperty("cover").GetProperty("A").GetString());
        Assert.True(states[0].GetProperty("linkUp").GetBoolean());
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests -p:EnableModelTrace=true --filter FullyQualifiedName~ModelTraceSinkTests`
Expected: FAIL, `ModelTraceSink` does not exist.

- [ ] **Step 3: Write the sink**

Create `src/Namotion.Interceptor.OpcUa.Tests/Formal/ModelTraceSink.cs`:

```csharp
using System.Text.Json;
using Namotion.Interceptor.Diagnostics;

namespace Namotion.Interceptor.OpcUa.Tests.Formal;

/// <summary>Folds ModelTrace events into full-state snapshots and appends one
/// newline-delimited JSON behavior per instance to a shared trace file.</summary>
internal sealed class ModelTraceSink : IDisposable
{
    private static readonly object FileGate = new();

    private readonly object _gate = new();
    private readonly string _file;
    private readonly Action<string, string?, string>? _previous;
    private readonly List<object> _snapshots = new();
    private readonly Dictionary<string, string> _scalar = new();
    private readonly Dictionary<string, string> _cover = new();
    private int _seq;

    public ModelTraceSink(string file)
    {
        _file = file;
        _previous = ModelTrace.Sink.Value;
        ModelTrace.Sink.Value = Record;
    }

    private void Record(string field, string? key, string value)
    {
        lock (_gate)
        {
            if (field == "cover" && key is not null) _cover[key] = value;
            else _scalar[field] = value;
            _snapshots.Add(Snapshot());
        }
    }

    private object Snapshot() => new
    {
        seq = _seq++,
        state = _scalar.GetValueOrDefault("state", "Disconnected"),
        linkUp = _scalar.GetValueOrDefault("linkUp", "true") == "true",
        buffering = _scalar.GetValueOrDefault("buffering", "false") == "true",
        stalled = _scalar.GetValueOrDefault("stalled", "false") == "true",
        cover = new Dictionary<string, string>(_cover),
    };

    public void Dispose()
    {
        ModelTrace.Sink.Value = _previous;
        lock (_gate)
        {
            if (_snapshots.Count == 0) return;
            var line = JsonSerializer.Serialize(_snapshots);
            lock (FileGate) File.AppendAllText(_file, line + "\n");
        }
    }
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests -p:EnableModelTrace=true --filter FullyQualifiedName~ModelTraceSinkTests`
Expected: PASS.

- [ ] **Step 5: Validate the produced JSON against the checker**

Run: point `check-traces.sh` at the file the test wrote (adapt the test to a known path, or write a tiny fixture that emits a legal Connect/Activate behavior), then:

```bash
tools/tla/check-traces.sh <that-file> docs/formal/opcua-client/OpcUaClient.tla; echo "exit: $?"
```

Expected: `exit: 0`. This closes the loop: the sink's real JSON validates with the Phase 2a checker.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Formal/ModelTraceSink.cs src/Namotion.Interceptor.OpcUa.Tests/Formal/ModelTraceSinkTests.cs
git commit -m "Add fold-and-flush ModelTraceSink and validate its output"
```

---

### Task 5: Instrument buffering (single choke point)

Buffering is the cheapest dimension: instrument it inside `SubjectPropertyWriter` so every caller is covered at one place.

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectPropertyWriter.cs`

- [ ] **Step 1: Emit on start and on resume**

In `StartBuffering()`, after the buffering flag is set, add:

```csharp
Namotion.Interceptor.Diagnostics.ModelTrace.Set("buffering", "true");
```

In the resume path (`LoadInitialStateAndResumeAsync`, at the point buffering is turned off and replay completes), add:

```csharp
Namotion.Interceptor.Diagnostics.ModelTrace.Set("buffering", "false");
```

- [ ] **Step 2: Build with the symbol to confirm it compiles**

Run: `dotnet build src/Namotion.Interceptor.Connectors -c Release -p:EnableModelTrace=true`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/SubjectPropertyWriter.cs
git commit -m "Emit buffering transitions for trace validation"
```

---

### Task 6: Per-test sink scope and SDK-thread capture (spike)

Install a sink per test around the client, and resolve the `AsyncLocal`-not-flowing-to-SDK-threads risk.

**Files:**
- Modify: the shared test base/fixture under `src/Namotion.Interceptor.OpcUa.Tests/Integration/Testing/`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Connection/SessionManager.cs` (capture point, if needed)

- [ ] **Step 1: Install the sink before the client starts**

In the test base's `InitializeAsync` (before the client is created and `StartAsync` is called), set `EnableModelTrace` output path and create a `ModelTraceSink` bound to `artifacts/model-traces/opcua-client/opcua.json`, disposed in `DisposeAsync`. Because the sink is set before `StartAsync`, the client's own `Task.Run` health loop captures it via `ExecutionContext`.

- [ ] **Step 2: Spike SDK-callback capture**

Add a temporary `ModelTrace.Set("state", "probe")` inside `OnKeepAlive` and force a reconnect test (`OpcUaReconnectionTests`). Run with `-p:EnableModelTrace=true` and inspect `opcua.json`.

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests -c Release -p:EnableModelTrace=true --filter FullyQualifiedName~OpcUaReconnectionTests`
Expected outcome, one of two:
- The probe appears: `ExecutionContext` flows to the SDK callback, no extra work needed. Remove the probe.
- The probe is missing: capture the `ExecutionContext` when the client installs its SDK callbacks and run the callback body under it, or have `SessionManager` hold a direct sink reference set at construction and call it instead of relying on `AsyncLocal`. Implement whichever the spike shows is needed, then remove the probe.

- [ ] **Step 3: Verify session events survive a reconnect**

After resolving Step 2, confirm a reconnection test's trace contains the `ReconnectingSdk` and back-to-`SessionActive` transitions (grep `opcua.json`). This proves SDK-path session events are captured.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Integration/Testing/ src/Namotion.Interceptor.OpcUa/Client/Connection/SessionManager.cs
git commit -m "Install per-test trace sink and ensure SDK-thread capture"
```

---

### Task 7: Instrument session state (build and validate the mapping)

The client's session state is implicit; emit the model's abstract `state` at each transition, then validate with the generated full-state checker. Because the checker pins all variables, reconnect traces need `linkUp` too, so do Task 9 together with this task; non-reconnect tests validate with `linkUp` at its default (true).

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Connection/SessionManager.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`

- [ ] **Step 1: Emit `state` at each transition site**

Add `ModelTrace.Set("state", "<value>")` right after each concrete transition, under the lock that guards it:

- Connecting: when `CreateSessionAsync` begins the connect. Value `"Connecting"`.
- SessionActive: after subscriptions are created and initial state loaded (end of `StartListeningAsync` success; and after `SdkReconnect` transfer success at `SetSession(reconnectedSession)`; and after `ManualReconnectRecreate` success). Value `"SessionActive"`.
- ReconnectingSdk: in `OnKeepAlive` when a bad status triggers `BeginReconnect`. Value `"ReconnectingSdk"`.
- Abandoning: at the top of `AbandonCurrentSession`. Value `"Abandoning"`.
- ReconnectingManual: when the manual reconnect begins (`ReconnectSessionAsync`, after `StartBuffering`). Value `"ReconnectingManual"`.
- Faulted: in the manual-reconnect exception path. Value `"Faulted"`.
- Stalled / ForceReset: in `TryForceResetIfStalled`. Values `"Stalled"` then `"ReconnectingManual"`.
- Disconnected: at `Reset`/initial. Value `"Disconnected"`.

- [ ] **Step 2: Validate over the suite**

The generated checker pins all model variables, so there is no trace-spec to edit. Instrument `linkUp` too (Task 9) so reconnect traces are well-formed, then run the integration suite and check:

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -c Release -p:EnableModelTrace=true --filter Category=Integration
tools/tla/check-traces.sh artifacts/model-traces/opcua-client/opcua.json docs/formal/opcua-client/OpcUaClient.tla; echo "exit: $?"
```

Expected: `exit: 0` across all behaviors. Any non-conformance is a real signal: either a missing/misplaced `Set("state", ...)` (fix the emission) or the code doing something the model does not permit (a bug, or a model gap to record). The TLC counterexample names the field that broke, so a coverage or buffering mismatch here just means that dimension is not instrumented yet (later tasks). Iterate until the session and linkUp transitions are clean, or a genuine finding is logged.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/Connection/SessionManager.cs src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs
git commit -m "Emit session-state transitions and validate session state"
```

---

### Task 8: Instrument item coverage

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Resilience/SubscriptionHealthMonitor.cs`

Emit `cover` per item using its node id as the key.

- [ ] **Step 1: Emit at each coverage transition**

- Subscribed: after `subscription.AddItem(item)` succeeds and the item is confirmed good (after `FilterOutFailedMonitoredItemsAsync` keeps it). `ModelTrace.SetItem("cover", nodeId, "Subscribed")`.
- Retrying: in `FilterOutFailedMonitoredItemsAsync` when `ClassifyFailedItem(...) == KeepForRetry`. `ModelTrace.SetItem("cover", nodeId, "Retrying")`.
- Polling: when an item moves to polling, in the `FallbackToPolling` handling and in `EscalatePersistentlyFailedItemsAsync` at `_pollingManager.AddItem`. `ModelTrace.SetItem("cover", nodeId, "Polling")`.
- Subscribed (healed): in `SubscriptionHealthMonitor` when a retrying item recovers. `ModelTrace.SetItem("cover", nodeId, "Subscribed")`.

Use the item's `StartNodeId.ToString()` as the key, consistent everywhere.

- [ ] **Step 2: Widen the trace-spec to coverage and validate**

The generated checker already pins `cover`, so no spec change is needed. Run:

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -c Release -p:EnableModelTrace=true --filter Category=Integration
tools/tla/check-traces.sh artifacts/model-traces/opcua-client/opcua.json docs/formal/opcua-client/OpcUaClient.tla; echo "exit: $?"
```

Expected: `exit: 0`, or a logged genuine finding. Note: `OpcUaDynamicServerClientTests` (runtime item-set growth) is the known model gap; exclude its trace with a documented reason (a tag or a skip in the validation step), do not silently drop it.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs src/Namotion.Interceptor.OpcUa/Client/Resilience/SubscriptionHealthMonitor.cs
git commit -m "Emit item-coverage transitions and validate the coverage dimension"
```

---

### Task 9: Emit `linkUp` from the test harness

`linkUp` is the environment the test controls, so the harness emits it, not the client.

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Integration/Testing/OpcUaTestServer.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs` (the `KillSessionAsync` fault injector)

- [ ] **Step 1: Emit on server and fault transitions**

- `OpcUaTestServer.StartAsync` / `RestartAsync` completion: `ModelTrace.Set("linkUp", "true")`.
- `OpcUaTestServer.StopAsync`: `ModelTrace.Set("linkUp", "false")`.
- `KillSessionAsync` (chaos): `ModelTrace.Set("linkUp", "false")`.

Because the server is in-process and shared, gate these so they only fire when a sink is installed on the current flow (the `Set` no-ops when `Sink.Value` is null, which is already the case for tests that did not opt in).

- [ ] **Step 2: Full-state validation over the suite**

The generated checker already pins the full state, so no spec change is needed. Run:

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -c Release -p:EnableModelTrace=true --filter Category=Integration
tools/tla/check-traces.sh artifacts/model-traces/opcua-client/opcua.json docs/formal/opcua-client/OpcUaClient.tla; echo "exit: $?"
```

Expected: `exit: 0` across all in-scope behaviors.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Integration/Testing/OpcUaTestServer.cs src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs
git commit -m "Emit linkUp from the test harness and validate full state"
```

---

### Task 10: Regression proof

Prove the binding actually catches the #359 regressions the model guards.

**Files:** none committed (a temporary local revert)

- [ ] **Step 1: Revert fix 1 locally and show a trace non-conforms**

Temporarily change `ClassifyFailedItem` so a retryable failure returns `Drop` instead of `KeepForRetry`. Rebuild and run a reconnection test with a transient item failure, then `check-traces.sh`.
Expected: at least one behavior is NON-CONFORMING (an item reaches an orphaned/uncovered state the model forbids). Revert the change.

- [ ] **Step 2: Revert fix 2 locally and show a trace non-conforms**

Temporarily remove the `ModelTrace.Set("buffering","true")`-guarded `StartBuffering()` in `AbandonCurrentSession` (the real buffering call, not the trace call), so abandon does not buffer. Rebuild, run a transfer-fail reconnection test, `check-traces.sh`.
Expected: a behavior is NON-CONFORMING (`BufferingCoversAbandon` shape). Revert.

- [ ] **Step 3: Record the outcome**

Add a short note to `docs/formal/opcua-client/OpcUaClient.md` under a "Trace validation" heading stating both reverts are caught, then commit.

```bash
git add docs/formal/opcua-client/OpcUaClient.md
git commit -m "Record that trace validation catches both #359 regressions"
```

---

### Task 11: CI job

**Files:**
- Modify: `.github/workflows/build.yml` (or a new `trace-validation.yml`)

- [ ] **Step 1: Add the job**

Add a job that:

```yaml
  trace-validation:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '9.0.x' }
      - uses: actions/setup-java@v4
        with: { distribution: temurin, java-version: '17' }
      - run: tools/tla/bootstrap.sh
      - run: dotnet test src/Namotion.Interceptor.OpcUa.Tests -c Release -p:EnableModelTrace=true --filter Category=Integration
      - run: tools/tla/check-traces.sh artifacts/model-traces/opcua-client/opcua.json docs/formal/opcua-client/OpcUaClient.tla
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/
git commit -m "Add trace-validation CI job for the OPC UA client"
```

---

## Self-Review

**Spec coverage (against `2026-07-08-...-trace-validation-design.md`):** emission `[Conditional]` helper (Task 2), `Directory.Build.props` (Task 3), split shim/sink (Tasks 2, 4), per-dimension events folded test-side (Task 4), per-client `AsyncLocal` isolation and the SDK-thread risk (Task 6), the mapping decisions including `linkUp` from the harness and permanent items excluded (Tasks 7-9), incremental session-then-coverage-then-buffering validation (Tasks 5, 7, 8, 9), validate every emitted trace with dynamic discovery excluded visibly (Task 8), regression proof (Task 10), CI (Task 11). The per-trace `Items` gap the spec implied is Task 1. Covered.

**Placeholder scan:** The instrumentation tasks name concrete methods and the exact `ModelTrace` calls; the `<value>` in Task 7 Step 1 is enumerated per site. The "confirm before editing" note reflects that line numbers come from the pre-merge fix branch, not a placeholder. No "handle edge cases".

**Consistency:** `ModelTrace.Set` / `SetItem`, the `AsyncLocal` `Sink` signature `Action<string,string?,string>`, the snapshot field names (`state`, `linkUp`, `cover`, `buffering`, `stalled`, `seq`), and the trace-file path are consistent across Tasks 2, 4, 5, 7, 8, 9, and the Phase 2a checker.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-08-opcua-client-trace-validation-phase2b-instrumentation.md`.
