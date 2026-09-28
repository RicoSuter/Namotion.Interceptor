# Modbus Connector Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Namotion.Interceptor.Modbus`, a read-only Modbus TCP client connector that polls registers into `[ModbusRegister]`-annotated subject properties.

**Architecture:** A `ModbusSubjectClientSource` derived from `SubjectSourceBase` connects with FluentModbus (internal only), runs the optional `IModbusDiscovery` of the root subject, resolves every `[ModbusRegister]` property in the root subtree into a binding, plans contiguous read batches and polls them. Each cycle reads all batches into per-binding raw buffers, then applies only values whose raw words changed through `SubjectPropertyWriter`. Connection loss is handled by a reconnect loop inside the source (MQTT pattern).

**Tech Stack:** .NET 9, C# 13, FluentModbus 5.3.2, xUnit 2.9.3, Verify + PublicApiGenerator.

**Spec:** [2026-09-28-modbus-luxtronik-design.md](../specs/2026-09-28-modbus-luxtronik-design.md), Part A (sections 3, 4, 6.1, 6.2, 5.10 for the connector doc). The Luxtronik library is a separate plan: [2026-09-28-luxtronik-shi.md](2026-09-28-luxtronik-shi.md).

---

## Conventions for every task

- Read `AGENTS.md` first. Test names `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert` comments, no `Task.Delay`/`Thread.Sleep` in tests (use `AsyncTestHelpers.WaitUntilAsync` or `SourceStateRecorder.WaitForStatesAsync`).
- No abbreviations in names, no em dashes in docs or comments, comments only for the why a reader cannot derive.
- Warnings are errors (`src/Directory.Build.props`). A broken `<see cref>` is a build error.
- The connector and test projects reference `SonarAnalyzer.CSharp`, so `S`-prefixed diagnostics are build errors. Follow the Analyzer Policy in `AGENTS.md` and never add an exception (`#pragma`, `SuppressMessage` or `src/.editorconfig`) without user approval.
- Commit after each task with a `feat:`/`test:`/`docs:` prefix. No AI attribution, no `Co-Authored-By` trailer.
- Unit tests: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "Category!=Integration"`. Integration tests: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "Category=Integration"`.
- Long-running verification: the Connector Tester is not run for this connector (agreed in spec 6.5: client-only, read-only).

## Verified FluentModbus 5.3.2 behavior (probed, do not re-derive)

- Low-level reads `ReadHoldingRegistersAsync(byte unitIdentifier, ushort startingAddress, ushort quantity, CancellationToken)` and `ReadInputRegistersAsync(...)` return `Task<Memory<byte>>` with the raw wire bytes (big endian per register). `ReadCoilsAsync(int, int, int, CancellationToken)` and `ReadDiscreteInputsAsync(...)` return packed bits, lowest address in bit 0 of byte 0.
- The returned memory is a reused internal buffer: the next request overwrites it. Copy before the next read.
- A Modbus exception response throws `FluentModbus.ModbusException` (constructors are internal) with `ExceptionCode`; the connection stays usable. FluentModbus also throws `ModbusException` for framing errors (invalid protocol identifier, response function code or message length) through its `ModbusException(string)` constructor, which sets `ExceptionCode` to 255. Those are not device rejections.
- The async TCP read (`TransceiveFrameAsync`) creates its own `CancellationTokenSource` from `NetworkStream.ReadTimeout` per request, links the caller's token to it and closes the network stream when either fires. A cancelled or timed out request therefore always leaves the connection unusable.
- The `int` unit ID overloads (`ReadCoilsAsync`, `ReadDiscreteInputsAsync`) go through `ModbusClient.ConvertUnitIdentifier`, which only rejects values outside 0 to 255. The 247 limit exists only in the RTU clients. Every `byte` unit ID is valid on Modbus TCP (255 commonly addresses the TCP device itself), so the connector does not restrict unit IDs further.
- `ModbusTcpClient.Initialize(TcpClient, ModbusEndianness)` accepts an externally connected `TcpClient`.
- `ModbusTcpServer` in single-unit mode (no `AddUnit`) only answers unit 0 and drops the connection for other unit IDs. Tests always call `AddUnit`. With units added, a request to an unknown unit gets no response (times out).
- `server.GetHoldingRegisters(unitId).SetBigEndian<T>(address, value)` writes wire order; `SetBigEndian<int>` writes the high word first. Bits: `server.GetCoils(unitId).Set(address, value)`. Mutate server memory inside `lock (server.Lock)`.
- `ModbusServer.RequestValidator` is `Func<byte, ModbusFunctionCode, ushort, ushort, ModbusExceptionCode>` (unit, function code, address, quantity); return `ModbusExceptionCode.OK` to accept.
- Stopping the server makes pending and later client reads throw `IOException`.

## File structure

```
src/Namotion.Interceptor.Modbus/
  Namotion.Interceptor.Modbus.csproj
  ModbusDataType.cs, ModbusAddressSpace.cs, ModbusWordOrder.cs, ModbusAccess.cs, ModbusNotAvailableValue.cs   public enums
  IModbusUnitIdProvider.cs, IModbusBaseAddressProvider.cs, IModbusDiscovery.cs                                     public interfaces
  ModbusConfigurationException.cs, ModbusResponseException.cs                                                    public exceptions
  ModbusClientConfiguration.cs                                                                                     public configuration
  ModbusDiscoveryContext.cs                                                                                        public discovery context
  ModbusSubjectClientSource.cs                                                                                     public source
  ModbusClientDiagnostics.cs                                                                                       public diagnostics (+ ModbusPollingDiagnostics)
  ModbusSubjectExtensions.cs                                                                                       public Create/Add extensions (Microsoft.Extensions.DependencyInjection namespace)
  Attributes/ModbusRegisterAttribute.cs, Attributes/ModbusUnitIdAttribute.cs                                      public attributes
  Mapping/ModbusRegisterCodec.cs          internal: raw bytes to integers, floats, strings, not-available detection
  Mapping/ModbusValueConverters.cs        internal: per-binding converter delegate from raw bytes to the CLR property type
  Mapping/ModbusRegisterBinding.cs        internal: one resolved property plus its poll-cycle state
  Mapping/ModbusRegisterResolver.cs       internal: walks the subject tree into bindings, validates
  Mapping/ModbusReadBatch.cs, Mapping/ModbusReadPlanner.cs   internal: contiguous batching
  Transport/IModbusRegisterReader.cs      internal: raw read abstraction (fakeable in unit tests)
  Transport/ModbusConnection.cs           internal: FluentModbus wrapper with timeout and exception translation
  Polling/ModbusPoller.cs                 internal: read cycle, split-on-failure, change detection, apply
  Polling/ModbusPollingMetrics.cs         internal: Interlocked counters
src/Namotion.Interceptor.Modbus.Tests/
  Namotion.Interceptor.Modbus.Tests.csproj, VerifyTests.cs, VerifyChecksTests.PublicApi.verified.txt
  Testing/ModbusTestServer.cs, Testing/FakeRegisterReader.cs, Testing/ModbusIntegrationCollection.cs
  Mapping/ModbusRegisterCodecTests.cs, Mapping/ModbusValueConvertersTests.cs, Mapping/ModbusRegisterResolverTests.cs, Mapping/ModbusReadPlannerTests.cs
  ModbusClientConfigurationTests.cs, ModbusClientDiagnosticsTests.cs, ModbusDiscoveryContextTests.cs, Polling/ModbusPollerTests.cs
  Transport/ModbusConnectionTests.cs (Integration), ModbusSubjectClientSourceTests.cs (Integration), ModbusRegistrationTests.cs
docs/connectors-modbus.md (new), docs/connectors.md, README.md (links), .github/workflows/build.yml (Modbus integration job)
```

---

## Task 1: Scaffold the connector and test projects

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/Namotion.Interceptor.Modbus.csproj`
- Create: `src/Namotion.Interceptor.Modbus.Tests/Namotion.Interceptor.Modbus.Tests.csproj`
- Create: `src/Namotion.Interceptor.Modbus.Tests/Testing/ModbusIntegrationCollection.cs`
- Modify: `src/Namotion.Interceptor.slnx` (the `/Connectors/` and `/Tests/` folders)

- [ ] **Step 1: Create the library project**

```xml
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		<TargetFramework>net9.0</TargetFramework>
		<ImplicitUsings>enable</ImplicitUsings>
		<Nullable>enable</Nullable>
		<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
	</PropertyGroup>

	<ItemGroup>
		<PackageReference Include="FluentModbus" Version="5.3.2" />
	</ItemGroup>

	<ItemGroup>
		<InternalsVisibleTo Include="Namotion.Interceptor.Modbus.Tests" />
	</ItemGroup>

	<ItemGroup>
		<ProjectReference Include="..\Namotion.Interceptor.Connectors\Namotion.Interceptor.Connectors.csproj" />
	</ItemGroup>
</Project>
```

- [ ] **Step 2: Create the test project**

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net9.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <IsPackable>false</IsPackable>
        <IsTestProject>true</IsTestProject>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.10" />
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.0" />
        <PackageReference Include="PublicApiGenerator" Version="11.5.4" />
        <PackageReference Include="Verify.Xunit" Version="31.3.0" />
        <PackageReference Include="xunit" Version="2.9.3" />
        <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
            <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
            <PrivateAssets>all</PrivateAssets>
        </PackageReference>
        <PackageReference Include="coverlet.collector" Version="6.0.4">
            <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
            <PrivateAssets>all</PrivateAssets>
        </PackageReference>
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false"/>
        <ProjectReference Include="..\Namotion.Interceptor.Testing\Namotion.Interceptor.Testing.csproj"/>
        <ProjectReference Include="..\Namotion.Interceptor.Modbus\Namotion.Interceptor.Modbus.csproj"/>
    </ItemGroup>

</Project>
```

- [ ] **Step 3: Add the integration test collection**

`src/Namotion.Interceptor.Modbus.Tests/Testing/ModbusIntegrationCollection.cs`:

```csharp
namespace Namotion.Interceptor.Modbus.Tests.Testing;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ModbusIntegrationCollection
{
    public const string Name = "Modbus integration";
}
```

- [ ] **Step 4: Add both projects to the solution**

In `src/Namotion.Interceptor.slnx`, inside `<Folder Name="/Connectors/">` insert after the `Namotion.Interceptor.ConnectorTester` line (alphabetical: Modbus sorts before Mqtt):

```xml
    <Project Path="Namotion.Interceptor.Modbus/Namotion.Interceptor.Modbus.csproj" />
```

Inside `<Folder Name="/Tests/">` insert before the `Namotion.Interceptor.Mqtt.Tests` line:

```xml
    <Project Path="Namotion.Interceptor.Modbus.Tests/Namotion.Interceptor.Modbus.Tests.csproj" />
```

- [ ] **Step 5: Build and check restore warnings**

Run: `dotnet build src/Namotion.Interceptor.Modbus.Tests`
Expected: `Build succeeded` with 0 warnings. FluentModbus depends on `System.IO.Ports 5.0.0` and `Microsoft.Extensions.Logging.Abstractions 5.0.0`. If restore reports an `NU1901`-`NU1904` vulnerability warning (an error under warnings-as-errors), add a direct `<PackageReference Include="System.IO.Ports" Version="9.0.10" />` to the library project and rebuild.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.Modbus.Tests src/Namotion.Interceptor.slnx
git commit -m "feat: scaffold the Modbus connector and test projects"
```

---

## Task 2: Public enums, attributes, interfaces and exceptions

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/ModbusDataType.cs`, `ModbusAddressSpace.cs`, `ModbusWordOrder.cs`, `ModbusAccess.cs`, `ModbusNotAvailableValue.cs`
- Create: `src/Namotion.Interceptor.Modbus/IModbusUnitIdProvider.cs`, `IModbusBaseAddressProvider.cs`
- Create: `src/Namotion.Interceptor.Modbus/ModbusConfigurationException.cs`, `ModbusResponseException.cs`
- Create: `src/Namotion.Interceptor.Modbus/Attributes/ModbusRegisterAttribute.cs`, `Attributes/ModbusUnitIdAttribute.cs`

These are declarations only; they are exercised by the tests of Tasks 3 to 6.

- [ ] **Step 1: Enums**

`ModbusDataType.cs`:

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// The wire type of a mapped Modbus value.
/// </summary>
public enum ModbusDataType
{
    /// <summary>A single bit of a coil or discrete input.</summary>
    Boolean,

    /// <summary>Unsigned 16-bit integer, one register.</summary>
    U16,

    /// <summary>Signed 16-bit integer, one register.</summary>
    S16,

    /// <summary>Unsigned 32-bit integer, two registers.</summary>
    U32,

    /// <summary>Signed 32-bit integer, two registers.</summary>
    S32,

    /// <summary>IEEE 754 single precision float, two registers.</summary>
    F32,

    /// <summary>ASCII string, two characters per register, <c>Length</c> registers.</summary>
    String
}
```

`ModbusAddressSpace.cs`:

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// The Modbus data table a mapping reads from.
/// </summary>
public enum ModbusAddressSpace
{
    /// <summary>Holding registers, read with function code 3.</summary>
    HoldingRegister,

    /// <summary>Input registers, read with function code 4.</summary>
    InputRegister,

    /// <summary>Coils, read with function code 1.</summary>
    Coil,

    /// <summary>Discrete inputs, read with function code 2.</summary>
    DiscreteInput
}
```

`ModbusWordOrder.cs`:

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Register and byte order of 32-bit values. Registers are A B (first) and C D (second) on the wire.
/// </summary>
public enum ModbusWordOrder
{
    /// <summary>A B C D: the first register holds the high word.</summary>
    HighWordFirst,

    /// <summary>C D A B: the first register holds the low word.</summary>
    LowWordFirst,

    /// <summary>B A D C: high word first with the bytes of each register swapped.</summary>
    HighWordFirstByteSwapped,

    /// <summary>D C B A: low word first with the bytes of each register swapped.</summary>
    LowWordFirstByteSwapped
}
```

`ModbusAccess.cs`:

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Whether a mapping may be written. Only reads are supported in this version.
/// </summary>
public enum ModbusAccess
{
    /// <summary>The device accepts writes to this mapping.</summary>
    ReadWrite,

    /// <summary>The mapping must never be written.</summary>
    ReadOnly
}
```

`ModbusNotAvailableValue.cs`:

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// A raw bit pattern a device uses for "not available", mapped to <c>null</c>.
/// </summary>
public enum ModbusNotAvailableValue
{
    /// <summary>No pattern is treated as not available.</summary>
    None,

    /// <summary>0x7FFF for 16-bit values, 0x7FFFFFFF for 32-bit values.</summary>
    SignedMaximum,

    /// <summary>0x8000 for 16-bit values, 0x80000000 for 32-bit values.</summary>
    SignedMinimum,

    /// <summary>0xFFFF for 16-bit values, 0xFFFFFFFF for 32-bit values.</summary>
    UnsignedMaximum
}
```

- [ ] **Step 2: Provider interfaces**

`IModbusUnitIdProvider.cs`:

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Supplies the Modbus unit ID for the registers of this subject and its children.
/// </summary>
/// <remarks>Read when the connector builds its read plan on connect.</remarks>
public interface IModbusUnitIdProvider
{
    byte UnitId { get; }
}
```

`IModbusBaseAddressProvider.cs`:

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Supplies the address the register addresses of this subject are relative to. Not inherited by child subjects.
/// </summary>
/// <remarks>Read when the connector builds its read plan on connect.</remarks>
public interface IModbusBaseAddressProvider
{
    int BaseAddress { get; }
}
```

- [ ] **Step 3: Exceptions**

`ModbusConfigurationException.cs`:

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Thrown when a <see cref="Attributes.ModbusRegisterAttribute"/> mapping is invalid.
/// </summary>
public sealed class ModbusConfigurationException : Exception
{
    public ModbusConfigurationException(string message)
        : base(message)
    {
    }
}
```

`ModbusResponseException.cs`:

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Thrown when a device answers a request with a Modbus exception response, for example
/// 2 (illegal data address) for an unmapped register. The connection stays usable.
/// </summary>
public sealed class ModbusResponseException : Exception
{
    internal ModbusResponseException(int exceptionCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ExceptionCode = exceptionCode;
    }

    /// <summary>
    /// Gets the Modbus exception code.
    /// </summary>
    public int ExceptionCode { get; }
}
```

- [ ] **Step 4: Attributes**

`Attributes/ModbusRegisterAttribute.cs`:

```csharp
namespace Namotion.Interceptor.Modbus.Attributes;

/// <summary>
/// Maps a subject property to a Modbus register or bit. <see cref="Address"/> is relative to the declaring
/// subject's <see cref="IModbusBaseAddressProvider.BaseAddress"/>, or absolute when the subject has none.
/// </summary>
/// <remarks>Not sealed, so device libraries can derive attributes with preset values.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public class ModbusRegisterAttribute : Attribute
{
    public ModbusRegisterAttribute(int address, ModbusDataType dataType)
    {
        Address = address;
        DataType = dataType;
    }

    public int Address { get; }

    public ModbusDataType DataType { get; }

    public ModbusAddressSpace Space { get; init; } = ModbusAddressSpace.HoldingRegister;

    /// <summary>
    /// Gets the register order of 32-bit values. Ignored for other types.
    /// </summary>
    public ModbusWordOrder WordOrder { get; init; } = ModbusWordOrder.HighWordFirst;

    /// <summary>
    /// Gets the static factor the raw value is multiplied with. Requires a floating point or decimal property.
    /// </summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>
    /// Gets the name of an integer register property on the same subject holding a power-of-ten exponent:
    /// value = raw * 10^exponent. Mutually exclusive with <see cref="Scale"/>.
    /// </summary>
    public string? ScaleFactorProperty { get; init; }

    /// <summary>
    /// Gets the register count of <see cref="ModbusDataType.String"/> values. Must be 0 for other types.
    /// </summary>
    public int Length { get; init; }

    public ModbusAccess Access { get; init; } = ModbusAccess.ReadWrite;

    /// <summary>
    /// Gets the raw pattern that maps to <c>null</c>. Requires a nullable property.
    /// </summary>
    public ModbusNotAvailableValue NotAvailableValue { get; init; } = ModbusNotAvailableValue.None;
}
```

`Attributes/ModbusUnitIdAttribute.cs`:

```csharp
namespace Namotion.Interceptor.Modbus.Attributes;

/// <summary>
/// Sets the Modbus unit ID for the registers of this subject class and its children.
/// <see cref="IModbusUnitIdProvider"/> takes precedence when both are present.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class ModbusUnitIdAttribute : Attribute
{
    public ModbusUnitIdAttribute(byte unitId)
    {
        UnitId = unitId;
    }

    public byte UnitId { get; }
}
```

- [ ] **Step 5: Build**

Run: `dotnet build src/Namotion.Interceptor.Modbus`
Expected: `Build succeeded`, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Modbus
git commit -m "feat: add Modbus mapping attributes, enums and exceptions"
```

---

