# OPC UA Resilience Testing Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add comprehensive E2E resilience tests for OPC UA client/server with diagnostics support, enabling confidence in 24/7 operation.

**Architecture:** Create shared test helper (`WaitUntilAsync`) in Testing library, add diagnostics classes to expose client/server state, implement resilience tests covering session transfer, full reconnect, write retry queue, and stability scenarios. Mark all integration tests with trait for selective execution.

**Tech Stack:** xUnit, .NET 9.0, OPC UA SDK, Microsoft.Extensions.Hosting

---

## Task 1: Add WaitUntilAsync Helper to Testing Library

**Files:**
- Create: `src/Namotion.Interceptor.Testing/AsyncTestHelpers.cs`

**Step 1: Write the failing test**

Create a simple test that uses `WaitUntilAsync` (it won't compile yet):

```csharp
// In any test file temporarily, or just verify compilation fails
var completed = false;
await AsyncTestHelpers.WaitUntilAsync(() => completed, timeout: TimeSpan.FromMilliseconds(100));
```

**Step 2: Verify it fails**

Run: `dotnet build src/Namotion.Interceptor.Testing`
Expected: FAIL - `AsyncTestHelpers` does not exist

**Step 3: Write the implementation**

Create `src/Namotion.Interceptor.Testing/AsyncTestHelpers.cs`:

```csharp
namespace Namotion.Interceptor.Testing;

/// <summary>
/// Helper methods for asynchronous test assertions with active waiting.
/// </summary>
public static class AsyncTestHelpers
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Waits until the specified condition becomes true, polling at regular intervals.
    /// Throws TimeoutException if the condition is not met within the timeout period.
    /// </summary>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <param name="pollInterval">Interval between condition checks. Defaults to 50ms.</param>
    /// <param name="message">Optional message to include in the timeout exception.</param>
    /// <exception cref="TimeoutException">Thrown when the condition is not met within the timeout.</exception>
    public static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        string? message = null)
    {
        var actualTimeout = timeout ?? DefaultTimeout;
        var actualPollInterval = pollInterval ?? DefaultPollInterval;
        var deadline = DateTime.UtcNow + actualTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(actualPollInterval).ConfigureAwait(false);
        }

        var errorMessage = string.IsNullOrEmpty(message)
            ? $"Condition was not met within {actualTimeout.TotalSeconds:F1} seconds."
            : $"{message} (timed out after {actualTimeout.TotalSeconds:F1} seconds)";

        throw new TimeoutException(errorMessage);
    }

    /// <summary>
    /// Waits until the specified async condition becomes true, polling at regular intervals.
    /// Throws TimeoutException if the condition is not met within the timeout period.
    /// </summary>
    /// <param name="condition">The async condition to wait for.</param>
    /// <param name="timeout">Maximum time to wait. Defaults to 30 seconds.</param>
    /// <param name="pollInterval">Interval between condition checks. Defaults to 50ms.</param>
    /// <param name="message">Optional message to include in the timeout exception.</param>
    /// <exception cref="TimeoutException">Thrown when the condition is not met within the timeout.</exception>
    public static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        string? message = null)
    {
        var actualTimeout = timeout ?? DefaultTimeout;
        var actualPollInterval = pollInterval ?? DefaultPollInterval;
        var deadline = DateTime.UtcNow + actualTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(actualPollInterval).ConfigureAwait(false);
        }

        var errorMessage = string.IsNullOrEmpty(message)
            ? $"Condition was not met within {actualTimeout.TotalSeconds:F1} seconds."
            : $"{message} (timed out after {actualTimeout.TotalSeconds:F1} seconds)";

        throw new TimeoutException(errorMessage);
    }
}
```

**Step 4: Verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.Testing`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Testing/AsyncTestHelpers.cs
git commit -m "feat(testing): add WaitUntilAsync helper for active waiting in tests

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

## Task 2: Add OpcUaClientDiagnostics Class

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientDiagnostics.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`

**Step 1: Create the diagnostics class**

Create `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientDiagnostics.cs`:

```csharp
namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Provides diagnostic information about the OPC UA client connection state.
/// Thread-safe for reading current values.
/// </summary>
public class OpcUaClientDiagnostics
{
    private readonly OpcUaSubjectClientSource _source;

    internal OpcUaClientDiagnostics(OpcUaSubjectClientSource source)
    {
        _source = source;
    }

    /// <summary>
    /// Gets a value indicating whether the client is currently connected to the server.
    /// </summary>
    public bool IsConnected => _source.SessionManager?.IsConnected ?? false;

    /// <summary>
    /// Gets a value indicating whether the client is currently attempting to reconnect.
    /// </summary>
    public bool IsReconnecting => _source.SessionManager?.IsReconnecting ?? false;

    /// <summary>
    /// Gets the current session identifier, or null if not connected.
    /// </summary>
    public string? SessionId => _source.SessionManager?.CurrentSession?.SessionId?.ToString();

    /// <summary>
    /// Gets the number of active OPC UA subscriptions.
    /// </summary>
    public int SubscriptionCount => _source.SessionManager?.Subscriptions.Count ?? 0;

    /// <summary>
    /// Gets the number of monitored items across all subscriptions.
    /// </summary>
    public int MonitoredItemCount => _source.SessionManager?.SubscriptionManager.MonitoredItems.Count ?? 0;

    /// <summary>
    /// Gets the number of items being polled (when subscriptions are not supported).
    /// </summary>
    public int PollingItemCount => _source.SessionManager?.PollingManager?.PollingItemCount ?? 0;
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: FAIL - `SessionManager` is not accessible (internal)

**Step 3: Expose SessionManager internally and add Diagnostics property**

Modify `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`:

Add at line ~26 (after `_disposed` field):

```csharp
private OpcUaClientDiagnostics? _diagnostics;
```

Add public property (after `RootSubject` property around line 79):

```csharp
/// <summary>
/// Gets diagnostic information about the client connection state.
/// </summary>
public OpcUaClientDiagnostics Diagnostics => _diagnostics ??= new OpcUaClientDiagnostics(this);

/// <summary>
/// Gets the session manager for internal diagnostics access.
/// </summary>
internal SessionManager? SessionManager => _sessionManager;
```

**Step 4: Verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaClientDiagnostics.cs
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs
git commit -m "feat(opcua): add OpcUaClientDiagnostics for monitoring client state

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

## Task 3: Add OpcUaServerDiagnostics Class

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerDiagnostics.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServerBackgroundService.cs`

**Step 1: Create the diagnostics class**

Create `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerDiagnostics.cs`:

```csharp
namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// Provides diagnostic information about the OPC UA server state.
/// Thread-safe for reading current values.
/// </summary>
public class OpcUaServerDiagnostics
{
    private readonly OpcUaSubjectServerBackgroundService _service;

    internal OpcUaServerDiagnostics(OpcUaSubjectServerBackgroundService service)
    {
        _service = service;
    }

    /// <summary>
    /// Gets a value indicating whether the server is currently running and accepting connections.
    /// </summary>
    public bool IsRunning => _service.IsRunning;

    /// <summary>
    /// Gets the number of currently active client sessions.
    /// </summary>
    public int ActiveSessionCount => _service.ActiveSessionCount;

    /// <summary>
    /// Gets the number of nodes published in the server's address space.
    /// </summary>
    public int NodeCount => _service.NodeCount;

    /// <summary>
    /// Gets the time when the server started, or null if not running.
    /// </summary>
    public DateTimeOffset? StartTime => _service.StartTime;

    /// <summary>
    /// Gets the server uptime, or null if not running.
    /// </summary>
    public TimeSpan? Uptime => _service.StartTime.HasValue
        ? DateTimeOffset.UtcNow - _service.StartTime.Value
        : null;

    /// <summary>
    /// Gets the most recent error that occurred, or null if no errors.
    /// </summary>
    public Exception? LastError => _service.LastError;

    /// <summary>
    /// Gets the number of consecutive startup failures.
    /// </summary>
    public int ConsecutiveFailures => _service.ConsecutiveFailures;
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: FAIL - properties not accessible on `OpcUaSubjectServerBackgroundService`

**Step 3: Add required properties to OpcUaSubjectServerBackgroundService**

Modify `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServerBackgroundService.cs`:

Add fields after existing fields (around line 20):

```csharp
private OpcUaServerDiagnostics? _diagnostics;
private DateTimeOffset? _startTime;
private Exception? _lastError;
private int _consecutiveFailures;
```

Add public properties (find appropriate location, likely after constructor):

```csharp
/// <summary>
/// Gets diagnostic information about the server state.
/// </summary>
public OpcUaServerDiagnostics Diagnostics => _diagnostics ??= new OpcUaServerDiagnostics(this);

/// <summary>
/// Gets a value indicating whether the server is running.
/// </summary>
internal bool IsRunning => _server?.CurrentInstance != null;

/// <summary>
/// Gets the number of active sessions.
/// </summary>
internal int ActiveSessionCount => _server?.CurrentInstance?.SessionManager?.GetSessions()?.Count ?? 0;

/// <summary>
/// Gets the number of nodes in the address space.
/// </summary>
internal int NodeCount => _nodeManager?.RegisteredNodes.Count ?? 0;

/// <summary>
/// Gets the server start time.
/// </summary>
internal DateTimeOffset? StartTime => _startTime;

/// <summary>
/// Gets the last error.
/// </summary>
internal Exception? LastError => _lastError;

/// <summary>
/// Gets the consecutive failure count.
/// </summary>
internal int ConsecutiveFailures => _consecutiveFailures;
```

Update the `ExecuteAsync` method to track these values:
- Set `_startTime = DateTimeOffset.UtcNow` when server starts successfully
- Set `_lastError = ex` in catch blocks
- Increment `_consecutiveFailures` on failure, reset to 0 on success
- Set `_startTime = null` when server stops

**Step 4: Verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.OpcUa`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/Server/OpcUaServerDiagnostics.cs
git add src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServerBackgroundService.cs
git commit -m "feat(opcua): add OpcUaServerDiagnostics for monitoring server state

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

## Task 4: Add Integration Test Trait to Existing Tests

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaServerClientReadWriteTests.cs`
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaTransactionTests.cs`
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaDynamicServerClientTests.cs`

**Step 1: Add trait to OpcUaServerClientReadWriteTests**

Add `[Trait("Category", "Integration")]` to the class in `OpcUaServerClientReadWriteTests.cs`:

```csharp
[Collection("OPC UA Integration")]
[Trait("Category", "Integration")]
public class OpcUaServerClientReadWriteTests
```

**Step 2: Add trait to OpcUaTransactionTests**

Add `[Trait("Category", "Integration")]` to the class in `OpcUaTransactionTests.cs`:

```csharp
[Collection("OPC UA Integration")]
[Trait("Category", "Integration")]
public class OpcUaTransactionTests
```

**Step 3: Add trait to OpcUaDynamicServerClientTests**

Add `[Trait("Category", "Integration")]` to the class in `OpcUaDynamicServerClientTests.cs`:

```csharp
[Collection("OPC UA Integration")]
[Trait("Category", "Integration")]
public class OpcUaDynamicServerClientTests
```

**Step 4: Verify tests still run**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category=Integration" --list-tests`
Expected: Lists the integration test classes

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Integration/*.cs
git commit -m "test(opcua): add Integration trait to existing E2E tests

Enables filtering with: dotnet test --filter \"Category!=Integration\"

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

## Task 5: Update OpcUaTestServer to Expose Diagnostics and Support Restart

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaTestServer.cs`

**Step 1: Add diagnostics access and restart capability**

Update `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaTestServer.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

public class OpcUaTestServer<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;
    private IInterceptorSubjectContext? _context;
    private Func<IInterceptorSubjectContext, TRoot>? _createRoot;
    private Action<IInterceptorSubjectContext, TRoot>? _initializeDefaults;

    public TRoot? Root { get; private set; }

    /// <summary>
    /// Gets the server diagnostics, or null if not started.
    /// </summary>
    public OpcUaServerDiagnostics? Diagnostics { get; private set; }

    public OpcUaTestServer(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Action<IInterceptorSubjectContext, TRoot>? initializeDefaults = null)
    {
        _createRoot = createRoot;
        _initializeDefaults = initializeDefaults;

        await StartInternalAsync();
    }

    private async Task StartInternalAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddConsole();
        });

        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(builder.Services);

        Root = _createRoot!(_context);

        _initializeDefaults?.Invoke(_context, Root);

        builder.Services.AddSingleton(Root);
        builder.Services.AddOpcUaSubjectServer<TRoot>("opc", rootName: "Root");

        _host = builder.Build();

        // Get diagnostics from the server service
        var serverService = _host.Services.GetRequiredService<OpcUaSubjectServerBackgroundService>();
        Diagnostics = serverService.Diagnostics;

        await _host.StartAsync();
        _output.WriteLine("Server started");
    }

    public async Task StopAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
            _output.WriteLine("Server stopped");
        }
    }

    /// <summary>
    /// Restarts the server (stop and start again with same configuration).
    /// </summary>
    public async Task RestartAsync()
    {
        _output.WriteLine("Restarting server...");
        await StopAsync();
        await StartInternalAsync();
        _output.WriteLine("Server restarted");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_host != null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
                _output.WriteLine("Server host disposed");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error disposing server: {ex.Message}");
        }
    }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.OpcUa.Tests`
Expected: PASS

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaTestServer.cs
git commit -m "test(opcua): add diagnostics and restart support to OpcUaTestServer

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

## Task 6: Update OpcUaTestClient to Expose Diagnostics and Support Configuration

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaTestClient.cs`

**Step 1: Add diagnostics access and configurable timeouts**

Update `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaTestClient.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

public class OpcUaTestClientConfiguration
{
    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReconnectHandlerTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan SubscriptionHealthCheckInterval { get; init; } = TimeSpan.FromSeconds(10);
}

public class OpcUaTestClient<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private const string DefaultServerUrl = "opc.tcp://localhost:4840";

    private readonly ITestOutputHelper _output;
    private IHost? _host;
    private IInterceptorSubjectContext? _context;

    public TRoot? Root { get; private set; }

    public IInterceptorSubjectContext Context => _context ?? throw new InvalidOperationException("Client not started.");

    /// <summary>
    /// Gets the client diagnostics, or null if not started.
    /// </summary>
    public OpcUaClientDiagnostics? Diagnostics { get; private set; }

    public OpcUaTestClient(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Func<TRoot, bool> isConnected,
        string serverUrl = DefaultServerUrl)
    {
        return StartAsync(createRoot, isConnected, new OpcUaTestClientConfiguration(), serverUrl);
    }

    public async Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Func<TRoot, bool> isConnected,
        OpcUaTestClientConfiguration configuration,
        string serverUrl = DefaultServerUrl)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddConsole();
        });

        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithSourceTransactions()
            .WithHostedServices(builder.Services);

        Root = createRoot(_context);

        builder.Services.AddSingleton(Root);
        builder.Services.AddOpcUaSubjectClientSource<TRoot>(
            serverUrl,
            "opc",
            rootName: "Root",
            configure: config =>
            {
                config.ReconnectInterval = configuration.ReconnectInterval;
                config.ReconnectHandlerTimeout = configuration.ReconnectHandlerTimeout;
                config.SessionTimeout = configuration.SessionTimeout;
                config.SubscriptionHealthCheckInterval = configuration.SubscriptionHealthCheckInterval;
            });

        _host = builder.Build();

        // Get diagnostics from the client source
        var clientSource = _host.Services.GetRequiredService<OpcUaSubjectClientSource>();
        Diagnostics = clientSource.Diagnostics;

        await _host.StartAsync();
        _output.WriteLine("Client started");

        // Wait for client to connect using active waiting
        await AsyncTestHelpers.WaitUntilAsync(
            () => Root != null && isConnected(Root),
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(200),
            message: "Client failed to connect to server");

        _output.WriteLine("Client connected");
    }

    public async Task StopAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
            _output.WriteLine("Client stopped");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_host != null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
                _output.WriteLine("Client host disposed");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Error disposing client: {ex.Message}");
        }
    }
}
```

**Step 2: Add project reference to Testing library**

Add to `src/Namotion.Interceptor.OpcUa.Tests/Namotion.Interceptor.OpcUa.Tests.csproj`:

```xml
<ProjectReference Include="..\Namotion.Interceptor.Testing\Namotion.Interceptor.Testing.csproj" />
```

**Step 3: Verify AddOpcUaSubjectClientSource supports configure parameter**

Check `src/Namotion.Interceptor.OpcUa/OpcUaHostingExtensions.cs` for the extension method signature. If `configure` parameter doesn't exist, we need to add it or use a different approach.

**Step 4: Verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.OpcUa.Tests`
Expected: PASS (or adjust based on actual API)

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaTestClient.cs
git add src/Namotion.Interceptor.OpcUa.Tests/Namotion.Interceptor.OpcUa.Tests.csproj
git commit -m "test(opcua): add diagnostics and configurable timeouts to OpcUaTestClient

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

## Task 7: Create OpcUaResilienceTests - Test Infrastructure

**Files:**
- Create: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaResilienceTests.cs`

**Step 1: Create test class with setup**

Create `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaResilienceTests.cs`:

```csharp
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

[Collection("OPC UA Integration")]
[Trait("Category", "Integration")]
public class OpcUaResilienceTests
{
    private readonly ITestOutputHelper _output;

    // Fast configuration for resilience tests
    private readonly OpcUaTestClientConfiguration _fastClientConfig = new()
    {
        ReconnectInterval = TimeSpan.FromMilliseconds(500),
        ReconnectHandlerTimeout = TimeSpan.FromSeconds(5),
        SessionTimeout = TimeSpan.FromSeconds(10),
        SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(5)
    };

    public OpcUaResilienceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task<(OpcUaTestServer<TestRoot> Server, OpcUaTestClient<TestRoot> Client)> StartServerAndClientAsync()
    {
        var server = new OpcUaTestServer<TestRoot>(_output);
        await server.StartAsync(
            context => new TestRoot(context),
            (context, root) =>
            {
                root.Connected = true;
                root.Name = "Initial";
                root.Number = 42m;
            });

        var client = new OpcUaTestClient<TestRoot>(_output);
        await client.StartAsync(
            context => new TestRoot(context),
            isConnected: root => root.Connected,
            configuration: _fastClientConfig);

        return (server, client);
    }
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/Namotion.Interceptor.OpcUa.Tests`
Expected: PASS

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaResilienceTests.cs
git commit -m "test(opcua): add OpcUaResilienceTests class with fast config

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

## Task 8: Add Full Reconnect Test (Server Restart)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaResilienceTests.cs`

**Step 1: Write the test**

Add to `OpcUaResilienceTests.cs`:

```csharp
[Fact]
public async Task ServerRestart_ClientFullyReconnects_SubscriptionsRecreated()
{
    OpcUaTestServer<TestRoot>? server = null;
    OpcUaTestClient<TestRoot>? client = null;

    try
    {
        // Arrange - Start server and client, verify initial sync
        (server, client) = await StartServerAndClientAsync();

        Assert.NotNull(server.Root);
        Assert.NotNull(client.Root);
        Assert.NotNull(client.Diagnostics);

        // Verify initial connection
        Assert.True(client.Diagnostics.IsConnected, "Client should be connected initially");
        Assert.Equal("Initial", client.Root.Name);
        _output.WriteLine("Initial sync verified");

        // Act - Stop server completely
        _output.WriteLine("Stopping server...");
        await server.StopAsync();

        // Wait for client to detect disconnection
        await AsyncTestHelpers.WaitUntilAsync(
            () => !client.Diagnostics.IsConnected,
            timeout: TimeSpan.FromSeconds(15),
            message: "Client should detect server disconnection");
        _output.WriteLine("Client detected disconnection");

        // Wait a bit longer to ensure session is truly dead
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Restart server
        _output.WriteLine("Restarting server...");
        await server.RestartAsync();

        // Wait for client to reconnect
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Diagnostics.IsConnected && !client.Diagnostics.IsReconnecting,
            timeout: TimeSpan.FromSeconds(20),
            message: "Client should reconnect after server restart");
        _output.WriteLine("Client reconnected");

        // Assert - Verify subscriptions are working by changing a value
        server.Root.Name = "AfterRestart";

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root.Name == "AfterRestart",
            timeout: TimeSpan.FromSeconds(10),
            message: "Property change should propagate after reconnection");

        _output.WriteLine($"Value propagated: {client.Root.Name}");
        Assert.Equal("AfterRestart", client.Root.Name);
    }
    finally
    {
        if (client != null) await client.DisposeAsync();
        if (server != null) await server.DisposeAsync();
    }
}
```

**Step 2: Run the test**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~ServerRestart_ClientFullyReconnects" -v n`
Expected: Test runs (may pass or fail depending on actual behavior)

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaResilienceTests.cs
git commit -m "test(opcua): add full reconnect test for server restart scenario

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

## Task 9: Add Session Transfer Test (Brief Disconnect)

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaResilienceTests.cs`

**Step 1: Write the test**

Add to `OpcUaResilienceTests.cs`:

```csharp
[Fact]
public async Task ServerBrieflyUnavailable_SessionTransfer_SubscriptionsContinue()
{
    OpcUaTestServer<TestRoot>? server = null;
    OpcUaTestClient<TestRoot>? client = null;

    try
    {
        // Arrange - Start server and client
        (server, client) = await StartServerAndClientAsync();

        Assert.NotNull(server.Root);
        Assert.NotNull(client.Root);
        Assert.NotNull(client.Diagnostics);

        // Verify initial connection and sync
        Assert.True(client.Diagnostics.IsConnected);
        server.Root.Name = "BeforeDisconnect";
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root.Name == "BeforeDisconnect",
            timeout: TimeSpan.FromSeconds(5));
        _output.WriteLine("Initial sync verified");

        var initialSessionId = client.Diagnostics.SessionId;
        _output.WriteLine($"Initial session ID: {initialSessionId}");

        // Act - Brief server restart (quick enough that session might be transferred)
        _output.WriteLine("Brief server restart...");
        await server.StopAsync();
        await Task.Delay(500); // Very brief outage
        await server.RestartAsync();

        // Wait for client to recover
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Diagnostics.IsConnected && !client.Diagnostics.IsReconnecting,
            timeout: TimeSpan.FromSeconds(20),
            message: "Client should recover after brief outage");

        var newSessionId = client.Diagnostics.SessionId;
        _output.WriteLine($"Session ID after recovery: {newSessionId}");

        // Assert - Verify data still flows
        server.Root.Name = "AfterBriefOutage";

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root.Name == "AfterBriefOutage",
            timeout: TimeSpan.FromSeconds(10),
            message: "Property change should propagate after recovery");

        _output.WriteLine($"Value propagated: {client.Root.Name}");
        Assert.Equal("AfterBriefOutage", client.Root.Name);
    }
    finally
    {
        if (client != null) await client.DisposeAsync();
        if (server != null) await server.DisposeAsync();
    }
}
```

**Step 2: Run the test**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~SessionTransfer" -v n`
Expected: Test runs

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaResilienceTests.cs
git commit -m "test(opcua): add session transfer test for brief disconnect scenario

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

## Task 10: Add Multiple Reconnects Stability Test

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaResilienceTests.cs`

**Step 1: Write the test**

Add to `OpcUaResilienceTests.cs`:

```csharp
[Fact]
public async Task MultipleServerRestarts_ClientRecoveryEveryTime_NoStateCorruption()
{
    OpcUaTestServer<TestRoot>? server = null;
    OpcUaTestClient<TestRoot>? client = null;

    try
    {
        // Arrange
        (server, client) = await StartServerAndClientAsync();

        Assert.NotNull(server.Root);
        Assert.NotNull(client.Root);
        Assert.NotNull(client.Diagnostics);

        // Act & Assert - Multiple restart cycles
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            _output.WriteLine($"=== Restart cycle {cycle} ===");

            // Verify connection
            Assert.True(client.Diagnostics.IsConnected, $"Cycle {cycle}: Should be connected");

            // Update value and verify sync
            var testValue = $"Cycle{cycle}";
            server.Root.Name = testValue;

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Root.Name == testValue,
                timeout: TimeSpan.FromSeconds(10),
                message: $"Cycle {cycle}: Value should propagate");

            Assert.Equal(testValue, client.Root.Name);
            _output.WriteLine($"Cycle {cycle}: Value propagated correctly");

            // Restart server
            await server.StopAsync();

            await AsyncTestHelpers.WaitUntilAsync(
                () => !client.Diagnostics.IsConnected,
                timeout: TimeSpan.FromSeconds(10),
                message: $"Cycle {cycle}: Client should detect disconnection");

            await server.RestartAsync();

            await AsyncTestHelpers.WaitUntilAsync(
                () => client.Diagnostics.IsConnected && !client.Diagnostics.IsReconnecting,
                timeout: TimeSpan.FromSeconds(20),
                message: $"Cycle {cycle}: Client should reconnect");

            _output.WriteLine($"Cycle {cycle}: Reconnected successfully");
        }

        // Final verification
        server.Root.Name = "FinalValue";
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root.Name == "FinalValue",
            timeout: TimeSpan.FromSeconds(10),
            message: "Final value should propagate");

        Assert.Equal("FinalValue", client.Root.Name);
        _output.WriteLine("All cycles completed successfully");
    }
    finally
    {
        if (client != null) await client.DisposeAsync();
        if (server != null) await server.DisposeAsync();
    }
}
```

**Step 2: Run the test**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~MultipleServerRestarts" -v n`
Expected: Test runs

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaResilienceTests.cs
git commit -m "test(opcua): add multiple restarts stability test

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

## Task 11: Run All Tests and Verify

**Step 1: Build everything**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: PASS

**Step 2: Run all OPC UA tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests -v n`
Expected: Tests run (note any failures for debugging)

**Step 3: Run only integration tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category=Integration" -v n`
Expected: Only integration tests run

**Step 4: Run excluding integration tests**

Run: `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category!=Integration" -v n`
Expected: Only unit tests run

**Step 5: Final commit if all passes**

```bash
git add -A
git commit -m "test(opcua): complete resilience testing implementation

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
```

---

## Summary

| Task | Component | Purpose |
|------|-----------|---------|
| 1 | AsyncTestHelpers | Shared WaitUntilAsync helper |
| 2 | OpcUaClientDiagnostics | Client state monitoring |
| 3 | OpcUaServerDiagnostics | Server state monitoring |
| 4 | Existing tests | Add Integration trait |
| 5 | OpcUaTestServer | Diagnostics + restart support |
| 6 | OpcUaTestClient | Diagnostics + configurable timeouts |
| 7 | OpcUaResilienceTests | Test infrastructure |
| 8 | Full reconnect test | Server restart scenario |
| 9 | Session transfer test | Brief disconnect scenario |
| 10 | Multiple restarts test | Stability verification |
| 11 | Final verification | Build and run all tests |

**Exclusion filter for local dev:** `dotnet test --filter "Category!=Integration"`

---

## Technical Findings (Implementation Notes)

### OPC UA SDK Keep-Alive Mechanism

The OPC UA SDK's keep-alive mechanism is critical for detecting disconnections. Key learnings:

1. **KeepAliveInterval Configuration**: Must be explicitly set on the session after creation:
   ```csharp
   newSession.KeepAliveInterval = (int)configuration.KeepAliveInterval.TotalMilliseconds;
   ```

2. **OperationTimeout Impact**: The `TransportQuotas.OperationTimeout` (default 60 seconds) determines how long the SDK waits for responses. For tests, this should be reduced to 3-5 seconds for faster failure detection.

3. **Keep-Alive Event Behavior**:
   - During normal operation: `Status = null`, `ServerState = Running`, `Connected = true`
   - When disconnection detected: `Status = [80850000]` or `[80310000] 'Server not responding'`, `ServerState = Unknown`

4. **IsConnected Check**: Must check multiple conditions:
   ```csharp
   public bool IsConnected =>
       Volatile.Read(ref _isReconnecting) == 0 &&
       (Volatile.Read(ref _session)?.Connected ?? false);
   ```

### Reconnection Behavior

The SDK's `SessionReconnectHandler` has specific behaviors to understand:

1. **Exponential Backoff**: Reconnect intervals increase: 1000ms → 2000ms → 4000ms → 5000ms (max)

2. **No Total Timeout**: The handler retries indefinitely. The `ReconnectHandlerTimeout` only applies to individual reconnect attempts, not total time.

3. **OnReconnectComplete**: This callback may never be called if reconnection keeps failing. The health check's stall detection is the fallback mechanism.

4. **Stall Detection**: Implemented in `ExecuteAsync` health check loop:
   - Tracks iterations while `IsReconnecting` is true
   - After 10 iterations × SubscriptionHealthCheckInterval, calls `TryForceResetIfStalled()`
   - For tests, use shorter intervals: 10 × 2 seconds = 20 seconds total
   - **CRITICAL**: Stall detection immediately triggers `ReconnectSessionAsync()`, don't wait for next iteration

5. **Session.Connected Caveat**: The `Session.Connected` property may return `true` even when the server is down:
   - The SDK maintains internal state that may not reflect actual connectivity
   - During active reconnection attempts, `Connected` often stays `true`
   - Use `SessionReconnectHandler.State` as an additional signal:
     - `Ready` = handler completed (success or gave up)
     - `Triggered` or `Reconnecting` = handler still trying
   - Stall detection should check: `session is null || !session.Connected || handlerNotReady`

### MonitoredItem ServerId Reset

Critical fix for subscriptions after server restart:

```csharp
// Reset ServerId on all monitored items to force SDK to re-create them on the new server.
// The SDK skips items where Status.Created (which checks Id != 0) is true.
// After server restart, the old server-assigned IDs are no longer valid.
foreach (var item in _initialMonitoredItems)
{
    item.ServerId = 0;
}
```

### Test Configuration

Recommended settings for fast resilience testing:

```csharp
private readonly OpcUaTestClientConfiguration _fastClientConfig = new()
{
    ReconnectInterval = TimeSpan.FromMilliseconds(500),
    ReconnectHandlerTimeout = TimeSpan.FromSeconds(2),
    SessionTimeout = TimeSpan.FromSeconds(10),
    SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(2), // Fast health checks
    KeepAliveInterval = TimeSpan.FromSeconds(1), // Fast keep-alive detection
    OperationTimeout = TimeSpan.FromSeconds(3), // Short timeout for fast failure
    StallDetectionIterations = 3 // Fast stall detection: 3 × 2s = 6s
};
```

With these settings:
- Disconnection detected in ~3-5 seconds (OperationTimeout + KeepAliveInterval)
- Stall detection triggers in ~6 seconds (3 × 2s SubscriptionHealthCheckInterval)
- Total reconnection timeout: ~15-20 seconds for tests

### StallDetectionIterations Configuration

New configuration property to control how quickly stall detection triggers:

```csharp
/// <summary>
/// Gets the number of health check iterations while reconnecting before triggering stall detection.
/// When the SDK's automatic reconnection appears stalled, manual reconnection is triggered after
/// this many iterations × SubscriptionHealthCheckInterval.
/// Default is 10 iterations. Lower values mean faster stall detection but less tolerance for slow reconnections.
/// </summary>
public int StallDetectionIterations { get; init; } = 10;
```

- **Production default**: 10 iterations (with default 10s interval = 100s tolerance)
- **Test recommended**: 3 iterations (with 2s interval = 6s for fast tests)

### TryForceResetIfStalled Implementation

The stall detection logic in `SessionManager.TryForceResetIfStalled()`:

```csharp
internal bool TryForceResetIfStalled()
{
    lock (_reconnectingLock)
    {
        var session = Volatile.Read(ref _session);
        var isReconnecting = Volatile.Read(ref _isReconnecting) == 1;
        var sessionConnected = session?.Connected ?? false;
        var reconnectHandlerState = _reconnectHandler.State;

        // If reconnect handler is actively reconnecting, the session may show Connected=true
        // but the handler hasn't completed yet. Check handler state as additional signal.
        var handlerStalled = reconnectHandlerState is not SessionReconnectHandler.ReconnectState.Ready;

        // Double-check: still reconnecting AND either:
        // - session is null or disconnected, OR
        // - reconnect handler is stalled (not Ready state)
        if (isReconnecting && (session is null || !sessionConnected || handlerStalled))
        {
            Interlocked.Exchange(ref _isReconnecting, 0);
            return true;
        }

        return false;
    }
}
```

Key insight: The `handlerStalled` check catches the case where `Session.Connected` returns true but the reconnect handler is still in `Triggered` or `Reconnecting` state.

### Health Check Stall Detection Flow

The complete flow in `OpcUaSubjectClientSource.ExecuteAsync()`:

1. Track iterations while `isReconnecting` is true
2. When iterations > 10, call `TryForceResetIfStalled()`
3. If stall confirmed, **immediately** trigger `ReconnectSessionAsync()` (don't wait for next iteration)
4. `ReconnectSessionAsync()` resets `ServerId` on all monitored items and recreates subscriptions
