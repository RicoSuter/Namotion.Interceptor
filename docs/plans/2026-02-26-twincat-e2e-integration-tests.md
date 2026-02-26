# TwinCAT ADS E2E Integration Tests Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add end-to-end integration tests for the TwinCAT ADS connector using an in-process Beckhoff ADS server (no PLC required).

**Architecture:** Tests use `AmsTcpIpRouter` (in-process TCP loopback) + `AdsSymbolicServer` (symbol registration with reads/writes/notifications) from Beckhoff's official NuGet packages. The `TwinCatSubjectClientSource` connects through this in-process stack exactly as it would to a real PLC. Tests verify the full pipeline: connect → subscribe → initial read → notification → write → reconnect.

**Tech Stack:** Beckhoff.TwinCAT.Ads 7.0.x, Beckhoff.TwinCAT.Ads.SymbolicServer 7.0.x, Beckhoff.TwinCAT.Ads.TcpRouter 7.0.x, xUnit, Namotion.Interceptor.Testing (AsyncTestHelpers)

---

## Task 1: Upgrade Beckhoff NuGet packages to 7.0.x

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.TwinCAT/Namotion.Interceptor.Connectors.TwinCAT.csproj`
- Modify: `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Namotion.Interceptor.Connectors.TwinCAT.Tests.csproj`

**Step 1: Update main library package versions**

In `src/Namotion.Interceptor.Connectors.TwinCAT/Namotion.Interceptor.Connectors.TwinCAT.csproj`, change:

```xml
<PackageReference Include="Beckhoff.TwinCAT.Ads" Version="6.1.312" />
<PackageReference Include="Beckhoff.TwinCAT.Ads.Reactive" Version="6.1.312" />
```

to:

```xml
<PackageReference Include="Beckhoff.TwinCAT.Ads" Version="7.0.152" />
<PackageReference Include="Beckhoff.TwinCAT.Ads.Reactive" Version="7.0.152" />
```

**Step 2: Add test server packages**

In `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Namotion.Interceptor.Connectors.TwinCAT.Tests.csproj`, add these to the PackageReference ItemGroup:

```xml
<PackageReference Include="Beckhoff.TwinCAT.Ads.SymbolicServer" Version="7.0.123" />
<PackageReference Include="Beckhoff.TwinCAT.Ads.TcpRouter" Version="7.0.172" />
```

**Step 3: Build and fix any breaking API changes**

Run: `dotnet build src/Namotion.Interceptor.Connectors.TwinCAT`
Expected: May fail if Beckhoff 7.0.x has breaking API changes.

If build fails, read the compiler errors and fix them. Common 7.0.x changes include:
- `AdsSession` constructor changes
- `SymbolLoaderFactory.Create` signature changes
- Namespace renames
- Event args changes

Then run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: 0 errors

**Step 4: Verify all existing tests still pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.TwinCAT.Tests`
Expected: All 170 existing tests pass

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors.TwinCAT/Namotion.Interceptor.Connectors.TwinCAT.csproj src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Namotion.Interceptor.Connectors.TwinCAT.Tests.csproj
git commit -m "chore: upgrade Beckhoff TwinCAT packages to 7.0.x, add server/router packages for E2E tests"
```

---

## Task 2: Create minimal ADS test server infrastructure

**Files:**
- Create: `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/Testing/TestSymbol.cs`
- Create: `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/Testing/AdsTestServer.cs`

**Context:** We need a minimal in-process ADS server for E2E tests. Study the Beckhoff sample at `/tmp/TF6000_ADS_DOTNET_V5_Samples/Sources/ServerSamples/AdsSymbolicServerSample/AdsSymbolicServer.cs` for the `AdsSymbolicServer` subclass pattern. Key points:
- Subclass `AdsSymbolicServer` (from `Beckhoff.TwinCAT.Ads.SymbolicServer`)
- Constructor takes `(ushort port, string name, ILoggerFactory? loggerFactory)`
- Override `OnConnected()` to call `AddSymbols()` which uses `base.symbolFactory.AddType(...)` and `base.symbolFactory.AddSymbol(...)`
- Override `OnReadRawValue`, `OnWriteRawValue`, `OnSetValue`, `OnGetValue` for value storage
- Override `OnReadDeviceStateAsync` to return `AdsState.Run`
- Use `SymbolicAnyTypeMarshaler` for byte marshaling
- Start via `ConnectServerAndWaitAsync(CancellationToken)`
- Uses `SetValue(symbolPath, value)` to change values (triggers notifications when `OnSetValue` returns `valueChanged = true`)

For the router, study `/tmp/TF6000_ADS_DOTNET_V5_Samples/Sources/RouterSamples/AdsRouterConsoleApp/src/Worker.cs`:
- `new AmsTcpIpRouter(amsNetId, tcpPort, loopbackAddress, tcpPort, loggerFactory)` for code-based setup
- `router.StartAsync(cancellationToken)` to start
- The router must be running before the server and client connect

**Step 1: Create TestSymbol record**

Create `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/Testing/TestSymbol.cs`:

```csharp
namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Integration.Testing;