## Task 3: Register codec

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/Mapping/ModbusRegisterCodec.cs`
- Test: `src/Namotion.Interceptor.Modbus.Tests/Mapping/ModbusRegisterCodecTests.cs`

Raw input is always the wire bytes: two big-endian bytes per register, or one byte (0 or 1) for a bit.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace Namotion.Interceptor.Modbus.Tests.Mapping;

using Namotion.Interceptor.Modbus.Mapping;

public class ModbusRegisterCodecTests
{
    [Theory]
    [InlineData(ModbusDataType.Boolean, 0, 1)]
    [InlineData(ModbusDataType.U16, 0, 1)]
    [InlineData(ModbusDataType.S16, 0, 1)]
    [InlineData(ModbusDataType.U32, 0, 2)]
    [InlineData(ModbusDataType.S32, 0, 2)]
    [InlineData(ModbusDataType.F32, 0, 2)]
    [InlineData(ModbusDataType.String, 8, 8)]
    public void WhenGettingRegisterCount_ThenMatchesDataType(ModbusDataType dataType, int length, int expected)
    {
        // Act
        var count = ModbusRegisterCodec.GetRegisterCount(dataType, length);

        // Assert
        Assert.Equal(expected, count);
    }

    [Theory]
    [InlineData(ModbusDataType.U16, new byte[] { 0xFF, 0xFE }, 65534L)]
    [InlineData(ModbusDataType.S16, new byte[] { 0xFF, 0xFE }, -2L)]
    [InlineData(ModbusDataType.U32, new byte[] { 0xFF, 0xFF, 0xFF, 0xFE }, 4294967294L)]
    [InlineData(ModbusDataType.S32, new byte[] { 0xFF, 0xFF, 0xFF, 0xFE }, -2L)]
    [InlineData(ModbusDataType.Boolean, new byte[] { 1 }, 1L)]
    public void WhenReadingInteger_ThenSignIsExtendedPerDataType(ModbusDataType dataType, byte[] raw, long expected)
    {
        // Act
        var value = ModbusRegisterCodec.ReadInteger(raw, dataType, ModbusWordOrder.HighWordFirst);

        // Assert
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(ModbusWordOrder.HighWordFirst, new byte[] { 0x01, 0x02, 0x03, 0x04 })]
    [InlineData(ModbusWordOrder.LowWordFirst, new byte[] { 0x03, 0x04, 0x01, 0x02 })]
    [InlineData(ModbusWordOrder.HighWordFirstByteSwapped, new byte[] { 0x02, 0x01, 0x04, 0x03 })]
    [InlineData(ModbusWordOrder.LowWordFirstByteSwapped, new byte[] { 0x04, 0x03, 0x02, 0x01 })]
    public void WhenReadingU32_ThenWordOrderIsApplied(ModbusWordOrder wordOrder, byte[] raw)
    {
        // Act
        var value = ModbusRegisterCodec.ReadU32(raw, wordOrder);

        // Assert
        Assert.Equal(0x01020304u, value);
    }

    [Fact]
    public void WhenReadingSingle_ThenIeeeBitsAreDecoded()
    {
        // Arrange (1.5f is 0x3FC00000)
        byte[] raw = [0x3F, 0xC0, 0x00, 0x00];

        // Act
        var value = ModbusRegisterCodec.ReadSingle(raw, ModbusWordOrder.HighWordFirst);

        // Assert
        Assert.Equal(1.5f, value);
    }

    [Fact]
    public void WhenReadingS32WithLowWordFirst_ThenWordsAreSwappedBeforeSignExtension()
    {
        // Arrange (-2 is 0xFFFFFFFE, so the low word 0xFFFE comes first on the wire)
        byte[] raw = [0xFF, 0xFE, 0xFF, 0xFF];

        // Act
        var value = ModbusRegisterCodec.ReadInteger(raw, ModbusDataType.S32, ModbusWordOrder.LowWordFirst);

        // Assert
        Assert.Equal(-2L, value);
    }

    [Fact]
    public void WhenReadingSingleWithLowWordFirstByteSwapped_ThenIeeeBitsAreDecoded()
    {
        // Arrange (1.5f is 0x3FC00000, which is D C B A on the wire)
        byte[] raw = [0x00, 0x00, 0xC0, 0x3F];

        // Act
        var value = ModbusRegisterCodec.ReadSingle(raw, ModbusWordOrder.LowWordFirstByteSwapped);

        // Assert
        Assert.Equal(1.5f, value);
    }

    [Fact]
    public void WhenReadingString_ThenTrailingNullsAndSpacesAreTrimmed()
    {
        // Arrange
        byte[] raw = [(byte)'S', (byte)'o', (byte)'l', (byte)'a', (byte)'r', (byte)' ', 0, 0];

        // Act
        var value = ModbusRegisterCodec.ReadString(raw);

        // Assert
        Assert.Equal("Solar", value);
    }

    [Theory]
    [InlineData(ModbusDataType.U16, ModbusNotAvailableValue.SignedMaximum, new byte[] { 0x7F, 0xFF }, true)]
    [InlineData(ModbusDataType.S16, ModbusNotAvailableValue.SignedMaximum, new byte[] { 0x7F, 0xFE }, false)]
    [InlineData(ModbusDataType.S16, ModbusNotAvailableValue.SignedMinimum, new byte[] { 0x80, 0x00 }, true)]
    [InlineData(ModbusDataType.U16, ModbusNotAvailableValue.UnsignedMaximum, new byte[] { 0xFF, 0xFF }, true)]
    [InlineData(ModbusDataType.S32, ModbusNotAvailableValue.SignedMaximum, new byte[] { 0x7F, 0xFF, 0xFF, 0xFF }, true)]
    [InlineData(ModbusDataType.S32, ModbusNotAvailableValue.SignedMaximum, new byte[] { 0x00, 0x00, 0x7F, 0xFF }, false)]
    [InlineData(ModbusDataType.S32, ModbusNotAvailableValue.SignedMinimum, new byte[] { 0x80, 0x00, 0x00, 0x00 }, true)]
    [InlineData(ModbusDataType.U32, ModbusNotAvailableValue.UnsignedMaximum, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, true)]
    [InlineData(ModbusDataType.U16, ModbusNotAvailableValue.None, new byte[] { 0x7F, 0xFF }, false)]
    public void WhenCheckingNotAvailable_ThenPatternIsMatchedForTheWidth(
        ModbusDataType dataType, ModbusNotAvailableValue notAvailableValue, byte[] raw, bool expected)
    {
        // Act
        var isNotAvailable = ModbusRegisterCodec.IsNotAvailable(raw, dataType, ModbusWordOrder.HighWordFirst, notAvailableValue);

        // Assert
        Assert.Equal(expected, isNotAvailable);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusRegisterCodecTests"`
Expected: build error, `ModbusRegisterCodec` does not exist.

- [ ] **Step 3: Implement the codec**

```csharp
using System.Buffers.Binary;
using System.Text;

namespace Namotion.Interceptor.Modbus.Mapping;

internal static class ModbusRegisterCodec
{
    public static int GetRegisterCount(ModbusDataType dataType, int length) => dataType switch
    {
        ModbusDataType.Boolean or ModbusDataType.U16 or ModbusDataType.S16 => 1,
        ModbusDataType.U32 or ModbusDataType.S32 or ModbusDataType.F32 => 2,
        ModbusDataType.String => length,
        _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, null)
    };

    public static ushort ReadU16(ReadOnlySpan<byte> raw) => BinaryPrimitives.ReadUInt16BigEndian(raw);

    public static uint ReadU32(ReadOnlySpan<byte> raw, ModbusWordOrder wordOrder)
    {
        var first = BinaryPrimitives.ReadUInt16BigEndian(raw);
        var second = BinaryPrimitives.ReadUInt16BigEndian(raw[2..]);
        return wordOrder switch
        {
            ModbusWordOrder.HighWordFirst => ((uint)first << 16) | second,
            ModbusWordOrder.LowWordFirst => ((uint)second << 16) | first,
            ModbusWordOrder.HighWordFirstByteSwapped =>
                ((uint)BinaryPrimitives.ReverseEndianness(first) << 16) | BinaryPrimitives.ReverseEndianness(second),
            ModbusWordOrder.LowWordFirstByteSwapped =>
                ((uint)BinaryPrimitives.ReverseEndianness(second) << 16) | BinaryPrimitives.ReverseEndianness(first),
            _ => throw new ArgumentOutOfRangeException(nameof(wordOrder), wordOrder, null)
        };
    }

    public static long ReadInteger(ReadOnlySpan<byte> raw, ModbusDataType dataType, ModbusWordOrder wordOrder) => dataType switch
    {
        ModbusDataType.Boolean => raw[0] != 0 ? 1 : 0,
        ModbusDataType.U16 => ReadU16(raw),
        ModbusDataType.S16 => (short)ReadU16(raw),
        ModbusDataType.U32 => ReadU32(raw, wordOrder),
        ModbusDataType.S32 => (int)ReadU32(raw, wordOrder),
        _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, null)
    };

    public static float ReadSingle(ReadOnlySpan<byte> raw, ModbusWordOrder wordOrder)
        => BitConverter.UInt32BitsToSingle(ReadU32(raw, wordOrder));

    public static string ReadString(ReadOnlySpan<byte> raw)
    {
        var length = raw.Length;
        while (length > 0 && raw[length - 1] is 0x00 or 0x20)
        {
            length--;
        }

        return Encoding.ASCII.GetString(raw[..length]);
    }

    public static bool IsNotAvailable(
        ReadOnlySpan<byte> raw, ModbusDataType dataType, ModbusWordOrder wordOrder, ModbusNotAvailableValue notAvailableValue)
    {
        if (notAvailableValue == ModbusNotAvailableValue.None)
        {
            return false;
        }

        switch (dataType)
        {
            case ModbusDataType.U16 or ModbusDataType.S16:
            {
                var value = ReadU16(raw);
                return notAvailableValue switch
                {
                    ModbusNotAvailableValue.SignedMaximum => value == 0x7FFF,
                    ModbusNotAvailableValue.SignedMinimum => value == 0x8000,
                    ModbusNotAvailableValue.UnsignedMaximum => value == 0xFFFF,
                    _ => false
                };
            }
            case ModbusDataType.U32 or ModbusDataType.S32:
            {
                var value = ReadU32(raw, wordOrder);
                return notAvailableValue switch
                {
                    ModbusNotAvailableValue.SignedMaximum => value == 0x7FFFFFFFu,
                    ModbusNotAvailableValue.SignedMinimum => value == 0x80000000u,
                    ModbusNotAvailableValue.UnsignedMaximum => value == 0xFFFFFFFFu,
                    _ => false
                };
            }
            default:
                return false;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusRegisterCodecTests"`
Expected: PASS, 29 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Modbus/Mapping src/Namotion.Interceptor.Modbus.Tests/Mapping
git commit -m "feat: decode Modbus register values"
```

---

## Task 4: Value converters

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/Mapping/ModbusValueConverters.cs`
- Test: `src/Namotion.Interceptor.Modbus.Tests/Mapping/ModbusValueConvertersTests.cs`

A converter is created once per property when the read plan is built, validates the mapping, and turns raw bytes into the boxed CLR value on every change. `scaleFactorExponent` is only used when `ScaleFactorProperty` is set.

- [ ] **Step 1: Write the failing tests**

```csharp
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Modbus.Mapping;

namespace Namotion.Interceptor.Modbus.Tests.Mapping;

public class ModbusValueConvertersTests
{
    private enum Mode : ushort
    {
        Off = 0,
        On = 1
    }

    [Flags]
    private enum Features : ushort
    {
        None = 0,
        First = 1,
        Second = 2
    }

    private static object? Convert(ModbusRegisterAttribute attribute, Type propertyType, byte[] raw, int exponent = 0)
        => ModbusValueConverters.Create(attribute, propertyType, "Test.Property")(raw, exponent);

    [Fact]
    public void WhenScalingIntoDecimal_ThenResultIsExact()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.S16) { Scale = 0.1 }, typeof(decimal?), [0x00, 0xEA]);

        // Assert
        Assert.Equal(23.4m, value);
    }

    [Fact]
    public void WhenScalingNegativeIntoDouble_ThenSignIsKept()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.S16) { Scale = 0.5 }, typeof(double), [0xFF, 0xFE]);

        // Assert
        Assert.Equal(-1.0, value);
    }

    [Theory]
    [InlineData(-2, 1.23)]
    [InlineData(0, 123)]
    [InlineData(1, 1230)]
    public void WhenUsingDynamicScaleFactor_ThenPowerOfTenIsApplied(int exponent, double expected)
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16) { ScaleFactorProperty = "Factor" },
            typeof(decimal?), [0x00, 0x7B], exponent);

        // Assert
        Assert.Equal((decimal)expected, value);
    }

    [Fact]
    public void WhenTargetIsEnum_ThenRawValueMapsToMember()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16), typeof(Mode?), [0x00, 0x01]);

        // Assert
        Assert.Equal(Mode.On, value);
    }

    [Fact]
    public void WhenTargetIsFlagsEnum_ThenBitsArePassedThrough()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16), typeof(Features), [0x00, 0x03]);

        // Assert
        Assert.Equal(Features.First | Features.Second, value);
    }

    [Fact]
    public void WhenTargetIsBoolFromRegister_ThenNonZeroIsTrue()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.U16), typeof(bool?), [0x00, 0x02]);

        // Assert
        Assert.Equal(true, value);
    }

    [Fact]
    public void WhenDataTypeIsBoolean_ThenBitIsReturned()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.Boolean) { Space = ModbusAddressSpace.Coil }, typeof(bool), [1]);

        // Assert
        Assert.Equal(true, value);
    }

    [Fact]
    public void WhenTargetIsIntegral_ThenValueIsConverted()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.S32), typeof(long?), [0xFF, 0xFF, 0xFF, 0xFE]);

        // Assert
        Assert.Equal(-2L, value);
    }

    [Fact]
    public void WhenDataTypeIsF32_ThenFloatIsReturned()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.F32), typeof(float), [0x3F, 0xC0, 0x00, 0x00]);

        // Assert
        Assert.Equal(1.5f, value);
    }

    [Fact]
    public void WhenDataTypeIsString_ThenTextIsReturned()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.String) { Length = 2 }, typeof(string), [(byte)'A', (byte)'B', 0, 0]);

        // Assert
        Assert.Equal("AB", value);
    }

    [Fact]
    public void WhenRawMatchesNotAvailableValue_ThenNullIsReturned()
    {
        // Act
        var value = Convert(new ModbusRegisterAttribute(0, ModbusDataType.S16) { Scale = 0.1, NotAvailableValue = ModbusNotAvailableValue.SignedMaximum },
            typeof(decimal?), [0x7F, 0xFF]);

        // Assert
        Assert.Null(value);
    }

    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(Mode))]
    public void WhenScaledTargetIsIntegralOrEnum_ThenConfigurationExceptionIsThrown(Type propertyType)
    {
        // Act & Assert
        Assert.Throws<ModbusConfigurationException>(() =>
            ModbusValueConverters.Create(new ModbusRegisterAttribute(0, ModbusDataType.U16) { Scale = 0.1 }, propertyType, "Test.Property"));
    }

    [Theory]
    [InlineData(ModbusDataType.U16, typeof(short))]
    [InlineData(ModbusDataType.S16, typeof(ushort))]
    [InlineData(ModbusDataType.U32, typeof(int))]
    [InlineData(ModbusDataType.S32, typeof(uint))]
    [InlineData(ModbusDataType.U16, typeof(byte))]
    public void WhenIntegralTargetCannotHoldTheRange_ThenConfigurationExceptionIsThrown(ModbusDataType dataType, Type propertyType)
    {
        // Act & Assert
        Assert.Throws<ModbusConfigurationException>(() =>
            ModbusValueConverters.Create(new ModbusRegisterAttribute(0, dataType), propertyType, "Test.Property"));
    }

    [Fact]
    public void WhenNotAvailableValueTargetsNonNullable_ThenConfigurationExceptionIsThrown()
    {
        // Act & Assert
        Assert.Throws<ModbusConfigurationException>(() =>
            ModbusValueConverters.Create(
                new ModbusRegisterAttribute(0, ModbusDataType.U16) { NotAvailableValue = ModbusNotAvailableValue.SignedMaximum },
                typeof(int), "Test.Property"));
    }

    [Theory]
    [InlineData(ModbusDataType.String, typeof(int))]
    [InlineData(ModbusDataType.Boolean, typeof(int))]
    [InlineData(ModbusDataType.U16, typeof(string))]
    [InlineData(ModbusDataType.F32, typeof(int))]
    public void WhenClrTypeDoesNotFitDataType_ThenConfigurationExceptionIsThrown(ModbusDataType dataType, Type propertyType)
    {
        // Act & Assert
        Assert.Throws<ModbusConfigurationException>(() =>
            ModbusValueConverters.Create(new ModbusRegisterAttribute(0, dataType) { Length = dataType == ModbusDataType.String ? 1 : 0 },
                propertyType, "Test.Property"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusValueConvertersTests"`
Expected: build error, `ModbusValueConverters` does not exist.

- [ ] **Step 3: Implement the converters**

```csharp
using Namotion.Interceptor.Modbus.Attributes;

namespace Namotion.Interceptor.Modbus.Mapping;

/// <summary>
/// Converts the raw wire bytes of one mapping into the boxed property value, or <c>null</c> for a not-available value.
/// </summary>
internal delegate object? ModbusValueReader(ReadOnlySpan<byte> raw, int scaleFactorExponent);

internal static class ModbusValueConverters
{
    private static readonly object True = true;
    private static readonly object False = false;

    public static ModbusValueReader Create(ModbusRegisterAttribute attribute, Type propertyType, string propertyPath)
    {
        var underlyingType = Nullable.GetUnderlyingType(propertyType);
        var targetType = underlyingType ?? propertyType;
        var isNullable = underlyingType is not null || !propertyType.IsValueType;

        var dataType = attribute.DataType;
        var wordOrder = attribute.WordOrder;
        var notAvailableValue = attribute.NotAvailableValue;
        var hasDynamicScale = attribute.ScaleFactorProperty is not null;
        var isScaled = hasDynamicScale || attribute.Scale != 1.0;

        if (notAvailableValue != ModbusNotAvailableValue.None)
        {
            if (!isNullable)
            {
                throw Error(propertyPath, "NotAvailableValue requires a nullable property type.");
            }

            if (dataType is ModbusDataType.Boolean or ModbusDataType.F32 or ModbusDataType.String)
            {
                throw Error(propertyPath, $"NotAvailableValue is not supported for {dataType}.");
            }
        }

        switch (dataType)
        {
            case ModbusDataType.String:
                RequireTarget(propertyPath, targetType == typeof(string), dataType, targetType);
                RequireUnscaled(propertyPath, isScaled, targetType);
                return static (raw, _) => ModbusRegisterCodec.ReadString(raw);

            case ModbusDataType.Boolean:
                RequireTarget(propertyPath, targetType == typeof(bool), dataType, targetType);
                RequireUnscaled(propertyPath, isScaled, targetType);
                return static (raw, _) => raw[0] != 0 ? True : False;

            case ModbusDataType.F32:
                return CreateFloatReader(attribute, targetType, propertyPath, wordOrder, hasDynamicScale, isScaled);

            default:
                return CreateIntegerReader(attribute, targetType, propertyPath, dataType, wordOrder, notAvailableValue, hasDynamicScale, isScaled);
        }
    }

    private static ModbusValueReader CreateFloatReader(
        ModbusRegisterAttribute attribute, Type targetType, string propertyPath,
        ModbusWordOrder wordOrder, bool hasDynamicScale, bool isScaled)
    {
        var staticScale = attribute.Scale;
        if (targetType == typeof(float))
        {
            return (raw, exponent) =>
            {
                var value = ModbusRegisterCodec.ReadSingle(raw, wordOrder);
                return isScaled ? (float)(value * GetDoubleScale(hasDynamicScale, staticScale, exponent)) : value;
            };
        }

        if (targetType == typeof(double))
        {
            return (raw, exponent) => ModbusRegisterCodec.ReadSingle(raw, wordOrder) * GetDoubleScale(hasDynamicScale, staticScale, exponent);
        }

        if (targetType == typeof(decimal))
        {
            var decimalScale = (decimal)staticScale;
            return (raw, exponent) => (decimal)ModbusRegisterCodec.ReadSingle(raw, wordOrder) * GetDecimalScale(hasDynamicScale, decimalScale, exponent);
        }

        throw Error(propertyPath, $"F32 requires a float, double or decimal property, not {targetType.Name}.");
    }

    private static ModbusValueReader CreateIntegerReader(
        ModbusRegisterAttribute attribute, Type targetType, string propertyPath, ModbusDataType dataType,
        ModbusWordOrder wordOrder, ModbusNotAvailableValue notAvailableValue, bool hasDynamicScale, bool isScaled)
    {
        var staticScale = attribute.Scale;

        if (targetType == typeof(decimal))
        {
            var decimalScale = (decimal)staticScale;
            return (raw, exponent) => IsNotAvailable(raw)
                ? null
                : ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder) * GetDecimalScale(hasDynamicScale, decimalScale, exponent);
        }

        if (targetType == typeof(double))
        {
            return (raw, exponent) => IsNotAvailable(raw)
                ? null
                : ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder) * GetDoubleScale(hasDynamicScale, staticScale, exponent);
        }

        if (targetType == typeof(float))
        {
            return (raw, exponent) => IsNotAvailable(raw)
                ? null
                : (float)(ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder) * GetDoubleScale(hasDynamicScale, staticScale, exponent));
        }

        RequireUnscaled(propertyPath, isScaled, targetType);

        if (targetType == typeof(bool))
        {
            return (raw, _) => IsNotAvailable(raw)
                ? null
                : ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder) != 0 ? True : False;
        }

        if (targetType.IsEnum)
        {
            RequireIntegralRange(propertyPath, dataType, Enum.GetUnderlyingType(targetType));
            return (raw, _) => IsNotAvailable(raw)
                ? null
                : Enum.ToObject(targetType, ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder));
        }

        var typeCode = Type.GetTypeCode(targetType);
        RequireIntegralRange(propertyPath, dataType, targetType);
        return (raw, _) => IsNotAvailable(raw)
            ? null
            : BoxIntegral(ModbusRegisterCodec.ReadInteger(raw, dataType, wordOrder), typeCode);

        bool IsNotAvailable(ReadOnlySpan<byte> raw)
            => ModbusRegisterCodec.IsNotAvailable(raw, dataType, wordOrder, notAvailableValue);
    }

    private static object BoxIntegral(long value, TypeCode typeCode) => typeCode switch
    {
        TypeCode.Byte => (byte)value,
        TypeCode.SByte => (sbyte)value,
        TypeCode.Int16 => (short)value,
        TypeCode.UInt16 => (ushort)value,
        TypeCode.Int32 => (int)value,
        TypeCode.UInt32 => (uint)value,
        TypeCode.Int64 => value,
        TypeCode.UInt64 => (ulong)value,
        _ => throw new ArgumentOutOfRangeException(nameof(typeCode), typeCode, null)
    };

    private static void RequireIntegralRange(string propertyPath, ModbusDataType dataType, Type targetType)
    {
        var (dataMinimum, dataMaximum) = dataType switch
        {
            ModbusDataType.U16 => (0m, (decimal)ushort.MaxValue),
            ModbusDataType.S16 => ((decimal)short.MinValue, (decimal)short.MaxValue),
            ModbusDataType.U32 => (0m, (decimal)uint.MaxValue),
            ModbusDataType.S32 => ((decimal)int.MinValue, (decimal)int.MaxValue),
            _ => throw Error(propertyPath, $"{dataType} cannot be converted to {targetType.Name}.")
        };

        var (targetMinimum, targetMaximum) = Type.GetTypeCode(targetType) switch
        {
            TypeCode.Byte => ((decimal)byte.MinValue, (decimal)byte.MaxValue),
            TypeCode.SByte => ((decimal)sbyte.MinValue, (decimal)sbyte.MaxValue),
            TypeCode.Int16 => ((decimal)short.MinValue, (decimal)short.MaxValue),
            TypeCode.UInt16 => ((decimal)ushort.MinValue, (decimal)ushort.MaxValue),
            TypeCode.Int32 => ((decimal)int.MinValue, (decimal)int.MaxValue),
            TypeCode.UInt32 => ((decimal)uint.MinValue, (decimal)uint.MaxValue),
            TypeCode.Int64 => ((decimal)long.MinValue, (decimal)long.MaxValue),
            TypeCode.UInt64 => ((decimal)ulong.MinValue, (decimal)ulong.MaxValue),
            _ => throw Error(propertyPath, $"{dataType} cannot be converted to {targetType.Name}.")
        };

        if (dataMinimum < targetMinimum || dataMaximum > targetMaximum)
        {
            throw Error(propertyPath, $"{targetType.Name} cannot hold every {dataType} value.");
        }
    }

    private static double GetDoubleScale(bool hasDynamicScale, double staticScale, int exponent)
        => hasDynamicScale ? Math.Pow(10, exponent) : staticScale;

    private static decimal GetDecimalScale(bool hasDynamicScale, decimal staticScale, int exponent)
    {
        if (!hasDynamicScale)
        {
            return staticScale;
        }

        var power = 1m;
        for (var index = 0; index < Math.Abs(exponent); index++)
        {
            power *= 10m;
        }

        return exponent < 0 ? 1m / power : power;
    }

    private static void RequireTarget(string propertyPath, bool condition, ModbusDataType dataType, Type targetType)
    {
        if (!condition)
        {
            throw Error(propertyPath, $"{dataType} cannot be converted to {targetType.Name}.");
        }
    }

    private static void RequireUnscaled(string propertyPath, bool isScaled, Type targetType)
    {
        if (isScaled)
        {
            throw Error(propertyPath, $"Scaling requires a float, double or decimal property, not {targetType.Name}.");
        }
    }

    private static ModbusConfigurationException Error(string propertyPath, string message)
        => new($"Invalid Modbus mapping on {propertyPath}: {message}");
}
```


- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusValueConvertersTests"`
Expected: PASS, 25 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Modbus/Mapping src/Namotion.Interceptor.Modbus.Tests/Mapping
git commit -m "feat: convert Modbus values to property types"
```

---

## Task 5: Client configuration

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/ModbusClientConfiguration.cs`
- Test: `src/Namotion.Interceptor.Modbus.Tests/ModbusClientConfigurationTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
namespace Namotion.Interceptor.Modbus.Tests;

public class ModbusClientConfigurationTests
{
    [Fact]
    public void WhenConfigurationUsesDefaults_ThenValidationPasses()
    {
        // Arrange
        var configuration = new ModbusClientConfiguration { Host = "192.168.1.10" };

        // Act
        configuration.Validate();

        // Assert
        Assert.Equal(502, configuration.Port);
        Assert.Equal(1, configuration.UnitId);
        Assert.Equal(TimeSpan.FromSeconds(2), configuration.PollingInterval);
        Assert.Equal(0, configuration.MaximumRegisterGap);
    }

    public static TheoryData<ModbusClientConfiguration> InvalidConfigurations => new()
    {
        new ModbusClientConfiguration { Host = " " },
        new ModbusClientConfiguration { Host = "host", Port = 0 },
        new ModbusClientConfiguration { Host = "host", Port = 65536 },
        new ModbusClientConfiguration { Host = "host", PollingInterval = TimeSpan.Zero },
        new ModbusClientConfiguration { Host = "host", RequestTimeout = TimeSpan.Zero },
        new ModbusClientConfiguration { Host = "host", RetryTime = TimeSpan.Zero },
        new ModbusClientConfiguration { Host = "host", BufferTime = TimeSpan.FromMilliseconds(-1) },
        new ModbusClientConfiguration { Host = "host", MaximumRegisterGap = -1 },
        new ModbusClientConfiguration { Host = "host", MaximumRegisterGap = 125 },
    };

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void WhenConfigurationIsInvalid_ThenValidateThrows(ModbusClientConfiguration configuration)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(configuration.Validate);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusClientConfigurationTests"`
Expected: build error, `ModbusClientConfiguration` does not exist.

- [ ] **Step 3: Implement the configuration**

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Configuration of a Modbus TCP client source.
/// </summary>
public sealed class ModbusClientConfiguration
{
    /// <summary>
    /// Gets the host name or IP address of the Modbus TCP server.
    /// </summary>
    public required string Host { get; init; }

    public int Port { get; init; } = 502;

