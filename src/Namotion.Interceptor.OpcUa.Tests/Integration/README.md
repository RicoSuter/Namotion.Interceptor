# OPC UA Integration Tests

This folder contains integration tests for the OPC UA client and server functionality. Tests verify real OPC UA communication, reconnection behavior, and data synchronization.

## Test Architecture

There are two types of integration tests:

### 1. Shared Server Tests (Most Common)

These tests share a single OPC UA server instance across all tests and reuse clients per test class. Use this pattern for most functional tests.

**Base class:** `SharedServerTestBase`

**Fixtures:**
- `SharedOpcUaServerFixture` (assembly-level) - One server for all tests
- `SharedOpcUaClientFixture` (class-level) - One client per test class

**Example:**
```csharp
[Trait("Category", "Integration")]
public class MyNewTests : SharedServerTestBase
{
    public MyNewTests(
        SharedOpcUaServerFixture serverFixture,
        SharedOpcUaClientFixture clientFixture,
        ITestOutputHelper output)
        : base(serverFixture, clientFixture, output) { }

    [Fact]
    public async Task MyTest()
    {
        var serverArea = ServerFixture.ServerRoot.MyArea;
        var clientArea = Client!.Root!.MyArea;

        // Test synchronization...
    }
}
```

**Existing shared server test classes:**
- `OpcUaDataTypesTests` - Data type synchronization
- `OpcUaReadWriteTests` - Basic read/write operations
- `OpcUaNestedStructureTests` - Nested object synchronization
- `OpcUaTransactionTests` - Transaction commit/rollback
- `OpcUaCollectionEdgeCaseTests` - Array edge cases
- `OpcUaMultiClientTests` - Multiple client scenarios
- `OpcUaDynamicServerClientTests` - Dynamic property discovery

### 2. Dedicated Server Tests (Lifecycle Tests)

These tests create their own server and client instances. Use this pattern when testing lifecycle scenarios like reconnection, stall detection, or server restarts.

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

**Existing dedicated server test classes:**
- `OpcUaReconnectionTests` - Server restart recovery
- `OpcUaStallDetectionTests` - Stall detection and recovery
- `OpcUaConcurrencyTests` - Concurrent operations during reconnection

## Shared Test Model

The `SharedTestModel` class defines the data structure exposed by the shared server. Each test class has its own isolated area to prevent test interference.

### Model Structure

```
SharedTestModel (Testing/SharedTestModel.cs)
├── Connected (bool)
├── DataTypes (DataTypesTestArea)
├── ReadWrite (ReadWriteTestArea)
│   ├── BasicSync (BasicSyncArea)
│   └── ArraySync (ArraySyncArea)
├── Transactions (TransactionTestArea)
├── Nested (NestedTestArea)
├── Collections (CollectionsTestArea)
└── MultiClient (MultiClientTestArea)
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

2. **Add to SharedTestModel**:

```csharp
public partial class SharedTestModel
{
    public SharedTestModel()
    {
        // ... existing areas ...
        MyNewArea = new MyNewTestArea();
    }

    [Path("opc", "MyNewArea")]
    public partial MyNewTestArea MyNewArea { get; set; }
}
```

3. **Use in your test**:

```csharp
[Fact]
public async Task MyNewArea_ShouldSync()
{
    var serverArea = ServerFixture.ServerRoot.MyNewArea;
    var clientArea = Client!.Root!.MyNewArea;

    serverArea.Name = "Test";
    await AsyncTestHelpers.WaitUntilAsync(
        () => clientArea.Name == "Test",
        timeout: TimeSpan.FromSeconds(30));
}
```

## Test Isolation

- **Shared server tests**: Each test class should use its own model area. Tests within a class share the same client, so ensure tests don't depend on specific initial state.
- **Dedicated server tests**: Fully isolated - each test has its own server and client.

## Key Classes

| Class | Purpose |
|-------|---------|
| `SharedOpcUaServerFixture` | Assembly fixture managing shared server lifetime |
| `SharedOpcUaClientFixture` | Class fixture managing per-class client reuse |
| `SharedServerTestBase` | Base class for shared server tests |
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
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OpcUaDataTypesTests"

# Run with verbose output
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v n
```
