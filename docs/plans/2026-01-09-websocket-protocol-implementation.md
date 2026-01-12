# WebSocket Subject Protocol Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement a bidirectional WebSocket protocol for synchronizing SubjectUpdate data between .NET servers and clients, using JSON serialization (extensible for MessagePack in future).

**Architecture:** Follow existing MQTT patterns - client implements `ISubjectSource`, server is a `BackgroundService`. Protocol uses message envelope `[MessageType, CorrelationId, Payload]`. V1 uses JSON only with existing SubjectUpdate serialization.

**Tech Stack:** .NET 9, System.Net.WebSockets, System.Text.Json, xUnit

**Design Document:** `docs/plans/2026-01-09-websocket-protocol-design.md`

---

## Task 1: Create Project Structure

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket/Namotion.Interceptor.WebSocket.csproj`
- Create: `src/Namotion.Interceptor.WebSocket.Tests/Namotion.Interceptor.WebSocket.Tests.csproj`
- Modify: `src/Namotion.Interceptor.slnx`

**Step 1: Create WebSocket library project**

Create `src/Namotion.Interceptor.WebSocket/Namotion.Interceptor.WebSocket.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		<TargetFramework>net9.0</TargetFramework>
		<Nullable>enable</Nullable>
		<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
	</PropertyGroup>

	<ItemGroup>
		<ProjectReference Include="..\Namotion.Interceptor.Connectors\Namotion.Interceptor.Connectors.csproj" />
	</ItemGroup>
</Project>
```

**Step 2: Create test project**

Create `src/Namotion.Interceptor.WebSocket.Tests/Namotion.Interceptor.WebSocket.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		<TargetFramework>net9.0</TargetFramework>
		<Nullable>enable</Nullable>
		<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
		<IsPackable>false</IsPackable>
	</PropertyGroup>

	<ItemGroup>
		<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
		<PackageReference Include="xunit" Version="2.9.2" />
		<PackageReference Include="xunit.runner.visualstudio" Version="3.0.1">
			<PrivateAssets>all</PrivateAssets>
			<IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
		</PackageReference>
	</ItemGroup>

	<ItemGroup>
		<ProjectReference Include="..\Namotion.Interceptor.WebSocket\Namotion.Interceptor.WebSocket.csproj" />
		<ProjectReference Include="..\Namotion.Interceptor.Testing\Namotion.Interceptor.Testing.csproj" />
	</ItemGroup>
</Project>
```

**Step 3: Add projects to solution**

Add to `src/Namotion.Interceptor.slnx` in `/Extensions/` folder section (after line 26):

```xml
    <Project Path="Namotion.Interceptor.WebSocket/Namotion.Interceptor.WebSocket.csproj" />
```

Add to `/Tests/` folder section (after line 68):

```xml
    <Project Path="Namotion.Interceptor.WebSocket.Tests/Namotion.Interceptor.WebSocket.Tests.csproj" />
```

**Step 4: Verify build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded with 0 errors

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket src/Namotion.Interceptor.WebSocket.Tests src/Namotion.Interceptor.slnx
git commit -m "feat(websocket): add project structure for WebSocket protocol"
```

---

## Task 2: Implement Protocol Message Types

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket/Protocol/MessageType.cs`
- Create: `src/Namotion.Interceptor.WebSocket/Protocol/WsFormat.cs`
- Create: `src/Namotion.Interceptor.WebSocket/Protocol/HelloPayload.cs`
- Create: `src/Namotion.Interceptor.WebSocket/Protocol/WelcomePayload.cs`
- Create: `src/Namotion.Interceptor.WebSocket/Protocol/ErrorPayload.cs`
- Create: `src/Namotion.Interceptor.WebSocket/Protocol/PropertyFailure.cs`
- Test: `src/Namotion.Interceptor.WebSocket.Tests/Protocol/PayloadTests.cs`

**Step 1: Write failing test for protocol payloads**

Create `src/Namotion.Interceptor.WebSocket.Tests/Protocol/PayloadTests.cs`:

```csharp
using Namotion.Interceptor.WebSocket.Protocol;

namespace Namotion.Interceptor.WebSocket.Tests.Protocol;

public class PayloadTests
{
    [Fact]
    public void HelloPayload_ShouldHaveDefaultValues()
    {
        var payload = new HelloPayload();

        Assert.Equal(1, payload.Version);
        Assert.Equal(WsFormat.Json, payload.Format);
    }

    [Fact]
    public void WelcomePayload_ShouldHaveDefaultValues()
    {
        var payload = new WelcomePayload();

        Assert.Equal(1, payload.Version);
        Assert.Equal(WsFormat.Json, payload.Format);
        Assert.Null(payload.State);
    }