    /// <summary>
    /// Gets the unit ID used for registers of subjects without <see cref="IModbusUnitIdProvider"/> or
    /// <see cref="Attributes.ModbusUnitIdAttribute"/> in their ancestry.
    /// </summary>
    public byte UnitId { get; init; } = 1;

    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets the timeout for connecting and for each request. A timeout is treated as a lost connection.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the delay before reconnecting after a failure.
    /// </summary>
    public TimeSpan RetryTime { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan BufferTime { get; init; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Gets how many unmapped registers or bits a read request may span to merge neighbouring mappings.
    /// 0 reads strictly contiguous blocks, which is safe for devices that reject reads of unmapped addresses.
    /// </summary>
    public int MaximumRegisterGap { get; init; }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when a value is out of range.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("Host must be specified.", nameof(Host));
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentException($"Port must be between 1 and 65535, got: {Port}", nameof(Port));
        }

        if (PollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException($"PollingInterval must be positive, got: {PollingInterval}", nameof(PollingInterval));
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException($"RequestTimeout must be positive, got: {RequestTimeout}", nameof(RequestTimeout));
        }

        if (RetryTime <= TimeSpan.Zero)
        {
            throw new ArgumentException($"RetryTime must be positive, got: {RetryTime}", nameof(RetryTime));
        }

        if (BufferTime < TimeSpan.Zero)
        {
            throw new ArgumentException($"BufferTime must not be negative, got: {BufferTime}", nameof(BufferTime));
        }

        if (MaximumRegisterGap is < 0 or > 124)
        {
            throw new ArgumentException($"MaximumRegisterGap must be between 0 and 124, got: {MaximumRegisterGap}", nameof(MaximumRegisterGap));
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusClientConfigurationTests"`
Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Modbus/ModbusClientConfiguration.cs src/Namotion.Interceptor.Modbus.Tests/ModbusClientConfigurationTests.cs
git commit -m "feat: add Modbus client configuration"
```

---
## Task 6: Register bindings and the resolver

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/Mapping/ModbusRegisterBinding.cs`
- Create: `src/Namotion.Interceptor.Modbus/Mapping/ModbusRegisterResolver.cs`
- Test: `src/Namotion.Interceptor.Modbus.Tests/Mapping/ModbusRegisterResolverTests.cs`

The resolver walks the root subtree through the registry (not the whole context), so a shared context with other devices is not picked up.

- [ ] **Step 1: Write the failing tests**

```csharp
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Modbus.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Modbus.Tests.Mapping;

public partial class ModbusRegisterResolverTests
{
    [InterceptorSubject]
    public partial class ResolverRoot
    {
        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? Value { get; set; }

        [ModbusRegister(1, ModbusDataType.S16, ScaleFactorProperty = nameof(PowerScaleFactor))]
        public partial decimal? Power { get; set; }

        [ModbusRegister(2, ModbusDataType.S16)]
        public partial short? PowerScaleFactor { get; set; }

        public partial ResolverChild? Child { get; set; }

        public partial ResolverUnit? Unit { get; set; }
    }

    [InterceptorSubject]
    public partial class ResolverChild : IModbusBaseAddressProvider
    {
        public int BaseAddress { get; init; }

        [ModbusRegister(5, ModbusDataType.U16, Space = ModbusAddressSpace.InputRegister)]
        public partial int? Value { get; set; }
    }

    [ModbusUnitId(7)]
    [InterceptorSubject]
    public partial class ResolverUnit
    {
        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? Value { get; set; }

        public partial ResolverChild? Nested { get; set; }
    }

    [ModbusUnitId(3)]
    [InterceptorSubject]
    public partial class ProviderWinsSubject : IModbusUnitIdProvider
    {
        public byte UnitId => 9;

        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class ScaleAndScaleFactorSubject
    {
        [ModbusRegister(0, ModbusDataType.U16, Scale = 0.1, ScaleFactorProperty = nameof(Factor))]
        public partial decimal? Value { get; set; }

        [ModbusRegister(1, ModbusDataType.S16)]
        public partial short? Factor { get; set; }
    }

    [InterceptorSubject]
    public partial class MissingScaleFactorSubject
    {
        [ModbusRegister(0, ModbusDataType.U16, ScaleFactorProperty = "Missing")]
        public partial decimal? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class StringWithoutLengthSubject
    {
        [ModbusRegister(0, ModbusDataType.String)]
        public partial string? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class LengthOnIntegerSubject
    {
        [ModbusRegister(0, ModbusDataType.U16, Length = 2)]
        public partial int? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class BooleanInRegisterSubject
    {
        [ModbusRegister(0, ModbusDataType.Boolean)]
        public partial bool? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class IntegerInCoilSubject
    {
        [ModbusRegister(0, ModbusDataType.U16, Space = ModbusAddressSpace.Coil)]
        public partial int? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class AddressOverflowSubject
    {
        [ModbusRegister(65535, ModbusDataType.U32)]
        public partial long? Value { get; set; }
    }

    [InterceptorSubject]
    public partial class NegativeAddressSubject
    {
        [ModbusRegister(-1, ModbusDataType.U16)]
        public partial int? Value { get; set; }
    }

    public sealed class PresetRegisterAttribute : ModbusRegisterAttribute
    {
        public PresetRegisterAttribute(int address)
            : base(address, ModbusDataType.S16)
        {
            Space = ModbusAddressSpace.InputRegister;
            NotAvailableValue = ModbusNotAvailableValue.SignedMaximum;
        }
    }

    [InterceptorSubject]
    public partial class PresetSubject
    {
        [PresetRegister(4)]
        public partial decimal? Value { get; set; }
    }

    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();

    private static ModbusRegisterBinding Find(IEnumerable<ModbusRegisterBinding> bindings, IInterceptorSubject subject, string propertyName)
        => bindings.Single(binding => ReferenceEquals(binding.Property.Subject, subject) && binding.Property.Name == propertyName);

    [Fact]
    public void WhenSubjectTreeHasRegisters_ThenBindingsHaveAbsoluteAddressesAndUnitIds()
    {
        // Arrange
        var root = new ResolverRoot(CreateContext());
        var child = new ResolverChild { BaseAddress = 100 };
        var nested = new ResolverChild { BaseAddress = 200 };
        var unit = new ResolverUnit { Nested = nested };
        root.Child = child;
        root.Unit = unit;

        // Act
        var bindings = ModbusRegisterResolver.Resolve(root, defaultUnitId: 1, excludedProperties: new HashSet<PropertyReference>());

        // Assert
        Assert.Equal(6, bindings.Count);
        var rootBinding = Find(bindings, root, nameof(ResolverRoot.Value));
        Assert.Equal((byte)1, rootBinding.UnitId);
        Assert.Equal(0, rootBinding.Address);
        var childBinding = Find(bindings, child, nameof(ResolverChild.Value));
        Assert.Equal((byte)1, childBinding.UnitId);
        Assert.Equal(105, childBinding.Address);
        Assert.Equal(ModbusAddressSpace.InputRegister, childBinding.Space);
        Assert.Equal((byte)7, Find(bindings, unit, nameof(ResolverUnit.Value)).UnitId);
        var nestedBinding = Find(bindings, nested, nameof(ResolverChild.Value));
        Assert.Equal((byte)7, nestedBinding.UnitId);
        Assert.Equal(205, nestedBinding.Address);
    }

    [Fact]
    public void WhenScaleFactorPropertyIsSet_ThenBindingIsLinked()
    {
        // Arrange
        var root = new ResolverRoot(CreateContext());

        // Act
        var bindings = ModbusRegisterResolver.Resolve(root, 1, new HashSet<PropertyReference>());

        // Assert
        var power = Find(bindings, root, nameof(ResolverRoot.Power));
        Assert.Same(Find(bindings, root, nameof(ResolverRoot.PowerScaleFactor)), power.ScaleFactor);
    }

    [Fact]
    public void WhenPropertyIsExcluded_ThenNoBindingIsCreated()
    {
        // Arrange
        var root = new ResolverRoot(CreateContext());
        var excluded = new HashSet<PropertyReference> { new(root, nameof(ResolverRoot.Value)) };

        // Act
        var bindings = ModbusRegisterResolver.Resolve(root, 1, excluded);

        // Assert
        Assert.DoesNotContain(bindings, binding => binding.Property.Name == nameof(ResolverRoot.Value));
    }

    [Fact]
    public void WhenUnitIdProviderAndAttributeArePresent_ThenProviderWins()
    {
        // Arrange
        var subject = new ProviderWinsSubject(CreateContext());

        // Act
        var bindings = ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>());

        // Assert
        Assert.Equal((byte)9, Assert.Single(bindings).UnitId);
    }

    [Fact]
    public void WhenAttributeIsDerivedWithPresets_ThenPresetsApply()
    {
        // Arrange
        var subject = new PresetSubject(CreateContext());

        // Act
        var bindings = ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>());

        // Assert
        var binding = Assert.Single(bindings);
        Assert.Equal(4, binding.Address);
        Assert.Equal(ModbusAddressSpace.InputRegister, binding.Space);
        Assert.Null(binding.Reader(new byte[] { 0x7F, 0xFF }, 0));
    }

    public static TheoryData<Func<IInterceptorSubjectContext, IInterceptorSubject>> InvalidSubjects => new()
    {
        context => new ScaleAndScaleFactorSubject(context),
        context => new MissingScaleFactorSubject(context),
        context => new StringWithoutLengthSubject(context),
        context => new LengthOnIntegerSubject(context),
        context => new BooleanInRegisterSubject(context),
        context => new IntegerInCoilSubject(context),
        context => new AddressOverflowSubject(context),
        context => new NegativeAddressSubject(context),
    };

    [Theory]
    [MemberData(nameof(InvalidSubjects))]
    public void WhenMappingIsInvalid_ThenConfigurationExceptionIsThrown(Func<IInterceptorSubjectContext, IInterceptorSubject> createSubject)
    {
        // Arrange
        var subject = createSubject(CreateContext());

        // Act & Assert
        Assert.Throws<ModbusConfigurationException>(() => ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>()));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusRegisterResolverTests"`
Expected: build error, `ModbusRegisterResolver` does not exist.

- [ ] **Step 3: Implement the binding**

`Mapping/ModbusRegisterBinding.cs`:

```csharp
using Namotion.Interceptor.Modbus.Attributes;

namespace Namotion.Interceptor.Modbus.Mapping;

/// <summary>
/// One resolved <see cref="ModbusRegisterAttribute"/> property plus its poll-cycle state.
/// </summary>
internal sealed class ModbusRegisterBinding
{
    private int _reapplyRequested;

    public ModbusRegisterBinding(
        PropertyReference property, string path, byte unitId, int address,
        ModbusRegisterAttribute attribute, ModbusValueReader reader)
    {
        Property = property;
        Path = path;
        UnitId = unitId;
        Address = address;
        Attribute = attribute;
        Reader = reader;
        Count = ModbusRegisterCodec.GetRegisterCount(attribute.DataType, attribute.Length);
        IsBitSpace = attribute.Space is ModbusAddressSpace.Coil or ModbusAddressSpace.DiscreteInput;

        var rawLength = IsBitSpace ? 1 : Count * 2;
        CurrentRaw = new byte[rawLength];
        LastRaw = new byte[rawLength];
    }

    public PropertyReference Property { get; }

    public string Path { get; }

    public byte UnitId { get; }

    public int Address { get; }

    /// <summary>
    /// Gets the number of registers, or 1 for a bit.
    /// </summary>
    public int Count { get; }

    public bool IsBitSpace { get; }

    public ModbusAddressSpace Space => Attribute.Space;

    public ModbusRegisterAttribute Attribute { get; }

    public ModbusValueReader Reader { get; }

    public ModbusRegisterBinding? ScaleFactor { get; set; }

    // Poll-cycle state: only the read and the apply of one cycle touch it, and the source runs one cycle at a time.
    public byte[] CurrentRaw { get; }

    public byte[] LastRaw { get; }

    public bool HasCurrent { get; set; }

    public bool HasLast { get; set; }

    public bool ChangedThisCycle { get; set; }

    public bool IsUnavailable { get; set; }

    /// <summary>
    /// Gets or sets whether this binding is read in a request of its own, set after a request spanning it was
    /// rejected. Holds until the next connect creates new bindings.
    /// </summary>
    public bool IsIsolated { get; set; }

    // Requested from the change queue thread, consumed by the poll loop.
    public void RequestReapply() => Volatile.Write(ref _reapplyRequested, 1);

    public bool ConsumeReapplyRequest() => Interlocked.Exchange(ref _reapplyRequested, 0) == 1;
}
```

- [ ] **Step 4: Implement the resolver**

`Mapping/ModbusRegisterResolver.cs`:

```csharp
using System.Reflection;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Modbus.Mapping;

internal static class ModbusRegisterResolver
{
    public static List<ModbusRegisterBinding> Resolve(
        IInterceptorSubject root, byte defaultUnitId, IReadOnlySet<PropertyReference> excludedProperties)
    {
        var registeredRoot = root.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("The root subject is not registered. Add WithRegistry() to the subject context.");

        var bindings = new List<ModbusRegisterBinding>();
        Walk(registeredRoot, defaultUnitId, excludedProperties, bindings, []);
        LinkScaleFactors(bindings);
        return bindings;
    }

    private static void Walk(
        RegisteredSubject subject, byte inheritedUnitId, IReadOnlySet<PropertyReference> excludedProperties,
        List<ModbusRegisterBinding> bindings, HashSet<RegisteredSubject> visited)
    {
        if (!visited.Add(subject))
        {
            return;
        }

        var unitId = GetUnitId(subject.Subject) ?? inheritedUnitId;
        var baseAddress = subject.Subject is IModbusBaseAddressProvider provider ? provider.BaseAddress : 0;

        foreach (var property in subject.Properties)
        {
            var attribute = GetRegisterAttribute(property);
            if (attribute is not null && !excludedProperties.Contains(property.Reference))
            {
                bindings.Add(CreateBinding(property, attribute, unitId, baseAddress));
            }

            foreach (var child in property.Children)
            {
                if (child.Subject.TryGetRegisteredSubject() is { } registeredChild)
                {
                    Walk(registeredChild, unitId, excludedProperties, bindings, visited);
                }
            }
        }
    }

    private static byte? GetUnitId(IInterceptorSubject subject)
    {
        if (subject is IModbusUnitIdProvider provider)
        {
            return provider.UnitId;
        }

        return subject.GetType().GetCustomAttribute<ModbusUnitIdAttribute>(inherit: true)?.UnitId;
    }

    private static ModbusRegisterAttribute? GetRegisterAttribute(RegisteredSubjectProperty property)
    {
        foreach (var attribute in property.ReflectionAttributes)
        {
            if (attribute is ModbusRegisterAttribute registerAttribute)
            {
                return registerAttribute;
            }
        }

        return null;
    }

    private static ModbusRegisterBinding CreateBinding(
        RegisteredSubjectProperty property, ModbusRegisterAttribute attribute, byte unitId, int baseAddress)
    {
        var path = $"{property.Subject.GetType().Name}.{property.Name}";
        var dataType = attribute.DataType;

        var isBitSpace = attribute.Space is ModbusAddressSpace.Coil or ModbusAddressSpace.DiscreteInput;
        if (isBitSpace && dataType != ModbusDataType.Boolean)
        {
            throw Error(path, $"{attribute.Space} requires the Boolean data type.");
        }

        if (!isBitSpace && dataType == ModbusDataType.Boolean)
        {
            throw Error(path, "Boolean requires the Coil or DiscreteInput space.");
        }

        if (dataType == ModbusDataType.String)
        {
            if (attribute.Length is < 1 or > 125)
            {
                throw Error(path, "String requires a Length between 1 and 125 registers.");
            }
        }
        else if (attribute.Length != 0)
        {
            throw Error(path, "Length is only valid for String.");
        }

        if (attribute.ScaleFactorProperty is not null && attribute.Scale != 1.0)
        {
            throw Error(path, "Scale and ScaleFactorProperty are mutually exclusive.");
        }

        if (!double.IsFinite(attribute.Scale) || attribute.Scale == 0)
        {
            throw Error(path, "Scale must be a finite, non-zero number.");
        }

        var address = baseAddress + attribute.Address;
        var count = ModbusRegisterCodec.GetRegisterCount(dataType, attribute.Length);
        if (attribute.Address < 0 || address < 0 || address + count - 1 > 65535)
        {
            throw Error(path, $"Address {address} with {count} register(s) is outside 0 to 65535.");
        }

        var reader = ModbusValueConverters.Create(attribute, property.Type, path);
        return new ModbusRegisterBinding(property.Reference, path, unitId, address, attribute, reader);
    }

    private static void LinkScaleFactors(List<ModbusRegisterBinding> bindings)
    {
        foreach (var binding in bindings)
        {
            var name = binding.Attribute.ScaleFactorProperty;
            if (name is null)
            {
                continue;
            }

            ModbusRegisterBinding? scaleFactor = null;
            foreach (var candidate in bindings)
            {
                if (ReferenceEquals(candidate.Property.Subject, binding.Property.Subject) && candidate.Property.Name == name)
                {
                    scaleFactor = candidate;
                    break;
                }
            }

            if (scaleFactor is null || scaleFactor.Attribute.DataType is not (ModbusDataType.U16 or ModbusDataType.S16))
            {
                throw Error(binding.Path,
                    $"ScaleFactorProperty '{name}' must name a U16 or S16 register property on the same subject that is not excluded.");
            }

            binding.ScaleFactor = scaleFactor;
        }
    }

    private static ModbusConfigurationException Error(string path, string message)
        => new($"Invalid Modbus mapping on {path}: {message}");
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusRegisterResolverTests"`
Expected: PASS, 13 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Modbus/Mapping src/Namotion.Interceptor.Modbus.Tests/Mapping
git commit -m "feat: resolve Modbus register mappings from the subject tree"
```

---

## Task 7: Read planner

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/Mapping/ModbusReadBatch.cs`
- Create: `src/Namotion.Interceptor.Modbus/Mapping/ModbusReadPlanner.cs`
- Test: `src/Namotion.Interceptor.Modbus.Tests/Mapping/ModbusReadPlannerTests.cs`

Scale-factor ordering (spec 4.5) is not needed in the planner: the poller reads every batch of a cycle before it converts anything (Task 9), so a scale factor from any batch of the same cycle is current when its dependents are converted.

A binding marked `IsIsolated` (the poller sets it after a request spanning it was rejected, Task 9) is never merged with a neighbour, so a rejected gap register or a device that rejects block-crossing reads costs one failed request per connect, not one per cycle.

- [ ] **Step 1: Write the failing tests**

```csharp
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Modbus.Mapping;

namespace Namotion.Interceptor.Modbus.Tests.Mapping;

public partial class ModbusReadPlannerTests
{
    [InterceptorSubject]
    public partial class PlannerSubject
    {
        public partial int? Value { get; set; }
    }

    private static readonly PlannerSubject Subject = new();

    private static ModbusRegisterBinding CreateBinding(
        int address, ModbusDataType dataType = ModbusDataType.U16,
        ModbusAddressSpace space = ModbusAddressSpace.HoldingRegister, byte unitId = 1, int length = 0)
        => new(new PropertyReference(Subject, nameof(PlannerSubject.Value)), $"Value{address}", unitId, address,
            new ModbusRegisterAttribute(address, dataType) { Space = space, Length = length }, static (_, _) => null);

    [Fact]
    public void WhenRegistersAreContiguous_ThenOneBatchCoversThem()
    {
        // Act
        var batches = ModbusReadPlanner.Plan([CreateBinding(0), CreateBinding(1), CreateBinding(2, ModbusDataType.U32)], maximumGap: 0);

        // Assert
        var batch = Assert.Single(batches);
        Assert.Equal((0, 4), (batch.StartAddress, batch.Count));
        Assert.Equal(3, batch.Bindings.Length);
    }

    [Fact]
    public void WhenGapIsNotAllowed_ThenGapSplitsTheBatch()
    {
        // Act
        var batches = ModbusReadPlanner.Plan([CreateBinding(0), CreateBinding(2)], maximumGap: 0);

        // Assert
        Assert.Equal(new[] { (0, 1), (2, 1) }, batches.Select(batch => (batch.StartAddress, batch.Count)));
    }

    [Fact]
    public void WhenGapIsWithinTolerance_ThenBatchSpansTheGap()
    {
        // Act
        var batches = ModbusReadPlanner.Plan([CreateBinding(0), CreateBinding(2)], maximumGap: 1);

        // Assert
        var batch = Assert.Single(batches);
        Assert.Equal((0, 3), (batch.StartAddress, batch.Count));
    }

    [Fact]
    public void WhenRegistersExceedTheRequestLimit_ThenBatchesAreSplitAt125()
    {
        // Act
        var batches = ModbusReadPlanner.Plan(Enumerable.Range(0, 130).Select(address => CreateBinding(address)), maximumGap: 0);

        // Assert
        Assert.Equal(new[] { (0, 125), (125, 5) }, batches.Select(batch => (batch.StartAddress, batch.Count)));
    }

    [Fact]
    public void WhenBitsExceedTheRequestLimit_ThenBatchesAreSplitAt2000()
    {
        // Act
        var batches = ModbusReadPlanner.Plan(
            Enumerable.Range(0, 2005).Select(address => CreateBinding(address, ModbusDataType.Boolean, ModbusAddressSpace.Coil)),
            maximumGap: 0);

        // Assert
        Assert.Equal(new[] { (0, 2000), (2000, 5) }, batches.Select(batch => (batch.StartAddress, batch.Count)));
    }

    [Fact]
    public void WhenUnitsOrSpacesDiffer_ThenBatchesAreSeparate()
    {
        // Act
        var batches = ModbusReadPlanner.Plan(
            [CreateBinding(0), CreateBinding(1, unitId: 2), CreateBinding(1, space: ModbusAddressSpace.InputRegister)],
            maximumGap: 10);

        // Assert
        Assert.Equal(3, batches.Length);
    }

    [Fact]
    public void WhenMappingsOverlap_ThenTheyShareOneBatch()
    {
        // Act
        var batches = ModbusReadPlanner.Plan([CreateBinding(0, ModbusDataType.String, length: 4), CreateBinding(2)], maximumGap: 0);

        // Assert
        var batch = Assert.Single(batches);
        Assert.Equal((0, 4), (batch.StartAddress, batch.Count));
    }

    [Fact]
    public void WhenBindingsAreIsolated_ThenEachIsReadAloneAndOthersStillMerge()
    {
        // Arrange
        var first = CreateBinding(0);
        var second = CreateBinding(1);
        first.IsIsolated = true;
        second.IsIsolated = true;

        // Act
        var batches = ModbusReadPlanner.Plan([first, second, CreateBinding(2), CreateBinding(3)], maximumGap: 0);

        // Assert
        Assert.Equal(new[] { (0, 1), (1, 1), (2, 2) }, batches.Select(batch => (batch.StartAddress, batch.Count)));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusReadPlannerTests"`
Expected: build error, `ModbusReadPlanner` does not exist.

- [ ] **Step 3: Implement the batch and the planner**

`Mapping/ModbusReadBatch.cs`:

```csharp
namespace Namotion.Interceptor.Modbus.Mapping;

/// <summary>
/// One read request covering <see cref="Count"/> registers or bits from <see cref="StartAddress"/>.
/// </summary>
internal sealed class ModbusReadBatch
{
    public ModbusReadBatch(byte unitId, ModbusAddressSpace space, int startAddress, int count, ModbusRegisterBinding[] bindings)
    {
        UnitId = unitId;
        Space = space;
        StartAddress = startAddress;
        Count = count;
        Bindings = bindings;
    }

    public byte UnitId { get; }

    public ModbusAddressSpace Space { get; }

    public int StartAddress { get; }

    public int Count { get; }

    public ModbusRegisterBinding[] Bindings { get; }
}
```

`Mapping/ModbusReadPlanner.cs`:

```csharp
namespace Namotion.Interceptor.Modbus.Mapping;

internal static class ModbusReadPlanner
{
    public const int MaximumRegistersPerRequest = 125;
    public const int MaximumBitsPerRequest = 2000;

    public static ModbusReadBatch[] Plan(IEnumerable<ModbusRegisterBinding> bindings, int maximumGap)
    {
        var batches = new List<ModbusReadBatch>();
        var groups = bindings
            .GroupBy(binding => (binding.UnitId, binding.Space))
            .OrderBy(group => group.Key.UnitId)
            .ThenBy(group => group.Key.Space);

        foreach (var group in groups)
        {
            var (unitId, space) = group.Key;
            var limit = space is ModbusAddressSpace.Coil or ModbusAddressSpace.DiscreteInput
                ? MaximumBitsPerRequest
                : MaximumRegistersPerRequest;

            var current = new List<ModbusRegisterBinding>();
            var start = 0;
            var end = 0;
            foreach (var binding in group.OrderBy(binding => binding.Address).ThenBy(binding => binding.Count))
            {
                var bindingEnd = binding.Address + binding.Count;
                if (current.Count > 0 &&
                    !binding.IsIsolated &&
                    !current[^1].IsIsolated &&
                    binding.Address - end <= maximumGap &&
                    Math.Max(end, bindingEnd) - start <= limit)
                {
                    current.Add(binding);
                    end = Math.Max(end, bindingEnd);
                    continue;
                }

                if (current.Count > 0)
                {
                    batches.Add(new ModbusReadBatch(unitId, space, start, end - start, current.ToArray()));
                }

                current.Clear();
                current.Add(binding);
                start = binding.Address;
                end = bindingEnd;
            }

            if (current.Count > 0)
            {
                batches.Add(new ModbusReadBatch(unitId, space, start, end - start, current.ToArray()));
            }
        }

        return batches.ToArray();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusReadPlannerTests"`
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Modbus/Mapping src/Namotion.Interceptor.Modbus.Tests/Mapping
git commit -m "feat: plan contiguous Modbus read requests"
```

---

## Task 8: Transport and the in-process test server

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/Transport/IModbusRegisterReader.cs`
- Create: `src/Namotion.Interceptor.Modbus/Transport/ModbusConnection.cs`
- Create: `src/Namotion.Interceptor.Modbus.Tests/Testing/ModbusTestServer.cs`
- Test: `src/Namotion.Interceptor.Modbus.Tests/Transport/ModbusConnectionTests.cs`

- [ ] **Step 1: Write the reader interface**

`Transport/IModbusRegisterReader.cs`:

```csharp
namespace Namotion.Interceptor.Modbus.Transport;

internal interface IModbusRegisterReader
{
    /// <summary>
    /// Reads <paramref name="count"/> registers (two big-endian bytes each) or bits (packed, lowest address in
    /// bit 0 of byte 0).
    /// </summary>
    /// <remarks>
    /// The returned memory may be a pooled buffer that the next read overwrites; copy it before reading again.
    /// Throws <see cref="ModbusResponseException"/> for a Modbus exception response; any other exception means
    /// the connection is lost.
    /// </remarks>
    Task<ReadOnlyMemory<byte>> ReadAsync(
        byte unitId, ModbusAddressSpace space, int address, int count, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write the test server helper**

`Testing/ModbusTestServer.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentModbus;

namespace Namotion.Interceptor.Modbus.Tests.Testing;

/// <summary>
/// In-process Modbus TCP server on a free loopback port. Register values are written in wire (big endian) order.
/// </summary>
internal sealed class ModbusTestServer : IDisposable
{
    private readonly byte[] _unitIds;
    private readonly Lock _rejectionsLock = new();
    private readonly List<(byte UnitId, ModbusAddressSpace Space, int Address)> _rejectedAddresses = [];
    private readonly ConcurrentQueue<(byte UnitId, ModbusFunctionCode FunctionCode, int Address, int Quantity)> _requests = new();
    private ModbusTcpServer? _server;

    public ModbusTestServer(params byte[] unitIds)
    {
        // Single-unit mode answers only unit 0, so the server always runs with explicit units.
        _unitIds = unitIds.Length == 0 ? [1] : unitIds;
        Port = GetFreeTcpPort();
    }

    public int Port { get; }

    public IReadOnlyList<(byte UnitId, ModbusFunctionCode FunctionCode, int Address, int Quantity)> Requests => _requests.ToArray();

    public void Start()
    {
        var server = new ModbusTcpServer(true);
        foreach (var unitId in _unitIds)
        {
            server.AddUnit(unitId);
        }

        server.RequestValidator = ValidateRequest;
        server.Start(new IPEndPoint(IPAddress.Loopback, Port));
        _server = server;
    }

    public void Stop()
    {
        var server = _server;
        _server = null;
        if (server is not null)
        {
            server.Stop();
            server.Dispose();
        }
    }

    public void SetHoldingRegister<T>(int address, T value, byte unitId = 1) where T : unmanaged
    {
        var server = GetServer();
        lock (server.Lock)
        {
            server.GetHoldingRegisters(unitId).SetBigEndian(address, value);
        }
    }

    public void SetInputRegister<T>(int address, T value, byte unitId = 1) where T : unmanaged
    {
        var server = GetServer();
        lock (server.Lock)
        {
            server.GetInputRegisters(unitId).SetBigEndian(address, value);
        }
    }

    public void SetCoil(int address, bool value, byte unitId = 1)
    {
        var server = GetServer();
        lock (server.Lock)
        {
            server.GetCoils(unitId).Set(address, value);
        }
    }

    public void SetDiscreteInput(int address, bool value, byte unitId = 1)
    {
        var server = GetServer();
        lock (server.Lock)
        {
            server.GetDiscreteInputs(unitId).Set(address, value);
        }
    }

    public void RejectAddress(ModbusAddressSpace space, int address, byte unitId = 1)
    {
        lock (_rejectionsLock)
        {
            _rejectedAddresses.Add((unitId, space, address));
        }
    }

    public void Dispose() => Stop();

    private ModbusTcpServer GetServer() => _server ?? throw new InvalidOperationException("The test server is not started.");

    private ModbusExceptionCode ValidateRequest(byte unitId, ModbusFunctionCode functionCode, ushort address, ushort quantity)
    {
        _requests.Enqueue((unitId, functionCode, address, quantity));

        ModbusAddressSpace? space = functionCode switch
        {
            ModbusFunctionCode.ReadHoldingRegisters or ModbusFunctionCode.WriteSingleRegister or ModbusFunctionCode.WriteMultipleRegisters
                => ModbusAddressSpace.HoldingRegister,
            ModbusFunctionCode.ReadInputRegisters => ModbusAddressSpace.InputRegister,
            ModbusFunctionCode.ReadCoils or ModbusFunctionCode.WriteSingleCoil or ModbusFunctionCode.WriteMultipleCoils
                => ModbusAddressSpace.Coil,
            ModbusFunctionCode.ReadDiscreteInputs => ModbusAddressSpace.DiscreteInput,
            _ => null
        };

        lock (_rejectionsLock)
        {
            foreach (var rejected in _rejectedAddresses)
            {
                if (rejected.UnitId == unitId && rejected.Space == space &&
                    rejected.Address >= address && rejected.Address < address + quantity)
                {
                    return ModbusExceptionCode.IllegalDataAddress;
                }
            }
        }

        return ModbusExceptionCode.OK;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
```

- [ ] **Step 3: Write the failing connection tests**

`Transport/ModbusConnectionTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using Namotion.Interceptor.Modbus.Tests.Testing;
using Namotion.Interceptor.Modbus.Transport;

namespace Namotion.Interceptor.Modbus.Tests.Transport;

[Trait("Category", "Integration")]
[Collection(ModbusIntegrationCollection.Name)]
public class ModbusConnectionTests
{
    private static Task<ModbusConnection> ConnectAsync(ModbusTestServer server, TimeSpan? requestTimeout = null)
        => ModbusConnection.ConnectAsync("127.0.0.1", server.Port, requestTimeout ?? TimeSpan.FromSeconds(5), CancellationToken.None);

    [Fact]
    public async Task WhenReadingEachSpace_ThenWireBytesAreReturned()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        server.SetHoldingRegister<short>(10, -2);
        server.SetInputRegister<ushort>(10, 0xABCD);
        server.SetCoil(3, true);
        server.SetDiscreteInput(1, true);
        using var connection = await ConnectAsync(server);

        // Act
        var holding = (await connection.ReadAsync(1, ModbusAddressSpace.HoldingRegister, 10, 1, CancellationToken.None)).ToArray();
        var input = (await connection.ReadAsync(1, ModbusAddressSpace.InputRegister, 10, 1, CancellationToken.None)).ToArray();
        var coils = (await connection.ReadAsync(1, ModbusAddressSpace.Coil, 0, 8, CancellationToken.None)).ToArray();
        var discreteInputs = (await connection.ReadAsync(1, ModbusAddressSpace.DiscreteInput, 0, 4, CancellationToken.None)).ToArray();

        // Assert
        Assert.Equal(new byte[] { 0xFF, 0xFE }, holding);
        Assert.Equal(new byte[] { 0xAB, 0xCD }, input);
        Assert.Equal(new byte[] { 0x08 }, coils);
        Assert.Equal(new byte[] { 0x02 }, discreteInputs);
    }

    [Fact]
    public async Task WhenDeviceRejectsAddress_ThenResponseExceptionIsThrownAndConnectionStaysUsable()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        server.RejectAddress(ModbusAddressSpace.HoldingRegister, 5);
        server.SetHoldingRegister<short>(0, 7);
        using var connection = await ConnectAsync(server);

        // Act
        var exception = await Assert.ThrowsAsync<ModbusResponseException>(() =>
            connection.ReadAsync(1, ModbusAddressSpace.HoldingRegister, 4, 2, CancellationToken.None));
        var afterwards = (await connection.ReadAsync(1, ModbusAddressSpace.HoldingRegister, 0, 1, CancellationToken.None)).ToArray();

        // Assert
        Assert.Equal(2, exception.ExceptionCode);
        Assert.Equal(new byte[] { 0x00, 0x07 }, afterwards);
    }

    [Fact]
    public async Task WhenUnitDoesNotAnswer_ThenTimeoutExceptionIsThrown()
    {
        // Arrange
        using var server = new ModbusTestServer(1);
        server.Start();
        using var connection = await ConnectAsync(server, TimeSpan.FromMilliseconds(300));

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(() =>
            connection.ReadAsync(9, ModbusAddressSpace.HoldingRegister, 0, 1, CancellationToken.None));
    }

    [Fact]
    public async Task WhenCallerCancels_ThenOperationCanceledExceptionIsThrown()
    {
        // Arrange
        using var server = new ModbusTestServer(1);
        server.Start();
        using var connection = await ConnectAsync(server, TimeSpan.FromSeconds(30));
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            connection.ReadAsync(9, ModbusAddressSpace.HoldingRegister, 0, 1, cancellationTokenSource.Token));
    }

    [Fact]
    public async Task WhenResponseHasInvalidProtocolIdentifier_ThenItIsNotReportedAsDeviceRejection()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var release = new TaskCompletionSource();
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var request = new byte[12];
            await stream.ReadExactlyAsync(request);

            // Echoed transaction ID, protocol ID 1 instead of 0, length 5, unit, function 3, 2 bytes, value 7.
            await stream.WriteAsync(new byte[] { request[0], request[1], 0x00, 0x01, 0x00, 0x05, request[6], 0x03, 0x02, 0x00, 0x07 });
            await release.Task;
        });
        using var connection = await ModbusConnection.ConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(5), CancellationToken.None);

        try
        {
            // Act
            var exception = await Record.ExceptionAsync(() =>
                connection.ReadAsync(1, ModbusAddressSpace.HoldingRegister, 0, 1, CancellationToken.None));

            // Assert
            Assert.NotNull(exception);
            Assert.IsNotType<ModbusResponseException>(exception);
            Assert.IsNotType<TimeoutException>(exception);
        }
        finally
        {
            release.TrySetResult();
            await serverTask;
        }
    }

    [Fact]
    public async Task WhenServerStops_ThenReadFailsWithConnectionError()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        using var connection = await ConnectAsync(server, TimeSpan.FromSeconds(2));
        server.Stop();

        // Act
        var exception = await Record.ExceptionAsync(() =>
            connection.ReadAsync(1, ModbusAddressSpace.HoldingRegister, 0, 1, CancellationToken.None));

        // Assert
        Assert.NotNull(exception);
        Assert.IsNotType<ModbusResponseException>(exception);
    }

    [Fact]
    public async Task WhenDisposed_ThenReadThrowsObjectDisposedException()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        var connection = await ConnectAsync(server);
        connection.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            connection.ReadAsync(1, ModbusAddressSpace.HoldingRegister, 0, 1, CancellationToken.None));
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusConnectionTests"`
Expected: build error, `ModbusConnection` does not exist.

