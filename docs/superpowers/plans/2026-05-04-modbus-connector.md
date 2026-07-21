# Modbus Connector Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Namotion.Interceptor.Modbus`, a Modbus-TCP `ISubjectSource` that polls and writes register values declared via `[ModbusRegister]` attributes on `[InterceptorSubject]` classes.

**Architecture:** Single library at `src/Namotion.Interceptor.Modbus/`, layered on FluentModbus. `ISubjectSource` implementation owns the connection, polls registered properties on a fixed interval (default 2 s), batches contiguous reads, applies scale factors, and dispatches FC6/FC16 writes. Reconnect/retry inherits from existing `SubjectSourceBackgroundService`. Tests use FluentModbus.Server in-process for both unit and integration coverage.

**Tech Stack:** C# 13 (partial properties), .NET 9, FluentModbus 5.x (NuGet `FluentModbus`), xUnit, Moq, existing `Namotion.Interceptor.Connectors` infrastructure.

**Spec:** [`docs/superpowers/specs/2026-05-04-modbus-design.md`](../specs/2026-05-04-modbus-design.md)

**Out of scope (covered by Plan 2 / Plan 3):** SunSpec model classes, SunSpec discovery hook, HomeBlaze UI components.

---

## Conventions for executing this plan

- Use `dotnet build src/Namotion.Interceptor.slnx` to compile the whole solution. The repo treats warnings as errors (set in `Directory.Build.props`); the build must end with `0 Warning(s) 0 Error(s)`.
- Use `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "Category!=Integration"` for the fast loop. Integration tests are tagged `[Trait("Category", "Integration")]` and start a FluentModbus.Server in-process; run them with the unfiltered command before each commit.
- All test classes follow the repo convention: method names `When<Condition>_Then<ExpectedBehavior>`, explicit `// Arrange`, `// Act`, `// Assert` comments separating phases. Use `// Act & Assert` for exception tests.
- Never include "Claude" or AI attribution in any commit message.
- All `[InterceptorSubject]` properties are `partial` and initialized in constructors, NOT in field initializers (per `CLAUDE.md`).
- Avoid abbreviations in identifiers (`attribute` not `attr`).
- Use `dotnet format` after each task only if you've touched many files; the repo doesn't enforce style automatically.

---

## Task 1: Scaffold the connector library

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/Namotion.Interceptor.Modbus.csproj`
- Modify: `src/Namotion.Interceptor.slnx` (add `<Project>` entry under `/Connectors/`)

- [ ] **Step 1: Create the project directory and csproj**

Create `src/Namotion.Interceptor.Modbus/Namotion.Interceptor.Modbus.csproj` with this exact content:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net9.0</TargetFramework>
        <Nullable>enable</Nullable>
        <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
        <LangVersion>preview</LangVersion>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="FluentModbus" Version="5.3.0" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\Namotion.Interceptor.Connectors\Namotion.Interceptor.Connectors.csproj" />
        <ProjectReference Include="..\Namotion.Interceptor.Registry\Namotion.Interceptor.Registry.csproj" />
    </ItemGroup>

    <ItemGroup>
        <InternalsVisibleTo Include="Namotion.Interceptor.Modbus.Tests" />
    </ItemGroup>
</Project>
```

- [ ] **Step 2: Register the project in the solution**

Edit `src/Namotion.Interceptor.slnx`. In the `<Folder Name="/Connectors/">` block, add (alphabetical position, after `Namotion.Interceptor.Mqtt.SampleServer`):

```xml
    <Project Path="Namotion.Interceptor.Modbus/Namotion.Interceptor.Modbus.csproj" />
```

- [ ] **Step 3: Verify the solution builds**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`. Any FluentModbus restore will happen automatically; if it fails, run `dotnet restore src/Namotion.Interceptor.slnx` first.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.slnx
git commit -m "feat(modbus): scaffold Namotion.Interceptor.Modbus project"
```

---

## Task 2: Scaffold the test project

**Files:**
- Create: `src/Namotion.Interceptor.Modbus.Tests/Namotion.Interceptor.Modbus.Tests.csproj`
- Modify: `src/Namotion.Interceptor.slnx`

- [ ] **Step 1: Create the test csproj**

Create `src/Namotion.Interceptor.Modbus.Tests/Namotion.Interceptor.Modbus.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net9.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <IsPackable>false</IsPackable>
        <IsTestProject>true</IsTestProject>
        <LangVersion>preview</LangVersion>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.0" />
        <PackageReference Include="Moq" Version="4.20.72" />
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

- [ ] **Step 2: Register the test project**

Edit `src/Namotion.Interceptor.slnx`. In the `<Folder Name="/Tests/">` block, add (alphabetical, after `Namotion.Interceptor.Mqtt.Tests`):

```xml
    <Project Path="Namotion.Interceptor.Modbus.Tests/Namotion.Interceptor.Modbus.Tests.csproj" />
```

- [ ] **Step 3: Add a sentinel test to verify wiring**

Create `src/Namotion.Interceptor.Modbus.Tests/SmokeTests.cs`:

```csharp
namespace Namotion.Interceptor.Modbus.Tests;

public class SmokeTests
{
    [Fact]
    public void WhenTestProjectIsBuilt_ThenItCanReferenceTheConnectorAssembly()
    {
        // Arrange
        var assembly = typeof(Namotion.Interceptor.Modbus.ModbusType).Assembly;

        // Act
        var name = assembly.GetName().Name;

        // Assert
        Assert.Equal("Namotion.Interceptor.Modbus", name);
    }
}
```

This test won't compile yet (the type doesn't exist). It compiles after Task 3.

- [ ] **Step 4: Commit (project skeleton only; build will fail until Task 3 lands its types)**

```bash
git add src/Namotion.Interceptor.Modbus.Tests src/Namotion.Interceptor.slnx
git commit -m "feat(modbus): scaffold Namotion.Interceptor.Modbus.Tests project"
```

---

## Task 3: Public enums

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/ModbusType.cs`
- Create: `src/Namotion.Interceptor.Modbus/AddressSpace.cs`
- Create: `src/Namotion.Interceptor.Modbus/WordOrder.cs`
- Create: `src/Namotion.Interceptor.Modbus/ModbusAccess.cs`

- [ ] **Step 1: Create `ModbusType.cs`**

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Wire types supported by Modbus register decoders/encoders in this connector.
/// </summary>
public enum ModbusType
{
    U16,
    S16,
    U32,
    S32,
    F32,
    String,
}
```

- [ ] **Step 2: Create `AddressSpace.cs`**

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// One of the four Modbus address spaces. Holding registers and coils are read/write;
/// input registers and discrete inputs are read-only.
/// </summary>
public enum AddressSpace
{
    HoldingRegister,
    InputRegister,
    Coil,
    DiscreteInput,
}
```

- [ ] **Step 3: Create `WordOrder.cs`**

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Byte/word order for multi-register values. Modbus on the wire is big-endian
/// per spec; many devices flip word order, so per-property override is required.
/// </summary>
public enum WordOrder
{
    /// <summary>Modbus default: high word first, high byte first within word.</summary>
    AB_CD,
    /// <summary>Word-swapped (low word first), bytes per word still big-endian.</summary>
    CD_AB,
    /// <summary>Byte-swapped within each word.</summary>
    BA_DC,
    /// <summary>Both byte and word swapped (full little-endian).</summary>
    DC_BA,
}
```

- [ ] **Step 4: Create `ModbusAccess.cs`**

```csharp
namespace Namotion.Interceptor.Modbus;

public enum ModbusAccess
{
    ReadOnly,
    WriteOnly,
    ReadWrite,
}
```

- [ ] **Step 5: Build and run smoke test**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~SmokeTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Modbus
git commit -m "feat(modbus): add ModbusType, AddressSpace, WordOrder, ModbusAccess enums"
```

---

## Task 4: Subject-level interfaces

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/IModbusBaseAddressProvider.cs`
- Create: `src/Namotion.Interceptor.Modbus/IModbusUnitIdProvider.cs`

- [ ] **Step 1: Create `IModbusBaseAddressProvider.cs`**

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Implemented by subjects that anchor a relative-address sub-tree at a known
/// Modbus register offset. The connector resolves a property's wire address as
/// <c>BaseAddress + [ModbusRegister].Offset</c> for any property whose owning
/// subject implements this interface; subjects that don't implement it default
/// to <c>BaseAddress = 0</c>, making relative offsets equivalent to absolute
/// addresses.
/// </summary>
public interface IModbusBaseAddressProvider
{
    int BaseAddress { get; }
}
```

