# OPC UA Integration Tests

This folder contains integration tests for the OPC UA client and server functionality. Tests verify real OPC UA communication, reconnection behavior, and data synchronization.

## Test Architecture

There are two types of integration tests:

### 1. Shared Server Tests (Most Common)

These tests share a single OPC UA server instance across all tests, with a fresh client per test method. Use this pattern for most functional tests.

**Base class:** `SharedServerTestBase`

**Fixtures:**
- `SharedOpcUaServerFixture` (assembly-level) - One server for all tests

**Configuration:**
- `EnableLiveSync = true` (both server and client)
- `EnableModelChangeEvents = true` (server emits structural change events)
- `EnableExternalNodeManagement = true` (server accepts AddNodes/DeleteNodes from clients)
- `EnableRemoteNodeManagement = true` (client can create/delete nodes on server)

**Example:**
```csharp
[Trait("Category", "Integration")]
public class MyNewTests : SharedServerTestBase
{
    public MyNewTests(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
        : base(serverFixture, output) { }

    [Fact]
    public async Task MyTest()
    {
        var serverArea = ServerFixture.ServerRoot.MyNewArea;
        var clientArea = Client!.Root!.MyNewArea;

        // Test synchronization...
    }
}
```

### 2. Dedicated Server Tests (Special Config Tests)

These tests create their own server and client instances. Use this pattern when testing:
- **Lifecycle scenarios** (reconnection, stall detection, server restarts)
- **Special configurations** (PeriodicResync mode, different timing configs)

**No base class** - Implements `IAsyncLifetime` directly

**Example:**
```csharp
[Trait("Category", "Integration")]
public class MyLifecycleTests
{
    private readonly ITestOutputHelper _output;

    public MyLifecycleTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task MyReconnectionTest()
    {
        var logger = new TestLogger(_output);
        var port = await OpcUaTestPortPool.AcquireAsync();

        try
        {
            var server = new OpcUaTestServer<TestRoot>(logger);
            await server.StartAsync(...);

            var client = new OpcUaTestClient<TestRoot>(logger);
            await client.StartAsync(...);

            // Test lifecycle scenario...
        }
        finally
        {
            // Clean up...
            port?.Dispose();
        }
    }
}
```

## Test Organization

### Test Naming Convention

**IMPORTANT:** Each test class must have a corresponding test area in `SharedTestModel` with a 1:1 name mapping:

| Test Class | Test Area |
|------------|-----------|
| `ServerToClientReferenceTests` | `ServerToClientReferenceTestArea` |
| `ServerToClientCollectionTests` | `ServerToClientCollectionTestArea` |
| `ValueSyncBasicTests` | `ValueSyncBasicTestArea` |

This 1:1 naming prevents test interference by ensuring each test class operates on its own isolated data.

## Shared Test Model

The `SharedTestModel` class defines the data structure exposed by the shared server. Each test class has its own isolated area to prevent test interference.

### Model Structure

```
SharedTestModel (Testing/SharedTestModel.cs)
├── Connected (bool)
├── ServerToClientReferenceTestArea
├── ServerToClientCollectionTestArea
│   ├── ContainerCollection (with container node)
│   └── FlatCollection (no container node)
├── ServerToClientDictionaryTestArea
├── ServerToClientSharedSubjectTestArea
├── ClientToServerReferenceTestArea
├── ClientToServerCollectionTestArea
│   ├── ContainerCollection
│   └── FlatCollection
├── ClientToServerDictionaryTestArea
├── ClientToServerSharedSubjectTestArea
├── ValueSyncBasicTestArea
├── ValueSyncDataTypesTestArea
├── ValueSyncNestedTestArea
└── ... (other areas)
```

### Adding a New Test Area

1. **Define your model class** in `Testing/SharedTestModel.cs`:

```csharp
[InterceptorSubject]
public partial class MyNewTestArea
{
    public MyNewTestArea()
    {
        // Initialize properties with defaults
        Name = "";
        Values = [];
    }

    [Path("opc", "Name")]
    public partial string Name { get; set; }

    [Path("opc", "Values")]
    public partial int[] Values { get; set; }
}
```

2. **Add to SharedTestModel** (use the SAME name as your test class, minus "Tests"):

```csharp
public partial class SharedTestModel
{
    public SharedTestModel()
    {
        // ... existing areas ...
        MyNewTestArea = new MyNewTestArea();
    }

    [Path("opc", "MyNewTestArea")]
    public partial MyNewTestArea MyNewTestArea { get; set; }
}
```

3. **Create your test class** with matching name:

```csharp
[Trait("Category", "Integration")]
public class MyNewTests : SharedServerTestBase
{
    // ...

    [Fact]
    public async Task MyNewArea_ShouldSync()
    {
        var serverArea = ServerFixture.ServerRoot.MyNewTestArea;
        var clientArea = Client!.Root!.MyNewTestArea;

        serverArea.Name = "Test";
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.Name == "Test",
            timeout: TimeSpan.FromSeconds(30));
    }
}
```

## Test Isolation

- **Shared server tests**: Each test class MUST use its own test area (1:1 naming). Each test method gets a fresh client for isolation.
- **Dedicated server tests**: Fully isolated - each test has its own server and client.

## Key Classes

| Class | Purpose |
|-------|---------|
| `SharedOpcUaServerFixture` | Assembly fixture managing shared server lifetime |
| `SharedServerTestBase` | Base class for shared server tests (fresh client per test) |
| `OpcUaTestServer<T>` | Test server wrapper with start/stop/restart |
| `OpcUaTestClient<T>` | Test client wrapper with diagnostics |
| `OpcUaTestPortPool` | Port allocation for dedicated server tests |
| `TestLogger` | xUnit output helper with timestamps |
| `AsyncTestHelpers` | Async wait utilities |

## Running Tests

```bash
# Run all OPC UA tests
dotnet test src/Namotion.Interceptor.OpcUa.Tests

# Run specific test class
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~ServerToClientCollectionTests"

# Run with verbose output
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v n
```