    [Fact]
    public void ErrorPayload_ShouldSupportMultipleFailures()
    {
        var payload = new ErrorPayload
        {
            Code = 100,
            Message = "Multiple failures",
            Failures =
            [
                new PropertyFailure { Path = "Motor/Speed", Code = 101, Message = "Read-only" },
                new PropertyFailure { Path = "Sensor/Unknown", Code = 100, Message = "Not found" }
            ]
        };

        Assert.Equal(100, payload.Code);
        Assert.Equal(2, payload.Failures!.Count);
        Assert.Equal("Motor/Speed", payload.Failures[0].Path);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "PayloadTests" -v n`
Expected: FAIL with compilation errors (types not found)

**Step 3: Implement MessageType enum**

Create `src/Namotion.Interceptor.WebSocket/Protocol/MessageType.cs`:

```csharp
namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// WebSocket protocol message types.
/// </summary>
public enum MessageType
{
    /// <summary>
    /// Client sends to server on connection.
    /// </summary>
    Hello = 0,

    /// <summary>
    /// Server responds to client with initial state.
    /// </summary>
    Welcome = 1,

    /// <summary>
    /// Bidirectional subject updates.
    /// </summary>
    Update = 2,

    /// <summary>
    /// Error response.
    /// </summary>
    Error = 3
}
```

**Step 4: Implement WsFormat enum**

Create `src/Namotion.Interceptor.WebSocket/Protocol/WsFormat.cs`:

```csharp
namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Serialization format for WebSocket messages.
/// Reserved for future format negotiation (e.g., MessagePack).
/// </summary>
public enum WsFormat
{
    /// <summary>
    /// JSON serialization (human-readable, native browser support).
    /// </summary>
    Json = 0,

    /// <summary>
    /// MessagePack serialization (binary, compact, fast). Reserved for future use.
    /// </summary>
    MessagePack = 1
}
```

**Step 5: Implement HelloPayload**

Create `src/Namotion.Interceptor.WebSocket/Protocol/HelloPayload.cs`:

```csharp
namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Payload for Hello message sent by client on connection.
/// </summary>
public class HelloPayload
{
    /// <summary>
    /// Protocol version. Default is 1.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Preferred serialization format. Reserved for future format negotiation.
    /// </summary>
    public WsFormat Format { get; set; } = WsFormat.Json;
}
```

**Step 6: Implement WelcomePayload**

Create `src/Namotion.Interceptor.WebSocket/Protocol/WelcomePayload.cs`:

```csharp
using Namotion.Interceptor.Connectors.Updates;

namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Payload for Welcome message sent by server after Hello.
/// </summary>
public class WelcomePayload
{
    /// <summary>
    /// Protocol version.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Negotiated serialization format.
    /// </summary>
    public WsFormat Format { get; set; } = WsFormat.Json;

    /// <summary>
    /// Complete initial state.
    /// </summary>
    public SubjectUpdate? State { get; set; }
}
```

**Step 7: Implement PropertyFailure**

Create `src/Namotion.Interceptor.WebSocket/Protocol/PropertyFailure.cs`:

```csharp
namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Represents a single property update failure.
/// </summary>
public class PropertyFailure
{
    /// <summary>
    /// Property path (e.g., "Motor/Speed").
    /// </summary>
    public required string Path { get; set; }

    /// <summary>
    /// Error code for this property.
    /// </summary>
    public int Code { get; set; }

    /// <summary>
    /// Error message for this property.
    /// </summary>
    public required string Message { get; set; }
}
```

**Step 8: Implement ErrorPayload**

Create `src/Namotion.Interceptor.WebSocket/Protocol/ErrorPayload.cs`:

```csharp
namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Payload for Error message.
/// </summary>
public class ErrorPayload
{
    /// <summary>
    /// Primary error code.
    /// </summary>
    public int Code { get; set; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public required string Message { get; set; }

    /// <summary>
    /// Individual property failures (if applicable).
    /// </summary>
    public IReadOnlyList<PropertyFailure>? Failures { get; set; }
}

/// <summary>
/// Standard error codes for the WebSocket protocol.
/// </summary>
public static class ErrorCode
{
    public const int UnknownProperty = 100;
    public const int ReadOnlyProperty = 101;
    public const int ValidationFailed = 102;
    public const int InvalidFormat = 200;
    public const int VersionMismatch = 201;
    public const int InternalError = 500;
}
```

**Step 9: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "PayloadTests" -v n`
Expected: PASS (3 tests)

**Step 10: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Protocol src/Namotion.Interceptor.WebSocket.Tests/Protocol
git commit -m "feat(websocket): add protocol message types and payloads"
```

---

## Task 3: Implement JSON Serializer

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket/Serialization/IWsSerializer.cs`
- Create: `src/Namotion.Interceptor.WebSocket/Serialization/JsonWsSerializer.cs`
- Test: `src/Namotion.Interceptor.WebSocket.Tests/Serialization/JsonWsSerializerTests.cs`

**Step 1: Write failing test for JSON serializer**

Create `src/Namotion.Interceptor.WebSocket.Tests/Serialization/JsonWsSerializerTests.cs`:

```csharp
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;

namespace Namotion.Interceptor.WebSocket.Tests.Serialization;

public class JsonWsSerializerTests
{
    private readonly JsonWsSerializer _serializer = new();

    [Fact]
    public void SerializeAndDeserialize_HelloPayload_ShouldRoundTrip()
    {
        var original = new HelloPayload { Version = 1, Format = WsFormat.Json };

        var bytes = _serializer.Serialize(original);
        var deserialized = _serializer.Deserialize<HelloPayload>(bytes);

        Assert.Equal(original.Version, deserialized.Version);
        Assert.Equal(original.Format, deserialized.Format);
    }

    [Fact]
    public void SerializeAndDeserialize_SubjectUpdate_ShouldRoundTrip()
    {
        var original = new SubjectUpdate
        {
            Id = "test-123",
            Properties =
            {
                ["Temperature"] = SubjectPropertyUpdate.Create(23.5)
            }
        };

        var bytes = _serializer.Serialize(original);
        var deserialized = _serializer.Deserialize<SubjectUpdate>(bytes);

        Assert.Equal("test-123", deserialized.Id);
        Assert.True(deserialized.Properties.ContainsKey("Temperature"));
    }

    [Fact]
    public void SerializeMessage_ShouldCreateEnvelopeArray()
    {
        var payload = new HelloPayload { Version = 1, Format = WsFormat.Json };

        var bytes = _serializer.SerializeMessage(MessageType.Hello, null, payload);
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.StartsWith("[0,null,", json); // [MessageType.Hello, null, payload]
    }

    [Fact]
    public void DeserializeMessageEnvelope_ShouldExtractComponents()
    {
        var payload = new HelloPayload { Version = 1, Format = WsFormat.Json };
        var bytes = _serializer.SerializeMessage(MessageType.Hello, 42, payload);

        var (messageType, correlationId, payloadBytes) = _serializer.DeserializeMessageEnvelope(bytes);

        Assert.Equal(MessageType.Hello, messageType);
        Assert.Equal(42, correlationId);

        var deserializedPayload = _serializer.Deserialize<HelloPayload>(payloadBytes.Span);
        Assert.Equal(1, deserializedPayload.Version);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "JsonWsSerializerTests" -v n`
Expected: FAIL with compilation errors

**Step 3: Implement IWsSerializer interface**

Create `src/Namotion.Interceptor.WebSocket/Serialization/IWsSerializer.cs`:

```csharp
using Namotion.Interceptor.WebSocket.Protocol;

namespace Namotion.Interceptor.WebSocket.Serialization;

/// <summary>
/// Serializer interface for WebSocket messages.
/// Extensible for future format support (e.g., MessagePack).
/// </summary>
public interface IWsSerializer
{
    /// <summary>
    /// Gets the serialization format.
    /// </summary>
    WsFormat Format { get; }

    /// <summary>
    /// Serializes a payload to bytes.
    /// </summary>
    byte[] Serialize<T>(T value);

    /// <summary>
    /// Deserializes bytes to a payload.
    /// </summary>
    T Deserialize<T>(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Serializes a complete message envelope [MessageType, CorrelationId, Payload].
    /// </summary>
    byte[] SerializeMessage<T>(MessageType messageType, int? correlationId, T payload);

    /// <summary>
    /// Deserializes a message envelope and returns the message type, correlation ID, and raw payload bytes.
    /// </summary>
    (MessageType Type, int? CorrelationId, ReadOnlyMemory<byte> PayloadBytes) DeserializeMessageEnvelope(ReadOnlySpan<byte> bytes);
}
```

**Step 4: Implement JsonWsSerializer**

Create `src/Namotion.Interceptor.WebSocket/Serialization/JsonWsSerializer.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Namotion.Interceptor.WebSocket.Protocol;

namespace Namotion.Interceptor.WebSocket.Serialization;

/// <summary>
/// JSON serializer for WebSocket messages.
/// </summary>
public class JsonWsSerializer : IWsSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public WsFormat Format => WsFormat.Json;

    public byte[] Serialize<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, Options);
    }

    public T Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        return JsonSerializer.Deserialize<T>(bytes, Options)
            ?? throw new InvalidOperationException("Deserialization returned null");
    }

    public byte[] SerializeMessage<T>(MessageType messageType, int? correlationId, T payload)
    {
        var envelope = new object?[] { (int)messageType, correlationId, payload };
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
    }

    public (MessageType Type, int? CorrelationId, ReadOnlyMemory<byte> PayloadBytes) DeserializeMessageEnvelope(ReadOnlySpan<byte> bytes)
    {
        var array = JsonSerializer.Deserialize<JsonArray>(bytes, Options)
            ?? throw new InvalidOperationException("Invalid message envelope");

        if (array.Count < 3)
        {
            throw new InvalidOperationException("Message envelope must have at least 3 elements");
        }

        var messageType = (MessageType)array[0]!.GetValue<int>();
        var correlationId = array[1]?.GetValue<int>();

        // Re-serialize payload element to bytes for later deserialization
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(array[2], Options);

        return (messageType, correlationId, payloadBytes);
    }
}
```

**Step 5: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "JsonWsSerializerTests" -v n`
Expected: PASS (4 tests)

**Step 6: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Serialization src/Namotion.Interceptor.WebSocket.Tests/Serialization
git commit -m "feat(websocket): add JSON serializer implementation"
```

---

## Task 4: Implement Server Configuration

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket/Server/WebSocketServerConfiguration.cs`
- Test: `src/Namotion.Interceptor.WebSocket.Tests/Server/WebSocketServerConfigurationTests.cs`

**Step 1: Write failing test for configuration validation**

Create `src/Namotion.Interceptor.WebSocket.Tests/Server/WebSocketServerConfigurationTests.cs`:

```csharp
using Namotion.Interceptor.WebSocket.Server;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

public class WebSocketServerConfigurationTests
{
    [Fact]
    public void DefaultConfiguration_ShouldHaveExpectedValues()
    {
        var configuration = new WebSocketServerConfiguration();

        Assert.Equal(8080, configuration.Port);
        Assert.Equal("/ws", configuration.Path);
        Assert.Null(configuration.BindAddress);
        Assert.Equal(TimeSpan.FromMilliseconds(8), configuration.BufferTime);
    }

    [Fact]
    public void Validate_WithInvalidPort_ShouldThrow()
    {
        var configuration = new WebSocketServerConfiguration { Port = 0 };

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithValidConfiguration_ShouldNotThrow()
    {
        var configuration = new WebSocketServerConfiguration
        {
            Port = 8080,
            Path = "/ws"
        };

        configuration.Validate(); // Should not throw
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "WebSocketServerConfigurationTests" -v n`
Expected: FAIL with compilation errors

**Step 3: Implement WebSocketServerConfiguration**

Create `src/Namotion.Interceptor.WebSocket/Server/WebSocketServerConfiguration.cs`:

```csharp
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Configuration for WebSocket subject server.
/// </summary>
public class WebSocketServerConfiguration
{
    /// <summary>
    /// Port to listen on. Default: 8080
    /// </summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// WebSocket path. Default: "/ws"
    /// </summary>
    public string Path { get; set; } = "/ws";

    /// <summary>
    /// Bind address. Default: any (null)
    /// </summary>
    public string? BindAddress { get; set; }

    /// <summary>
    /// Buffer time for batching outbound updates. Default: 8ms
    /// </summary>
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Path provider for property filtering/mapping.
    /// </summary>
    public PathProviderBase? PathProvider { get; set; }

    /// <summary>
    /// Subject factory for creating subjects from client updates.
    /// </summary>
    public ISubjectFactory? SubjectFactory { get; set; }

    /// <summary>
    /// Update processors for filtering/transforming updates.
    /// </summary>
    public ISubjectUpdateProcessor[]? Processors { get; set; }

    /// <summary>
    /// Validates the configuration and throws if invalid.
    /// </summary>
    public void Validate()
    {
        if (Port is < 1 or > 65535)
        {
            throw new ArgumentException($"Port must be between 1 and 65535, got: {Port}", nameof(Port));
        }

        if (string.IsNullOrWhiteSpace(Path))
        {
            throw new ArgumentException("Path must be specified.", nameof(Path));
        }

        if (BufferTime < TimeSpan.Zero)
        {
            throw new ArgumentException($"BufferTime must be non-negative, got: {BufferTime}", nameof(BufferTime));
        }
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "WebSocketServerConfigurationTests" -v n`
Expected: PASS (3 tests)

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Server src/Namotion.Interceptor.WebSocket.Tests/Server
git commit -m "feat(websocket): add server configuration"
```

---

## Task 5: Implement Client Configuration

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket/Client/WebSocketClientConfiguration.cs`
- Test: `src/Namotion.Interceptor.WebSocket.Tests/Client/WebSocketClientConfigurationTests.cs`

**Step 1: Write failing test**

Create `src/Namotion.Interceptor.WebSocket.Tests/Client/WebSocketClientConfigurationTests.cs`:

```csharp
using Namotion.Interceptor.WebSocket.Client;

namespace Namotion.Interceptor.WebSocket.Tests.Client;

public class WebSocketClientConfigurationTests
{
    [Fact]
    public void DefaultConfiguration_ShouldHaveExpectedValues()
    {
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws")
        };

        Assert.Equal(TimeSpan.FromSeconds(5), configuration.ReconnectDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.ConnectTimeout);
    }

    [Fact]
    public void Validate_WithoutServerUri_ShouldThrow()
    {
        var configuration = new WebSocketClientConfiguration();

        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void Validate_WithValidConfiguration_ShouldNotThrow()
    {
        var configuration = new WebSocketClientConfiguration
        {
            ServerUri = new Uri("ws://localhost:8080/ws")
        };

        configuration.Validate(); // Should not throw
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "WebSocketClientConfigurationTests" -v n`
Expected: FAIL with compilation errors

**Step 3: Implement WebSocketClientConfiguration**

Create `src/Namotion.Interceptor.WebSocket/Client/WebSocketClientConfiguration.cs`:

```csharp
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.WebSocket.Client;

/// <summary>
/// Configuration for WebSocket subject client source.
/// </summary>
public class WebSocketClientConfiguration
{
    /// <summary>
    /// Server URI. Required. Example: "ws://localhost:8080/ws"
    /// </summary>
    public Uri? ServerUri { get; set; }

    /// <summary>
    /// Reconnect delay after disconnection. Default: 5 seconds
    /// </summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Connection timeout. Default: 30 seconds
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Buffer time for batching outbound updates. Default: 8ms
    /// </summary>
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Retry time for failed writes. Default: 10 seconds
    /// </summary>
    public TimeSpan RetryTime { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Write retry queue size. Default: 1000
    /// </summary>
    public int WriteRetryQueueSize { get; set; } = 1000;

    /// <summary>
    /// Path provider for property filtering/mapping.
    /// </summary>
    public PathProviderBase? PathProvider { get; set; }

    /// <summary>
    /// Subject factory for creating subjects from server updates.
    /// </summary>
    public ISubjectFactory? SubjectFactory { get; set; }

    /// <summary>
    /// Update processors for filtering/transforming updates.
    /// </summary>
    public ISubjectUpdateProcessor[]? Processors { get; set; }

    /// <summary>
    /// Validates the configuration and throws if invalid.
    /// </summary>
    public void Validate()
    {
        if (ServerUri is null)
        {
            throw new ArgumentException("ServerUri must be specified.", nameof(ServerUri));
        }

        if (ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException($"ConnectTimeout must be positive, got: {ConnectTimeout}", nameof(ConnectTimeout));
        }

        if (ReconnectDelay <= TimeSpan.Zero)
        {
            throw new ArgumentException($"ReconnectDelay must be positive, got: {ReconnectDelay}", nameof(ReconnectDelay));
        }

        if (WriteRetryQueueSize < 0)
        {
            throw new ArgumentException($"WriteRetryQueueSize must be non-negative, got: {WriteRetryQueueSize}", nameof(WriteRetryQueueSize));
        }
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "WebSocketClientConfigurationTests" -v n`
Expected: PASS (3 tests)

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Client src/Namotion.Interceptor.WebSocket.Tests/Client
git commit -m "feat(websocket): add client configuration"
```

---

## Task 6: Implement WebSocket Server

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket/Server/WebSocketClientConnection.cs`
- Create: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectServer.cs`
- Test: (Integration tests in Task 9)

**Step 1: Implement WebSocketClientConnection**

Create `src/Namotion.Interceptor.WebSocket/Server/WebSocketClientConnection.cs`:

```csharp
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Represents a single client connection to the WebSocket server.
/// </summary>
internal sealed class WebSocketClientConnection : IAsyncDisposable
{
    private readonly WebSocket _webSocket;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly IWsSerializer _serializer = new JsonWsSerializer();

    public string ConnectionId { get; } = Guid.NewGuid().ToString("N")[..8];
    public bool IsConnected => _webSocket.State == WebSocketState.Open;

    public WebSocketClientConnection(WebSocket webSocket, ILogger logger)
    {
        _webSocket = webSocket;
        _logger = logger;
    }

    public async Task<HelloPayload?> ReceiveHelloAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var result = await _webSocket.ReceiveAsync(buffer, cancellationToken);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            return null;
        }

        try
        {
            var (messageType, _, payloadBytes) = _serializer.DeserializeMessageEnvelope(buffer.AsSpan(0, result.Count));

            if (messageType == MessageType.Hello)
            {
                return _serializer.Deserialize<HelloPayload>(payloadBytes.Span);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Hello message from client {ConnectionId}", ConnectionId);
        }

        return null;
    }

    public async Task SendWelcomeAsync(SubjectUpdate initialState, CancellationToken cancellationToken)
    {
        var welcome = new WelcomePayload
        {
            Version = 1,
            Format = WsFormat.Json,
            State = initialState
        };

        var bytes = _serializer.SerializeMessage(MessageType.Welcome, null, welcome);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    public async Task SendUpdateAsync(SubjectUpdate update, CancellationToken cancellationToken)
    {
        if (!IsConnected) return;

        try
        {
            var bytes = _serializer.SerializeMessage(MessageType.Update, null, update);
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Failed to send update to client {ConnectionId}", ConnectionId);
        }
    }

    public async Task SendErrorAsync(ErrorPayload error, CancellationToken cancellationToken)
    {
        if (!IsConnected) return;

        try
        {
            var bytes = _serializer.SerializeMessage(MessageType.Error, null, error);
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Failed to send error to client {ConnectionId}", ConnectionId);
        }
    }

    public async Task<SubjectUpdate?> ReceiveUpdateAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];

        try
        {
            var result = await _webSocket.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            var (messageType, _, payloadBytes) = _serializer.DeserializeMessageEnvelope(buffer.AsSpan(0, result.Count));

            if (messageType == MessageType.Update)
            {
                return _serializer.Deserialize<SubjectUpdate>(payloadBytes.Span);
            }

            _logger.LogWarning("Received unexpected message type {MessageType} from client {ConnectionId}",
                messageType, ConnectionId);
            return null;
        }
        catch (WebSocketException)
        {
            return null;
        }
    }

    public async Task CloseAsync(string reason = "Server closing")
    {
        if (_webSocket.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // Ignore close errors
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await CloseAsync();
        _webSocket.Dispose();
        _cts.Dispose();
    }
}
```

**Step 2: Implement WebSocketSubjectServer**

Create `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectServer.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// WebSocket server that exposes subject updates to connected clients.
/// </summary>
public class WebSocketSubjectServer : BackgroundService, IAsyncDisposable
{
    private readonly IInterceptorSubject _subject;
    private readonly IInterceptorSubjectContext _context;
    private readonly WebSocketServerConfiguration _configuration;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, WebSocketClientConnection> _connections = new();
    private readonly ISubjectUpdateProcessor[] _processors;

    private HttpListener? _httpListener;
    private int _disposed;

    public int ConnectionCount => _connections.Count;

    public WebSocketSubjectServer(
        IInterceptorSubject subject,
        WebSocketServerConfiguration configuration,
        ILogger<WebSocketSubjectServer> logger)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _context = subject.Context;
        _processors = configuration.Processors ?? [];

        configuration.Validate();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var prefix = _configuration.BindAddress is not null
            ? $"http://{_configuration.BindAddress}:{_configuration.Port}{_configuration.Path}/"
            : $"http://+:{_configuration.Port}{_configuration.Path}/";

        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add(prefix);

        try
        {
            _httpListener.Start();
            _logger.LogInformation("WebSocket server started on port {Port} at path {Path}",
                _configuration.Port, _configuration.Path);

            using var changeQueueProcessor = new ChangeQueueProcessor(
                source: this,
                _context,
                propertyFilter: IsPropertyIncluded,
                writeHandler: BroadcastChangesAsync,
                _configuration.BufferTime,
                _logger);

            var processorTask = changeQueueProcessor.ProcessAsync(stoppingToken);
            var acceptTask = AcceptConnectionsAsync(stoppingToken);

            await Task.WhenAll(processorTask, acceptTask);
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            _logger.LogError("Access denied. Run as administrator or use 'netsh http add urlacl url={Prefix} user=Everyone'", prefix);
            throw;
        }
        finally
        {
            _httpListener.Stop();
            _httpListener.Close();
        }
    }

    private async Task AcceptConnectionsAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && _httpListener?.IsListening == true)
        {
            try
            {
                var context = await _httpListener.GetContextAsync();

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                var wsContext = await context.AcceptWebSocketAsync(null);
                _ = HandleClientAsync(wsContext.WebSocket, stoppingToken);
            }
            catch (HttpListenerException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting WebSocket connection");
            }
        }
    }

    private async Task HandleClientAsync(System.Net.WebSockets.WebSocket webSocket, CancellationToken stoppingToken)
    {
        var connection = new WebSocketClientConnection(webSocket, _logger);

        try
        {
            // Receive Hello
            var hello = await connection.ReceiveHelloAsync(stoppingToken);
            if (hello is null)
            {
                await connection.CloseAsync("No Hello received");
                return;
            }

            _logger.LogInformation("Client {ConnectionId} connected", connection.ConnectionId);

            // Send Welcome with initial state
            var initialState = SubjectUpdate.CreateCompleteUpdate(_subject, _processors);
            await connection.SendWelcomeAsync(initialState, stoppingToken);

            // Register connection
            _connections[connection.ConnectionId] = connection;

            // Handle incoming updates
            await ReceiveUpdatesAsync(connection, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client {ConnectionId}", connection.ConnectionId);
        }
        finally
        {
            _connections.TryRemove(connection.ConnectionId, out _);
            await connection.DisposeAsync();
            _logger.LogInformation("Client {ConnectionId} disconnected", connection.ConnectionId);
        }
    }

    private async Task ReceiveUpdatesAsync(WebSocketClientConnection connection, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && connection.IsConnected)
        {
            var update = await connection.ReceiveUpdateAsync(stoppingToken);
            if (update is null)
            {
                break;
            }

            try
            {
                var factory = _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance;
                _subject.ApplySubjectUpdate(update, factory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying update from client {ConnectionId}", connection.ConnectionId);

                await connection.SendErrorAsync(new Protocol.ErrorPayload
                {
                    Code = Protocol.ErrorCode.InternalError,
                    Message = ex.Message
                }, stoppingToken);
            }
        }
    }

    private async ValueTask BroadcastChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (changes.Length == 0 || _connections.IsEmpty) return;

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(_subject, changes.Span, _processors);

        var tasks = _connections.Values.Select(c => c.SendUpdateAsync(update, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private bool IsPropertyIncluded(Registry.Abstractions.RegisteredSubjectProperty property)
    {
        return _configuration.PathProvider?.IsPropertyIncluded(property) ?? true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        foreach (var connection in _connections.Values)
        {
            await connection.DisposeAsync();
        }
        _connections.Clear();

        _httpListener?.Stop();
        _httpListener?.Close();

        Dispose();
    }
}
```

**Step 3: Verify build**

Run: `dotnet build src/Namotion.Interceptor.WebSocket`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Server
git commit -m "feat(websocket): implement WebSocket server"
```

---

## Task 7: Implement WebSocket Client Source

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs`

**Step 1: Implement WebSocketSubjectClientSource**

Create `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs`:

```csharp
using System.Net.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;

namespace Namotion.Interceptor.WebSocket.Client;

/// <summary>
/// WebSocket client source that connects to a WebSocket server and synchronizes subjects.
/// </summary>
public sealed class WebSocketSubjectClientSource : BackgroundService, ISubjectSource, IAsyncDisposable
{
    private readonly IInterceptorSubject _subject;
    private readonly WebSocketClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly ISubjectUpdateProcessor[] _processors;
    private readonly IWsSerializer _serializer = new JsonWsSerializer();

    private ClientWebSocket? _webSocket;
    private SubjectPropertyWriter? _propertyWriter;
    private SubjectUpdate? _initialState;
    private CancellationTokenSource? _receiveCts;

    private volatile bool _isStarted;
    private int _disposed;

    public IInterceptorSubject RootSubject => _subject;
    public int WriteBatchSize => 0;

    public WebSocketSubjectClientSource(
        IInterceptorSubject subject,
        WebSocketClientConfiguration configuration,
        ILogger<WebSocketSubjectClientSource> logger)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processors = configuration.Processors ?? [];

        configuration.Validate();
    }

    public async Task<IDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        _propertyWriter = propertyWriter;
        _receiveCts = new CancellationTokenSource();

        _webSocket = new ClientWebSocket();

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(_configuration.ConnectTimeout);

        _logger.LogInformation("Connecting to WebSocket server at {Uri}", _configuration.ServerUri);
        await _webSocket.ConnectAsync(_configuration.ServerUri!, connectCts.Token);

        // Send Hello
        var hello = new HelloPayload { Version = 1, Format = WsFormat.Json };
        var helloBytes = _serializer.SerializeMessage(MessageType.Hello, null, hello);
        await _webSocket.SendAsync(helloBytes, WebSocketMessageType.Text, true, cancellationToken);

        // Receive Welcome
        var buffer = new byte[1024 * 1024]; // 1MB buffer for initial state
        var result = await _webSocket.ReceiveAsync(buffer, cancellationToken);

        var (messageType, _, payloadBytes) = _serializer.DeserializeMessageEnvelope(buffer.AsSpan(0, result.Count));

        if (messageType != MessageType.Welcome)
        {
            throw new InvalidOperationException($"Expected Welcome message, got {messageType}");
        }

        var welcome = _serializer.Deserialize<WelcomePayload>(payloadBytes.Span);
        _initialState = welcome.State;

        _logger.LogInformation("Connected to WebSocket server");

        // Start receive loop
        _ = ReceiveLoopAsync(_receiveCts.Token);

        _isStarted = true;

        return new ConnectionLifetime(async () =>
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", CancellationToken.None);
            }
        });
    }

    public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        if (_initialState is null)
        {
            return Task.FromResult<Action?>(null);
        }

        return Task.FromResult<Action?>(() =>
        {
            var factory = _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance;
            _subject.ApplySubjectUpdate(_initialState, factory);
        });
    }

    public async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (_webSocket?.State != WebSocketState.Open)
        {
            return WriteResult.Failure(changes, new InvalidOperationException("WebSocket is not connected"));
        }

        try
        {
            var update = SubjectUpdate.CreatePartialUpdateFromChanges(_subject, changes.Span, _processors);
            var bytes = _serializer.SerializeMessage(MessageType.Update, null, update);
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            return WriteResult.Success;
        }
        catch (Exception ex)
        {
            return WriteResult.Failure(changes, ex);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];

        while (!cancellationToken.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
        {
            try
            {
                var result = await _webSocket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Server closed connection");
                    break;
                }

                var (messageType, _, payloadBytes) = _serializer.DeserializeMessageEnvelope(buffer.AsSpan(0, result.Count));

                switch (messageType)
                {
                    case MessageType.Update:
                        var update = _serializer.Deserialize<SubjectUpdate>(payloadBytes.Span);
                        HandleUpdate(update);
                        break;

                    case MessageType.Error:
                        var error = _serializer.Deserialize<ErrorPayload>(payloadBytes.Span);
                        _logger.LogWarning("Received error from server: {Code} - {Message}", error.Code, error.Message);
                        break;
                }
            }
            catch (WebSocketException ex)
            {
                _logger.LogWarning(ex, "WebSocket error in receive loop");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing received message");
            }
        }
    }

    private void HandleUpdate(SubjectUpdate update)
    {
        if (_propertyWriter is null) return;

        _propertyWriter.Write(
            (update, _subject, _configuration.SubjectFactory ?? DefaultSubjectFactory.Instance),
            static state => state._subject.ApplySubjectUpdate(state.update, state.Item3));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_isStarted && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(100, stoppingToken);
        }

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        if (_receiveCts is not null)
        {
            await _receiveCts.CancelAsync();
            _receiveCts.Dispose();
        }

        if (_webSocket is not null)
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None);
                }
                catch { }
            }
            _webSocket.Dispose();
        }

        Dispose();
    }

    private sealed class ConnectionLifetime(Func<Task> onDispose) : IDisposable
    {
        public void Dispose() => onDispose().GetAwaiter().GetResult();
    }
}
```

**Step 2: Verify build**

Run: `dotnet build src/Namotion.Interceptor.WebSocket`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/Client
git commit -m "feat(websocket): implement WebSocket client source"
```

---

## Task 8: Implement DI Extension Methods

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket/WebSocketSubjectExtensions.cs`

**Step 1: Implement extension methods**

Create `src/Namotion.Interceptor.WebSocket/WebSocketSubjectExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.WebSocket.Client;
using Namotion.Interceptor.WebSocket.Server;

namespace Namotion.Interceptor.WebSocket;

public static class WebSocketSubjectExtensions
{
    /// <summary>
    /// Adds a WebSocket subject server that exposes subjects to connected clients.
    /// </summary>
    public static IServiceCollection AddWebSocketSubjectServer<TSubject>(
        this IServiceCollection services,
        Action<WebSocketServerConfiguration> configure)
        where TSubject : IInterceptorSubject
    {
        return services.AddWebSocketSubjectServer(
            serviceProvider => serviceProvider.GetRequiredService<TSubject>(),
            configure);
    }

    /// <summary>
    /// Adds a WebSocket subject server with custom subject selector.
    /// </summary>
    public static IServiceCollection AddWebSocketSubjectServer(
        this IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Action<WebSocketServerConfiguration> configure)
    {
        var key = Guid.NewGuid().ToString();

        return services
            .AddKeyedSingleton(key, (serviceProvider, _) =>
            {
                var configuration = new WebSocketServerConfiguration();
                configure(configuration);
                return configuration;
            })
            .AddKeyedSingleton(key, (serviceProvider, _) => subjectSelector(serviceProvider))
            .AddKeyedSingleton(key, (serviceProvider, _) =>
            {
                var subject = serviceProvider.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new WebSocketSubjectServer(
                    subject,
                    serviceProvider.GetRequiredKeyedService<WebSocketServerConfiguration>(key),
                    serviceProvider.GetRequiredService<ILogger<WebSocketSubjectServer>>());
            })
            .AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredKeyedService<WebSocketSubjectServer>(key));
    }

    /// <summary>
    /// Adds a WebSocket subject client source that connects to a server and synchronizes subjects.
    /// </summary>
    public static IServiceCollection AddWebSocketSubjectClientSource<TSubject>(
        this IServiceCollection services,
        Action<WebSocketClientConfiguration> configure)
        where TSubject : IInterceptorSubject
    {
        return services.AddWebSocketSubjectClientSource(
            serviceProvider => serviceProvider.GetRequiredService<TSubject>(),
            configure);
    }

    /// <summary>
    /// Adds a WebSocket subject client source with custom subject selector.
    /// </summary>
    public static IServiceCollection AddWebSocketSubjectClientSource(
        this IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Action<WebSocketClientConfiguration> configure)
    {
        var key = Guid.NewGuid().ToString();

        return services
            .AddKeyedSingleton(key, (serviceProvider, _) =>
            {
                var configuration = new WebSocketClientConfiguration();
                configure(configuration);
                return configuration;
            })
            .AddKeyedSingleton(key, (serviceProvider, _) => subjectSelector(serviceProvider))
            .AddKeyedSingleton(key, (serviceProvider, _) =>
            {
                var subject = serviceProvider.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new WebSocketSubjectClientSource(
                    subject,
                    serviceProvider.GetRequiredKeyedService<WebSocketClientConfiguration>(key),
                    serviceProvider.GetRequiredService<ILogger<WebSocketSubjectClientSource>>());
            })
            .AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredKeyedService<WebSocketSubjectClientSource>(key))
            .AddSingleton<IHostedService>(serviceProvider =>
            {
                var configuration = serviceProvider.GetRequiredKeyedService<WebSocketClientConfiguration>(key);
                var subject = serviceProvider.GetRequiredKeyedService<IInterceptorSubject>(key);
                return new SubjectSourceBackgroundService(
                    serviceProvider.GetRequiredKeyedService<WebSocketSubjectClientSource>(key),
                    subject.Context,
                    serviceProvider.GetRequiredService<ILogger<SubjectSourceBackgroundService>>(),
                    configuration.BufferTime,
                    configuration.RetryTime,
                    configuration.WriteRetryQueueSize);
            });
    }
}
```

**Step 2: Verify build**

Run: `dotnet build src/Namotion.Interceptor.WebSocket`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket/WebSocketSubjectExtensions.cs
git commit -m "feat(websocket): add DI extension methods"
```

---

## Task 9: Create Integration Test Infrastructure and Tests

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket.Tests/Integration/TestModel.cs`
- Create: `src/Namotion.Interceptor.WebSocket.Tests/Integration/WebSocketTestServer.cs`
- Create: `src/Namotion.Interceptor.WebSocket.Tests/Integration/WebSocketTestClient.cs`
- Create: `src/Namotion.Interceptor.WebSocket.Tests/Integration/WebSocketServerClientTests.cs`

**Step 1: Create TestModel**

Create `src/Namotion.Interceptor.WebSocket.Tests/Integration/TestModel.cs`:

```csharp
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

[InterceptorSubject]
public partial class TestRoot
{
    public TestRoot()
    {
        Name = "";
        Items = [];
    }

    public partial bool Connected { get; set; }
    public partial string Name { get; set; }
    public partial decimal Number { get; set; }
    public partial TestItem[] Items { get; set; }
}

[InterceptorSubject]
public partial class TestItem
{
    public TestItem()
    {
        Label = "";
    }

    public partial string Label { get; set; }
    public partial int Value { get; set; }
}
```

**Step 2: Create WebSocketTestServer**

Create `src/Namotion.Interceptor.WebSocket.Tests/Integration/WebSocketTestServer.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Server;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

public class WebSocketTestServer<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;

    public TRoot? Root { get; private set; }
    public WebSocketSubjectServer? Server { get; private set; }

    public WebSocketTestServer(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Action<IInterceptorSubjectContext, TRoot>? initializeDefaults = null,
        int port = 18080)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddConsole();
        });

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithHostedServices(builder.Services);

        Root = createRoot(context);
        initializeDefaults?.Invoke(context, Root);

        builder.Services.AddSingleton(Root);
        builder.Services.AddWebSocketSubjectServer<TRoot>(configuration =>
        {
            configuration.Port = port;
        });

        _host = builder.Build();

        Server = _host.Services.GetServices<IHostedService>()
            .OfType<WebSocketSubjectServer>()
            .FirstOrDefault();

        await _host.StartAsync();

        // Wait for server to be ready
        await Task.Delay(500);
    }

    public async Task StopAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
```

**Step 3: Create WebSocketTestClient**

Create `src/Namotion.Interceptor.WebSocket.Tests/Integration/WebSocketTestClient.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

public class WebSocketTestClient<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;

    public TRoot? Root { get; private set; }

    public WebSocketTestClient(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Func<TRoot, bool>? isConnected = null,
        int port = 18080)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddConsole();
        });

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithHostedServices(builder.Services);

        Root = createRoot(context);

        builder.Services.AddSingleton(Root);
        builder.Services.AddWebSocketSubjectClientSource<TRoot>(configuration =>
        {
            configuration.ServerUri = new Uri($"ws://localhost:{port}/ws");
        });

        _host = builder.Build();
        await _host.StartAsync();

        // Wait for connection
        if (isConnected != null)
        {
            var timeout = DateTime.UtcNow.AddSeconds(10);
            while (!isConnected(Root) && DateTime.UtcNow < timeout)
            {
                await Task.Delay(100);
            }
        }
        else
        {
            await Task.Delay(1000);
        }
    }

    public async Task StopAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
```

**Step 4: Create integration tests**

Create `src/Namotion.Interceptor.WebSocket.Tests/Integration/WebSocketServerClientTests.cs`:

```csharp
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

[Collection("WebSocket Integration")]
public class WebSocketServerClientTests
{
    private readonly ITestOutputHelper _output;

    public WebSocketServerClientTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ServerWriteProperty_ShouldUpdateClient()
    {
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial");

        await client.StartAsync(context => new TestRoot(context));

        // Wait for initial sync
        await Task.Delay(500);
        Assert.Equal("Initial", client.Root!.Name);

        // Server updates property
        server.Root!.Name = "Updated from Server";
        await Task.Delay(500);

        Assert.Equal("Updated from Server", client.Root.Name);
    }

    [Fact]
    public async Task ClientWriteProperty_ShouldUpdateServer()
    {
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context));
        await client.StartAsync(context => new TestRoot(context));

        // Client updates property
        client.Root!.Name = "Updated from Client";
        await Task.Delay(500);

        Assert.Equal("Updated from Client", server.Root!.Name);
    }

    [Fact]
    public async Task NumericProperty_ShouldSyncBidirectionally()
    {
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context));
        await client.StartAsync(context => new TestRoot(context));

        // Server updates
        server.Root!.Number = 123.45m;
        await Task.Delay(500);
        Assert.Equal(123.45m, client.Root!.Number);

        // Client updates
        client.Root.Number = 678.90m;
        await Task.Delay(500);
        Assert.Equal(678.90m, server.Root.Number);
    }

    [Fact]
    public async Task MultipleClients_ShouldAllReceiveUpdates()
    {
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client1 = new WebSocketTestClient<TestRoot>(_output);
        await using var client2 = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context));
        await client1.StartAsync(context => new TestRoot(context));
        await client2.StartAsync(context => new TestRoot(context));

        server.Root!.Name = "Broadcast Test";
        await Task.Delay(500);

        Assert.Equal("Broadcast Test", client1.Root!.Name);
        Assert.Equal("Broadcast Test", client2.Root!.Name);
    }
}
```

**Step 5: Run integration tests**

Run: `dotnet test src/Namotion.Interceptor.WebSocket.Tests --filter "WebSocketServerClientTests" -v n`
Expected: PASS (4 tests)

Note: Tests may require running as administrator or adding URL ACL reservation.

**Step 6: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket.Tests/Integration
git commit -m "test(websocket): add integration test infrastructure and tests"
```

---

## Task 10: Create Sample Server Application

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket.SampleServer/Namotion.Interceptor.WebSocket.SampleServer.csproj`
- Create: `src/Namotion.Interceptor.WebSocket.SampleServer/Program.cs`
- Modify: `src/Namotion.Interceptor.slnx`

**Step 1: Create sample server project**

Create `src/Namotion.Interceptor.WebSocket.SampleServer/Namotion.Interceptor.WebSocket.SampleServer.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		<OutputType>Exe</OutputType>
		<TargetFramework>net9.0</TargetFramework>
		<Nullable>enable</Nullable>
		<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
	</PropertyGroup>

	<ItemGroup>
		<ProjectReference Include="..\Namotion.Interceptor.WebSocket\Namotion.Interceptor.WebSocket.csproj" />
		<ProjectReference Include="..\Namotion.Interceptor.SamplesModel\Namotion.Interceptor.SamplesModel.csproj" />
	</ItemGroup>
</Project>
```

**Step 2: Create Program.cs**

Create `src/Namotion.Interceptor.WebSocket.SampleServer/Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.SamplesModel;
using Namotion.Interceptor.SamplesModel.Workers;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket;

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithParents()
    .WithLifecycle()
    .WithHostedServices(builder.Services);

var root = Root.CreateWithPersons(context, count: 100);
context.AddService(root);

builder.Services.AddSingleton(root);
builder.Services.AddHostedService<ServerWorker>();
builder.Services.AddWebSocketSubjectServer<Root>(configuration =>
{
    configuration.Port = 8080;
});

Console.WriteLine("Starting WebSocket server on ws://localhost:8080/ws");

using var performanceProfiler = new PerformanceProfiler(context, "Server");
var host = builder.Build();
host.Run();
```

**Step 3: Add to solution**

Add to `src/Namotion.Interceptor.slnx` in `/Extensions/` folder:

```xml
    <Project Path="Namotion.Interceptor.WebSocket.SampleServer/Namotion.Interceptor.WebSocket.SampleServer.csproj" />
```

**Step 4: Verify build**

Run: `dotnet build src/Namotion.Interceptor.WebSocket.SampleServer`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket.SampleServer src/Namotion.Interceptor.slnx
git commit -m "feat(websocket): add sample server application"
```

---

## Task 11: Create Sample Client Application

**Files:**
- Create: `src/Namotion.Interceptor.WebSocket.SampleClient/Namotion.Interceptor.WebSocket.SampleClient.csproj`
- Create: `src/Namotion.Interceptor.WebSocket.SampleClient/Program.cs`
- Modify: `src/Namotion.Interceptor.slnx`

**Step 1: Create sample client project**

Create `src/Namotion.Interceptor.WebSocket.SampleClient/Namotion.Interceptor.WebSocket.SampleClient.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		<OutputType>Exe</OutputType>
		<TargetFramework>net9.0</TargetFramework>
		<Nullable>enable</Nullable>
		<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
	</PropertyGroup>

	<ItemGroup>
		<ProjectReference Include="..\Namotion.Interceptor.WebSocket\Namotion.Interceptor.WebSocket.csproj" />
		<ProjectReference Include="..\Namotion.Interceptor.SamplesModel\Namotion.Interceptor.SamplesModel.csproj" />
	</ItemGroup>
</Project>
```

**Step 2: Create Program.cs**

Create `src/Namotion.Interceptor.WebSocket.SampleClient/Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.SamplesModel;
using Namotion.Interceptor.SamplesModel.Workers;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket;

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithParents()
    .WithLifecycle()
    .WithHostedServices(builder.Services);

var root = new Root(context);
context.AddService(root);

builder.Services.AddSingleton(root);
builder.Services.AddHostedService<ClientWorker>();
builder.Services.AddWebSocketSubjectClientSource<Root>(configuration =>
{
    configuration.ServerUri = new Uri("ws://localhost:8080/ws");
});

Console.WriteLine("Connecting to WebSocket server...");

using var performanceProfiler = new PerformanceProfiler(context, "Client");
var host = builder.Build();
host.Run();
```

**Step 3: Add to solution**

Add to `src/Namotion.Interceptor.slnx` in `/Extensions/` folder:

```xml
    <Project Path="Namotion.Interceptor.WebSocket.SampleClient/Namotion.Interceptor.WebSocket.SampleClient.csproj" />
```

**Step 4: Verify build**

Run: `dotnet build src/Namotion.Interceptor.WebSocket.SampleClient`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.WebSocket.SampleClient src/Namotion.Interceptor.slnx
git commit -m "feat(websocket): add sample client application"
```

---

## Task 12: Final Verification

**Step 1: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "WebSocket" -v n`
Expected: All tests pass

**Step 2: Build entire solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded with 0 errors

**Step 3: Manual testing (optional)**

Terminal 1:
```bash
dotnet run --project src/Namotion.Interceptor.WebSocket.SampleServer
```

Terminal 2:
```bash
dotnet run --project src/Namotion.Interceptor.WebSocket.SampleClient
```

**Step 4: Final commit**

```bash
git add -A
git commit -m "feat(websocket): complete WebSocket protocol implementation

- WebSocket server and client with bidirectional sync
- JSON serialization (extensible for MessagePack in future)
- Protocol: Hello/Welcome handshake, Update messages, Error handling
- Integration tests following OPC UA patterns
- Sample server and client applications

Closes: WebSocket Protocol Design"
```

---

## Summary

This plan implements the WebSocket Subject Protocol in 12 tasks:

1. **Tasks 1-3**: Project structure, protocol messages, JSON serializer
2. **Tasks 4-5**: Configuration classes
3. **Tasks 6-8**: Server, client, and DI extensions
4. **Task 9**: Integration test infrastructure and tests
5. **Tasks 10-11**: Sample applications
6. **Task 12**: Final verification

Each task follows TDD where applicable: write failing test → implement → verify → commit.

**Future extensibility:** The `IWsSerializer` interface and `WsFormat` enum are in place to add MessagePack support later without breaking changes.