- [ ] **Step 5: Implement the connection**

`Transport/ModbusConnection.cs`:

```csharp
using System.Net.Sockets;
using FluentModbus;

namespace Namotion.Interceptor.Modbus.Transport;

/// <summary>
/// One Modbus TCP connection. Not thread-safe: the source issues one request at a time.
/// </summary>
internal sealed class ModbusConnection : IModbusRegisterReader, IDisposable
{
    // FluentModbus throws ModbusException with this code for framing errors (invalid protocol identifier,
    // function code or length). Those are not device rejections and must propagate as a lost connection.
    private const ModbusExceptionCode FramingErrorCode = (ModbusExceptionCode)255;

    private readonly TcpClient _tcpClient;
    private readonly ModbusTcpClient _client;
    private readonly TimeSpan _requestTimeout;

    // Reused by every request of this connection and replaced only after it fired.
    private CancellationTokenSource _timeoutSource = new();
    private int _disposed;

    private ModbusConnection(TcpClient tcpClient, ModbusTcpClient client, TimeSpan requestTimeout)
    {
        _tcpClient = tcpClient;
        _client = client;
        _requestTimeout = requestTimeout;
    }

    public static async Task<ModbusConnection> ConnectAsync(
        string host, int port, TimeSpan requestTimeout, CancellationToken cancellationToken)
    {
        var tcpClient = new TcpClient { NoDelay = true };
        try
        {
            await tcpClient.ConnectAsync(host, port, cancellationToken).AsTask()
                .WaitAsync(requestTimeout, cancellationToken).ConfigureAwait(false);

            var client = new ModbusTcpClient();
            client.Initialize(tcpClient, ModbusEndianness.BigEndian);
            return new ModbusConnection(tcpClient, client, requestTimeout);
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }

    public async Task<ReadOnlyMemory<byte>> ReadAsync(
        byte unitId, ModbusAddressSpace space, int address, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        var timeoutSource = _timeoutSource;
        timeoutSource.CancelAfter(_requestTimeout);

        // The caller's token differs per call (poll attempt, discovery, initial load), so it is registered per
        // request instead of being linked once. Re-registering on a long-lived token reuses its callback nodes.
        var registration = cancellationToken.UnsafeRegister(
            static state => ((CancellationTokenSource)state!).Cancel(), timeoutSource);
        try
        {
            var token = timeoutSource.Token;
            return space switch
            {
                ModbusAddressSpace.HoldingRegister => await _client.ReadHoldingRegistersAsync(unitId, (ushort)address, (ushort)count, token).ConfigureAwait(false),
                ModbusAddressSpace.InputRegister => await _client.ReadInputRegistersAsync(unitId, (ushort)address, (ushort)count, token).ConfigureAwait(false),
                ModbusAddressSpace.Coil => await _client.ReadCoilsAsync(unitId, address, count, token).ConfigureAwait(false),
                ModbusAddressSpace.DiscreteInput => await _client.ReadDiscreteInputsAsync(unitId, address, count, token).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(space), space, null)
            };
        }
        catch (ModbusException exception) when (exception.ExceptionCode is not ModbusExceptionCode.OK and not FramingErrorCode)
        {
            throw new ModbusResponseException((int)exception.ExceptionCode, exception.Message, exception);
        }
        catch (Exception exception) when (timeoutSource.IsCancellationRequested)
        {
            // FluentModbus closes the stream when its token fires, so both cases leave the connection unusable.
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"The Modbus request did not complete within {_requestTimeout}.", exception);
        }
        finally
        {
            registration.Dispose();
            if (!timeoutSource.TryReset())
            {
                _timeoutSource = new CancellationTokenSource();
                timeoutSource.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _client.Dispose();
        _tcpClient.Dispose();
        _timeoutSource.Dispose();
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusConnectionTests"`
Expected: PASS, 7 tests.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Modbus/Transport src/Namotion.Interceptor.Modbus.Tests/Testing src/Namotion.Interceptor.Modbus.Tests/Transport
git commit -m "feat: add the Modbus TCP connection and an in-process test server"
```

---

## Task 9: Poller and polling metrics

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/Polling/ModbusPollingMetrics.cs`
- Create: `src/Namotion.Interceptor.Modbus/Polling/ModbusPoller.cs`
- Create: `src/Namotion.Interceptor.Modbus.Tests/Testing/FakeRegisterReader.cs`
- Test: `src/Namotion.Interceptor.Modbus.Tests/Polling/ModbusPollerTests.cs`

A cycle has two phases. `ReadAsync` reads every batch into each binding's `CurrentRaw`. A rejected multi-mapping batch is re-read one mapping at a time; its mappings are then isolated (read in requests of their own) and mappings that still fail are marked unavailable and dropped, both until the next connect, and the plan is rebuilt. `ApplyChanges` then converts and applies only mappings whose raw words changed, whose scale factor changed, or that were asked to be reapplied, and finally copies `CurrentRaw` to `LastRaw`. An unchanged cycle converts nothing and raises no change events.

- [ ] **Step 1: Write the fake reader**

`Testing/FakeRegisterReader.cs`:

```csharp
using System.Buffers.Binary;
using Namotion.Interceptor.Modbus.Transport;

namespace Namotion.Interceptor.Modbus.Tests.Testing;

internal sealed class FakeRegisterReader : IModbusRegisterReader
{
    private readonly Dictionary<(byte UnitId, ModbusAddressSpace Space, int Address), ushort> _registers = [];
    private readonly Dictionary<(byte UnitId, ModbusAddressSpace Space, int Address), bool> _bits = [];
    private readonly HashSet<(byte UnitId, ModbusAddressSpace Space, int Address)> _rejected = [];

    public List<(byte UnitId, ModbusAddressSpace Space, int Address, int Count)> Requests { get; } = [];

    public Exception? ConnectionFailure { get; set; }

    /// <summary>
    /// Gets or sets the zero-based index of the first request that fails with <see cref="ConnectionFailure"/>.
    /// </summary>
    public int ConnectionFailureFromRequest { get; set; }

    public void SetRegister(int address, ushort value, ModbusAddressSpace space = ModbusAddressSpace.HoldingRegister, byte unitId = 1)
        => _registers[(unitId, space, address)] = value;

    public void SetBit(int address, bool value, ModbusAddressSpace space = ModbusAddressSpace.Coil, byte unitId = 1)
        => _bits[(unitId, space, address)] = value;

    public void Reject(int address, ModbusAddressSpace space = ModbusAddressSpace.HoldingRegister, byte unitId = 1)
        => _rejected.Add((unitId, space, address));

    public Task<ReadOnlyMemory<byte>> ReadAsync(
        byte unitId, ModbusAddressSpace space, int address, int count, CancellationToken cancellationToken)
    {
        Requests.Add((unitId, space, address, count));
        if (ConnectionFailure is not null && Requests.Count > ConnectionFailureFromRequest)
        {
            return Task.FromException<ReadOnlyMemory<byte>>(ConnectionFailure);
        }

        for (var index = 0; index < count; index++)
        {
            if (_rejected.Contains((unitId, space, address + index)))
            {
                return Task.FromException<ReadOnlyMemory<byte>>(
                    new ModbusResponseException(2, "Illegal data address", new InvalidOperationException()));
            }
        }

        if (space is ModbusAddressSpace.Coil or ModbusAddressSpace.DiscreteInput)
        {
            var bits = new byte[(count + 7) / 8];
            for (var index = 0; index < count; index++)
            {
                if (_bits.GetValueOrDefault((unitId, space, address + index)))
                {
                    bits[index / 8] |= (byte)(1 << (index % 8));
                }
            }

            return Task.FromResult<ReadOnlyMemory<byte>>(bits);
        }

        var registers = new byte[count * 2];
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(registers.AsSpan(index * 2), _registers.GetValueOrDefault((unitId, space, address + index)));
        }

        return Task.FromResult<ReadOnlyMemory<byte>>(registers);
    }
}
```

- [ ] **Step 2: Write the failing poller tests**

`Polling/ModbusPollerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Modbus.Mapping;
using Namotion.Interceptor.Modbus.Polling;
using Namotion.Interceptor.Modbus.Tests.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Modbus.Tests.Polling;

public partial class ModbusPollerTests
{
    [InterceptorSubject]
    public partial class PollerSubject
    {
        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? First { get; set; }

        [ModbusRegister(1, ModbusDataType.S16, Scale = 0.1)]
        public partial decimal? Second { get; set; }

        [ModbusRegister(2, ModbusDataType.U16, ScaleFactorProperty = nameof(Factor))]
        public partial decimal? Scaled { get; set; }

        [ModbusRegister(3, ModbusDataType.S16)]
        public partial short? Factor { get; set; }

        [ModbusRegister(0, ModbusDataType.Boolean, Space = ModbusAddressSpace.Coil)]
        public partial bool? Pump { get; set; }
    }

    [InterceptorSubject]
    public partial class GapSubject
    {
        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? First { get; set; }

        [ModbusRegister(2, ModbusDataType.U16)]
        public partial int? Second { get; set; }
    }

    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();

    private static (ModbusPoller Poller, FakeRegisterReader Reader, ModbusPollingMetrics Metrics) Create()
    {
        var subject = new PollerSubject(CreateContext());
        var bindings = ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>());
        var metrics = new ModbusPollingMetrics();
        var reader = new FakeRegisterReader();
        reader.SetRegister(0, 42);
        reader.SetRegister(1, 215);
        reader.SetRegister(2, 123);
        reader.SetRegister(3, unchecked((ushort)-1));
        reader.SetBit(0, true);
        return (new ModbusPoller(bindings, 0, metrics, NullLogger.Instance), reader, metrics);
    }

    private static Dictionary<string, object?> Apply(ModbusPoller poller)
    {
        var applied = new Dictionary<string, object?>();
        poller.ApplyChanges(applied, static (state, property, value) => state[property.Name] = value);
        return applied;
    }

    [Fact]
    public async Task WhenFirstCycleCompletes_ThenAllValuesAreApplied()
    {
        // Arrange
        var (poller, reader, _) = Create();

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);

        // Assert
        Assert.Equal(42, applied["First"]);
        Assert.Equal(21.5m, applied["Second"]);
        Assert.Equal(12.3m, applied["Scaled"]);
        Assert.Equal((short)-1, applied["Factor"]);
        Assert.Equal(true, applied["Pump"]);
    }

    [Fact]
    public async Task WhenRawValuesAreUnchanged_ThenNothingIsApplied()
    {
        // Arrange
        var (poller, reader, _) = Create();
        await poller.ReadAsync(reader, CancellationToken.None);
        Apply(poller);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);

        // Assert
        Assert.Empty(applied);
    }

    [Fact]
    public async Task WhenOneRegisterChanges_ThenOnlyThatPropertyIsApplied()
    {
        // Arrange
        var (poller, reader, _) = Create();
        await poller.ReadAsync(reader, CancellationToken.None);
        Apply(poller);
        reader.SetRegister(0, 43);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);

        // Assert
        Assert.Equal(43, Assert.Single(applied).Value);
    }

    [Fact]
    public async Task WhenScaleFactorChanges_ThenDependentIsReapplied()
    {
        // Arrange
        var (poller, reader, _) = Create();
        await poller.ReadAsync(reader, CancellationToken.None);
        Apply(poller);
        reader.SetRegister(3, unchecked((ushort)-2));

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);

        // Assert
        Assert.Equal(1.23m, applied["Scaled"]);
        Assert.Equal((short)-2, applied["Factor"]);
        Assert.Equal(2, applied.Count);
    }

    [Fact]
    public async Task WhenReapplyIsRequested_ThenUnchangedValueIsApplied()
    {
        // Arrange
        var (poller, reader, _) = Create();
        await poller.ReadAsync(reader, CancellationToken.None);
        Apply(poller);
        poller.RequestReapply(poller.Bindings.Single(binding => binding.Property.Name == "First").Property);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);

        // Assert
        Assert.Equal(42, Assert.Single(applied).Value);
    }

    [Fact]
    public async Task WhenBatchIsRejected_ThenMappingsAreReadOneByOneAndTheRejectedOneIsDropped()
    {
        // Arrange
        var (poller, reader, metrics) = Create();
        reader.Reject(1);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);
        reader.Requests.Clear();
        await poller.ReadAsync(reader, CancellationToken.None);

        // Assert
        Assert.False(applied.ContainsKey("Second"));
        Assert.Equal(42, applied["First"]);
        Assert.Equal(12.3m, applied["Scaled"]);
        Assert.Equal(1, metrics.UnavailableProperties);
        Assert.Equal(1, metrics.FailedBatches);
        Assert.DoesNotContain(reader.Requests, request =>
            request.Space == ModbusAddressSpace.HoldingRegister && request.Address <= 1 && request.Address + request.Count > 1);
    }

    [Fact]
    public async Task WhenSingleMappingBatchIsRejected_ThenFailedBatchIsCountedAndOthersContinue()
    {
        // Arrange
        var (poller, reader, metrics) = Create();
        reader.Reject(0, ModbusAddressSpace.Coil);

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);
        var applied = Apply(poller);

        // Assert
        Assert.False(applied.ContainsKey("Pump"));
        Assert.Equal(42, applied["First"]);
        Assert.Equal(1, metrics.FailedBatches);
        Assert.Equal(0, metrics.UnavailableProperties);
    }

    [Fact]
    public async Task WhenConnectionFails_ThenExceptionPropagates()
    {
        // Arrange
        var (poller, reader, _) = Create();
        reader.ConnectionFailure = new IOException("Connection reset");

        // Act & Assert
        await Assert.ThrowsAsync<IOException>(() => poller.ReadAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task WhenConnectionFailsWhileReadingIndividually_ThenExceptionPropagatesAndNothingIsMarkedUnavailable()
    {
        // Arrange (request 0 is the rejected holding batch, request 1 reads First, request 2 reads Second)
        var (poller, reader, metrics) = Create();
        reader.Reject(1);
        reader.ConnectionFailure = new IOException("Connection reset");
        reader.ConnectionFailureFromRequest = 2;

        // Act & Assert
        await Assert.ThrowsAsync<IOException>(() => poller.ReadAsync(reader, CancellationToken.None));
        Assert.Equal(0, metrics.UnavailableProperties);
    }

    [Fact]
    public async Task WhenGapAddressIsRejected_ThenLaterCyclesReadTheMappingsSeparately()
    {
        // Arrange
        var subject = new GapSubject(CreateContext());
        var bindings = ModbusRegisterResolver.Resolve(subject, 1, new HashSet<PropertyReference>());
        var metrics = new ModbusPollingMetrics();
        var poller = new ModbusPoller(bindings, maximumRegisterGap: 1, metrics, NullLogger.Instance);
        var reader = new FakeRegisterReader();
        reader.SetRegister(0, 5);
        reader.SetRegister(2, 6);
        reader.Reject(1);
        await poller.ReadAsync(reader, CancellationToken.None);
        var firstCycle = Apply(poller);
        reader.Requests.Clear();

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);

        // Assert
        Assert.Equal(5, firstCycle["First"]);
        Assert.Equal(6, firstCycle["Second"]);
        Assert.Equal(2, reader.Requests.Count);
        Assert.All(reader.Requests, request => Assert.Equal(1, request.Count));
        Assert.Equal(1, metrics.FailedBatches);
        Assert.Equal(2, metrics.BatchCount);
        Assert.Equal(0, metrics.UnavailableProperties);
    }

    [Fact]
    public async Task WhenCycleCompletes_ThenMetricsAreRecorded()
    {
        // Arrange
        var (poller, reader, metrics) = Create();

        // Act
        await poller.ReadAsync(reader, CancellationToken.None);

        // Assert
        Assert.Equal(1, metrics.TotalPolls);
        Assert.NotNull(metrics.LastPollTime);
        Assert.Equal(2, metrics.BatchCount);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusPollerTests"`
Expected: build error, `ModbusPoller` does not exist.

- [ ] **Step 4: Implement the metrics**

`Polling/ModbusPollingMetrics.cs`:

```csharp
using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Modbus.Polling;

internal sealed class ModbusPollingMetrics : IResettableMetrics
{
    private long _totalPolls;
    private long _failedBatches;
    private long _lastPollDurationTicks;
    private long _lastPollTimeUtcTicks;
    private int _batchCount;
    private int _unavailableProperties;

    public long TotalPolls => Interlocked.Read(ref _totalPolls);

    public long FailedBatches => Interlocked.Read(ref _failedBatches);

    public int BatchCount => Volatile.Read(ref _batchCount);

    public int UnavailableProperties => Volatile.Read(ref _unavailableProperties);

    public DateTimeOffset? LastPollTime
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastPollTimeUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public TimeSpan? LastPollDuration => LastPollTime is null
        ? null
        : TimeSpan.FromTicks(Interlocked.Read(ref _lastPollDurationTicks));

    public void RecordPoll(TimeSpan duration, DateTimeOffset time)
    {
        Interlocked.Exchange(ref _lastPollDurationTicks, duration.Ticks);
        Interlocked.Exchange(ref _lastPollTimeUtcTicks, time.UtcTicks);
        Interlocked.Increment(ref _totalPolls);
    }

    public void RecordFailedBatch() => Interlocked.Increment(ref _failedBatches);

    public void SetPlan(int batchCount, int unavailableProperties)
    {
        Volatile.Write(ref _batchCount, batchCount);
        Volatile.Write(ref _unavailableProperties, unavailableProperties);
    }

    /// <summary>
    /// Resets the cumulative counters. The plan gauges and the last poll time and duration are left alone.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalPolls, 0);
        Interlocked.Exchange(ref _failedBatches, 0);
    }
}
```

- [ ] **Step 5: Implement the poller**

`Polling/ModbusPoller.cs`:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Modbus.Mapping;
using Namotion.Interceptor.Modbus.Transport;

namespace Namotion.Interceptor.Modbus.Polling;