/// <summary>
/// Defines a symbol to register on the test ADS server.
/// </summary>
/// <param name="Path">The symbol path (e.g., "GVL.Temperature").</param>
/// <param name="DataType">The .NET type of the symbol value.</param>
/// <param name="InitialValue">The initial value of the symbol.</param>
public record TestSymbol(string Path, Type DataType, object? InitialValue);
```

**Step 2: Create AdsTestServer**

Create `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/Testing/AdsTestServer.cs`.

This is the most complex file. It contains two classes:

1. `AdsTestServer : IAsyncDisposable` — the public test helper that manages router + server lifecycle
2. `TestAdsSymbolicServer : AdsSymbolicServer` — the internal server subclass

Key design:
- `AdsTestServer` creates and manages both `AmsTcpIpRouter` and `TestAdsSymbolicServer`
- Router uses `AmsNetId("1.2.3.4.5.6")` on loopback (matching the Beckhoff sample pattern)
- Server registers on AMS port 25000 (Beckhoff user port range 25000-25999)
- `SetSymbolValue(path, value)` delegates to the internal server's `SetValue()` method (which triggers notifications)
- `GetSymbolValue(path)` reads back from the server's internal dictionary
- `StopAsync()` cancels the server (for reconnection tests)
- `RestartAsync()` stops then starts a new server instance (same symbols)
- Exposes `AmsNetId` and `AmsPort` for configuring `AdsClientConfiguration`

The `TestAdsSymbolicServer` implements:
- `OnConnected()` → registers types and symbols from the `TestSymbol[]` list
- `OnReadRawValue()` → marshals value from dictionary to bytes
- `OnWriteRawValue()` → unmarshals bytes, stores in dictionary
- `OnSetValue()` → stores value, returns `valueChanged = true` for notifications
- `OnGetValue()` → reads from dictionary
- `OnReadDeviceStateAsync()` → returns `AdsState.Run`

For each `TestSymbol`, map its `Type` to a Beckhoff `PrimitiveType`:
- `typeof(bool)` → `new PrimitiveType("BOOL", typeof(bool))`
- `typeof(short)` → `new PrimitiveType("INT", typeof(short))`
- `typeof(int)` → `new PrimitiveType("DINT", typeof(int))`
- `typeof(float)` → `new PrimitiveType("REAL", typeof(float))`
- `typeof(double)` → `new PrimitiveType("LREAL", typeof(double))`
- `typeof(string)` → `new StringType(80, Encoding.Unicode)`

**Step 3: Verify the infrastructure builds**

Run: `dotnet build src/Namotion.Interceptor.Connectors.TwinCAT.Tests`
Expected: 0 errors

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/Testing/
git commit -m "feat: add AdsTestServer infrastructure for in-process E2E testing"
```

---

## Task 3: Create integration test model and first connection test

**Files:**
- Create: `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/Models/IntegrationTestModel.cs`
- Create: `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/AdsIntegrationTests.cs`

**Step 1: Create the integration test model**

Create `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/Models/IntegrationTestModel.cs`:

```csharp
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.TwinCAT.Attributes;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Integration.Models;

[InterceptorSubject]
public partial class IntegrationTestModel
{
    [AdsVariable("GVL.Temperature")]
    public partial double Temperature { get; set; }

    [AdsVariable("GVL.MachineName")]
    public partial string? MachineName { get; set; }

    [AdsVariable("GVL.IsRunning")]
    public partial bool IsRunning { get; set; }

    [AdsVariable("GVL.Counter")]
    public partial int Counter { get; set; }
}
```

**Step 2: Create the test class with the first test**

Create `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/AdsIntegrationTests.cs`:

Write a test class with shared helper methods:
- `CreateSymbols()` → returns `TestSymbol[]` matching the model properties
- `CreateContext()` → creates `IInterceptorSubjectContext` with tracking + registry
- `CreateClientSource(model, server)` → creates `TwinCatSubjectClientSource` configured to connect to the test server

The first test `ConnectToServer_ShouldEstablishConnection` should:
1. Create server with symbols, start it
2. Create model and client source
3. Connect via `StartListeningAsync` (or `ConnectWithRetryAsync` via the internal manager)
4. Assert `IsConnected` becomes true using `AsyncTestHelpers.WaitUntilAsync`