- [ ] **Step 2: Create `IModbusUnitIdProvider.cs`**

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Implemented by subjects that should be polled at a non-default Modbus unit ID.
/// Used when unit ID is determined dynamically (e.g. SunSpec discovery probing
/// multiple unit IDs on one TCP connection). Static cases can use
/// <see cref="ModbusUnitIdAttribute"/> on the subject class instead.
/// If neither is present, the connector uses unit ID 1.
/// </summary>
public interface IModbusUnitIdProvider
{
    byte UnitId { get; }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Modbus
git commit -m "feat(modbus): add IModbusBaseAddressProvider and IModbusUnitIdProvider"
```

---

## Task 5: `ModbusRegisterAttribute` and `ModbusUnitIdAttribute`

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/ModbusRegisterAttribute.cs`
- Create: `src/Namotion.Interceptor.Modbus/ModbusUnitIdAttribute.cs`
- Test: `src/Namotion.Interceptor.Modbus.Tests/ModbusRegisterAttributeTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Modbus.Tests/ModbusRegisterAttributeTests.cs`:

```csharp
namespace Namotion.Interceptor.Modbus.Tests;

public class ModbusRegisterAttributeTests
{
    [Fact]
    public void WhenAttributeIsConstructedWithDefaults_ThenSpaceIsHoldingRegisterAndOrderIsAbCd()
    {
        // Arrange & Act
        var attribute = new ModbusRegisterAttribute(offset: 5, type: ModbusType.U16);

        // Assert
        Assert.Equal(5, attribute.Offset);
        Assert.Equal(ModbusType.U16, attribute.Type);
        Assert.Equal(AddressSpace.HoldingRegister, attribute.Space);
        Assert.Equal(WordOrder.AB_CD, attribute.WordOrder);
        Assert.Equal(ModbusAccess.ReadWrite, attribute.Access);
        Assert.Null(attribute.ScaleFactorProperty);
        Assert.Equal(1.0, attribute.Scale);
        Assert.Equal(0, attribute.Length);
    }

    [Fact]
    public void WhenScaleFactorPropertyIsSet_ThenItRoundTripsThroughTheAttribute()
    {
        // Arrange & Act
        var attribute = new ModbusRegisterAttribute(0, ModbusType.U16)
        {
            ScaleFactorProperty = "A_SF",
        };

        // Assert
        Assert.Equal("A_SF", attribute.ScaleFactorProperty);
    }

    [Fact]
    public void WhenUnitIdAttributeIsConstructed_ThenItExposesUnitId()
    {
        // Arrange & Act
        var attribute = new ModbusUnitIdAttribute(7);

        // Assert
        Assert.Equal(7, attribute.UnitId);
    }
}
```

- [ ] **Step 2: Run the test, expect compile failure**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusRegisterAttributeTests"`
Expected: FAIL with compilation error: `ModbusRegisterAttribute` and `ModbusUnitIdAttribute` not defined.

- [ ] **Step 3: Implement `ModbusRegisterAttribute`**

Create `src/Namotion.Interceptor.Modbus/ModbusRegisterAttribute.cs`:

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Annotates an <see cref="InterceptorSubject"/>-property with its Modbus register
/// layout. Offsets are relative to the owning subject's <c>BaseAddress</c> (zero
/// when no <see cref="IModbusBaseAddressProvider"/> is implemented).
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ModbusRegisterAttribute(int offset, ModbusType type) : Attribute
{
    public int Offset { get; } = offset;
    public ModbusType Type { get; } = type;

    /// <summary>
    /// Modbus address space. Defaults to <see cref="AddressSpace.HoldingRegister"/>
    /// (FC3 read, FC6/FC16 write).
    /// </summary>
    public AddressSpace Space { get; init; } = AddressSpace.HoldingRegister;

    /// <summary>
    /// Word order on the wire for multi-register values (u32/s32/f32). Defaults to
    /// <see cref="WordOrder.AB_CD"/> per Modbus spec.
    /// </summary>
    public WordOrder WordOrder { get; init; } = WordOrder.AB_CD;

    /// <summary>
    /// Name of a sibling property holding the dynamic scale factor. The connector
    /// applies <c>raw * 10^sf</c> on read and <c>scaled / 10^sf</c> on write.
    /// Mutually exclusive with <see cref="Scale"/>.
    /// </summary>
    public string? ScaleFactorProperty { get; init; }

    /// <summary>
    /// Static scale multiplier applied to raw values. Defaults to 1.0 (no scaling).
    /// Mutually exclusive with <see cref="ScaleFactorProperty"/>.
    /// </summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>
    /// Register count for <see cref="ModbusType.String"/>. Each register is 2 bytes.
    /// </summary>
    public int Length { get; init; }

    public ModbusAccess Access { get; init; } = ModbusAccess.ReadWrite;
}
```

- [ ] **Step 4: Implement `ModbusUnitIdAttribute`**

Create `src/Namotion.Interceptor.Modbus/ModbusUnitIdAttribute.cs`:

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Static unit-ID assignment for an <see cref="InterceptorSubject"/> class.
/// Use <see cref="IModbusUnitIdProvider"/> instead when the unit ID is dynamic.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ModbusUnitIdAttribute(byte unitId) : Attribute
{
    public byte UnitId { get; } = unitId;
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusRegisterAttributeTests"`
Expected: 3 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.Modbus.Tests
git commit -m "feat(modbus): add ModbusRegisterAttribute and ModbusUnitIdAttribute"
```

---

## Task 6: 16-bit value codec (u16, s16)

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/Internal/ModbusValueCodec.cs`
- Test: `src/Namotion.Interceptor.Modbus.Tests/Internal/ModbusValueCodecTests.cs`

A "register" in this codec is a `ushort` (2 bytes). The codec converts between raw register words on the wire and CLR primitives.

- [ ] **Step 1: Write failing tests for u16/s16**

Create `src/Namotion.Interceptor.Modbus.Tests/Internal/ModbusValueCodecTests.cs`:

```csharp
using Namotion.Interceptor.Modbus.Internal;

namespace Namotion.Interceptor.Modbus.Tests.Internal;

public class ModbusValueCodecTests
{
    [Fact]
    public void WhenDecodingU16_ThenRegisterValueIsReturnedAsUShort()
    {
        // Arrange
        ushort[] registers = [1234];

        // Act
        var value = ModbusValueCodec.Decode(registers, ModbusType.U16, WordOrder.AB_CD, length: 0);

        // Assert
        Assert.Equal((ushort)1234, value);
    }

    [Fact]
    public void WhenDecodingS16WithNegativeValue_ThenSignIsPreserved()
    {
        // Arrange  raw 0xFF9C = -100 as int16
        ushort[] registers = [0xFF9C];

        // Act
        var value = ModbusValueCodec.Decode(registers, ModbusType.S16, WordOrder.AB_CD, length: 0);

        // Assert
        Assert.Equal((short)-100, value);
    }

    [Fact]
    public void WhenEncodingU16_ThenSingleRegisterIsProduced()
    {
        // Arrange
        ushort[] buffer = new ushort[1];

        // Act
        ModbusValueCodec.Encode(buffer, (ushort)4321, ModbusType.U16, WordOrder.AB_CD, length: 0);

        // Assert
        Assert.Equal((ushort)4321, buffer[0]);
    }

    [Fact]
    public void WhenEncodingS16WithNegativeValue_ThenTwosComplementIsWritten()
    {
        // Arrange
        ushort[] buffer = new ushort[1];

        // Act
        ModbusValueCodec.Encode(buffer, (short)-100, ModbusType.S16, WordOrder.AB_CD, length: 0);

        // Assert
        Assert.Equal((ushort)0xFF9C, buffer[0]);
    }

    [Theory]
    [InlineData(ModbusType.U16, 1)]
    [InlineData(ModbusType.S16, 1)]
    public void WhenAskingForRegisterCount_ThenOneIsReturnedFor16BitTypes(ModbusType type, int expected)
    {
        // Act
        var count = ModbusValueCodec.RegisterCount(type, length: 0);

        // Assert
        Assert.Equal(expected, count);
    }
}
```

- [ ] **Step 2: Run tests, expect compile failure (codec missing)**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusValueCodecTests"`
Expected: FAIL.

- [ ] **Step 3: Implement the codec skeleton with u16/s16 support**

Create `src/Namotion.Interceptor.Modbus/Internal/ModbusValueCodec.cs`:

```csharp
namespace Namotion.Interceptor.Modbus.Internal;

/// <summary>
/// Encode/decode CLR primitives to/from raw Modbus register words (16-bit).
/// All multi-register values are mapped through <see cref="WordOrder"/>.
/// </summary>
internal static class ModbusValueCodec
{
    public static int RegisterCount(ModbusType type, int length) => type switch
    {
        ModbusType.U16 or ModbusType.S16 => 1,
        ModbusType.U32 or ModbusType.S32 or ModbusType.F32 => 2,
        ModbusType.String => length,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static object Decode(ReadOnlySpan<ushort> registers, ModbusType type, WordOrder order, int length)
    {
        return type switch
        {
            ModbusType.U16 => registers[0],
            ModbusType.S16 => unchecked((short)registers[0]),
            _ => throw new NotSupportedException($"Type {type} not yet supported."),
        };
    }

    public static void Encode(Span<ushort> registers, object value, ModbusType type, WordOrder order, int length)
    {
        switch (type)
        {
            case ModbusType.U16:
                registers[0] = Convert.ToUInt16(value);
                return;
            case ModbusType.S16:
                registers[0] = unchecked((ushort)Convert.ToInt16(value));
                return;
            default:
                throw new NotSupportedException($"Type {type} not yet supported.");
        }
    }
}
```

- [ ] **Step 4: Run the codec tests**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusValueCodecTests"`
Expected: 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.Modbus.Tests
git commit -m "feat(modbus): add value codec for u16/s16"
```

---

## Task 7: 32-bit codec (u32, s32) with word-order handling

**Files:**
- Modify: `src/Namotion.Interceptor.Modbus/Internal/ModbusValueCodec.cs`
- Modify: `src/Namotion.Interceptor.Modbus.Tests/Internal/ModbusValueCodecTests.cs`

- [ ] **Step 1: Add failing word-order tests**

Append to `ModbusValueCodecTests.cs`:

```csharp
    // Reference value: 0x12345678 = 305419896.
    // AB_CD = high word first, big-endian within word: registers [0x1234, 0x5678]
    // CD_AB = low word first:                            registers [0x5678, 0x1234]
    // BA_DC = byte-swapped within each word:            registers [0x3412, 0x7856]
    // DC_BA = full little-endian:                        registers [0x7856, 0x3412]

    [Theory]
    [InlineData(WordOrder.AB_CD, (ushort)0x1234, (ushort)0x5678)]
    [InlineData(WordOrder.CD_AB, (ushort)0x5678, (ushort)0x1234)]
    [InlineData(WordOrder.BA_DC, (ushort)0x3412, (ushort)0x7856)]
    [InlineData(WordOrder.DC_BA, (ushort)0x7856, (ushort)0x3412)]
    public void WhenDecodingU32WithEachWordOrder_ThenSameValueIsRecovered(
        WordOrder order, ushort r0, ushort r1)
    {
        // Arrange
        ushort[] registers = [r0, r1];

        // Act
        var value = ModbusValueCodec.Decode(registers, ModbusType.U32, order, length: 0);

        // Assert
        Assert.Equal(0x12345678U, value);
    }

    [Theory]
    [InlineData(WordOrder.AB_CD, (ushort)0x1234, (ushort)0x5678)]
    [InlineData(WordOrder.CD_AB, (ushort)0x5678, (ushort)0x1234)]
    [InlineData(WordOrder.BA_DC, (ushort)0x3412, (ushort)0x7856)]
    [InlineData(WordOrder.DC_BA, (ushort)0x7856, (ushort)0x3412)]
    public void WhenEncodingU32WithEachWordOrder_ThenWireBytesMatch(
        WordOrder order, ushort expected0, ushort expected1)
    {
        // Arrange
        ushort[] buffer = new ushort[2];

        // Act
        ModbusValueCodec.Encode(buffer, 0x12345678U, ModbusType.U32, order, length: 0);

        // Assert
        Assert.Equal(expected0, buffer[0]);
        Assert.Equal(expected1, buffer[1]);
    }