/// <summary>
/// Runs the read cycle of one connection. Not thread-safe except <see cref="RequestReapply"/>: the source calls
/// <see cref="ReadAsync"/> and <see cref="ApplyChanges{TState}"/> strictly one after another.
/// </summary>
internal sealed class ModbusPoller
{
    private readonly ModbusRegisterBinding[] _bindings;
    private readonly Dictionary<PropertyReference, ModbusRegisterBinding> _bindingsByProperty;
    private readonly int _maximumRegisterGap;
    private readonly ModbusPollingMetrics _metrics;
    private readonly ILogger _logger;
    private readonly HashSet<ModbusReadBatch> _failingBatches = [];
    private ModbusReadBatch[] _batches;

    public ModbusPoller(
        IReadOnlyCollection<ModbusRegisterBinding> bindings, int maximumRegisterGap,
        ModbusPollingMetrics metrics, ILogger logger)
    {
        _bindings = bindings.ToArray();
        _bindingsByProperty = _bindings.ToDictionary(binding => binding.Property, PropertyReference.Comparer);
        _maximumRegisterGap = maximumRegisterGap;
        _metrics = metrics;
        _logger = logger;
        _batches = ModbusReadPlanner.Plan(_bindings, maximumRegisterGap);
        _metrics.SetPlan(_batches.Length, unavailableProperties: 0);
    }

    public IReadOnlyList<ModbusRegisterBinding> Bindings => _bindings;

    public IReadOnlyList<ModbusReadBatch> Batches => _batches;

    public void RequestReapply(PropertyReference property)
    {
        if (_bindingsByProperty.TryGetValue(property, out var binding))
        {
            binding.RequestReapply();
        }
    }