Note: Since `TwinCatSubjectClientSource` is `internal`, the test project has `InternalsVisibleTo` access. The test can instantiate it directly with `new TwinCatSubjectClientSource(model, configuration, logger)`.

For the client configuration, use:
```csharp
new AdsClientConfiguration
{
    Host = "127.0.0.1",
    AmsNetId = server.AmsNetId,  // "1.2.3.4.5.6"
    AmsPort = server.AmsPort,     // 25000
    PathProvider = new AttributeBasedPathProvider(AdsConstants.DefaultConnectorName, '.')
}
```

**Step 3: Run the test**

Run: `dotnet test src/Namotion.Interceptor.Connectors.TwinCAT.Tests --filter "Category=Integration"`
Expected: Test passes (connection established to in-process server)

If the test fails due to router/connectivity issues, debug by:
1. Check if `AmsTcpIpRouter` started successfully
2. Check if `AdsSymbolicServer.ConnectServerAndWaitAsync` completed
3. Check the AMS Net ID / Port configuration
4. Check if another TwinCAT router is conflicting on the default TCP port

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/
git commit -m "feat: add first E2E integration test - connection to in-process ADS server"
```

---

## Task 4: Add read/write integration tests

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/AdsIntegrationTests.cs`

**Step 1: Add `ReadInitialState_ShouldPopulateProperties` test**

Test flow:
1. Server starts with `Temperature=25.0`, `MachineName="TestPLC"`, `IsRunning=true`, `Counter=42`
2. Create model + client source, call `StartListeningAsync`
3. Call `LoadInitialStateAsync` to trigger initial bulk read
4. Execute the returned action to apply values
5. Assert `model.Temperature == 25.0`, `model.MachineName == "TestPLC"`, etc.

**Step 2: Add `Notification_ServerValueChange_UpdatesClientProperty` test**

Test flow:
1. Server starts with `Temperature=25.0`
2. Client connects and subscribes (via `StartListeningAsync`)
3. Wait for initial state to propagate
4. Server sets `Temperature=42.0` via `server.SetSymbolValue("GVL.Temperature", 42.0)`
5. `WaitUntilAsync(() => model.Temperature == 42.0)`

**Step 3: Add `WriteProperty_ShouldUpdateServerSymbol` test**

Test flow:
1. Server starts with `MachineName="TestPLC"`
2. Client connects
3. Model sets `model.MachineName = "NewName"`
4. Wait for write to propagate
5. `WaitUntilAsync(() => server.GetSymbolValue("GVL.MachineName") as string == "NewName")`

Note: For writes to work, the model property change must trigger `WriteChangesAsync` on the source. This requires the `SubjectSourceBackgroundService` to be running, OR we call `WriteChangesAsync` directly in the test.

**Step 4: Add `MultipleProperties_ReadAndWrite_RoundTrip` test**

Test flow:
1. Server starts with known initial values
2. Client connects, reads initial state
3. Client writes multiple properties
4. Server reads back, values match
5. Server updates multiple values
6. Client reads, values match

**Step 5: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.TwinCAT.Tests --filter "Category=Integration"`
Expected: All tests pass

**Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/AdsIntegrationTests.cs
git commit -m "feat: add read/write E2E integration tests for TwinCAT connector"
```

---

## Task 5: Add batch polling integration test

**Files:**
- Create: `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/Models/PolledIntegrationTestModel.cs`
- Modify: `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/AdsIntegrationTests.cs`

**Step 1: Create a model with polled property**

Create `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/Models/PolledIntegrationTestModel.cs`:

```csharp
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.TwinCAT.Attributes;

namespace Namotion.Interceptor.Connectors.TwinCAT.Tests.Integration.Models;

[InterceptorSubject]
public partial class PolledIntegrationTestModel
{
    [AdsVariable("GVL.PolledCounter", ReadMode = AdsReadMode.Polled)]
    public partial int PolledCounter { get; set; }
}
```

**Step 2: Add `BatchPolling_PolledProperty_ReceivesUpdates` test**

Test flow:
1. Server starts with `PolledCounter=0`
2. Client connects with `PolledIntegrationTestModel`
3. Wait for initial value to propagate (via polling)
4. Server sets `PolledCounter=99`
5. `WaitUntilAsync(() => model.PolledCounter == 99)` — should update within polling interval

**Step 3: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.TwinCAT.Tests --filter "Category=Integration"`
Expected: All tests pass

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/
git commit -m "feat: add batch polling E2E integration test"
```