    [Fact]
    public void WhenDecodingS32_ThenSignedValueIsReturned()
    {
        // Arrange  -1 in s32 is 0xFFFFFFFF, AB_CD layout is [0xFFFF, 0xFFFF].
        ushort[] registers = [0xFFFF, 0xFFFF];

        // Act
        var value = ModbusValueCodec.Decode(registers, ModbusType.S32, WordOrder.AB_CD, length: 0);

        // Assert
        Assert.Equal(-1, value);
    }
```

- [ ] **Step 2: Run, expect failure (NotSupportedException for u32/s32)**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusValueCodecTests"`
Expected: 9 tests, 4 new ones fail.

- [ ] **Step 3: Add a `WordOrderSwizzle` helper and extend the codec**

Replace the body of `ModbusValueCodec.cs` with:

```csharp
namespace Namotion.Interceptor.Modbus.Internal;

internal static class ModbusValueCodec
{
    public static int RegisterCount(ModbusType type, int length) => type switch
    {
        ModbusType.U16 or ModbusType.S16 => 1,
        ModbusType.U32 or ModbusType.S32 or ModbusType.F32 => 2,
        ModbusType.String => length,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static object Decode(ReadOnlySpan<ushort> registers, ModbusType type, WordOrder order, int length)
    {
        return type switch
        {
            ModbusType.U16 => registers[0],
            ModbusType.S16 => unchecked((short)registers[0]),
            ModbusType.U32 => DecodeU32(registers, order),
            ModbusType.S32 => unchecked((int)DecodeU32(registers, order)),
            _ => throw new NotSupportedException($"Type {type} not yet supported."),
        };
    }

    public static void Encode(Span<ushort> registers, object value, ModbusType type, WordOrder order, int length)
    {
        switch (type)
        {
            case ModbusType.U16:
                registers[0] = Convert.ToUInt16(value);
                return;
            case ModbusType.S16:
                registers[0] = unchecked((ushort)Convert.ToInt16(value));
                return;
            case ModbusType.U32:
                EncodeU32(registers, Convert.ToUInt32(value), order);
                return;
            case ModbusType.S32:
                EncodeU32(registers, unchecked((uint)Convert.ToInt32(value)), order);
                return;
            default:
                throw new NotSupportedException($"Type {type} not yet supported.");
        }
    }

    private static uint DecodeU32(ReadOnlySpan<ushort> registers, WordOrder order)
    {
        // Apply word/byte swap defined by order, then assemble as big-endian u32.
        var (hi, lo) = order switch
        {
            WordOrder.AB_CD => (registers[0],            registers[1]),
            WordOrder.CD_AB => (registers[1],            registers[0]),
            WordOrder.BA_DC => (SwapBytes(registers[0]), SwapBytes(registers[1])),
            WordOrder.DC_BA => (SwapBytes(registers[1]), SwapBytes(registers[0])),
            _               => throw new ArgumentOutOfRangeException(nameof(order), order, null),
        };
        return ((uint)hi << 16) | lo;
    }

    private static void EncodeU32(Span<ushort> registers, uint value, WordOrder order)
    {
        var hi = (ushort)(value >> 16);
        var lo = (ushort)(value & 0xFFFF);

        switch (order)
        {
            case WordOrder.AB_CD: registers[0] = hi;            registers[1] = lo;            break;
            case WordOrder.CD_AB: registers[0] = lo;            registers[1] = hi;            break;
            case WordOrder.BA_DC: registers[0] = SwapBytes(hi); registers[1] = SwapBytes(lo); break;
            case WordOrder.DC_BA: registers[0] = SwapBytes(lo); registers[1] = SwapBytes(hi); break;
            default: throw new ArgumentOutOfRangeException(nameof(order), order, null);
        }
    }

    private static ushort SwapBytes(ushort value) => (ushort)((value >> 8) | (value << 8));
}
```

- [ ] **Step 4: Run all codec tests**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusValueCodecTests"`
Expected: 9 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.Modbus.Tests
git commit -m "feat(modbus): codec support for u32/s32 with word-order swizzle"
```

---

## Task 8: F32 codec

**Files:**
- Modify: `src/Namotion.Interceptor.Modbus/Internal/ModbusValueCodec.cs`
- Modify: `src/Namotion.Interceptor.Modbus.Tests/Internal/ModbusValueCodecTests.cs`

- [ ] **Step 1: Add a failing F32 round-trip test**

Append to `ModbusValueCodecTests.cs`:

```csharp
    [Theory]
    [InlineData(WordOrder.AB_CD)]
    [InlineData(WordOrder.CD_AB)]
    [InlineData(WordOrder.BA_DC)]
    [InlineData(WordOrder.DC_BA)]
    public void WhenF32IsRoundTripped_ThenItRecoversTheOriginalValue(WordOrder order)
    {
        // Arrange
        const float original = 12345.6789f;
        ushort[] buffer = new ushort[2];

        // Act
        ModbusValueCodec.Encode(buffer, original, ModbusType.F32, order, length: 0);
        var decoded = (float)ModbusValueCodec.Decode(buffer, ModbusType.F32, order, length: 0);

        // Assert
        Assert.Equal(original, decoded);
    }
```

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusValueCodecTests"`
Expected: 13 tests, 4 new fail with `NotSupportedException`.

- [ ] **Step 3: Implement F32 by reusing the U32 swizzle**

Modify the `Decode` switch in `ModbusValueCodec.cs` to add:

```csharp
            ModbusType.F32 => BitConverter.UInt32BitsToSingle(DecodeU32(registers, order)),
```

Modify the `Encode` switch to add:

```csharp
            case ModbusType.F32:
                EncodeU32(registers, BitConverter.SingleToUInt32Bits(Convert.ToSingle(value)), order);
                return;
```

- [ ] **Step 4: Run codec tests**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusValueCodecTests"`
Expected: 13 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.Modbus.Tests
git commit -m "feat(modbus): codec support for f32"
```

---

## Task 9: String codec (multi-register ASCII)

**Files:**
- Modify: `src/Namotion.Interceptor.Modbus/Internal/ModbusValueCodec.cs`
- Modify: `src/Namotion.Interceptor.Modbus.Tests/Internal/ModbusValueCodecTests.cs`

Strings on Modbus are typically packed two ASCII chars per register, big-endian (high byte = first char). They are null- or space-padded to the declared length. Empty strings are represented by all zeros.

- [ ] **Step 1: Add failing string tests**

Append to `ModbusValueCodecTests.cs`:

```csharp
    [Fact]
    public void WhenDecodingNullPaddedString_ThenTrailingNullsAreStripped()
    {
        // Arrange  "AB" + nulls = [0x4142, 0x0000, 0x0000].
        ushort[] registers = [0x4142, 0x0000, 0x0000];

        // Act
        var value = (string)ModbusValueCodec.Decode(registers, ModbusType.String, WordOrder.AB_CD, length: 3);

        // Assert
        Assert.Equal("AB", value);
    }

    [Fact]
    public void WhenDecodingSpacePaddedString_ThenTrailingSpacesAreTrimmed()
    {
        // Arrange  "Hi  " = [0x4869, 0x2020].
        ushort[] registers = [0x4869, 0x2020];

        // Act
        var value = (string)ModbusValueCodec.Decode(registers, ModbusType.String, WordOrder.AB_CD, length: 2);

        // Assert
        Assert.Equal("Hi", value);
    }

    [Fact]
    public void WhenEncodingString_ThenItIsPackedTwoCharsPerRegisterAndNullPadded()
    {
        // Arrange
        ushort[] buffer = new ushort[3];

        // Act
        ModbusValueCodec.Encode(buffer, "AB", ModbusType.String, WordOrder.AB_CD, length: 3);

        // Assert
        Assert.Equal((ushort)0x4142, buffer[0]);
        Assert.Equal((ushort)0x0000, buffer[1]);
        Assert.Equal((ushort)0x0000, buffer[2]);
    }