    /// <summary>
    /// Reads every batch into the bindings' current raw buffers. Modbus exception responses are handled here;
    /// any other exception means the connection is lost and propagates.
    /// </summary>
    public async Task ReadAsync(IModbusRegisterReader reader, CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        foreach (var binding in _bindings)
        {
            binding.HasCurrent = false;
        }

        var isReplanRequired = false;
        foreach (var batch in _batches)
        {
            try
            {
                var data = await reader.ReadAsync(batch.UnitId, batch.Space, batch.StartAddress, batch.Count, cancellationToken).ConfigureAwait(false);

                // The reader may hand out a pooled buffer that its next read overwrites, so copy before reading on.
                foreach (var binding in batch.Bindings)
                {
                    CopyToBinding(binding, data.Span, batch.StartAddress);
                }

                if (_failingBatches.Remove(batch))
                {
                    _logger.LogInformation(
                        "Modbus read of {Space} {Address} (unit {UnitId}) succeeds again.",
                        batch.Space, batch.StartAddress, batch.UnitId);
                }
            }
            catch (ModbusResponseException exception) when (batch.Bindings.Length > 1)
            {
                _metrics.RecordFailedBatch();
                await ReadIndividuallyAsync(reader, batch, exception, cancellationToken).ConfigureAwait(false);
                isReplanRequired = true;
            }
            catch (ModbusResponseException exception)
            {
                _metrics.RecordFailedBatch();
                if (_failingBatches.Add(batch))
                {
                    _logger.LogWarning(
                        "Modbus read of {Path} ({Space} {Address}, unit {UnitId}) was rejected with exception code {ExceptionCode}.",
                        batch.Bindings[0].Path, batch.Space, batch.StartAddress, batch.UnitId, exception.ExceptionCode);
                }
            }
        }

        if (isReplanRequired)
        {
            var availableBindings = _bindings.Where(binding => !binding.IsUnavailable).ToArray();
            _batches = ModbusReadPlanner.Plan(availableBindings, _maximumRegisterGap);
            _metrics.SetPlan(_batches.Length, _bindings.Length - availableBindings.Length);
        }

        _metrics.RecordPoll(Stopwatch.GetElapsedTime(startTimestamp), DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Applies the bindings whose raw value changed, whose scale factor changed, or that were asked to be reapplied,
    /// then remembers the current raw values.
    /// </summary>
    /// <returns>The number of applied values.</returns>
    public int ApplyChanges<TState>(TState state, Action<TState, PropertyReference, object?> apply)
    {
        foreach (var binding in _bindings)
        {
            binding.ChangedThisCycle = binding.HasCurrent &&
                (!binding.HasLast || !binding.CurrentRaw.AsSpan().SequenceEqual(binding.LastRaw));
        }

        var appliedCount = 0;
        foreach (var binding in _bindings)
        {
            if (!binding.HasCurrent)
            {
                continue;
            }

            var isReapplyRequested = binding.ConsumeReapplyRequest();
            var scaleFactor = binding.ScaleFactor;
            if (!binding.ChangedThisCycle && !isReapplyRequested && scaleFactor is not { ChangedThisCycle: true })
            {
                continue;
            }

            var exponent = 0;
            if (scaleFactor is not null)
            {
                ReadOnlySpan<byte> scaleFactorRaw;
                if (scaleFactor.HasCurrent)
                {
                    scaleFactorRaw = scaleFactor.CurrentRaw;
                }
                else if (scaleFactor.HasLast)
                {
                    scaleFactorRaw = scaleFactor.LastRaw;
                }
                else
                {
                    // Not known yet; the scale factor's first successful read marks it changed and reapplies this.
                    continue;
                }

                exponent = (int)ModbusRegisterCodec.ReadInteger(scaleFactorRaw, scaleFactor.Attribute.DataType, ModbusWordOrder.HighWordFirst);
            }

            object? value;
            try
            {
                value = binding.Reader(binding.CurrentRaw, exponent);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to convert Modbus mapping {Path}.", binding.Path);
                continue;
            }

            apply(state, binding.Property, value);
            appliedCount++;
        }

        foreach (var binding in _bindings)
        {
            if (binding.HasCurrent)
            {
                binding.CurrentRaw.CopyTo(binding.LastRaw, 0);
                binding.HasLast = true;
            }
        }

        return appliedCount;
    }

    /// <summary>
    /// Reads the bindings of a rejected batch one by one and isolates them until the next connect.
    /// </summary>
    private async Task ReadIndividuallyAsync(
        IModbusRegisterReader reader, ModbusReadBatch batch, ModbusResponseException batchException, CancellationToken cancellationToken)
    {
        _logger.LogDebug(batchException,
            "Modbus read of {Count} {Space} from {Address} (unit {UnitId}) was rejected; reading its mappings one by one from now on.",
            batch.Count, batch.Space, batch.StartAddress, batch.UnitId);

        foreach (var binding in batch.Bindings)
        {
            // Even when every binding reads fine alone (a rejected gap register, or a device rejecting
            // block-crossing reads), merging them again would fail and re-read one by one every cycle.
            binding.IsIsolated = true;
            try
            {
                var data = await reader.ReadAsync(binding.UnitId, binding.Space, binding.Address, binding.Count, cancellationToken).ConfigureAwait(false);
                CopyToBinding(binding, data.Span, binding.Address);
            }
            catch (ModbusResponseException exception)
            {
                binding.IsUnavailable = true;
                _logger.LogWarning(
                    "Modbus mapping {Path} ({Space} {Address}, unit {UnitId}) was rejected with exception code {ExceptionCode} and is not read again until the next connect.",
                    binding.Path, binding.Space, binding.Address, binding.UnitId, exception.ExceptionCode);
            }
        }
    }

    private static void CopyToBinding(ModbusRegisterBinding binding, ReadOnlySpan<byte> data, int startAddress)
    {
        var offset = binding.Address - startAddress;
        if (binding.IsBitSpace)
        {
            binding.CurrentRaw[0] = (byte)((data[offset / 8] >> (offset % 8)) & 1);
        }
        else
        {
            data.Slice(offset * 2, binding.CurrentRaw.Length).CopyTo(binding.CurrentRaw);
        }

        binding.HasCurrent = true;
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusPollerTests"`
Expected: PASS, 11 tests.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Modbus/Polling src/Namotion.Interceptor.Modbus.Tests/Testing src/Namotion.Interceptor.Modbus.Tests/Polling
git commit -m "feat: poll Modbus registers and apply only changed values"
```

---

## Task 10: Diagnostics

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/ModbusClientDiagnostics.cs`
- Test: `src/Namotion.Interceptor.Modbus.Tests/ModbusClientDiagnosticsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Modbus.Polling;

namespace Namotion.Interceptor.Modbus.Tests;

public class ModbusClientDiagnosticsTests
{
    [Fact]
    public void WhenMetricsAreRecorded_ThenDiagnosticsReportThem()
    {
        // Arrange
        var pollingMetrics = new ModbusPollingMetrics();
        var diagnostics = new ModbusClientDiagnostics(new SourceMetrics(), pollingMetrics);
        var time = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        // Act
        pollingMetrics.SetPlan(batchCount: 3, unavailableProperties: 1);
        pollingMetrics.RecordFailedBatch();
        pollingMetrics.RecordPoll(TimeSpan.FromMilliseconds(12), time);

        // Assert
        Assert.Equal(1, diagnostics.Polling.TotalPolls);
        Assert.Equal(1, diagnostics.Polling.FailedBatches);
        Assert.Equal(3, diagnostics.Polling.BatchCount);
        Assert.Equal(1, diagnostics.Polling.UnavailableProperties);
        Assert.Equal(TimeSpan.FromMilliseconds(12), diagnostics.Polling.LastPollDuration);
        Assert.Equal(time, diagnostics.Polling.LastPollTime);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusClientDiagnosticsTests"`
Expected: build error, `ModbusClientDiagnostics` does not exist.

- [ ] **Step 3: Implement the diagnostics**

```csharp
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Modbus.Polling;

namespace Namotion.Interceptor.Modbus;

/// <summary>
/// What a Modbus client source reports about its connection and polling.
/// </summary>
public sealed class ModbusClientDiagnostics : SourceDiagnostics
{
    internal ModbusClientDiagnostics(SourceMetrics metrics, ModbusPollingMetrics pollingMetrics)
        : base(metrics)
    {
        Polling = new ModbusPollingDiagnostics(pollingMetrics);
    }

    public ModbusPollingDiagnostics Polling { get; }
}

/// <summary>
/// Polling statistics of a Modbus client source.
/// </summary>
public sealed class ModbusPollingDiagnostics
{
    private readonly ModbusPollingMetrics _metrics;

    internal ModbusPollingDiagnostics(ModbusPollingMetrics metrics)
    {
        _metrics = metrics;
    }

    public long TotalPolls => _metrics.TotalPolls;

    /// <summary>
    /// Gets the number of read requests answered with a Modbus exception response since the source started or the diagnostics were last reset.
    /// </summary>
    public long FailedBatches => _metrics.FailedBatches;

    /// <summary>
    /// Gets the number of read requests per poll cycle.
    /// </summary>
    public int BatchCount => _metrics.BatchCount;

    /// <summary>
    /// Gets the number of mappings the device rejected, which are not read until the next connect.
    /// </summary>
    public int UnavailableProperties => _metrics.UnavailableProperties;

    public TimeSpan? LastPollDuration => _metrics.LastPollDuration;

    public DateTimeOffset? LastPollTime => _metrics.LastPollTime;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusClientDiagnosticsTests"`
Expected: PASS, 1 test.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Modbus/ModbusClientDiagnostics.cs src/Namotion.Interceptor.Modbus.Tests/ModbusClientDiagnosticsTests.cs
git commit -m "feat: add Modbus client diagnostics"
```

---

## Task 11: Discovery and its context

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/IModbusDiscovery.cs`
- Create: `src/Namotion.Interceptor.Modbus/ModbusDiscoveryContext.cs`
- Test: `src/Namotion.Interceptor.Modbus.Tests/ModbusDiscoveryContextTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus.Tests.Testing;

namespace Namotion.Interceptor.Modbus.Tests;

public partial class ModbusDiscoveryContextTests
{
    [InterceptorSubject]
    public partial class ContextSubject
    {
        public partial int? Value { get; set; }
    }

    private static ModbusDiscoveryContext Create(FakeRegisterReader reader) => new(source: null!, reader, defaultUnitId: 1);

    [Fact]
    public async Task WhenReadingHoldingRegisters_ThenValuesAreDecodedWithTheDefaultUnit()
    {
        // Arrange
        var reader = new FakeRegisterReader();
        reader.SetRegister(400, 3);
        reader.SetRegister(401, 92);
        var context = Create(reader);

        // Act
        var registers = await context.ReadHoldingRegistersAsync(400, 2);

        // Assert
        Assert.Equal(new ushort[] { 3, 92 }, registers);
        Assert.Equal((byte)1, Assert.Single(reader.Requests).UnitId);
    }

    [Fact]
    public async Task WhenUnitIdIsGiven_ThenItIsUsed()
    {
        // Arrange
        var reader = new FakeRegisterReader();
        reader.SetRegister(0, 5, ModbusAddressSpace.InputRegister, unitId: 4);
        var context = Create(reader);

        // Act
        var registers = await context.ReadInputRegistersAsync(0, 1, unitId: 4);

        // Assert
        Assert.Equal(new ushort[] { 5 }, registers);
    }

    [Fact]
    public async Task WhenReadingDiscreteInputs_ThenBitsAreDecoded()
    {
        // Arrange
        var reader = new FakeRegisterReader();
        reader.SetBit(10001, true, ModbusAddressSpace.DiscreteInput);
        var context = Create(reader);

        // Act
        var bits = await context.ReadDiscreteInputsAsync(10000, 3);

        // Assert
        Assert.Equal(new[] { false, true, false }, bits);
    }

    [Fact]
    public void WhenExcludingProperty_ThenItIsInTheExcludedSet()
    {
        // Arrange
        var context = Create(new FakeRegisterReader());
        var property = new PropertyReference(new ContextSubject(), nameof(ContextSubject.Value));

        // Act
        context.ExcludeProperty(property);

        // Assert
        Assert.Contains(property, context.ExcludedProperties);
    }

    [Fact]
    public async Task WhenInvalidated_ThenReadsAndExclusionsThrow()
    {
        // Arrange
        var context = Create(new FakeRegisterReader());
        context.Invalidate();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.ReadHoldingRegistersAsync(0, 1));
        Assert.Throws<ObjectDisposedException>(() =>
            context.ExcludeProperty(new PropertyReference(new ContextSubject(), nameof(ContextSubject.Value))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(126)]
    public async Task WhenRegisterCountIsOutOfRange_ThenArgumentOutOfRangeExceptionIsThrown(int count)
    {
        // Arrange
        var context = Create(new FakeRegisterReader());

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => context.ReadHoldingRegistersAsync(0, count));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusDiscoveryContextTests"`
Expected: build error, `ModbusDiscoveryContext` does not exist.

- [ ] **Step 3: Implement the discovery interface**

`IModbusDiscovery.cs`:

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Implemented by a source's root subject to inspect the device on every connect (first connect and each
/// reconnect), before the connector resolves its register bindings. Typical uses are firmware gating and
/// runtime discovery.
/// </summary>
public interface IModbusDiscovery
{
    /// <summary>
    /// Called after the connection is established. All context calls must complete before the returned task completes:
    /// the context is invalid afterwards and polling then uses the connection.
    /// Throwing fails the connect attempt, which is retried.
    /// </summary>
    Task DiscoverAsync(ModbusDiscoveryContext context, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement the context**

`ModbusDiscoveryContext.cs`:

```csharp
using System.Buffers.Binary;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Modbus.Transport;

namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Raw access to the connected device for <see cref="IModbusDiscovery.DiscoverAsync"/>. Only valid while it runs.
/// </summary>
public sealed class ModbusDiscoveryContext
{
    private readonly IModbusRegisterReader _reader;
    private readonly byte _defaultUnitId;
    private readonly HashSet<PropertyReference> _excludedProperties = new(PropertyReference.Comparer);
    private bool _isInvalidated;

    internal ModbusDiscoveryContext(ISubjectSource source, IModbusRegisterReader reader, byte defaultUnitId)
    {
        Source = source;
        _reader = reader;
        _defaultUnitId = defaultUnitId;
    }

    /// <summary>
    /// Gets the source, for applying values the discovery reads with <c>SetValueFromSource</c>.
    /// </summary>
    public ISubjectSource Source { get; }

    internal IReadOnlySet<PropertyReference> ExcludedProperties => _excludedProperties;

    /// <exception cref="ModbusResponseException">The device rejected the request.</exception>
    public async Task<ushort[]> ReadHoldingRegistersAsync(int address, int count, byte? unitId = null, CancellationToken cancellationToken = default)
        => ToRegisters(await ReadAsync(ModbusAddressSpace.HoldingRegister, address, count, 125, unitId, cancellationToken).ConfigureAwait(false), count);

    /// <exception cref="ModbusResponseException">The device rejected the request.</exception>
    public async Task<ushort[]> ReadInputRegistersAsync(int address, int count, byte? unitId = null, CancellationToken cancellationToken = default)
        => ToRegisters(await ReadAsync(ModbusAddressSpace.InputRegister, address, count, 125, unitId, cancellationToken).ConfigureAwait(false), count);

    /// <exception cref="ModbusResponseException">The device rejected the request.</exception>
    public async Task<bool[]> ReadCoilsAsync(int address, int count, byte? unitId = null, CancellationToken cancellationToken = default)
        => ToBits(await ReadAsync(ModbusAddressSpace.Coil, address, count, 2000, unitId, cancellationToken).ConfigureAwait(false), count);

    /// <exception cref="ModbusResponseException">The device rejected the request.</exception>
    public async Task<bool[]> ReadDiscreteInputsAsync(int address, int count, byte? unitId = null, CancellationToken cancellationToken = default)
        => ToBits(await ReadAsync(ModbusAddressSpace.DiscreteInput, address, count, 2000, unitId, cancellationToken).ConfigureAwait(false), count);

    /// <summary>
    /// Excludes a mapped property from this connection's read plan: it is neither claimed nor read. Has no effect for a property the connector does not map.
    /// </summary>
    public void ExcludeProperty(PropertyReference property)
    {
        ThrowIfInvalidated();
        _excludedProperties.Add(property);
    }

    internal void Invalidate() => _isInvalidated = true;

    private Task<ReadOnlyMemory<byte>> ReadAsync(
        ModbusAddressSpace space, int address, int count, int maximumCount, byte? unitId, CancellationToken cancellationToken)
    {
        ThrowIfInvalidated();
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, maximumCount);
        ArgumentOutOfRangeException.ThrowIfNegative(address);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(address + count - 1, 65535, nameof(address));
        return _reader.ReadAsync(unitId ?? _defaultUnitId, space, address, count, cancellationToken);
    }

    private static ushort[] ToRegisters(ReadOnlyMemory<byte> data, int count)
    {
        var registers = new ushort[count];
        var span = data.Span;
        for (var index = 0; index < count; index++)
        {
            registers[index] = BinaryPrimitives.ReadUInt16BigEndian(span[(index * 2)..]);
        }

        return registers;
    }

    private static bool[] ToBits(ReadOnlyMemory<byte> data, int count)
    {
        var bits = new bool[count];
        var span = data.Span;
        for (var index = 0; index < count; index++)
        {
            bits[index] = ((span[index / 8] >> (index % 8)) & 1) != 0;
        }

        return bits;
    }

    private void ThrowIfInvalidated()
    {
        if (_isInvalidated)
        {
            throw new ObjectDisposedException(nameof(ModbusDiscoveryContext), "The context is only valid while DiscoverAsync runs.");
        }
    }
}
```

Note: `ReadAsync` validates synchronously, so a range error surfaces from the awaiting public method as a faulted task, which `Assert.ThrowsAsync` observes. The conversion to `ushort[]`/`bool[]` happens right after the await, before any later read can reuse the connection's buffer.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusDiscoveryContextTests"`
Expected: PASS, 7 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.Modbus.Tests
git commit -m "feat: add Modbus discovery and its context"
```

---
## Task 12: The client source

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/ModbusSubjectClientSource.cs`
- Create: `src/Namotion.Interceptor.Modbus/ModbusSubjectExtensions.cs` (only `CreateModbusClientSource` in this task; DI in Task 13)
- Test: `src/Namotion.Interceptor.Modbus.Tests/ModbusSubjectClientSourceTests.cs`

Lifecycle contract of `SubjectSourceBase` (read `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs` `RunAsync`): per attempt the base calls `StartBuffering`, then `StartListeningAsync`, then `LoadInitialStateAndResumeAsync` (which calls `LoadInitialStateAsync`, runs the returned action under the writer lock, replays buffered updates and reports `Synchronized`), then runs the change queue processor until stopped. The base retries only failures of that sequence. Failures after it (a dropped connection while polling) are handled by the source's own reconnect loop, following `MqttSubjectClientSource.RunMonitorWithKillRestartAsync`.

This source:
- `StartListeningAsync`: connect, run discovery, resolve and claim, build the poller, `MarkOperational`, start the poll loop via `BackgroundTaskLifetime` (cleanup closes the connection).
- `LoadInitialStateAsync`: one full read; the returned action applies every value and opens the poll loop's gate, so the loop never overlaps the initial read.
- Poll loop: each polling period, inside `RunAttemptAsync` (so `FaultType.Kill` works), read and apply through `SubjectPropertyWriter.Write`. Any exception that is not cancellation means the connection is lost: report it, `MarkNotOperational`, `StartBuffering`, close, then retry `ConnectAndPrepareAsync` plus `LoadInitialStateAndResumeAsync` every `RetryTime` until it succeeds, then `MarkOperational`.
- `WriteChangesAsync`: sends nothing, logs a warning once per property per connection, asks the poller to reapply the device value next cycle, returns `WriteResult.Success`.

- [ ] **Step 1: Write the failing integration tests**

`ModbusSubjectClientSourceTests.cs`:

```csharp
using FluentModbus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Modbus.Tests.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Modbus.Tests;

[Trait("Category", "Integration")]
[Collection(ModbusIntegrationCollection.Name)]
public partial class ModbusSubjectClientSourceTests
{
    private static readonly ModbusFunctionCode[] ReadFunctionCodes =
    [
        ModbusFunctionCode.ReadCoils,
        ModbusFunctionCode.ReadDiscreteInputs,
        ModbusFunctionCode.ReadHoldingRegisters,
        ModbusFunctionCode.ReadInputRegisters
    ];

    [InterceptorSubject]
    public partial class TestDevice : IModbusDiscovery
    {
        private int _discoveryCount;

        [ModbusRegister(0, ModbusDataType.S16, Scale = 0.1)]
        public partial decimal? Temperature { get; set; }

        [ModbusRegister(1, ModbusDataType.U16)]
        public partial int? Counter { get; set; }

        [ModbusRegister(10, ModbusDataType.U32, Space = ModbusAddressSpace.InputRegister)]
        public partial long? Energy { get; set; }

        [ModbusRegister(3, ModbusDataType.Boolean, Space = ModbusAddressSpace.Coil)]
        public partial bool? Pump { get; set; }

        [ModbusRegister(1, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
        public partial bool? Alarm { get; set; }

        [ModbusRegister(20, ModbusDataType.U16)]
        public partial int? Optional { get; set; }

        public partial SecondUnit? Second { get; set; }

        public Func<ModbusDiscoveryContext, CancellationToken, Task>? OnDiscover { get; set; }

        public int DiscoveryCount => Volatile.Read(ref _discoveryCount);

        public Task DiscoverAsync(ModbusDiscoveryContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _discoveryCount);
            return OnDiscover?.Invoke(context, cancellationToken) ?? Task.CompletedTask;
        }
    }

    [ModbusUnitId(2)]
    [InterceptorSubject]
    public partial class SecondUnit
    {
        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? Value { get; set; }
    }

    private static void SeedServer(ModbusTestServer server)
    {
        server.SetHoldingRegister<short>(0, 215);
        server.SetHoldingRegister<ushort>(1, 42);
        server.SetHoldingRegister<ushort>(20, 7);
        server.SetInputRegister<uint>(10, 100000);
        server.SetCoil(3, true);
        server.SetDiscreteInput(1, true);
    }

    private static async Task<(TestDevice Device, ModbusSubjectClientSource Source, SourceStateRecorder Recorder)> StartAsync(
        ModbusTestServer server, Action<TestDevice>? configure = null)
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        var device = new TestDevice(context);
        configure?.Invoke(device);

        var source = device.CreateModbusClientSource(
            new ModbusClientConfiguration
            {
                Host = "127.0.0.1",
                Port = server.Port,
                PollingInterval = TimeSpan.FromMilliseconds(100),
                RetryTime = TimeSpan.FromMilliseconds(200),
                RequestTimeout = TimeSpan.FromSeconds(2)
            },
            NullLogger.Instance);

        var recorder = SourceStateRecorder.SubscribeTo(source);
        await source.StartAsync(CancellationToken.None);
        await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should synchronize.", SourceState.Synchronized);
        return (device, source, recorder);
    }

    [Fact]
    public async Task WhenSourceStarts_ThenInitialStateIsLoaded()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);

        // Act
        var (device, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            Assert.Equal(21.5m, device.Temperature);
            Assert.Equal(42, device.Counter);
            Assert.Equal(100000L, device.Energy);
            Assert.Equal(true, device.Pump);
            Assert.Equal(true, device.Alarm);
            Assert.Equal(7, device.Optional);
            Assert.Equal(1, device.DiscoveryCount);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenRegisterChanges_ThenPropertyIsUpdated()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var (device, source, recorder) = await StartAsync(server);
        try
        {
            // Act
            server.SetHoldingRegister<ushort>(1, 43);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => device.Counter == 43, TimeSpan.FromSeconds(10), message: "Counter should update.");
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenPropertyIsWrittenLocally_ThenNothingIsSentAndTheDeviceValueIsRestored()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var (device, source, recorder) = await StartAsync(server);
        try
        {
            // Act
            device.Counter = 999;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => device.Counter == 42, TimeSpan.FromSeconds(10), message: "The device value should be restored.");
            Assert.All(server.Requests, request => Assert.Contains(request.FunctionCode, ReadFunctionCodes));
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenDiscoveryExcludesProperty_ThenItIsNeitherClaimedNorRead()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);

        // Act
        var (device, source, recorder) = await StartAsync(server, testDevice => testDevice.OnDiscover = (context, _) =>
        {
            context.ExcludeProperty(new PropertyReference(testDevice, nameof(TestDevice.Optional)));
            return Task.CompletedTask;
        });
        try
        {
            // Assert
            Assert.Null(device.Optional);
            Assert.False(new PropertyReference(device, nameof(TestDevice.Optional)).TryGetSource(out _));
            Assert.DoesNotContain(server.Requests, request =>
                request.FunctionCode == ModbusFunctionCode.ReadHoldingRegisters &&
                request.Address <= 20 && request.Address + request.Quantity > 20);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenDiscoveryReadsAllSpaces_ThenValuesAreReturnedAndTheContextExpiresAfterwards()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        ushort[]? holdingRegisters = null;
        ushort[]? inputRegisters = null;
        bool[]? coils = null;
        bool[]? discreteInputs = null;
        ModbusDiscoveryContext? capturedContext = null;

        // Act
        var (_, source, recorder) = await StartAsync(server, testDevice => testDevice.OnDiscover = async (context, cancellationToken) =>
        {
            capturedContext = context;
            holdingRegisters = await context.ReadHoldingRegistersAsync(0, 2, cancellationToken: cancellationToken);
            inputRegisters = await context.ReadInputRegistersAsync(10, 2, cancellationToken: cancellationToken);
            coils = await context.ReadCoilsAsync(0, 4, cancellationToken: cancellationToken);
            discreteInputs = await context.ReadDiscreteInputsAsync(0, 2, cancellationToken: cancellationToken);
        });
        try
        {
            // Assert (100000 is 0x000186A0: high word 1, low word 0x86A0)
            Assert.Equal(new ushort[] { 215, 42 }, holdingRegisters);
            Assert.Equal(new ushort[] { 1, 0x86A0 }, inputRegisters);
            Assert.Equal(new[] { false, false, false, true }, coils);
            Assert.Equal(new[] { false, true }, discreteInputs);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                capturedContext!.ReadHoldingRegistersAsync(0, 1));
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenDeviceRejectsARegister_ThenNeighboursUpdateAndTheRejectedOneIsUnavailable()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        server.RejectAddress(ModbusAddressSpace.HoldingRegister, 1);

        // Act
        var (device, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            Assert.Equal(21.5m, device.Temperature);
            Assert.Null(device.Counter);
            Assert.Equal(1, source.Diagnostics.Polling.UnavailableProperties);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenSubjectsUseDifferentUnits_ThenEachUnitIsRead()
    {
        // Arrange
        using var server = new ModbusTestServer(1, 2);
        server.Start();
        SeedServer(server);
        server.SetHoldingRegister<ushort>(0, 99, unitId: 2);

        // Act
        var (device, source, recorder) = await StartAsync(server, testDevice => testDevice.Second = new SecondUnit());
        try
        {
            // Assert
            Assert.Equal(21.5m, device.Temperature);
            Assert.Equal(99, device.Second!.Value);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenServerRestarts_ThenSourceReconnectsAndRunsDiscoveryAgain()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var (device, source, recorder) = await StartAsync(server);
        try
        {
            // Act
            server.Stop();
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The outage should be reported.",
                SourceState.Synchronized, SourceState.Synchronizing);
            server.Start();
            SeedServer(server);
            server.SetHoldingRegister<ushort>(1, 77);

            // Assert
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should recover.",
                SourceState.Synchronized, SourceState.Synchronizing, SourceState.Synchronized);
            await AsyncTestHelpers.WaitUntilAsync(() => device.Counter == 77, TimeSpan.FromSeconds(10), message: "Counter should update after the reconnect.");
            Assert.True(device.DiscoveryCount >= 2);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenKillFaultIsInjected_ThenSourceRecovers()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);
        var (_, source, recorder) = await StartAsync(server);
        try
        {
            // The kill only acts inside a poll attempt, so wait until the poll loop runs.
            await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.Polling.TotalPolls >= 2, TimeSpan.FromSeconds(10), message: "Polling should start.");

            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);

            // Assert
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The source should recover from the kill.",
                SourceState.Synchronized, SourceState.Synchronizing, SourceState.Synchronized);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenSourceIsSynchronized_ThenDiagnosticsReportOperationalStateAndClaims()
    {
        // Arrange
        using var server = new ModbusTestServer();
        server.Start();
        SeedServer(server);

        // Act
        var (_, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            Assert.True(source.Diagnostics.IsOperational);
            Assert.Equal(6, source.Diagnostics.ClaimedPropertyCount);
            await AsyncTestHelpers.WaitUntilAsync(() => source.Diagnostics.Polling.TotalPolls >= 2, TimeSpan.FromSeconds(10), message: "Polling should continue.");
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusSubjectClientSourceTests"`
Expected: build error, `ModbusSubjectClientSource` and `CreateModbusClientSource` do not exist.

- [ ] **Step 3: Implement the source**

`ModbusSubjectClientSource.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Modbus.Mapping;
using Namotion.Interceptor.Modbus.Polling;
using Namotion.Interceptor.Modbus.Transport;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Polls Modbus TCP registers into the subject properties mapped with
/// <see cref="Attributes.ModbusRegisterAttribute"/>. Read only: local changes are not sent to the device and
/// are replaced by the device value on the next poll.
/// </summary>
public sealed class ModbusSubjectClientSource : SubjectSourceBase, IFaultInjectable, IAsyncDisposable
{
    private static readonly IReadOnlySet<PropertyReference> NoExcludedProperties = new HashSet<PropertyReference>();

    private readonly IInterceptorSubject _subject;
    private readonly ModbusClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly SourceOwnershipManager _ownership;
    private readonly ModbusPollingMetrics _pollingMetrics = new();
    private readonly ConcurrentDictionary<PropertyReference, byte> _writeWarnings = new(PropertyReference.Comparer);

    private volatile ModbusConnection? _connection;
    private volatile ModbusPoller? _poller;
    private volatile SubjectPropertyWriter? _propertyWriter;
    private volatile TaskCompletionSource? _initialLoadGate;
    private int _disposed;

    internal ModbusSubjectClientSource(IInterceptorSubject subject, ModbusClientConfiguration configuration, ILogger logger)
        : base(subject.Context, logger, configuration.BufferTime, configuration.RetryTime)
    {
        configuration.Validate();

        _subject = subject;
        _configuration = configuration;
        _logger = logger;
        _ownership = new SourceOwnershipManager(this);

        Metrics.RegisterClaimedProperties(() => _ownership.Count);
        Metrics.RegisterResettable(_pollingMetrics);
        Diagnostics = new ModbusClientDiagnostics(Metrics, _pollingMetrics);
    }

    /// <inheritdoc />
    public override IInterceptorSubject RootSubject => _subject;

    /// <summary>
    /// Gets what this source reports about its connection and polling.
    /// </summary>
    public override ModbusClientDiagnostics Diagnostics { get; }

    /// <inheritdoc />
    protected override async Task<IAsyncDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        _propertyWriter = propertyWriter;
        var initialLoadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _initialLoadGate = initialLoadGate;

        await ConnectAndPrepareAsync(cancellationToken).ConfigureAwait(false);
        Metrics.MarkOperational();

        return BackgroundTaskLifetime.Start(
            cancellationToken,
            _logger,
            token => RunPollLoopAsync(initialLoadGate.Task, token),
            () =>
            {
                DisposeConnection();
                Metrics.MarkNotOperational();
                return ValueTask.CompletedTask;
            });
    }

    /// <inheritdoc />
    public override async Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        var connection = _connection ?? throw new InvalidOperationException("The Modbus connection is not established.");
        var poller = _poller ?? throw new InvalidOperationException("The Modbus read plan is not built.");

        await poller.ReadAsync(connection, cancellationToken).ConfigureAwait(false);

        var timestamp = DateTimeOffset.UtcNow;
        var initialLoadGate = _initialLoadGate;
        return () =>
        {
            poller.ApplyChanges(
                (Source: this, Timestamp: timestamp),
                static (state, property, value) => property.SetValueFromSource(state.Source, state.Timestamp, state.Timestamp, value));

            // Opened only after the apply, so the poll loop never reads while the initial values are applied.
            initialLoadGate?.TrySetResult();
        };
    }

    /// <inheritdoc />
    public override ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var poller = _poller;
        foreach (var change in changes.Span)
        {
            if (_writeWarnings.TryAdd(change.Property, 0))
            {
                _logger.LogWarning(
                    "Property {PropertyName} is read from Modbus and cannot be written in this version; the next poll restores the device value.",
                    change.Property.Name);
            }

            poller?.RequestReapply(change.Property);
        }

        // Success, not Failure: a failure would park the change in the retry queue for good.
        return new ValueTask<WriteResult>(WriteResult.Success);
    }

    /// <inheritdoc />
    async Task IFaultInjectable.InjectFaultAsync(FaultType faultType, CancellationToken cancellationToken)
    {
        switch (faultType)
        {
            case FaultType.Kill:
                await ForceKillCurrentAttemptAsync().ConfigureAwait(false);
                break;

            case FaultType.Disconnect:
                _connection?.Dispose();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(faultType), faultType, null);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        DisposeResources();
        base.Dispose();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        DisposeResources();
        base.Dispose();
    }

    private async Task ConnectAndPrepareAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Connecting to Modbus server at {Host}:{Port}.", _configuration.Host, _configuration.Port);

        var connection = await ModbusConnection.ConnectAsync(
            _configuration.Host, _configuration.Port, _configuration.RequestTimeout, cancellationToken).ConfigureAwait(false);
        try
        {
            var excludedProperties = NoExcludedProperties;
            if (_subject is IModbusDiscovery discovery)
            {
                var context = new ModbusDiscoveryContext(this, connection, _configuration.UnitId);
                try
                {
                    await discovery.DiscoverAsync(context, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    context.Invalidate();
                }

                excludedProperties = context.ExcludedProperties;
            }

            var bindings = ModbusRegisterResolver.Resolve(_subject, _configuration.UnitId, excludedProperties);
            var claimedBindings = ClaimOwnership(bindings);
            var poller = new ModbusPoller(claimedBindings, _configuration.MaximumRegisterGap, _pollingMetrics, _logger);

            _writeWarnings.Clear();
            var previousConnection = _connection;
            _connection = connection;
            previousConnection?.Dispose();
            _poller = poller;

            _logger.LogInformation(
                "Connected to Modbus server at {Host}:{Port}: {PropertyCount} properties in {BatchCount} read requests per poll.",
                _configuration.Host, _configuration.Port, claimedBindings.Count, poller.Batches.Count);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private List<ModbusRegisterBinding> ClaimOwnership(List<ModbusRegisterBinding> bindings)
    {
        var claimedBindings = new List<ModbusRegisterBinding>(bindings.Count);
        var claimedProperties = new HashSet<PropertyReference>(PropertyReference.Comparer);
        foreach (var binding in bindings)
        {
            if (_ownership.ClaimSource(binding.Property))
            {
                claimedBindings.Add(binding);
                claimedProperties.Add(binding.Property);
            }
            else
            {
                _logger.LogError("Property {PropertyPath} is owned by another source and is not read from Modbus.", binding.Path);
            }
        }

        foreach (var property in _ownership.Properties)
        {
            if (!claimedProperties.Contains(property))
            {
                _ownership.ReleaseSource(property);
            }
        }

        return claimedBindings;
    }

    private async Task RunPollLoopAsync(Task initialLoadCompleted, CancellationToken cancellationToken)
    {
        try
        {
            await initialLoadCompleted.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            Exception? connectionFailure = null;
            var wasForceKilled = false;

            await RunAttemptAsync(cancellationToken, async attempt =>
            {
                try
                {
                    await PollUntilFailureAsync(attempt.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (OperationCanceledException) when (attempt.WasForceKilled)
                {
                    wasForceKilled = true;
                }
                catch (Exception exception)
                {
                    connectionFailure = exception;
                }
            }).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (connectionFailure is not null || wasForceKilled)
            {
                await ReconnectAsync(connectionFailure, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PollUntilFailureAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_configuration.PollingInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var connection = _connection ?? throw new InvalidOperationException("The Modbus connection was closed.");
            var poller = _poller ?? throw new InvalidOperationException("The Modbus read plan is not built.");
            var propertyWriter = _propertyWriter ?? throw new InvalidOperationException("The source is not listening.");

            await poller.ReadAsync(connection, cancellationToken).ConfigureAwait(false);

            var timestamp = DateTimeOffset.UtcNow;
            poller.ApplyChanges(
                (Source: this, PropertyWriter: propertyWriter, Timestamp: timestamp),
                static (state, property, value) => state.PropertyWriter.Write(
                    (state.Source, Property: property, Value: value, state.Timestamp),
                    static update => update.Property.SetValueFromSource(update.Source, update.Timestamp, update.Timestamp, update.Value)));
        }
    }

    private async Task ReconnectAsync(Exception? failure, CancellationToken cancellationToken)
    {
        if (failure is not null)
        {
            Metrics.ReportError(failure);
            _logger.LogWarning(failure, "Modbus connection to {Host}:{Port} lost. Reconnecting.", _configuration.Host, _configuration.Port);
        }
        else
        {
            _logger.LogWarning("Modbus connection to {Host}:{Port} was killed. Reconnecting.", _configuration.Host, _configuration.Port);
        }

        Metrics.MarkNotOperational();
        _propertyWriter?.StartBuffering();
        DisposeConnection();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_configuration.RetryTime, cancellationToken).ConfigureAwait(false);
                await ConnectAndPrepareAsync(cancellationToken).ConfigureAwait(false);
                if (_propertyWriter is { } propertyWriter)
                {
                    await propertyWriter.LoadInitialStateAndResumeAsync(cancellationToken).ConfigureAwait(false);
                }

                Metrics.MarkOperational();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Metrics.ReportError(exception);
                _logger.LogError(exception, "Failed to reconnect to Modbus server at {Host}:{Port}.", _configuration.Host, _configuration.Port);
                DisposeConnection();
            }
        }
    }

    private void DisposeConnection()
    {
        var connection = _connection;
        _connection = null;
        connection?.Dispose();
    }

    private void DisposeResources()
    {
        DisposeConnection();
        _ownership.Dispose();
    }
}
```

- [ ] **Step 4: Add the imperative factory**

`ModbusSubjectExtensions.cs` (Task 13 adds the DI methods to this class):

```csharp
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Modbus;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ModbusSubjectExtensions
{
    /// <summary>
    /// Creates a Modbus client source for <paramref name="subject"/>. Start it as a hosted service, for example with
    /// <c>AttachHostedServiceAsync</c>, and dispose it when done.
    /// </summary>
    public static ModbusSubjectClientSource CreateModbusClientSource(
        this IInterceptorSubject subject, ModbusClientConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        return new ModbusSubjectClientSource(subject, configuration, logger);
    }
}
```

- [ ] **Step 5: Run the integration tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusSubjectClientSourceTests"`
Expected: PASS, 10 tests.

If `WhenServerRestarts` fails to bind the port on restart, check that `ModbusTestServer.Stop` disposes the server (it does) and that no other test runs in parallel (the collection disables parallelization).

- [ ] **Step 6: Run all Modbus tests**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests`
Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.Modbus.Tests
git commit -m "feat: add the read-only Modbus client source"
```

---

## Task 13: Dependency injection registration

**Files:**
- Modify: `src/Namotion.Interceptor.Modbus/ModbusSubjectExtensions.cs`
- Test: `src/Namotion.Interceptor.Modbus.Tests/ModbusRegistrationTests.cs`

Follows `src/Namotion.Interceptor.OpcUa/OpcUaSubjectExtensions.cs`: unkeyed registrations use a generated key and a duplicate guard, keyed registrations use the given name.

- [ ] **Step 1: Write the failing tests**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Modbus.Tests;

public partial class ModbusRegistrationTests
{
    [InterceptorSubject]
    public partial class RegistrationSubject
    {
        [ModbusRegister(0, ModbusDataType.U16)]
        public partial int? Value { get; set; }
    }

    private static RegistrationSubject CreateSubject()
        => new(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle());

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(CreateSubject());
        return services;
    }

    [Fact]
    public void WhenUnnamedSourceIsRegistered_ThenItResolvesAsSingletonHostedService()
    {
        // Arrange
        var services = CreateServices();
        services.AddModbusSubjectClientSource<RegistrationSubject>("127.0.0.1");
        using var provider = services.BuildServiceProvider();

        // Act
        var first = provider.GetRequiredService<ModbusSubjectClientSource>();
        var second = provider.GetRequiredService<ModbusSubjectClientSource>();

        // Assert
        Assert.Same(first, second);
        Assert.Contains(first, provider.GetServices<IHostedService>());
    }

    [Fact]
    public void WhenUnnamedSourceIsRegisteredTwice_ThenInvalidOperationExceptionIsThrown()
    {
        // Arrange
        var services = CreateServices();
        services.AddModbusSubjectClientSource<RegistrationSubject>("127.0.0.1");

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => services.AddModbusSubjectClientSource<RegistrationSubject>("127.0.0.2"));
    }

    [Fact]
    public void WhenNamedSourcesAreRegistered_ThenEachResolvesByName()
    {
        // Arrange
        var services = CreateServices();
        services.AddKeyedModbusSubjectClientSource("first", sp => sp.GetRequiredService<RegistrationSubject>(), _ => new ModbusClientConfiguration { Host = "127.0.0.1" });
        services.AddKeyedModbusSubjectClientSource("second", _ => CreateSubject(), _ => new ModbusClientConfiguration { Host = "127.0.0.2" });
        using var provider = services.BuildServiceProvider();

        // Act
        var first = provider.GetRequiredKeyedService<ModbusSubjectClientSource>("first");
        var second = provider.GetRequiredKeyedService<ModbusSubjectClientSource>("second");

        // Assert
        Assert.NotSame(first, second);
    }

    [Fact]
    public void WhenNamedSourceIsRegisteredTwice_ThenInvalidOperationExceptionIsThrown()
    {
        // Arrange
        var services = CreateServices();
        services.AddKeyedModbusSubjectClientSource("device", sp => sp.GetRequiredService<RegistrationSubject>(), _ => new ModbusClientConfiguration { Host = "127.0.0.1" });

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            services.AddKeyedModbusSubjectClientSource("device", sp => sp.GetRequiredService<RegistrationSubject>(), _ => new ModbusClientConfiguration { Host = "127.0.0.1" }));
    }

    [Fact]
    public void WhenSourceIsResolved_ThenConfigurationProviderRunsOnce()
    {
        // Arrange
        var services = CreateServices();
        var calls = 0;
        services.AddModbusSubjectClientSource(
            sp => sp.GetRequiredService<RegistrationSubject>(),
            _ =>
            {
                calls++;
                return new ModbusClientConfiguration { Host = "127.0.0.1" };
            });
        using var provider = services.BuildServiceProvider();

        // Act
        provider.GetRequiredService<ModbusSubjectClientSource>();
        provider.GetServices<IHostedService>().ToList();

        // Assert
        Assert.Equal(1, calls);
    }

    [Fact]
    public void WhenCreatingSourceFromSubject_ThenRootSubjectIsTheSubject()
    {
        // Arrange
        var subject = CreateSubject();

        // Act
        using var source = subject.CreateModbusClientSource(new ModbusClientConfiguration { Host = "127.0.0.1" }, NullLogger.Instance);

        // Assert
        Assert.Same(subject, source.RootSubject);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusRegistrationTests"`
Expected: build error, `AddModbusSubjectClientSource` does not exist.

- [ ] **Step 3: Add the DI methods**

Replace `ModbusSubjectExtensions.cs` with:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Modbus;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ModbusSubjectExtensions
{
    /// <summary>
    /// Creates a Modbus client source for <paramref name="subject"/>. Start it as a hosted service, for example with
    /// <c>AttachHostedServiceAsync</c>, and dispose it when done.
    /// </summary>
    public static ModbusSubjectClientSource CreateModbusClientSource(
        this IInterceptorSubject subject, ModbusClientConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        return new ModbusSubjectClientSource(subject, configuration, logger);
    }

    /// <summary>
    /// Registers a hosted Modbus client source for the <typeparamref name="TSubject"/> singleton.
    /// </summary>
    public static IServiceCollection AddModbusSubjectClientSource<TSubject>(
        this IServiceCollection services, string host, int port = 502)
        where TSubject : IInterceptorSubject
    {
        return services.AddModbusSubjectClientSource(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new ModbusClientConfiguration { Host = host, Port = port });
    }

    /// <summary>
    /// Registers a hosted Modbus client source, resolvable as <see cref="ModbusSubjectClientSource"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">An unnamed Modbus client source is already registered.</exception>
    public static IServiceCollection AddModbusSubjectClientSource(
        this IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, ModbusClientConfiguration> configurationProvider)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ModbusSubjectClientSource) && descriptor.ServiceKey is null))
        {
            throw new InvalidOperationException(
                "An unnamed ModbusSubjectClientSource is already registered. " +
                "Use AddKeyedModbusSubjectClientSource to register multiple sources with distinct names.");
        }

        var key = Guid.NewGuid().ToString();
        RegisterClientSourceCore(services, key, subjectSelector, configurationProvider);
        services.AddSingleton(sp => sp.GetRequiredKeyedService<ModbusSubjectClientSource>(key));
        return services;
    }

    /// <summary>
    /// Registers a hosted Modbus client source, resolvable as a keyed <see cref="ModbusSubjectClientSource"/>
    /// under <paramref name="name"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A Modbus client source with this name is already registered.</exception>
    public static IServiceCollection AddKeyedModbusSubjectClientSource(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, ModbusClientConfiguration> configurationProvider)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ModbusSubjectClientSource) && name.Equals(descriptor.ServiceKey)))
        {
            throw new InvalidOperationException($"A ModbusSubjectClientSource with name '{name}' is already registered.");
        }

        var key = Guid.NewGuid().ToString();
        RegisterClientSourceCore(services, key, subjectSelector, configurationProvider);
        services.AddKeyedSingleton(name, (sp, _) => sp.GetRequiredKeyedService<ModbusSubjectClientSource>(key));
        return services;
    }

    private static void RegisterClientSourceCore(
        IServiceCollection services,
        string key,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, ModbusClientConfiguration> configurationProvider)
    {
        services
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddKeyedSingleton(key, (sp, _) => new ModbusSubjectClientSource(
                sp.GetRequiredKeyedService<IInterceptorSubject>(key),
                sp.GetRequiredKeyedService<ModbusClientConfiguration>(key),
                sp.GetRequiredService<ILogger<ModbusSubjectClientSource>>()))
            .AddSingleton<IHostedService>(sp => sp.GetRequiredKeyedService<ModbusSubjectClientSource>(key));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusRegistrationTests"`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Modbus/ModbusSubjectExtensions.cs src/Namotion.Interceptor.Modbus.Tests/ModbusRegistrationTests.cs
git commit -m "feat: register the Modbus client source with dependency injection"
```

---

## Task 14: Public API snapshot, documentation and spec update

**Files:**
- Create: `src/Namotion.Interceptor.Modbus.Tests/VerifyTests.cs`
- Create: `src/Namotion.Interceptor.Modbus.Tests/VerifyChecksTests.PublicApi.verified.txt` (accepted output)
- Create: `docs/connectors-modbus.md`
- Modify: `docs/connectors.md` (protocol list near line 14)
- Modify: `README.md` (line 11, the "same pattern" paragraph near line 356, the connectors table near line 399)
- Modify: `.github/workflows/build.yml` (Modbus integration test job)
- Modify: `docs/superpowers/specs/2026-09-28-modbus-luxtronik-design.md` (sections 3 and 4)

- [ ] **Step 1: Add the public API snapshot test**

`VerifyTests.cs`:

```csharp
using PublicApiGenerator;

namespace Namotion.Interceptor.Modbus.Tests
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
        public Task PublicApi() => Verify(typeof(ModbusClientConfiguration).Assembly.GeneratePublicApi(new ApiGeneratorOptions
        {
            DenyNamespacePrefixes = ["System", "XamlGeneratedNamespace"]
        }));
    }
}
```

- [ ] **Step 2: Generate and accept the snapshot**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~VerifyChecksTests"`
Expected: `PublicApi` FAILS and writes `VerifyChecksTests.PublicApi.received.txt`.