---

## Task 6: Add reconnection integration tests

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/AdsIntegrationTests.cs`

**Step 1: Add `ServerStop_ClientDetectsDisconnection` test**

Test flow:
1. Server starts, client connects
2. Verify `IsConnected` is true (via diagnostics)
3. Call `server.StopAsync()`
4. `WaitUntilAsync(() => !clientSource.Diagnostics.IsConnected)`

**Step 2: Add `ServerRestart_ClientReconnects` test**

Test flow:
1. Server starts, client connects
2. Stop server
3. Wait for disconnect detection
4. Restart server via `server.RestartAsync()`
5. `WaitUntilAsync(() => clientSource.Diagnostics.IsConnected)` — client should reconnect

Note: This test may need a longer timeout (e.g., 30-60 seconds) because reconnection involves the health check interval + circuit breaker cooldown.

**Step 3: Add `ServerRestart_PropertiesResyncAfterReconnection` test**

Test flow:
1. Server starts with `Temperature=25.0`, client connects and reads initial state
2. Stop server
3. Server restarts with `Temperature=42.0` (new value)
4. Client reconnects → full rescan → re-reads values
5. `WaitUntilAsync(() => model.Temperature == 42.0)`

**Step 4: Add `ServerRestart_NotificationsResumeAfterReconnection` test**

Test flow:
1. Server starts, client connects, verify notifications work
2. Stop server
3. Restart server
4. Client reconnects
5. Server sets `Temperature=99.0`
6. `WaitUntilAsync(() => model.Temperature == 99.0)` — notifications resumed after reconnect

**Step 5: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.TwinCAT.Tests --filter "Category=Integration"`
Expected: All tests pass

**Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Integration/AdsIntegrationTests.cs
git commit -m "feat: add reconnection E2E integration tests for TwinCAT connector"
```

---

## Task 7: Final verification and cleanup

**Step 1: Run all tests (unit + integration)**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All tests pass (170 existing unit tests + ~10 new integration tests)

**Step 2: Run only the full solution build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: 0 errors, 0 warnings

**Step 3: Review test output for flakiness**

Run integration tests 3 times:
```bash
dotnet test src/Namotion.Interceptor.Connectors.TwinCAT.Tests --filter "Category=Integration"
dotnet test src/Namotion.Interceptor.Connectors.TwinCAT.Tests --filter "Category=Integration"
dotnet test src/Namotion.Interceptor.Connectors.TwinCAT.Tests --filter "Category=Integration"
```
Expected: All 3 runs pass consistently

If any test is flaky, increase timeouts or add small delays for TCP socket cleanup between stop/restart.

**Step 4: Final commit if any cleanup was needed**

---

## Key Reference Files

- **Beckhoff SymbolicServer sample:** `/tmp/TF6000_ADS_DOTNET_V5_Samples/Sources/ServerSamples/AdsSymbolicServerSample/AdsSymbolicServer.cs`
- **Beckhoff Router sample:** `/tmp/TF6000_ADS_DOTNET_V5_Samples/Sources/RouterSamples/AdsRouterConsoleApp/src/Worker.cs`
- **Our AdsConnectionManager:** `src/Namotion.Interceptor.Connectors.TwinCAT/Client/AdsConnectionManager.cs`
- **Our AdsSubscriptionManager:** `src/Namotion.Interceptor.Connectors.TwinCAT/Client/AdsSubscriptionManager.cs`
- **Our TwinCatSubjectClientSource:** `src/Namotion.Interceptor.Connectors.TwinCAT/Client/TwinCatSubjectClientSource.cs`
- **Our AdsClientConfiguration:** `src/Namotion.Interceptor.Connectors.TwinCAT/Client/AdsClientConfiguration.cs`
- **Existing test models:** `src/Namotion.Interceptor.Connectors.TwinCAT.Tests/Models/`
- **AsyncTestHelpers:** `src/Namotion.Interceptor.Testing/AsyncTestHelpers.cs`
- **OPC UA integration test pattern:** `src/Namotion.Interceptor.OpcUa.Tests/Integration/Testing/`

## Notes

- The `AmsTcpIpRouter` runs fully in-process on TCP loopback — no TwinCAT installation required
- Tests are tagged `[Trait("Category", "Integration")]` so they can be filtered in CI if needed
- The test server uses AMS port 25000 (Beckhoff user port range 25000-25999)
- If a system TwinCAT router is running on the test machine, it may conflict with the in-process router on TCP port 48898. In that case, skip integration tests or configure a non-default TCP port.
- Reconnection tests may need generous timeouts (30-60s) due to health check intervals and circuit breaker cooldowns