    [Fact]
    public void WhenEncodingStringLongerThanLength_ThenItIsTruncated()
    {
        // Arrange
        ushort[] buffer = new ushort[2];

        // Act
        ModbusValueCodec.Encode(buffer, "ABCDE", ModbusType.String, WordOrder.AB_CD, length: 2);

        // Assert  truncated to "ABCD"
        Assert.Equal((ushort)0x4142, buffer[0]);
        Assert.Equal((ushort)0x4344, buffer[1]);
    }
```

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusValueCodecTests"`
Expected: 17 tests, 4 new fail.

- [ ] **Step 3: Implement string handling**

Add to the `Decode` switch:

```csharp
            ModbusType.String => DecodeString(registers, length),
```

Add to the `Encode` switch:

```csharp
            case ModbusType.String:
                EncodeString(registers, Convert.ToString(value) ?? string.Empty, length);
                return;
```

Append helper methods to the class:

```csharp
    private static string DecodeString(ReadOnlySpan<ushort> registers, int length)
    {
        if (length <= 0) return string.Empty;
        Span<byte> bytes = stackalloc byte[length * 2];
        for (var i = 0; i < length; i++)
        {
            var register = registers[i];
            bytes[i * 2]     = (byte)(register >> 8);
            bytes[i * 2 + 1] = (byte)(register & 0xFF);
        }
        var raw = System.Text.Encoding.ASCII.GetString(bytes);
        return raw.TrimEnd('\0', ' ');
    }

    private static void EncodeString(Span<ushort> registers, string value, int length)
    {
        var maxBytes = length * 2;
        Span<byte> bytes = stackalloc byte[maxBytes];
        var written = System.Text.Encoding.ASCII.GetBytes(value.AsSpan(0, Math.Min(value.Length, maxBytes)), bytes);
        // Remaining bytes are already zero (stackalloc clears the slice).
        for (var i = 0; i < length; i++)
        {
            registers[i] = (ushort)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
        }
        _ = written; // suppress unused warning under analyzers if any
    }
```

- [ ] **Step 4: Run all codec tests**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusValueCodecTests"`
Expected: 17 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.Modbus.Tests
git commit -m "feat(modbus): codec support for ASCII strings"
```

---

## Task 10: Address resolver

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/Internal/ResolvedRegister.cs`
- Create: `src/Namotion.Interceptor.Modbus/Internal/ModbusAddressResolver.cs`
- Test: `src/Namotion.Interceptor.Modbus.Tests/Internal/ModbusAddressResolverTests.cs`

The resolver walks all registered properties under a root subject's context and produces, for each property carrying `[ModbusRegister]`, a `ResolvedRegister` describing where on the wire it lives.

- [ ] **Step 1: Write a failing resolver test using a small subject**

Create `src/Namotion.Interceptor.Modbus.Tests/Internal/ModbusAddressResolverTests.cs`:

```csharp
using Namotion.Interceptor.Modbus.Internal;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Modbus.Tests.Internal;

[InterceptorSubject]
public partial class FixedMapDevice : IModbusBaseAddressProvider
{
    public int BaseAddress { get; init; } = 40000;

    [ModbusRegister(0,  ModbusType.U16)]
    public partial ushort RegisterA { get; set; }

    [ModbusRegister(2,  ModbusType.U32, WordOrder = WordOrder.CD_AB)]
    public partial uint   RegisterB { get; set; }
}

[InterceptorSubject]
[ModbusUnitId(7)]
public partial class StaticUnitDevice
{
    [ModbusRegister(10, ModbusType.S16)]
    public partial short RegisterC { get; set; }
}

public class ModbusAddressResolverTests
{
    [Fact]
    public void WhenSubjectImplementsBaseAddressProvider_ThenAbsoluteAddressIsBasePlusOffset()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var device  = new FixedMapDevice(context);

        // Act
        var resolved = ModbusAddressResolver.Resolve(context).ToList();

        // Assert
        var a = resolved.Single(r => r.Property.Name == nameof(FixedMapDevice.RegisterA));
        var b = resolved.Single(r => r.Property.Name == nameof(FixedMapDevice.RegisterB));
        Assert.Equal((byte)1,         a.UnitId);
        Assert.Equal(AddressSpace.HoldingRegister, a.Space);
        Assert.Equal(40000,            a.AbsoluteAddress);
        Assert.Equal(1,                a.RegisterCount);
        Assert.Equal(40002,            b.AbsoluteAddress);
        Assert.Equal(2,                b.RegisterCount);
        Assert.Equal(WordOrder.CD_AB,  b.WordOrder);
    }

    [Fact]
    public void WhenSubjectHasUnitIdAttribute_ThenItOverridesTheDefault()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        _ = new StaticUnitDevice(context);

        // Act
        var resolved = ModbusAddressResolver.Resolve(context).ToList();

        // Assert
        var c = Assert.Single(resolved);
        Assert.Equal((byte)7, c.UnitId);
        Assert.Equal(10,      c.AbsoluteAddress);
    }
}
```

- [ ] **Step 2: Run, expect compile failure**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusAddressResolverTests"`
Expected: FAIL with compilation error: types missing.

- [ ] **Step 3: Implement `ResolvedRegister`**

Create `src/Namotion.Interceptor.Modbus/Internal/ResolvedRegister.cs`:

```csharp
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Modbus.Internal;

internal sealed record ResolvedRegister(
    RegisteredSubjectProperty Property,
    byte                      UnitId,
    AddressSpace              Space,
    int                       AbsoluteAddress,
    int                       RegisterCount,
    ModbusType                Type,
    WordOrder                 WordOrder,
    ModbusAccess              Access,
    int                       Length,
    string?                   ScaleFactorProperty,
    double                    Scale);
```

- [ ] **Step 4: Implement `ModbusAddressResolver`**

Create `src/Namotion.Interceptor.Modbus/Internal/ModbusAddressResolver.cs`:

```csharp
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Modbus.Internal;

internal static class ModbusAddressResolver
{
    public static IEnumerable<ResolvedRegister> Resolve(IInterceptorSubjectContext context)
    {
        var registry = context.GetService<ISubjectRegistry>()
            ?? throw new InvalidOperationException("Modbus connector requires WithRegistry() on the context.");

        foreach (var registered in registry.RegisteredSubjects)
        {
            var unitId      = ResolveUnitId(registered.Subject);
            var baseAddress = registered.Subject is IModbusBaseAddressProvider p ? p.BaseAddress : 0;

            foreach (var property in registered.Properties.Values)
            {
                var attribute = property.ReflectionAttributes.OfType<ModbusRegisterAttribute>().FirstOrDefault();
                if (attribute is null) continue;

                yield return new ResolvedRegister(
                    Property:        property,
                    UnitId:          unitId,
                    Space:           attribute.Space,
                    AbsoluteAddress: baseAddress + attribute.Offset,
                    RegisterCount:   ModbusValueCodec.RegisterCount(attribute.Type, attribute.Length),
                    Type:            attribute.Type,
                    WordOrder:       attribute.WordOrder,
                    Access:          attribute.Access,
                    Length:          attribute.Length,
                    ScaleFactorProperty: attribute.ScaleFactorProperty,
                    Scale:           attribute.Scale);
            }
        }
    }

    private static byte ResolveUnitId(IInterceptorSubject subject)
    {
        if (subject is IModbusUnitIdProvider provider)
            return provider.UnitId;

        var attribute = subject.GetType().GetCustomAttributes(typeof(ModbusUnitIdAttribute), inherit: true)
            .Cast<ModbusUnitIdAttribute>().FirstOrDefault();
        return attribute?.UnitId ?? (byte)1;
    }
}
```

If `ISubjectRegistry`, `RegisteredSubject`, or `RegisteredSubjectProperty` have different names in this codebase, update accordingly. The reflection-attribute access pattern matches `AttributeBasedPathProvider` in `Namotion.Interceptor.Registry/Paths/AttributeBasedPathProvider.cs`.

- [ ] **Step 5: Run resolver tests**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusAddressResolverTests"`
Expected: 2 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.Modbus.Tests
git commit -m "feat(modbus): address resolver walks registry, computes wire addresses"
```

---

## Task 11: Batch planner

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/Internal/ModbusReadBatch.cs`
- Create: `src/Namotion.Interceptor.Modbus/Internal/ModbusBatchPlanner.cs`
- Test: `src/Namotion.Interceptor.Modbus.Tests/Internal/ModbusBatchPlannerTests.cs`

The planner takes a flat list of `ResolvedRegister` and produces batches of contiguous reads grouped by `(UnitId, Space)`. Each batch fits in a single Modbus PDU (max 125 registers for FC3 and FC4; shared as `MaxRegistersPerRead = 125`).

- [ ] **Step 1: Write failing planner tests**

Create `src/Namotion.Interceptor.Modbus.Tests/Internal/ModbusBatchPlannerTests.cs`:

```csharp
using Namotion.Interceptor.Modbus.Internal;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Modbus.Tests.Internal;

public class ModbusBatchPlannerTests
{
    private static ResolvedRegister R(byte unit, AddressSpace space, int address, int count = 1)
        => new(
            Property:        null!,   // unused in planner
            UnitId:          unit,
            Space:           space,
            AbsoluteAddress: address,
            RegisterCount:   count,
            Type:            ModbusType.U16,
            WordOrder:       WordOrder.AB_CD,
            Access:          ModbusAccess.ReadWrite,
            Length:          0,
            ScaleFactorProperty: null,
            Scale:           1.0);

    [Fact]
    public void WhenAllRegistersAreContiguous_ThenOneBatchIsProduced()
    {
        // Arrange
        var registers = new[] { R(1, AddressSpace.HoldingRegister, 100), R(1, AddressSpace.HoldingRegister, 101), R(1, AddressSpace.HoldingRegister, 102) };

        // Act
        var batches = ModbusBatchPlanner.Plan(registers).ToList();

        // Assert
        var batch = Assert.Single(batches);
        Assert.Equal((byte)1,                     batch.UnitId);
        Assert.Equal(AddressSpace.HoldingRegister, batch.Space);
        Assert.Equal(100,                          batch.StartAddress);
        Assert.Equal(3,                            batch.RegisterCount);
    }

    [Fact]
    public void WhenRegistersHaveAGap_ThenTwoBatchesAreProduced()
    {
        // Arrange
        var registers = new[] { R(1, AddressSpace.HoldingRegister, 100), R(1, AddressSpace.HoldingRegister, 200) };

        // Act
        var batches = ModbusBatchPlanner.Plan(registers).ToList();

        // Assert
        Assert.Equal(2, batches.Count);
        Assert.Equal(100, batches[0].StartAddress);
        Assert.Equal(200, batches[1].StartAddress);
    }

    [Fact]
    public void WhenRegistersBelongToDifferentUnitIds_ThenTheyAreNotMerged()
    {
        // Arrange
        var registers = new[] { R(1, AddressSpace.HoldingRegister, 100), R(2, AddressSpace.HoldingRegister, 100) };

        // Act
        var batches = ModbusBatchPlanner.Plan(registers).ToList();

        // Assert
        Assert.Equal(2, batches.Count);
        Assert.Contains(batches, b => b.UnitId == 1);
        Assert.Contains(batches, b => b.UnitId == 2);
    }

    [Fact]
    public void WhenBatchExceedsPduLimit_ThenItIsSplit()
    {
        // Arrange  130 contiguous u16 registers, PDU limit is 125
        var registers = Enumerable.Range(100, 130)
            .Select(i => R(1, AddressSpace.HoldingRegister, i))
            .ToArray();

        // Act
        var batches = ModbusBatchPlanner.Plan(registers).ToList();

        // Assert
        Assert.Equal(2, batches.Count);
        Assert.Equal(125, batches[0].RegisterCount);
        Assert.Equal(5,   batches[1].RegisterCount);
    }
}
```

- [ ] **Step 2: Run, expect compile failure**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusBatchPlannerTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `ModbusReadBatch`**

Create `src/Namotion.Interceptor.Modbus/Internal/ModbusReadBatch.cs`:

```csharp
namespace Namotion.Interceptor.Modbus.Internal;

internal sealed record ModbusReadBatch(
    byte                       UnitId,
    AddressSpace               Space,
    int                        StartAddress,
    int                        RegisterCount,
    IReadOnlyList<ResolvedRegister> Members);
```

- [ ] **Step 4: Implement `ModbusBatchPlanner`**

Create `src/Namotion.Interceptor.Modbus/Internal/ModbusBatchPlanner.cs`:

```csharp
namespace Namotion.Interceptor.Modbus.Internal;

internal static class ModbusBatchPlanner
{
    /// <summary>Modbus PDU max registers per FC3 / FC4 read response.</summary>
    public const int MaxRegistersPerRead = 125;

    public static IEnumerable<ModbusReadBatch> Plan(IEnumerable<ResolvedRegister> registers)
    {
        var sorted = registers
            .Where(r => r.Access != ModbusAccess.WriteOnly)
            .OrderBy(r => r.UnitId)
            .ThenBy(r => r.Space)
            .ThenBy(r => r.AbsoluteAddress)
            .ToList();

        var batchMembers = new List<ResolvedRegister>();
        byte currentUnit  = 0;
        var  currentSpace = AddressSpace.HoldingRegister;
        var  currentStart = 0;
        var  currentEnd   = 0;   // exclusive: address of the next register that would extend the batch

        foreach (var register in sorted)
        {
            var registerEnd = register.AbsoluteAddress + register.RegisterCount;
            var sameGroup   = batchMembers.Count > 0
                              && register.UnitId == currentUnit
                              && register.Space == currentSpace;
            var contiguous  = sameGroup && register.AbsoluteAddress <= currentEnd;
            var withinLimit = sameGroup && (registerEnd - currentStart) <= MaxRegistersPerRead;

            if (contiguous && withinLimit)
            {
                batchMembers.Add(register);
                currentEnd = Math.Max(currentEnd, registerEnd);
                continue;
            }

            if (batchMembers.Count > 0)
            {
                yield return BuildBatch(currentUnit, currentSpace, currentStart, currentEnd, batchMembers);
                batchMembers = new List<ResolvedRegister>();
            }

            currentUnit  = register.UnitId;
            currentSpace = register.Space;
            currentStart = register.AbsoluteAddress;
            currentEnd   = registerEnd;
            batchMembers.Add(register);
        }

        if (batchMembers.Count > 0)
            yield return BuildBatch(currentUnit, currentSpace, currentStart, currentEnd, batchMembers);
    }

    private static ModbusReadBatch BuildBatch(byte unit, AddressSpace space, int start, int end, List<ResolvedRegister> members)
        => new(unit, space, start, end - start, members);
}
```

- [ ] **Step 5: Run planner tests**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusBatchPlannerTests"`
Expected: 4 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.Modbus.Tests
git commit -m "feat(modbus): batch planner groups contiguous reads within PDU limit"
```

---

## Task 12: Configuration, connect-context, diagnostics skeleton, source interface

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/ModbusClientConfiguration.cs`
- Create: `src/Namotion.Interceptor.Modbus/ModbusConnectContext.cs`
- Create: `src/Namotion.Interceptor.Modbus/IModbusSubjectClientSource.cs`
- Create: `src/Namotion.Interceptor.Modbus/ModbusClientDiagnostics.cs`

These are public surface types with no behavior in this task; behavior lands in Tasks 13-18.

- [ ] **Step 1: Create `ModbusClientConfiguration.cs`**

```csharp
using FluentModbus;

namespace Namotion.Interceptor.Modbus;

public sealed class ModbusClientConfiguration
{
    public required string Host { get; init; }
    public int Port { get; init; } = 1502;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RetryTime    { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan BufferTime   { get; init; } = TimeSpan.FromMilliseconds(8);
    public int      WriteRetryQueueSize { get; init; } = 1024;

    /// <summary>
    /// Optional hook invoked once per successful connect. Use this to perform
    /// device-specific discovery and attach discovered subjects via
    /// <see cref="ISubjectSourceProperty.SetValueFromSource"/>.
    /// </summary>
    public Func<ModbusConnectContext, CancellationToken, Task>? OnConnectAsync { get; set; }
}
```

- [ ] **Step 2: Create `ModbusConnectContext.cs`**

```csharp
using FluentModbus;
using Namotion.Interceptor.Connectors;

namespace Namotion.Interceptor.Modbus;

public sealed record ModbusConnectContext(
    IInterceptorSubject Root,
    ModbusTcpClient     Client,   // FluentModbus type, exposed directly
    ISubjectSource      Source);
```

- [ ] **Step 3: Create `IModbusSubjectClientSource.cs`**

```csharp
using Namotion.Interceptor.Connectors;

namespace Namotion.Interceptor.Modbus;

public interface IModbusSubjectClientSource : ISubjectSource
{
    ModbusClientDiagnostics Diagnostics { get; }
}
```

- [ ] **Step 4: Create `ModbusClientDiagnostics.cs`** (skeleton; counters will be wired in Task 18)

```csharp
namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Diagnostic snapshot of a Modbus client source. Read-only view backed by atomic
/// counters maintained by the source. Thread-safe.
/// </summary>
public class ModbusClientDiagnostics
{
    public bool   IsConnected             { get; internal set; }
    public bool   IsReconnecting          { get; internal set; }
    public long   TotalReconnectionAttempts { get; internal set; }
    public long   SuccessfulReconnections { get; internal set; }
    public long   FailedReconnections     { get; internal set; }
    public long   AbandonedReconnections  { get; internal set; }
    public DateTimeOffset? LastConnectedAt { get; internal set; }
    public Exception?     LastError        { get; internal set; }
    public double IncomingChangesPerSecond { get; internal set; }
    public double OutgoingChangesPerSecond { get; internal set; }
    public long   TotalPolls              { get; internal set; }
    public long   FailedBatches           { get; internal set; }
    public double AveragePollDurationMs   { get; internal set; }
    public DateTimeOffset? LastPollAt      { get; internal set; }
    public int    DiscoveredUnitCount     { get; internal set; }
}
```

- [ ] **Step 5: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: 0 errors, 0 warnings. If `ISubjectSource` lives in a different namespace, fix the using accordingly (verify against `src/Namotion.Interceptor.Connectors/ISubjectSource.cs`).

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Modbus
git commit -m "feat(modbus): add public configuration, connect context, source interface, diagnostics skeleton"
```

---

## Task 13: `ModbusSubjectClientSource` skeleton

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/ModbusSubjectClientSource.cs`

The skeleton has correct lifecycle but no read/write yet. It connects, runs the optional hook, signals "ready", and idles until cancelled. Polling and writes land in the next tasks.

- [ ] **Step 1: Verify `ISubjectSource` shape**

Open `src/Namotion.Interceptor.Connectors/ISubjectSource.cs` and confirm the exact signatures:

- `Task<IDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken);`
- `int WriteBatchSize { get; }`
- `ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken);`
- `Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken);`

If any names differ from the spec, use the actual types and adjust the rest of the plan accordingly.

- [ ] **Step 2: Create `ModbusSubjectClientSource.cs`**

```csharp
using FluentModbus;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;

namespace Namotion.Interceptor.Modbus;

public sealed class ModbusSubjectClientSource : IModbusSubjectClientSource, IDisposable
{
    private readonly IInterceptorSubject       _root;
    private readonly ModbusClientConfiguration _configuration;
    private readonly ILogger                   _logger;
    private readonly ModbusTcpClient           _client = new();

    public ModbusSubjectClientSource(
        IInterceptorSubject       root,
        ModbusClientConfiguration configuration,
        ILogger                   logger)
    {
        _root          = root;
        _configuration = configuration;
        _logger        = logger;
    }

    public ModbusClientDiagnostics Diagnostics { get; } = new();

    public int WriteBatchSize => 0;   // 0 means no limit; we do our own grouping in Task 17.

    public async Task<IDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Connecting to Modbus TCP {Host}:{Port}", _configuration.Host, _configuration.Port);
        _client.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Parse(_configuration.Host), _configuration.Port));
        Diagnostics.IsConnected     = true;
        Diagnostics.LastConnectedAt = DateTimeOffset.UtcNow;

        if (_configuration.OnConnectAsync is { } hook)
        {
            await hook(new ModbusConnectContext(_root, _client, this), cancellationToken);
        }

        // Tasks 15+ replace this no-op with a real poll loop.
        return new SourceListener(this);
    }

    public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        => Task.FromResult<Action?>(null);

    public ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        => throw new NotImplementedException("Implemented in Task 17.");

    public void Dispose()
    {
        _client.Disconnect();
        _client.Dispose();
        Diagnostics.IsConnected = false;
    }

    private sealed class SourceListener(ModbusSubjectClientSource owner) : IDisposable
    {
        public void Dispose() => owner.Dispose();
    }
}
```

If `SubjectPropertyWriter`, `WriteResult`, and `SubjectPropertyChange` types need different namespaces, copy the using imports from `src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs`.

- [ ] **Step 3: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: 0 errors, 0 warnings. (The `NotImplementedException` is intentional and only thrown at runtime.)

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Modbus
git commit -m "feat(modbus): ModbusSubjectClientSource skeleton with connect + hook"
```

---

## Task 14: DI extension methods

**Files:**
- Create: `src/Namotion.Interceptor.Modbus/ModbusSubjectExtensions.cs`

Mirrors the pattern in `src/Namotion.Interceptor.Mqtt/MqttSubjectExtensions.cs`.

- [ ] **Step 1: Read the MQTT extensions for the canonical wire-up**

Open `src/Namotion.Interceptor.Mqtt/MqttSubjectExtensions.cs`. Note how it registers the source as a keyed singleton plus `SubjectSourceBackgroundService`. The Modbus version must do the same.

- [ ] **Step 2: Create `ModbusSubjectExtensions.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;

namespace Namotion.Interceptor.Modbus;

public static class ModbusSubjectExtensions
{
    /// <summary>
    /// Register a Modbus client source whose root subject is resolved from DI by type.
    /// </summary>
    public static IServiceCollection AddModbusSubjectClientSource<TSubject>(
        this IServiceCollection services,
        string host,
        int port = 1502,
        TimeSpan? pollInterval = null)
        where TSubject : IInterceptorSubject
    {
        return RegisterClientSourceCore(
            services,
            sp => sp.GetRequiredService<TSubject>(),
            sp => new ModbusClientConfiguration
            {
                Host         = host,
                Port         = port,
                PollInterval = pollInterval ?? TimeSpan.FromSeconds(2),
            });
    }