Review the received file: it must list only the public types of spec section 3 plus `ModbusConfigurationException` and the members inherited from `SubjectSourceBase`, and no `Mapping`, `Transport` or `Polling` types. Then accept it:

```bash
mv src/Namotion.Interceptor.Modbus.Tests/VerifyChecksTests.PublicApi.received.txt src/Namotion.Interceptor.Modbus.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Run the filter again. Expected: PASS, 2 tests.

- [ ] **Step 3: Write `docs/connectors-modbus.md`**

````markdown
# Modbus

The `Namotion.Interceptor.Modbus` package polls Modbus TCP devices into C# objects. Properties are mapped to registers with attributes, and the connector reads them on a fixed interval. This version is read only: local changes are not sent to the device and are replaced by the device value on the next poll.

## Key Features

- Attribute-based mapping of holding registers, input registers, coils and discrete inputs
- U16, S16, U32, S32, F32, String and Boolean values, all four 32-bit word orders
- Static scaling and dynamic scale factors (value = raw * 10^exponent)
- Conversion to numeric types, `decimal`, nullable types, enums and flags enums
- "Not available" raw patterns mapped to `null`
- Per-subject base addresses and unit IDs for reusable model classes
- Contiguous read batching with a configurable gap, split for good when the device rejects a request
- Discovery hook for firmware gating and runtime discovery
- Automatic reconnection and diagnostics

## Client Setup

```csharp
[InterceptorSubject]
public partial class HeatMeter
{
    [ModbusRegister(0, ModbusDataType.S16, Space = ModbusAddressSpace.InputRegister, Scale = 0.1)]
    public partial decimal? FlowTemperature { get; set; }

    [ModbusRegister(10, ModbusDataType.U32, Space = ModbusAddressSpace.InputRegister)]
    public partial long? Energy { get; set; }
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(sp => new HeatMeter(
    InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle()));
builder.Services.AddModbusSubjectClientSource<HeatMeter>("192.168.1.50");
```

The context needs `WithRegistry()` (the connector walks the subject tree) and `WithLifecycle()` (source ownership). To create a source for a subject at runtime, for example in a HomeBlaze device, use `subject.CreateModbusClientSource(configuration, logger)`, start it as a hosted service and dispose it when done. Use `AddKeyedModbusSubjectClientSource` to register several sources.

## Register Mapping

`[ModbusRegister(address, dataType)]` maps one property:

| Setting | Default | Meaning |
|---|---|---|
| `Space` | `HoldingRegister` | `HoldingRegister`, `InputRegister`, `Coil` or `DiscreteInput` (bits require `Boolean`) |
| `WordOrder` | `HighWordFirst` | Register and byte order of 32-bit values |
| `Scale` | `1.0` | Static factor, requires a `float`, `double` or `decimal` property |
| `ScaleFactorProperty` | none | Name of an integer register property on the same subject holding a power-of-ten exponent |
| `Length` | 0 | Register count of `String` values |
| `NotAvailableValue` | `None` | Raw pattern mapped to `null` (`SignedMaximum` is 0x7FFF or 0x7FFFFFFF) |
| `Access` | `ReadWrite` | Declares writability for a later write stage; not enforced yet |

Addresses are raw protocol addresses, without the `3xxxx`/`4xxxx` documentation prefixes and without the +1 offset some tools use.

A subject implementing `IModbusBaseAddressProvider` makes its addresses relative to `BaseAddress`, so one class can describe a repeated block. `IModbusUnitIdProvider` or `[ModbusUnitId]` sets the unit ID for a subject and its children; otherwise `ModbusClientConfiguration.UnitId` applies. Both are read when the connector builds its read plan on connect.

Device libraries can derive from `ModbusRegisterAttribute` to preset values such as `Space` and `NotAvailableValue`.

Invalid mappings (for example `Scale` on an `int` property, or `Length` on a non-string) throw `ModbusConfigurationException` when the source connects.

## Discovery

A root subject implementing `IModbusDiscovery` has `DiscoverAsync` called on every connect and reconnect, before the register bindings are resolved. The `ModbusDiscoveryContext` offers raw reads of all four spaces, `Source` for applying values with `SetValueFromSource`, and `ExcludeProperty` to leave a mapped property unread and unclaimed for this connection. Await every context call before `DiscoverAsync` returns: the context is invalid afterwards and polling then uses the connection. A rejected read throws `ModbusResponseException` with the Modbus exception code. Throwing from `DiscoverAsync` fails the connect attempt, which is retried after `RetryTime`.

## Configuration

| Property | Default | Description |
|---|---|---|
| `Host` | required | Host name or IP address |
| `Port` | 502 | TCP port |
| `UnitId` | 1 | Default unit ID |
| `PollingInterval` | 2 s | Time between poll cycles |
| `RequestTimeout` | 5 s | Connect and request timeout; a timeout counts as a lost connection |
| `RetryTime` | 10 s | Delay before reconnecting |
| `BufferTime` | 8 ms | Change queue buffer time |
| `MaximumRegisterGap` | 0 | Unmapped registers a request may span to merge neighbours |

## Batching and Polling

Mappings are grouped by unit ID and space, sorted by address and merged into requests of at most 125 registers or 2000 bits. With the default gap of 0 only contiguous mappings are merged, because many devices reject reads that touch unmapped addresses. Each cycle reads all requests first and then applies only values whose raw registers changed, so an unchanged cycle converts nothing and raises no change events.

## Resilience

- A request answered with a Modbus exception is re-read one mapping at a time, and its mappings keep being read one at a time until the next connect, so a rejected gap register or a device that rejects reads across block boundaries costs one failed request per connect, not one per cycle. Mappings that still fail alone are logged once, reported in `Diagnostics.Polling.UnavailableProperties` and skipped until the next connect.
- An I/O error, a timeout or a malformed response closes the connection. The source reports `Synchronizing`, reconnects every `RetryTime`, runs discovery again, reloads all values and reports `Synchronized`.

## Diagnostics

`ModbusSubjectClientSource.Diagnostics` extends the common `SourceDiagnostics` (operational state, last error, throughput, claimed properties) with `Polling`: total polls, failed requests, requests per cycle, unavailable mappings, and the duration and time of the last poll.

## Thread Safety

One connection per source, used by one request at a time. Values are applied through the source's property writer, like every other connector.

## Lifecycle

Properties of subjects attached after the source connected are picked up on the next reconnect. Detached subjects release their properties automatically.

## Limitations

- No writes (function codes 5, 6, 15, 16) yet.
- No Modbus RTU and no Modbus server.

## References

- [Modbus Application Protocol Specification V1.1b3](https://modbus.org/docs/Modbus_Application_Protocol_V1_1b3.pdf)
- [Modbus Messaging on TCP/IP Implementation Guide V1.0b](https://modbus.org/docs/Modbus_Messaging_Implementation_Guide_V1_0b.pdf)
- [FluentModbus](https://github.com/Apollo3zehn/FluentModbus)
````

- [ ] **Step 4: Link the page**

In `docs/connectors.md`, in the "Protocol-specific documentation" list after the OPC UA line, add:

```markdown
- [Modbus](connectors-modbus.md) - Read-only Modbus TCP client that polls device registers
```

In `README.md`:
- Line 11: change "Built-in integrations include MQTT, OPC UA, WebSocket, ASP.NET Core, Blazor, GraphQL, and MCP." to "Built-in integrations include MQTT, OPC UA, Modbus, WebSocket, ASP.NET Core, Blazor, GraphQL, and MCP."
- The paragraph starting "The same pattern applies to [OPC UA]" (near line 356): after the OPC UA parenthesis insert ", [Modbus](docs/connectors-modbus.md) (read-only polling of Modbus TCP devices)".
- The connectors table (near line 399): after the `Namotion.Interceptor.OpcUa` row add

```markdown
| **Namotion.Interceptor.Modbus** | Read-only Modbus TCP polling | [Modbus](docs/connectors-modbus.md) |
```

- [ ] **Step 5: Run the Modbus integration tests in CI**

The integration tests are excluded from the main `test` job (`Category!=Integration`), so without a job of their own they never run in CI. In `.github/workflows/build.yml`:

In the `changes` job `outputs`, after the `mqtt` line add:

```yaml
      modbus: ${{ github.event_name != 'pull_request' || steps.filter.outputs.modbus == 'true' }}
```

In the `filters` block, after the `mqtt` filter add:

```yaml
            modbus:
              - *shared
              - 'src/Namotion.Interceptor.Modbus*/**'
```

After the `test-mqtt-integration` job add:

```yaml
  test-modbus-integration:
    timeout-minutes: 15
    runs-on: ubuntu-latest
    needs: changes
    if: needs.changes.outputs.modbus == 'true'
    steps:
      - uses: actions/checkout@v3

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - name: Build
        run: dotnet build src/Namotion.Interceptor.slnx --configuration Release

      - name: Run Modbus integration tests
        run: |
          dotnet test src/Namotion.Interceptor.Modbus.Tests `
            --configuration Release `
            --no-build `
            --filter "Category=Integration" `
            --results-directory ./TestResults `
            --collect:"XPlat Code Coverage" `
            -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura

      - name: Upload Modbus integration test coverage
        uses: actions/upload-artifact@v4
        with:
          name: cobertura-coverage-modbus-integration
          path: '**/TestResults/**/coverage.cobertura.xml'
          if-no-files-found: warn
```

In the `deploy` job, append `test-modbus-integration` to `needs` after `test-mqtt-integration`.

- [ ] **Step 6: Check the spec**

Confirm `docs/superpowers/specs/2026-09-28-modbus-luxtronik-design.md` still matches the implementation: section 3 (public surface including `ModbusResponseException`, `ModbusPollingDiagnostics.BatchCount`) and section 4 (behavior). In 4.7, state that `ReconnectAsync` runs outside `RunAttemptAsync` (as in the MQTT source), so a `FaultType.Kill` injected while the source reconnects is a no-op; only the poll cycles run inside an attempt. Fix the spec wherever else the implementation had to deviate.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Modbus.Tests docs/connectors-modbus.md docs/connectors.md README.md .github/workflows/build.yml docs/superpowers/specs/2026-09-28-modbus-luxtronik-design.md
git commit -m "docs: document the Modbus connector, run its integration tests in CI and snapshot its public API"
```

---

## Task 15: Final verification

- [ ] **Step 1: Build the solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: `Build succeeded`, 0 warnings.

- [ ] **Step 2: Run all Modbus tests including integration**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests`
Expected: all pass.

- [ ] **Step 3: Run the unit test suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: all pass. `ContextRetentionLeakTests` are known to be flaky under the parallel full suite; rerun them in isolation before treating a failure as a regression.

- [ ] **Step 4: Check the docs**

Run: `grep -nP "\x{2014}" docs/connectors-modbus.md` and expect no output (no em dashes).

- [ ] **Step 5: Hand over**

Report the test counts. Do not push or open a pull request unless the user asks; the Luxtronik plan builds on this branch.