    /// <summary>
    /// Register a Modbus client source with explicit subject + configuration factories.
    /// </summary>
    public static IServiceCollection AddModbusSubjectClientSource(
        this IServiceCollection services,
        Func<IServiceProvider, IInterceptorSubject> subjectSelector,
        Func<IServiceProvider, ModbusClientConfiguration> configurationProvider)
    {
        return RegisterClientSourceCore(services, subjectSelector, configurationProvider);
    }

    /// <summary>
    /// Imperative wire-up. Used by HomeBlaze devices that build the source mid-flight in <c>StartAsync</c>.
    /// </summary>
    public static ModbusSubjectClientSource CreateModbusClientSource(
        this IInterceptorSubject  subject,
        ModbusClientConfiguration configuration,
        ILogger                   logger)
        => new(subject, configuration, logger);

    private static IServiceCollection RegisterClientSourceCore(
        IServiceCollection                                 services,
        Func<IServiceProvider, IInterceptorSubject>        subjectSelector,
        Func<IServiceProvider, ModbusClientConfiguration>  configurationProvider)
    {
        services.AddSingleton<ModbusSubjectClientSource>(sp =>
        {
            var subject       = subjectSelector(sp);
            var configuration = configurationProvider(sp);
            var logger        = sp.GetRequiredService<ILogger<ModbusSubjectClientSource>>();
            return new ModbusSubjectClientSource(subject, configuration, logger);
        });

        services.AddSingleton<IModbusSubjectClientSource>(sp => sp.GetRequiredService<ModbusSubjectClientSource>());
        services.AddSingleton<ISubjectSource>(sp => sp.GetRequiredService<ModbusSubjectClientSource>());

        services.AddHostedService(sp =>
        {
            var source        = sp.GetRequiredService<ModbusSubjectClientSource>();
            var configuration = configurationProvider(sp);
            var subject       = subjectSelector(sp);
            var logger        = sp.GetRequiredService<ILogger<SubjectSourceBackgroundService>>();
            return new SubjectSourceBackgroundService(
                source,
                subject.Context,
                logger,
                configuration.BufferTime,
                configuration.RetryTime,
                configuration.WriteRetryQueueSize);
        });

        return services;
    }
}
```

If `SubjectSourceBackgroundService`'s constructor signature differs from this, copy from MQTT and adjust.

- [ ] **Step 3: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Modbus
git commit -m "feat(modbus): add IServiceCollection extensions and CreateModbusClientSource"
```

---

## Task 15: Read poll loop (integration test against FluentModbus.Server)

**Files:**
- Modify: `src/Namotion.Interceptor.Modbus/ModbusSubjectClientSource.cs` (add poll loop)
- Create: `src/Namotion.Interceptor.Modbus.Tests/Integration/ModbusReadIntegrationTests.cs`

This is the first integration test. It hosts FluentModbus.Server in-process, populates known register values, points the source at it, and asserts the property gets the expected value after a poll cycle.

- [ ] **Step 1: Write the integration test**

Create `src/Namotion.Interceptor.Modbus.Tests/Integration/ModbusReadIntegrationTests.cs`:

```csharp
using FluentModbus;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Modbus.Tests.Internal;
using Namotion.Interceptor.Registry;
using System.Net;

namespace Namotion.Interceptor.Modbus.Tests.Integration;

[Trait("Category", "Integration")]
public class ModbusReadIntegrationTests
{
    [Fact]
    public async Task WhenServerHoldsRegisterValue_ThenSubjectPropertyReceivesIt()
    {
        // Arrange  start an in-process Modbus TCP server on a free port
        using var server = new ModbusTcpServer();
        var port         = GetFreeTcpPort();
        server.Start(new IPEndPoint(IPAddress.Loopback, port));

        // Write a known value to holding register 40000 on unit ID 1.
        var registers = server.GetHoldingRegisters(unitIdentifier: 1);
        // FluentModbus exposes registers as a Span<short> indexed by absolute holding-register address.
        registers[40000] = 1234;

        var context = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var device = new FixedMapDevice(context) { /* BaseAddress = 40000 set in init */ };

        var configuration = new ModbusClientConfiguration
        {
            Host         = IPAddress.Loopback.ToString(),
            Port         = port,
            PollInterval = TimeSpan.FromMilliseconds(100),
        };

        var source        = device.CreateModbusClientSource(configuration, NullLogger.Instance);
        using var lifetime = await source.StartListeningAsync(
            propertyWriter: (property, value, timestamp) =>
            {
                property.SetValueFromSource(source, timestamp, null, value);
            },
            cancellationToken: CancellationToken.None);

        // Act  wait one poll cycle
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // Assert
        Assert.Equal((ushort)1234, device.RegisterA);

        server.Stop();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
```

This test will fail because the source's poll loop is a no-op.

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusReadIntegrationTests"`
Expected: FAIL. `device.RegisterA` is still 0 because the source's poll loop is a no-op.

- [ ] **Step 3: Implement the poll loop in `ModbusSubjectClientSource`**

Replace the body of `StartListeningAsync` and add a poll method. The full updated file:

```csharp
using FluentModbus;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Modbus.Internal;
using System.Net;

namespace Namotion.Interceptor.Modbus;

public sealed class ModbusSubjectClientSource : IModbusSubjectClientSource, IDisposable
{
    private readonly IInterceptorSubject       _root;
    private readonly ModbusClientConfiguration _configuration;
    private readonly ILogger                   _logger;
    private readonly ModbusTcpClient           _client = new();

    private CancellationTokenSource? _pollCts;
    private Task?                    _pollTask;
    private SubjectPropertyWriter?   _writer;

    public ModbusSubjectClientSource(
        IInterceptorSubject       root,
        ModbusClientConfiguration configuration,
        ILogger                   logger)
    {
        _root          = root;
        _configuration = configuration;
        _logger        = logger;
    }

    public ModbusClientDiagnostics Diagnostics { get; } = new();

    public int WriteBatchSize => 0;

    public async Task<IDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        _writer = propertyWriter;

        _logger.LogInformation("Connecting to Modbus TCP {Host}:{Port}", _configuration.Host, _configuration.Port);
        _client.Connect(new IPEndPoint(IPAddress.Parse(_configuration.Host), _configuration.Port));
        Diagnostics.IsConnected     = true;
        Diagnostics.LastConnectedAt = DateTimeOffset.UtcNow;

        if (_configuration.OnConnectAsync is { } hook)
        {
            await hook(new ModbusConnectContext(_root, _client, this), cancellationToken);
        }

        _pollCts  = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token), _pollCts.Token);

        return new SourceListener(this);
    }

    public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        => Task.FromResult<Action?>(null);

    public ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        => throw new NotImplementedException("Implemented in Task 17.");

    public void Dispose()
    {
        _pollCts?.Cancel();
        try { _pollTask?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _client.Disconnect();
        _client.Dispose();
        Diagnostics.IsConnected = false;
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_configuration.PollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try { PollOnce(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Modbus poll cycle failed.");
                Diagnostics.LastError = ex;
            }
        }
    }

    private void PollOnce()
    {
        var resolved = ModbusAddressResolver.Resolve(_root.Context).ToList();
        var batches  = ModbusBatchPlanner.Plan(resolved);

        foreach (var batch in batches)
        {
            ushort[] words;
            try
            {
                words = batch.Space switch
                {
                    AddressSpace.HoldingRegister => ReadAsUInt16(_client.ReadHoldingRegisters<ushort>(batch.UnitId, batch.StartAddress, batch.RegisterCount)),
                    AddressSpace.InputRegister   => ReadAsUInt16(_client.ReadInputRegisters<ushort>  (batch.UnitId, batch.StartAddress, batch.RegisterCount)),
                    _ => throw new NotSupportedException($"Address space {batch.Space} not yet supported for read."),
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Read batch failed at unit={UnitId} space={Space} start={StartAddress} count={RegisterCount}",
                    batch.UnitId, batch.Space, batch.StartAddress, batch.RegisterCount);
                Diagnostics.FailedBatches++;
                continue;
            }

            foreach (var member in batch.Members)
            {
                var slice  = words.AsSpan(member.AbsoluteAddress - batch.StartAddress, member.RegisterCount);
                var raw    = ModbusValueCodec.Decode(slice, member.Type, member.WordOrder, member.Length);
                var value  = ApplyStaticScale(raw, member);   // dynamic scale comes in Task 16
                _writer?.Invoke(member.Property, value, DateTimeOffset.UtcNow);
            }
        }

        Diagnostics.TotalPolls++;
        Diagnostics.LastPollAt = DateTimeOffset.UtcNow;
    }

    private static object ApplyStaticScale(object raw, ResolvedRegister member)
    {
        if (member.Scale == 1.0) return raw;
        return Convert.ToDouble(raw) * member.Scale;
    }

    private static ushort[] ReadAsUInt16(Span<ushort> data) => data.ToArray();

    private sealed class SourceListener(ModbusSubjectClientSource owner) : IDisposable
    {
        public void Dispose() => owner.Dispose();
    }
}
```

`SubjectPropertyWriter` is a delegate; check its actual signature against `Namotion.Interceptor.Connectors`. The `_writer.Invoke(...)` call may need adjustment (some implementations pass timestamps and `previousReceived` separately). Match what MQTT's source does.

- [ ] **Step 4: Run the integration test**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusReadIntegrationTests"`
Expected: PASS.

- [ ] **Step 5: Run all tests to confirm nothing regressed**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All pass. Build: 0 errors, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.Modbus.Tests
git commit -m "feat(modbus): poll loop reads registers and dispatches values"
```

---

## Task 16: Scale factor handling on read

**Files:**
- Modify: `src/Namotion.Interceptor.Modbus/ModbusSubjectClientSource.cs`
- Create: `src/Namotion.Interceptor.Modbus.Tests/Integration/ModbusScaleFactorIntegrationTests.cs`

- [ ] **Step 1: Write the failing scale-factor integration test**

Create `src/Namotion.Interceptor.Modbus.Tests/Integration/ModbusScaleFactorIntegrationTests.cs`:

```csharp
using FluentModbus;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Registry;
using System.Net;

namespace Namotion.Interceptor.Modbus.Tests.Integration;

[InterceptorSubject]
public partial class ScaledDevice : IModbusBaseAddressProvider
{
    public int BaseAddress { get; init; } = 100;

    [ModbusRegister(0, ModbusType.U16, ScaleFactorProperty = nameof(A_SF))]
    public partial double A { get; set; }

    [ModbusRegister(1, ModbusType.S16)]
    public partial short A_SF { get; set; }
}

[Trait("Category", "Integration")]
public class ModbusScaleFactorIntegrationTests
{
    [Fact]
    public async Task WhenScaleFactorRegisterIsNegativeOne_ThenScaledValueIsRawDividedByTen()
    {
        // Arrange
        using var server = new ModbusTcpServer();
        var port         = ModbusTestPort.GetFreeTcpPort();
        server.Start(new IPEndPoint(IPAddress.Loopback, port));

        var registers   = server.GetHoldingRegisters(1);
        registers[100]  = 1234;     // raw A
        registers[101]  = unchecked((short)-1);  // A_SF = -1 means * 10^-1 = / 10

        var context = InterceptorSubjectContext.Create().WithRegistry().WithFullPropertyTracking();
        var device  = new ScaledDevice(context);

        var source        = device.CreateModbusClientSource(
            new ModbusClientConfiguration
            {
                Host         = IPAddress.Loopback.ToString(),
                Port         = port,
                PollInterval = TimeSpan.FromMilliseconds(100),
            },
            NullLogger.Instance);

        using var lifetime = await source.StartListeningAsync(
            (property, value, timestamp) => property.SetValueFromSource(source, timestamp, null, value),
            CancellationToken.None);

        // Act
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // Assert
        Assert.Equal(123.4, device.A, precision: 5);
        Assert.Equal((short)-1, device.A_SF);

        server.Stop();
    }
}

internal static class ModbusTestPort
{
    public static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
```

(Refactor `GetFreeTcpPort` from Task 15 into the shared helper as part of this task.)

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusScaleFactorIntegrationTests"`
Expected: FAIL. `device.A` is `1234.0` (no scale factor applied).

- [ ] **Step 3: Add scale-factor pairing to the poll cycle**

In `ModbusSubjectClientSource.PollOnce`, after reading all batches but before calling `_writer`, build a per-property cache of decoded values, then on a second pass apply the scale factor by name.

Replace `PollOnce` with:

```csharp
    private void PollOnce()
    {
        var resolved = ModbusAddressResolver.Resolve(_root.Context).ToList();
        var batches  = ModbusBatchPlanner.Plan(resolved);

        // Decode all batches first so SF registers are known before we apply scaling.
        var decoded = new Dictionary<ResolvedRegister, object>();
        foreach (var batch in batches)
        {
            ushort[] words;
            try
            {
                words = batch.Space switch
                {
                    AddressSpace.HoldingRegister => _client.ReadHoldingRegisters<ushort>(batch.UnitId, batch.StartAddress, batch.RegisterCount).ToArray(),
                    AddressSpace.InputRegister   => _client.ReadInputRegisters<ushort>  (batch.UnitId, batch.StartAddress, batch.RegisterCount).ToArray(),
                    _ => throw new NotSupportedException($"Address space {batch.Space} not yet supported for read."),
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Read batch failed at unit={UnitId} space={Space} start={StartAddress} count={RegisterCount}",
                    batch.UnitId, batch.Space, batch.StartAddress, batch.RegisterCount);
                Diagnostics.FailedBatches++;
                continue;
            }

            foreach (var member in batch.Members)
            {
                var slice = words.AsSpan(member.AbsoluteAddress - batch.StartAddress, member.RegisterCount);
                decoded[member] = ModbusValueCodec.Decode(slice, member.Type, member.WordOrder, member.Length);
            }
        }

        // Build a lookup of "property name on the same subject" -> raw decoded value, so we can resolve SF references.
        var byPropertyName = decoded
            .GroupBy(kvp => kvp.Key.Property.Subject)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(kvp => kvp.Key.Property.Name, kvp => kvp.Value));

        foreach (var (member, raw) in decoded)
        {
            var value = ApplyScale(raw, member, byPropertyName);
            _writer?.Invoke(member.Property, value, DateTimeOffset.UtcNow);
        }

        Diagnostics.TotalPolls++;
        Diagnostics.LastPollAt = DateTimeOffset.UtcNow;
    }

    private static object ApplyScale(
        object raw,
        ResolvedRegister member,
        Dictionary<IInterceptorSubject, Dictionary<string, object>> byPropertyName)
    {
        if (member.ScaleFactorProperty is { } sfName
            && byPropertyName.TryGetValue(member.Property.Subject, out var siblings)
            && siblings.TryGetValue(sfName, out var sfValue))
        {
            var sf = Convert.ToInt32(sfValue);
            return Convert.ToDouble(raw) * Math.Pow(10, sf);
        }

        if (member.Scale != 1.0)
            return Convert.ToDouble(raw) * member.Scale;

        return raw;
    }
```

(Remove the old `ApplyStaticScale`.)

- [ ] **Step 4: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All pass.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.Modbus.Tests
git commit -m "feat(modbus): apply dynamic scale factors during poll cycle"
```

---

## Task 17: Write path (FC6 / FC16 with reverse scale)

**Files:**
- Modify: `src/Namotion.Interceptor.Modbus/ModbusSubjectClientSource.cs`
- Create: `src/Namotion.Interceptor.Modbus.Tests/Integration/ModbusWriteIntegrationTests.cs`

- [ ] **Step 1: Write a failing write integration test**

The test sets a tracked property and lets `SubjectSourceBackgroundService` route the change through the source's `WriteChangesAsync`. This avoids constructing `SubjectPropertyChange` directly (whose ctor signature is internal API not exposed to tests cleanly).

Create `src/Namotion.Interceptor.Modbus.Tests/Integration/ModbusWriteIntegrationTests.cs`:

```csharp
using FluentModbus;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Modbus.Tests.Internal;
using Namotion.Interceptor.Registry;
using System.Net;

namespace Namotion.Interceptor.Modbus.Tests.Integration;

[Trait("Category", "Integration")]
public class ModbusWriteIntegrationTests
{
    [Fact]
    public async Task WhenPropertyIsSet_ThenServerHoldingRegisterReceivesEncodedValue()
    {
        // Arrange
        using var server = new ModbusTcpServer();
        var port         = ModbusTestPort.GetFreeTcpPort();
        server.Start(new IPEndPoint(IPAddress.Loopback, port));

        var context = InterceptorSubjectContext.Create().WithRegistry().WithFullPropertyTracking();
        var device  = new FixedMapDevice(context);

        var configuration = new ModbusClientConfiguration
        {
            Host         = IPAddress.Loopback.ToString(),
            Port         = port,
            PollInterval = TimeSpan.FromSeconds(5),  // long; we don't want polls during the write test
            BufferTime   = TimeSpan.FromMilliseconds(20),
        };
        var source = device.CreateModbusClientSource(configuration, NullLogger.Instance);
        var hosted = new SubjectSourceBackgroundService(
            source, context, NullLogger<SubjectSourceBackgroundService>.Instance,
            configuration.BufferTime, configuration.RetryTime, configuration.WriteRetryQueueSize);

        await hosted.StartAsync(CancellationToken.None);
        // Allow connect + first poll to settle.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        // Act  set the tracked property; SubjectSourceBackgroundService's ChangeQueueProcessor
        // batches the change and calls source.WriteChangesAsync after BufferTime.
        device.RegisterA = 5555;
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        // Assert
        var registers = server.GetHoldingRegisters(1);
        Assert.Equal((ushort)5555, registers[40000]);

        await hosted.StopAsync(CancellationToken.None);
        server.Stop();
    }
}
```

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusWriteIntegrationTests"`
Expected: FAIL. The `NotImplementedException` is thrown from `WriteChangesAsync`.

- [ ] **Step 3: Implement `WriteChangesAsync`**

Replace `WriteChangesAsync` in `ModbusSubjectClientSource.cs`:

```csharp
    public ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var resolvedByProperty = ModbusAddressResolver.Resolve(_root.Context)
            .ToDictionary(r => r.Property);

        var failures = 0;
        for (var i = 0; i < changes.Length; i++)
        {
            var change = changes.Span[i];
            if (!resolvedByProperty.TryGetValue(change.Property, out var member))
                continue;   // not a Modbus property

            if (member.Space is AddressSpace.InputRegister or AddressSpace.DiscreteInput || member.Access == ModbusAccess.ReadOnly)
            {
                _logger.LogWarning("Refusing write to read-only Modbus location: {Property}", change.Property.Name);
                failures++;
                continue;
            }

            if (member.Space != AddressSpace.HoldingRegister)
            {
                _logger.LogWarning("Coil writes (FC5/FC15) not yet supported in v1 (property {Property}).", change.Property.Name);
                failures++;
                continue;
            }

            try
            {
                var raw = ApplyReverseScale(change.NewValue ?? 0, member);
                Span<ushort> buffer = stackalloc ushort[member.RegisterCount];
                ModbusValueCodec.Encode(buffer, raw, member.Type, member.WordOrder, member.Length);

                if (member.RegisterCount == 1)
                    _client.WriteSingleRegister(member.UnitId, member.AbsoluteAddress, buffer[0]);
                else
                    _client.WriteMultipleRegisters(member.UnitId, member.AbsoluteAddress, buffer.ToArray());

                Diagnostics.OutgoingChangesPerSecond++;   // placeholder; Task 18 makes this a real rate
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Write failed for {Property}", change.Property.Name);
                Diagnostics.LastError = ex;
                failures++;
            }
        }

        return ValueTask.FromResult(failures == 0 ? WriteResult.Success : WriteResult.Failed);
    }

    private static object ApplyReverseScale(object scaled, ResolvedRegister member)
    {
        if (member.ScaleFactorProperty is { } sfName)
        {
            var sfValue = ReadSiblingProperty(member.Property.Subject, sfName);
            var sf      = Convert.ToInt32(sfValue);
            return Convert.ToDouble(scaled) / Math.Pow(10, sf);
        }
        if (member.Scale != 1.0)
            return Convert.ToDouble(scaled) / member.Scale;
        return scaled;
    }

    private static object ReadSiblingProperty(IInterceptorSubject subject, string name)
    {
        var property = subject.GetType().GetProperty(name)
            ?? throw new InvalidOperationException($"Scale factor sibling property '{name}' not found.");
        return property.GetValue(subject) ?? throw new InvalidOperationException($"Scale factor '{name}' has not been read yet.");
    }
```

If `WriteResult` has different members (e.g. `WriteResult.Ok` / `WriteResult.Error`), use the actual ones from `Namotion.Interceptor.Connectors`.

- [ ] **Step 4: Run write tests**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusWriteIntegrationTests"`
Expected: PASS.

- [ ] **Step 5: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All pass; 0 build errors/warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.Modbus.Tests
git commit -m "feat(modbus): write path (FC6/FC16) with reverse scale-factor support"
```

---

## Task 18: Diagnostics counters

**Files:**
- Modify: `src/Namotion.Interceptor.Modbus/ModbusClientDiagnostics.cs`
- Modify: `src/Namotion.Interceptor.Modbus/ModbusSubjectClientSource.cs`
- Create: `src/Namotion.Interceptor.Modbus.Tests/ModbusClientDiagnosticsTests.cs`

Replace the placeholder `OutgoingChangesPerSecond++` with a proper rate window. Use the same rolling-second counter pattern as `OpcUaClientDiagnostics` (look at `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientDiagnostics.cs` and the `Throughput` helpers it uses; reuse if public, otherwise inline a small implementation).

- [ ] **Step 1: Write failing diagnostics tests**

Create `src/Namotion.Interceptor.Modbus.Tests/ModbusClientDiagnosticsTests.cs`:

```csharp
namespace Namotion.Interceptor.Modbus.Tests;

public class ModbusClientDiagnosticsTests
{
    [Fact]
    public void WhenSourceIsCreated_ThenDiagnosticsReportNotConnectedAndZeroCounters()
    {
        // Arrange
        var diagnostics = new ModbusClientDiagnostics();

        // Act & Assert
        Assert.False(diagnostics.IsConnected);
        Assert.False(diagnostics.IsReconnecting);
        Assert.Equal(0, diagnostics.TotalPolls);
        Assert.Equal(0, diagnostics.FailedBatches);
        Assert.Null(diagnostics.LastError);
        Assert.Null(diagnostics.LastConnectedAt);
        Assert.Null(diagnostics.LastPollAt);
    }
}
```

(Functional throughput / reconnect counter behaviour is exercised by integration tests in Tasks 15-17 and 19.)

- [ ] **Step 2: Run, expect pass (skeleton already supports this)**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusClientDiagnosticsTests"`
Expected: PASS. This verifies the public API surface; no behavioural change yet.

- [ ] **Step 3: Replace the OutgoingChangesPerSecond placeholder with a real rolling-rate counter**

Look at `OpcUaClientDiagnostics`'s `IncomingThroughput` / `OutgoingThroughput` plumbing in `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`. If a reusable type exists in `Namotion.Interceptor.Connectors`, reference it; otherwise add a small private `RollingRateCounter` to the Modbus source:

```csharp
internal sealed class RollingRateCounter
{
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _events = new();

    public RollingRateCounter(TimeSpan? window = null) => _window = window ?? TimeSpan.FromSeconds(60);

    public void Record(DateTimeOffset at)
    {
        lock (_events)
        {
            _events.Enqueue(at);
            Evict(at);
        }
    }

    public double CurrentRate
    {
        get
        {
            lock (_events)
            {
                Evict(DateTimeOffset.UtcNow);
                return _events.Count / _window.TotalSeconds;
            }
        }
    }

    private void Evict(DateTimeOffset now)
    {
        while (_events.Count > 0 && now - _events.Peek() > _window)
            _events.Dequeue();
    }
}
```

Use it in the source: instantiate two counters (`_incoming`, `_outgoing`); call `_incoming.Record(now)` per `_writer.Invoke` in `PollOnce`, and `_outgoing.Record(now)` per successful write in `WriteChangesAsync`. Replace the `Diagnostics.OutgoingChangesPerSecond++` lines and instead return `_outgoing.CurrentRate` from a property accessor (change `ModbusClientDiagnostics.OutgoingChangesPerSecond` from `internal set;` to a backing-delegate model or have the source expose its own diagnostic class deriving from this one).

The simplest refactor: make `ModbusClientDiagnostics` a class with a settable computed-rate field that the source updates on a 1-second timer. For v1, use the simpler form: poll-time update of the counters.

In `PollOnce` add at end:
```csharp
        Diagnostics.IncomingChangesPerSecond = _incoming.CurrentRate;
        Diagnostics.OutgoingChangesPerSecond = _outgoing.CurrentRate;
        Diagnostics.AveragePollDurationMs    = ComputeAveragePollDuration();
```

Drop the bogus `Diagnostics.OutgoingChangesPerSecond++` line in `WriteChangesAsync`; replace with `_outgoing.Record(DateTimeOffset.UtcNow);` per success.

- [ ] **Step 4: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All pass.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.Modbus.Tests
git commit -m "feat(modbus): rolling-rate counters in diagnostics, drop placeholder increment"
```

---

## Task 19: Reconnect integration test

**Files:**
- Create: `src/Namotion.Interceptor.Modbus.Tests/Integration/ModbusReconnectIntegrationTests.cs`

The reconnect logic itself lives in `SubjectSourceBackgroundService`; this task confirms the Modbus source plays nicely with that loop. The test stops the server, restarts it, and verifies polling resumes without manual intervention.

- [ ] **Step 1: Write the reconnect test**

Create `src/Namotion.Interceptor.Modbus.Tests/Integration/ModbusReconnectIntegrationTests.cs`:

```csharp
using FluentModbus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Modbus.Tests.Internal;
using Namotion.Interceptor.Registry;
using System.Net;

namespace Namotion.Interceptor.Modbus.Tests.Integration;

[Trait("Category", "Integration")]
public class ModbusReconnectIntegrationTests
{
    [Fact]
    public async Task WhenServerStopsAndRestarts_ThenSourceRecoversAndResumesPolling()
    {
        // Arrange
        var port = ModbusTestPort.GetFreeTcpPort();

        var server1 = new ModbusTcpServer();
        server1.Start(new IPEndPoint(IPAddress.Loopback, port));
        server1.GetHoldingRegisters(1)[40000] = 11;

        var context = InterceptorSubjectContext.Create().WithRegistry().WithFullPropertyTracking();
        var device  = new FixedMapDevice(context);

        var configuration = new ModbusClientConfiguration
        {
            Host         = IPAddress.Loopback.ToString(),
            Port         = port,
            PollInterval = TimeSpan.FromMilliseconds(100),
            RetryTime    = TimeSpan.FromMilliseconds(200),
        };
        var source        = device.CreateModbusClientSource(configuration, NullLogger.Instance);
        var hosted        = new SubjectSourceBackgroundService(
            source, context, NullLogger<SubjectSourceBackgroundService>.Instance,
            configuration.BufferTime, configuration.RetryTime, configuration.WriteRetryQueueSize);

        await hosted.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        Assert.Equal((ushort)11, device.RegisterA);

        // Act 1  kill the server
        server1.Stop();
        server1.Dispose();
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // Restart with a different value, same port
        var server2 = new ModbusTcpServer();
        server2.Start(new IPEndPoint(IPAddress.Loopback, port));
        server2.GetHoldingRegisters(1)[40000] = 22;

        await Task.Delay(TimeSpan.FromMilliseconds(800));

        // Assert
        Assert.Equal((ushort)22, device.RegisterA);

        await hosted.StopAsync(CancellationToken.None);
        server2.Stop();
        server2.Dispose();
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusReconnectIntegrationTests"`
Expected: PASS. If the source doesn't propagate exceptions correctly from a dropped connection, `SubjectSourceBackgroundService` won't see them and won't reconnect. Fix in step 3 if needed.

- [ ] **Step 3 (only if step 2 fails): Surface poll-loop exceptions**

In `PollLoopAsync`, if a poll cycle fails repeatedly because the connection is dead, throw to break out of the loop. Currently the catch swallows. Change to: increment `Diagnostics.FailedBatches`, record the error, and after N consecutive failures (e.g. 3) re-throw to let `SubjectSourceBackgroundService` reconnect.

Concrete change:

```csharp
    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        using var timer = new PeriodicTimer(_configuration.PollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                PollOnce();
                consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Modbus poll cycle failed (consecutive: {N}).", ++consecutiveFailures);
                Diagnostics.LastError = ex;
                if (consecutiveFailures >= 3)
                    throw;   // let SubjectSourceBackgroundService reconnect us
            }
        }
    }
```

- [ ] **Step 4: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All pass.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Modbus src/Namotion.Interceptor.Modbus.Tests
git commit -m "feat(modbus): integration test for reconnect; surface persistent poll failures"
```

---

## Task 20: Multi-unit-ID dispatch integration test

**Files:**
- Create: `src/Namotion.Interceptor.Modbus.Tests/Integration/ModbusMultiUnitIntegrationTests.cs`

- [ ] **Step 1: Write the test**

Create `src/Namotion.Interceptor.Modbus.Tests/Integration/ModbusMultiUnitIntegrationTests.cs`:

```csharp
using FluentModbus;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Registry;
using System.Net;

namespace Namotion.Interceptor.Modbus.Tests.Integration;

[InterceptorSubject]
[ModbusUnitId(2)]
public partial class MeterDevice : IModbusBaseAddressProvider
{
    public int BaseAddress { get; init; } = 200;

    [ModbusRegister(0, ModbusType.U16)]
    public partial ushort EnergyKWh { get; set; }
}

[InterceptorSubject]
public partial class GatewayRoot
{
    public partial Tests.Internal.FixedMapDevice? Inverter { get; set; }
    public partial MeterDevice?                   Meter    { get; set; }
}

[Trait("Category", "Integration")]
public class ModbusMultiUnitIntegrationTests
{
    [Fact]
    public async Task WhenSubjectsTargetDifferentUnitIds_ThenEachIsPolledOnItsOwnUnit()
    {
        // Arrange
        using var server = new ModbusTcpServer();
        var port         = ModbusTestPort.GetFreeTcpPort();
        server.Start(new IPEndPoint(IPAddress.Loopback, port));

        server.GetHoldingRegisters(1)[40000] = 100;     // inverter at unit 1
        server.GetHoldingRegisters(2)[200]   = 200;     // meter at unit 2

        var context = InterceptorSubjectContext.Create().WithRegistry().WithFullPropertyTracking();
        var root    = new GatewayRoot(context);
        root.Inverter = new Tests.Internal.FixedMapDevice(context);
        root.Meter    = new MeterDevice(context);

        var source        = root.CreateModbusClientSource(
            new ModbusClientConfiguration { Host = IPAddress.Loopback.ToString(), Port = port, PollInterval = TimeSpan.FromMilliseconds(100) },
            NullLogger.Instance);

        using var lifetime = await source.StartListeningAsync(
            (property, value, timestamp) => property.SetValueFromSource(source, timestamp, null, value),
            CancellationToken.None);

        // Act
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // Assert
        Assert.Equal((ushort)100, root.Inverter.RegisterA);
        Assert.Equal((ushort)200, root.Meter.EnergyKWh);

        server.Stop();
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test src/Namotion.Interceptor.Modbus.Tests --filter "FullyQualifiedName~ModbusMultiUnitIntegrationTests"`
Expected: PASS.

- [ ] **Step 3: Run the full suite once more**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All pass; build clean.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Modbus.Tests
git commit -m "feat(modbus): integration test for multi-unit-ID dispatch"
```

---

## Task 21: Connector documentation page

**Files:**
- Create: `docs/connectors-modbus.md`

The repo convention is `docs/connectors-<name>.md` (plural; see `docs/connectors-mqtt.md`, `docs/connectors-opcua.md`). The page sits next to the others and is linked from the README's connectors table.

- [ ] **Step 1: Create the doc**

Create `docs/connectors-modbus.md` with this content:

````markdown
# Modbus

The `Namotion.Interceptor.Modbus` package provides a Modbus TCP client connector. It polls registered properties on a fixed interval, applies scale factors, batches contiguous reads, and routes writes through FC6 / FC16. Designed for fixed-map devices (energy meters, heat pumps, wallboxes) and as the foundation for higher-level libraries such as `Namotion.Devices.SunSpec`.

## Key features

- Declarative register layout via `[ModbusRegister]` attribute (offset, type, address space, word order, scale, length, access)
- Static unit ID per class (`[ModbusUnitId(N)]`) or dynamic via `IModbusUnitIdProvider`
- Per-instance base offset via `IModbusBaseAddressProvider` for relative addressing
- Greedy contiguous batching up to the 125-register PDU limit
- Read holding (FC3) and input (FC4) registers; write holding (FC6 single, FC16 multi)
- Scale factors: dynamic (paired `_SF` register) or static
- Word order overrides for non-conformant devices (`AB_CD`, `CD_AB`, `BA_DC`, `DC_BA`)
- ASCII strings spanning multiple registers
- `OnConnectAsync` hook for runtime device discovery
- `ModbusClientDiagnostics` mirroring `OpcUaClientDiagnostics`
- Reconnect / retry inherited from `SubjectSourceBackgroundService`

## Quick start: a fixed-map device

```csharp
using Namotion.Interceptor;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Registry;

[InterceptorSubject]
[ModbusUnitId(1)]
public partial class EnergyMeter : IModbusBaseAddressProvider
{
    public int BaseAddress { get; init; } = 0;

    [ModbusRegister(0,  ModbusType.U32, Scale = 0.001)]   // Wh -> kWh
    public partial double EnergyTotal { get; set; }

    [ModbusRegister(2,  ModbusType.S16, Scale = 0.1)]     // 0.1 A
    public partial double Current { get; set; }

    [ModbusRegister(3,  ModbusType.U16, Scale = 0.1)]     // 0.1 V
    public partial double Voltage { get; set; }
}

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext.Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithHostedServices(builder.Services);

builder.Services.AddSingleton(new EnergyMeter(context));
builder.Services.AddModbusSubjectClientSource<EnergyMeter>(
    host: "192.168.1.50",
    port: 502,
    pollInterval: TimeSpan.FromSeconds(2));

var host  = builder.Build();
var meter = host.Services.GetRequiredService<EnergyMeter>();
await host.StartAsync();

// Properties update on each poll cycle.
Console.WriteLine($"{meter.Voltage:F1} V, {meter.Current:F2} A");
```

## Scale factors

Two forms, mutually exclusive on a single property:

```csharp
// Static: raw * Scale on read; scaled / Scale on write.
[ModbusRegister(0, ModbusType.U16, Scale = 0.1)]
public partial double Temperature { get; set; }

// Dynamic: raw * 10^sf on read, where sf is the value of the named sibling property.
[ModbusRegister(0, ModbusType.U16, ScaleFactorProperty = nameof(W_SF))]
public partial double W { get; set; }

[ModbusRegister(1, ModbusType.S16)]
public partial short W_SF { get; set; }
```

The connector reads the SF register first within a poll cycle and applies the scale before writing the dependent property. Writes reverse the operation.

## Multi-unit-ID dispatch

A single TCP connection can talk to multiple Modbus unit IDs. Annotate each subject with its unit ID and bind the source to a parent subject that contains them:

```csharp
[InterceptorSubject]
[ModbusUnitId(1)] public partial class Inverter : IModbusBaseAddressProvider { /* ... */ }

[InterceptorSubject]
[ModbusUnitId(2)] public partial class Meter    : IModbusBaseAddressProvider { /* ... */ }

[InterceptorSubject]
public partial class Gateway
{
    public partial Inverter? Inverter { get; set; }
    public partial Meter?    Meter    { get; set; }
}
```

The address resolver groups reads by `(UnitId, AddressSpace)` and the FluentModbus client multiplexes them on one connection.

## Discovery hook (advanced)

Set `ModbusClientConfiguration.OnConnectAsync` to run device-specific probing on connect (used by SunSpec for chain walking). The hook attaches discovered subjects via `property.SetValueFromSource(source, ...)` so the connector picks them up on the next poll cycle. See `Namotion.Devices.SunSpec` for a full example.

## Diagnostics

Each `ModbusSubjectClientSource` exposes `Diagnostics` of type `ModbusClientDiagnostics` with connection state, reconnect counters, throughput rates (incoming/outgoing changes per second), poll counts and durations, and last error.

## Out of scope (v1)

Modbus RTU (serial), coils (FC1 / FC5 / FC15), discrete inputs (FC2), bit-packed registers, BCD encoding, and per-property polling intervals are deferred. See [docs/superpowers/specs/2026-05-04-modbus-design.md](superpowers/specs/2026-05-04-modbus-design.md) for the full design and roadmap.
````

- [ ] **Step 2: Verify the doc renders**

Run: open the file in any markdown viewer (or `code docs/connectors-modbus.md` if VS Code is set up). Confirm code blocks parse and the table of contents reads cleanly.

- [ ] **Step 3: Commit**

```bash
git add docs/connectors-modbus.md
git commit -m "docs(modbus): add connector documentation page"
```

---

## Task 22: README integration

**Files:**
- Modify: `README.md` (lines around 11, 19, 31, 359, and the connectors table near 404)

- [ ] **Step 1: Add Modbus to the existing connector mentions**

Open `README.md`. Make these targeted edits (preserving surrounding text):

In the intro paragraph (around line 11), where it says "Built-in integrations include MQTT, OPC UA, ASP.NET Core, Blazor, and GraphQL.", change to:

```
Built-in integrations include MQTT, OPC UA, Modbus TCP, ASP.NET Core, Blazor, and GraphQL.
```

In the "Bidirectional synchronization" highlight bullet (around line 19), where it says "Connect your object model to MQTT brokers, OPC UA servers, or databases with minimal code", change to:

```
Connect your object model to MQTT brokers, OPC UA servers, Modbus TCP devices, or databases with minimal code
```

In the architecture summary (around line 31), where it says "Connectors: bidirectional synchronization with external systems (MQTT, OPC UA, WebSocket).", change to:

```
Connectors: bidirectional synchronization with external systems (MQTT, OPC UA, Modbus, WebSocket).
```

In the "deep dive" connector paragraph (around line 359), where it says "The same pattern applies to [OPC UA](docs/connectors-opcua.md) ... and [WebSocket](docs/connectors-websocket.md) ...", append a sentence:

```
[Modbus](docs/connectors-modbus.md) covers Modbus TCP devices with declarative register mapping, scale factors, and multi-unit-ID dispatch.
```

- [ ] **Step 2: Add the Modbus row to the Connectors table** (around line 404)

Find the table block:

```markdown
| **Namotion.Interceptor.Connectors** | Base infrastructure for external system integration | [Connectors](docs/connectors.md) |
| **Namotion.Interceptor.Mqtt** | Bidirectional MQTT synchronization | [MQTT](docs/connectors-mqtt.md) |
| **Namotion.Interceptor.OpcUa** | OPC UA client and server integration | [OPC UA](docs/connectors-opcua.md) |
| **Namotion.Interceptor.WebSocket** | Real-time WebSocket synchronization | [WebSocket](docs/connectors-websocket.md) |
| **Connector Tester** | Chaos and load testing for connector verification | [Connector Tester](docs/connector-tester.md) |
```

Insert a new row between OpcUa and WebSocket (alphabetical position):

```markdown
| **Namotion.Interceptor.Modbus** | Modbus TCP client with declarative register mapping | [Modbus](docs/connectors-modbus.md) |
```

- [ ] **Step 3: Verify the README still renders correctly**

Run: `git diff README.md` and confirm only the intended lines changed.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs(modbus): add Modbus connector to README integrations and table"
```

---

## Final verification

- [ ] **Build the entire solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Run all tests including integration**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All pass.

- [ ] **Confirm spec coverage**

Walk through `docs/superpowers/specs/2026-05-04-modbus-design.md` Section 1 (in scope) line by line and confirm each item is implemented. Anything missed (e.g. coil writes you intentionally deferred) should be documented as a follow-up issue, not silently dropped.

---

## Follow-up plans (post-merge)

These are tracked here so we don't lose them; each becomes its own design + plan when picked up.

### Immediate next

- **Plan 2: `Namotion.Devices.SunSpec` library.** Builds on this connector. Hand-coded Model 1 + Model 103, `ISunSpecModel`, `SunSpecModelRegistry`, `SunSpecUnit` (with `Models` dictionary keyed by modelId and `[Derived]` typed accessors), `SunSpecDevice` (BackgroundService root), `SunSpecDiscovery` chain walker, `ConfigureSunSpec` extension on `ModbusClientConfiguration`. Includes `SunSpecTestServer` test fixture matching SolarEdge's published layout.
- **Plan 3: `Namotion.Devices.SunSpec.HomeBlaze` UI.** Blazor widget / edit / setup components per the create-homeblaze-library skill.

### Connector follow-ups

- **`Namotion.Interceptor.Modbus.Server`.** Parallel to `Namotion.Interceptor.Mqtt.Server` and `Namotion.Interceptor.OpcUa.Server`. The C# graph is the source of truth; external Modbus clients can read/write the exposed registers. Reuses `[ModbusRegister]` attributes (the same subject can be both server and client), driven by a `ModbusServerConfiguration` and a FluentModbus.Server-based hosted service. Also gets its own `docs/connectors-modbus-server.md`.
- **Connector tester integration for Modbus chaos testing.** Extend `Namotion.Interceptor.ConnectorTester` (see `docs/connector-tester.md`) to run kill/disconnect cycles against a Modbus client (and later, the server). Mirrors the existing OPC UA / MQTT chaos profiles. Validates reconnect, write-retry queue, and discovery re-run idempotence under hostile networks.
- **SunSpec JSON to C# generator.** CLI tool at `tools/Namotion.Devices.SunSpec.Generator/` that consumes the SunSpec Alliance JSON model definitions and emits `[InterceptorSubject]` partial classes into `Namotion.Devices.SunSpec/Generated/`. CI check fails if generated files drift from JSON. Replaces the v1 hand-coded models. Also generates the per-model `[Derived]` accessors on `SunSpecUnit`.
- **MESA-Device extensions.** SunSpec-style chain extension for energy storage; "free" once SunSpec works (additional model IDs 700/800/200, same discovery mechanism).

### Connector v1.5 / v2 features

- Bit-packed registers (multiple booleans in one u16 / u32).
- BCD encoding for legacy meters.
- Coils (FC1, FC5, FC15) and discrete inputs (FC2).
- Modbus RTU (serial) via FluentModbus's `ModbusRtuClient`.
- Per-property polling rates (e.g. read voltage every 1 s but lifetime energy every 60 s).
- Registry-event-driven incremental address resolution (avoid re-walking on every poll).
- Batch-splitting on persistent failure to isolate a bad register.

### Additional device libraries

- `Namotion.Devices.Eastron.SDM630` (energy meter; fixed map; no SunSpec).
- `Namotion.Devices.Schneider.PM5560` (energy meter).
- `Namotion.Devices.Wallbox.*` (existing, may pick up Modbus-based wallboxes).
- `Namotion.Devices.Luxtronik` (Alpha Innotec / Alterra heat pumps). Need to verify whether SWCV 92H3 has Modbus TCP out of the box; if not, separate `Namotion.Interceptor.Luxtronik` connector for the proprietary protocol on port 8888.
