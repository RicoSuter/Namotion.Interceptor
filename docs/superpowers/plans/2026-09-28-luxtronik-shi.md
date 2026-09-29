# Luxtronik SHI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Namotion.Devices.Luxtronik`, a read-only, strongly typed HomeBlaze device for the Luxtronik 2.1 Smart Home Interface (Modbus TCP), plus its UI and docs.

**Architecture:** Plain `[InterceptorSubject]` model classes carry `[ModbusRegister]`-derived attributes and contain no Modbus code. `LuxtronikHeatPump` is a HomeBlaze device (`BackgroundService`) that creates a `ModbusSubjectClientSource` over itself. Its discovery (`IModbusDiscovery.DiscoverAsync`) runs on every (re)connect before the bindings are resolved: it reads the firmware version and the configured-feature flags through the discovery context and excludes properties the controller does not support. Measured temperatures are `ITemperatureSensor` child subjects; the device implements `IPowerSensor` and the new `IThermalPowerSensor`.

**Tech Stack:** .NET 10 (device), `Namotion.Interceptor.Modbus` (.NET 9), HomeBlaze.Abstractions, MudBlazor, xUnit, FluentModbus (tests only).

**Prerequisite:** the connector plan [2026-09-28-modbus-connector.md](2026-09-28-modbus-connector.md) is complete on this branch.

**Spec:** [2026-09-28-modbus-luxtronik-design.md](../specs/2026-09-28-modbus-luxtronik-design.md), Part B (sections 5 and 6.3 to 6.5).

---

## Conventions for every task

- Read `AGENTS.md`, `docs/subject-guidelines.md` and `.claude/commands/create-homeblaze-library.md` first.
- Every partial property is initialized in the constructor. Child subjects and register values use `{ get; internal set; }`.
- Every register property carries `[State]` with its unit (`DegreeCelsius`, `Kelvin`, `Watt`, `WattHour`, `Minute`, `Hour`), `IsCumulative = true` on totals and `IsDiscrete = true` on enums and bools. Without `[State]` HomeBlaze UI and history ignore the property.
- Test names `When<Condition>_Then<ExpectedBehavior>` with `// Arrange`, `// Act`, `// Assert`; no hardcoded waits.
- No abbreviations, no em dashes in docs and comments, commit after each task, no AI attribution.
- The device and test projects reference `SonarAnalyzer.CSharp` (Task 3), so `S`-prefixed diagnostics are build errors. Follow the Analyzer Policy in `AGENTS.md`: fix findings, and never add an exception to `src/.editorconfig` without user approval.
- Unit tests: `dotnet test src/HomeBlaze/Namotion.Devices.Luxtronik.Tests --filter "Category!=Integration"`; integration: `dotnet test src/HomeBlaze/Namotion.Devices.Luxtronik.Tests --filter "Category=Integration"`.
- Register addresses are raw (no +1). Input, holding and discrete input addresses all start at 10000 in separate spaces. The `3.92` gates come from python-luxtronik `since` fields (commit `02afea84bd5bf3ee87445de6f2a42b8029983169`); names, types and enum values follow the official AIT SHI manual (83026900aDE).

## File structure

```
src/HomeBlaze/HomeBlaze.Abstractions/Attributes/StateUnit.cs              + Kelvin, Minute, Hour
src/HomeBlaze/HomeBlaze.Host.Services/Display/StateUnitExtensions.cs       + suffixes
src/HomeBlaze/HomeBlaze/Data/Docs/development/building-subjects.md         + Kelvin, Minute, Hour in the unit list
src/HomeBlaze/HomeBlaze.Abstractions/Sensors/IThermalPowerSensor.cs        new abstraction
src/HomeBlaze/Namotion.Devices.Luxtronik/
  Namotion.Devices.Luxtronik.csproj
  Enums/LuxtronikOperationMode.cs, LuxtronikModeStatus.cs, LuxtronikControlMode.cs, LuxtronikOverallHeatingMode.cs,
        LuxtronikLevelMode.cs, LuxtronikPowerLimitMode.cs, LuxtronikBufferType.cs, LuxtronikSmartGridState.cs,
        LuxtronikFeature.cs, LuxtronikHeatPumpStatus.cs
  Attributes/LuxtronikInputRegisterAttribute.cs, LuxtronikHoldingRegisterAttribute.cs
  Gating/ILuxtronikRegisterGate.cs, ILuxtronikGatedSubject.cs, LuxtronikGating.cs      internal
  Model/LuxtronikFeatures.cs, LuxtronikOperatingStatus.cs, LuxtronikTemperatureSensor.cs, LuxtronikTemperatures.cs,
        LuxtronikEnergy.cs, LuxtronikOutputs.cs, LuxtronikSmartGrid.cs, LuxtronikRuntime.cs, LuxtronikExtraHotWater.cs,
        LuxtronikControl.cs, LuxtronikCoolingControl.cs, LuxtronikMixingCircuitSetpoints.cs, LuxtronikMixingCircuit.cs,
        LuxtronikPowerLimit.cs, LuxtronikLocks.cs, LuxtronikRoomControl.cs, LuxtronikOverallHeating.cs,
        LuxtronikHotWaterRequests.cs
  LuxtronikHeatPump.cs, LuxtronikServiceCollectionExtensions.cs
src/HomeBlaze/Namotion.Devices.Luxtronik.Tests/
  Namotion.Devices.Luxtronik.Tests.csproj
  Testing/TestHost.cs, Testing/LuxtronikTestServer.cs, Testing/LuxtronikHardwareFactAttribute.cs,
  Testing/LuxtronikIntegrationCollection.cs
  LuxtronikGatingTests.cs, LuxtronikModelTests.cs, LuxtronikHeatPumpDeviceTests.cs, LuxtronikHeatPumpTests.cs (Integration),
  LuxtronikHeatPumpLifecycleTests.cs (Integration), LuxtronikHardwareTests.cs
src/HomeBlaze/Namotion.Devices.Luxtronik.HomeBlaze/
  Namotion.Devices.Luxtronik.HomeBlaze.csproj, _Imports.razor, LuxtronikHeatPumpWidget.razor, LuxtronikHeatPumpEditComponent.razor
src/HomeBlaze/HomeBlaze/HomeBlaze.csproj, Program.cs, Data/Devices/Luxtronik.json, Data/Docs/devices/Luxtronik.md
src/Namotion.Interceptor.slnx
```

---

## Task 1: Kelvin, minute and hour state units

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Abstractions/Attributes/StateUnit.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Host.Services/Display/StateUnitExtensions.cs` (`GetUnitInfo`)
- Modify: `src/HomeBlaze/HomeBlaze/Data/Docs/development/building-subjects.md` (**Available units** list)
- Test: `src/HomeBlaze/HomeBlaze.Host.Services.Tests/Display/StateUnitExtensionsTests.cs`

- [ ] **Step 1: Write the failing test cases**

In `StateUnitExtensionsTests.WhenFormatWithUnit_ThenAutoScalesCorrectly`, add after the `DegreeCelsius` case:

```csharp
    [InlineData(StateUnit.Kelvin, 1.5, "1.5 K")]
    [InlineData(StateUnit.Minute, 30, "30 min")]
    [InlineData(StateUnit.Hour, 1234, "1234 h")]
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Host.Services.Tests --filter "FullyQualifiedName~StateUnitExtensionsTests"`
Expected: build error, `StateUnit.Kelvin` does not exist.

- [ ] **Step 3: Add the units**

Append to the end of the `StateUnit` enum (after `Byte`, so existing numeric values stay stable):

```csharp
    Byte,
    Kelvin,
    Minute,
    Hour
}
```

In `StateUnitExtensions.GetUnitInfo`, add before the `_ => null` arm:

```csharp
        StateUnit.Kelvin => ("K", true),
        StateUnit.Minute => ("min", true),
        StateUnit.Hour => ("h", true),
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Host.Services.Tests --filter "FullyQualifiedName~StateUnitExtensionsTests"`
Expected: PASS, including the 3 new cases.

- [ ] **Step 5: Document the units**

In `building-subjects.md`, section **Available units**, replace the last entry of the `StateUnit` code block (`HexColor           // #FF0000` and the closing brace) with:

```csharp
    HexColor,          // #FF0000
    Kelvin,            // 1.5 K (temperature differences)
    Minute,            // 30 min
    Hour               // 1234 h
}
```

The listed enum is already behind the source (it lacks `Kilometer`, `MeterPerSecond`, `Hectopascal`, `Degree`, `UvIndex`, `Byte`); only add the three new units here.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Abstractions/Attributes/StateUnit.cs src/HomeBlaze/HomeBlaze.Host.Services/Display/StateUnitExtensions.cs src/HomeBlaze/HomeBlaze.Host.Services.Tests/Display/StateUnitExtensionsTests.cs src/HomeBlaze/HomeBlaze/Data/Docs/development/building-subjects.md
git commit -m "feat: add Kelvin, minute and hour state units"
```

---

## Task 2: Thermal power abstraction

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/Sensors/IThermalPowerSensor.cs`

Declaration only; the Luxtronik tests in Task 8 exercise it.

- [ ] **Step 1: Add the interface**

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for heat producers such as heat pumps, solar thermal systems and heat meters.
/// </summary>
[SubjectAbstraction]
[Description("Reports thermal power output in watts and total thermal energy produced in watt-hours.")]
public interface IThermalPowerSensor
{
    /// <summary>
    /// The current thermal power output.
    /// </summary>
    [State(Unit = StateUnit.Watt, Position = 350)]
    decimal? ThermalPower { get; }

    /// <summary>
    /// The total thermal energy produced.
    /// </summary>
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 351)]
    decimal? ThermalEnergyProduced { get; }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Abstractions`
Expected: `Build succeeded`, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Abstractions/Sensors/IThermalPowerSensor.cs
git commit -m "feat: add the thermal power sensor abstraction"
```

---

## Task 3: Scaffold the device and test projects

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik/Namotion.Devices.Luxtronik.csproj`
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik.Tests/Namotion.Devices.Luxtronik.Tests.csproj`
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik.Tests/Testing/LuxtronikIntegrationCollection.cs`
- Modify: `src/Namotion.Interceptor.slnx` (`/HomeBlaze/Devices/` folder)

- [ ] **Step 1: Create the device project**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>preview</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Namotion.Devices.Luxtronik.Tests" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.*" />
    <PackageReference Include="SonarAnalyzer.CSharp" Version="10.33.0.1635" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\Namotion.Interceptor.Registry\Namotion.Interceptor.Registry.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor\Namotion.Interceptor.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Hosting\Namotion.Interceptor.Hosting.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Modbus\Namotion.Interceptor.Modbus.csproj" />
    <ProjectReference Include="..\HomeBlaze.Abstractions\HomeBlaze.Abstractions.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the test project**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>preview</LangVersion>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="FluentModbus" Version="5.3.2" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.*" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.*" />
    <PackageReference Include="SonarAnalyzer.CSharp" Version="10.33.0.1635" PrivateAssets="all" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Namotion.Devices.Luxtronik\Namotion.Devices.Luxtronik.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Testing\Namotion.Interceptor.Testing.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>

</Project>
```

`Microsoft.Extensions.DependencyInjection` provides `ServiceCollection.BuildServiceProvider` for the lifecycle test in Task 8. There is no `Verify.Xunit` here to supply the global `Xunit` using, so the project declares it.

- [ ] **Step 3: Add the integration test collection**

`Testing/LuxtronikIntegrationCollection.cs`:

```csharp
namespace Namotion.Devices.Luxtronik.Tests.Testing;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LuxtronikIntegrationCollection
{
    public const string Name = "Luxtronik integration";
}
```

The simulated-controller tests (Task 8) join it so their servers, sockets and polling loops do not compete with each other under a parallel run.

- [ ] **Step 4: Add the projects to the solution**

In `src/Namotion.Interceptor.slnx`, inside `<Folder Name="/HomeBlaze/Devices/">` after the Ecowitt lines:

```xml
    <Project Path="HomeBlaze/Namotion.Devices.Luxtronik/Namotion.Devices.Luxtronik.csproj" />
    <Project Path="HomeBlaze/Namotion.Devices.Luxtronik.Tests/Namotion.Devices.Luxtronik.Tests.csproj" />
```

- [ ] **Step 5: Build**

Run: `dotnet build src/HomeBlaze/Namotion.Devices.Luxtronik.Tests`
Expected: `Build succeeded`, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Luxtronik src/HomeBlaze/Namotion.Devices.Luxtronik.Tests src/Namotion.Interceptor.slnx
git commit -m "feat: scaffold the Luxtronik device library"
```

---

## Task 4: Enums, preset attributes and gating

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik/Enums/*.cs` (10 files)
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik/Attributes/LuxtronikInputRegisterAttribute.cs`, `LuxtronikHoldingRegisterAttribute.cs`
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik/Gating/ILuxtronikRegisterGate.cs`, `ILuxtronikGatedSubject.cs`, `LuxtronikGating.cs`
- Test: `src/HomeBlaze/Namotion.Devices.Luxtronik.Tests/LuxtronikGatingTests.cs`

- [ ] **Step 1: Add the enums**

Each in its own file under `Enums/`, namespace `Namotion.Devices.Luxtronik`:

```csharp
namespace Namotion.Devices.Luxtronik;

/// <summary>
/// The heat pump's current operation (input 10002).
/// </summary>
public enum LuxtronikOperationMode : ushort
{
    Heating = 0,
    HotWater = 1,
    PoolOrSolar = 2,
    UtilityLockout = 3,
    Defrost = 4,
    NoRequest = 5,

    /// <summary>Not assigned in the AIT manual; python-luxtronik reports it as heating with an external source.</summary>
    HeatingExternalSource = 6,

    Cooling = 7
}
```

```csharp
namespace Namotion.Devices.Luxtronik;

/// <summary>
/// State of an operating mode (inputs 10003 to 10007).
/// </summary>
public enum LuxtronikModeStatus : ushort
{
    Disabled = 0,
    NoRequest = 1,
    Requested = 2,
    Running = 3
}
```

```csharp
namespace Namotion.Devices.Luxtronik;

/// <summary>
/// How a Smart Home Interface control influences a circuit.
/// </summary>
public enum LuxtronikControlMode : ushort
{
    NoInfluence = 0,
    Setpoint = 1,
    Offset = 2,
    Level = 3
}
```

```csharp
namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Overall heating control (holding 10065). Value 1 is not defined.
/// </summary>
public enum LuxtronikOverallHeatingMode : ushort
{
    Individual = 0,
    Offset = 2,
    Level = 3
}
```

```csharp
namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Smart Home Interface level for heating and hot water.
/// </summary>
public enum LuxtronikLevelMode : ushort
{
    NoInfluence = 0,
    RaisedWithTimeProgram = 1,
    RaisedIgnoringTimeProgram = 2,
    Lowered = 3
}
```

```csharp
namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Power consumption limitation mode (holding 10040).
/// </summary>
public enum LuxtronikPowerLimitMode : ushort
{
    NoLimit = 0,
    SoftLimit = 1,
    HardLimit = 2
}
```

```csharp
namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Configured buffer tank type (input 10202).
/// </summary>
public enum LuxtronikBufferType : ushort
{
    SeriesBuffer = 0,
    SeparationBuffer = 1,
    MultifunctionBuffer = 2
}
```

```csharp
namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Smart Grid state signalled by the utility through the EVU1 and EVU2 inputs.
/// </summary>
public enum LuxtronikSmartGridState
{
    Locked,
    Reduced,
    Normal,
    Increased
}
```

```csharp
namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Controller functions that can be configured; the value is the offset of its discrete input from 10000.
/// </summary>
public enum LuxtronikFeature
{
    None = -1,
    Heating = 0,
    HotWater = 1,
    Cooling = 2,
    Pool = 3,
    Solar = 4,
    RoomControlUnit = 5,
    MixingCircuit1Heating = 6,
    MixingCircuit1Cooling = 7,
    MixingCircuit2Heating = 8,
    MixingCircuit2Cooling = 9,
    MixingCircuit3Heating = 10,
    MixingCircuit3Cooling = 11
}
```

```csharp
namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Running compressors and auxiliary heaters (input 10000).
/// </summary>
[Flags]
public enum LuxtronikHeatPumpStatus : ushort
{
    None = 0,
    Compressor1 = 1,
    Compressor2 = 2,
    AuxiliaryHeater1 = 4,
    AuxiliaryHeater2 = 8,
    AuxiliaryHeater3 = 16
}
```

- [ ] **Step 2: Add the gating interfaces**

`Gating/ILuxtronikRegisterGate.cs`:

```csharp
namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Firmware and feature requirements declared on a register attribute.
/// </summary>
internal interface ILuxtronikRegisterGate
{
    Version? MinimumFirmwareVersion { get; }

    LuxtronikFeature Feature { get; }
}
```

`Gating/ILuxtronikGatedSubject.cs`:

```csharp
namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Firmware and feature requirements of every register of a subject, for classes reused at several addresses.
/// </summary>
internal interface ILuxtronikGatedSubject
{
    string? MinimumFirmware { get; }

    LuxtronikFeature Feature { get; }
}
```

- [ ] **Step 3: Add the preset attributes**

`Attributes/LuxtronikInputRegisterAttribute.cs`:

```csharp
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Modbus.Attributes;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// A read-only Smart Home Interface input register; 0x7FFF and 0x7FFFFFFF map to <c>null</c>.
/// </summary>
public sealed class LuxtronikInputRegisterAttribute : ModbusRegisterAttribute, ILuxtronikRegisterGate
{
    private Version? _minimumFirmwareVersion;

    public LuxtronikInputRegisterAttribute(int address, ModbusDataType dataType)
        : base(address, dataType)
    {
        Space = ModbusAddressSpace.InputRegister;
        NotAvailableValue = ModbusNotAvailableValue.SignedMaximum;
    }

    /// <summary>
    /// Gets the first firmware version providing the register, such as "3.92.0".
    /// </summary>
    public string? MinimumFirmware { get; init; }

    /// <summary>
    /// Gets the controller function that must be configured for the register to be read;
    /// <see cref="LuxtronikFeature.None"/> (the default) means no requirement.
    /// </summary>
    public LuxtronikFeature Feature { get; init; } = LuxtronikFeature.None;

    Version? ILuxtronikRegisterGate.MinimumFirmwareVersion =>
        MinimumFirmware is null ? null : _minimumFirmwareVersion ??= Version.Parse(MinimumFirmware);
}
```

`Attributes/LuxtronikHoldingRegisterAttribute.cs`:

```csharp
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Modbus.Attributes;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// A Smart Home Interface holding register, read only in this version; 0x7FFF and 0x7FFFFFFF map to <c>null</c>.
/// </summary>
public sealed class LuxtronikHoldingRegisterAttribute : ModbusRegisterAttribute, ILuxtronikRegisterGate
{
    private Version? _minimumFirmwareVersion;

    public LuxtronikHoldingRegisterAttribute(int address, ModbusDataType dataType)
        : base(address, dataType)
    {
        Space = ModbusAddressSpace.HoldingRegister;
        Access = ModbusAccess.ReadOnly;
        NotAvailableValue = ModbusNotAvailableValue.SignedMaximum;
    }

    /// <summary>
    /// Gets the first firmware version providing the register, such as "3.92.0".
    /// </summary>
    public string? MinimumFirmware { get; init; }

    /// <summary>
    /// Gets the controller function that must be configured for the register to be read;
    /// <see cref="LuxtronikFeature.None"/> (the default) means no requirement.
    /// </summary>
    public LuxtronikFeature Feature { get; init; } = LuxtronikFeature.None;

    Version? ILuxtronikRegisterGate.MinimumFirmwareVersion =>
        MinimumFirmware is null ? null : _minimumFirmwareVersion ??= Version.Parse(MinimumFirmware);
}
```

- [ ] **Step 4: Write the failing gating tests**

`LuxtronikGatingTests.cs`:

```csharp
namespace Namotion.Devices.Luxtronik.Tests;

public class LuxtronikGatingTests
{
    [Theory]
    [InlineData(null, 3, 90, 1, true)]
    [InlineData("3.92.0", 3, 90, 1, false)]
    [InlineData("3.92.0", 3, 92, 0, true)]
    [InlineData("3.92.1", 3, 92, 0, false)]
    [InlineData("3.92.0", 3, 92, 3, true)]
    public void WhenCheckingFirmware_ThenMinimumVersionIsEnforced(string? minimumFirmware, int major, int minor, int patch, bool expected)
    {
        // Act
        var isSupported = LuxtronikGating.IsSupported(minimumFirmware, LuxtronikFeature.None, new Version(major, minor, patch), configuredFeatures: null);

        // Assert
        Assert.Equal(expected, isSupported);
    }

    [Fact]
    public void WhenFeatureIsNotConfigured_ThenItIsNotSupported()
    {
        // Arrange
        var configuredFeatures = new HashSet<LuxtronikFeature> { LuxtronikFeature.Heating };

        // Act
        var isSupported = LuxtronikGating.IsSupported(null, LuxtronikFeature.Pool, new Version(3, 92, 3), configuredFeatures);

        // Assert
        Assert.False(isSupported);
    }

    [Fact]
    public void WhenFeatureFlagsAreUnknown_ThenFeatureGatesAreIgnored()
    {
        // Act
        var isSupported = LuxtronikGating.IsSupported(null, LuxtronikFeature.Pool, new Version(3, 92, 3), configuredFeatures: null);

        // Assert
        Assert.True(isSupported);
    }

    [Fact]
    public void WhenReadingFeatureFlags_ThenSetBitsBecomeConfiguredFeatures()
    {
        // Arrange
        var flags = new bool[12];
        flags[0] = true;
        flags[2] = true;
        flags[6] = true;

        // Act
        var features = LuxtronikGating.GetConfiguredFeatures(flags);

        // Assert
        Assert.Equal(
            new[] { LuxtronikFeature.Heating, LuxtronikFeature.Cooling, LuxtronikFeature.MixingCircuit1Heating },
            features.OrderBy(feature => feature));
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Luxtronik.Tests --filter "FullyQualifiedName~LuxtronikGatingTests"`
Expected: build error, `LuxtronikGating` does not exist.

- [ ] **Step 6: Implement the gating helper**

`Gating/LuxtronikGating.cs`:

```csharp
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Devices.Luxtronik;

internal static class LuxtronikGating
{
    public const int FeatureFlagCount = 12;

    /// <summary>
    /// Checks the property's register attribute gate and its subject's gate.
    /// </summary>
    public static bool IsSupported(
        RegisteredSubjectProperty property, Version firmwareVersion, IReadOnlySet<LuxtronikFeature>? configuredFeatures)
    {
        foreach (var attribute in property.ReflectionAttributes)
        {
            if (attribute is ILuxtronikRegisterGate gate &&
                !IsSupportedCore(gate.MinimumFirmwareVersion, gate.Feature, firmwareVersion, configuredFeatures))
            {
                return false;
            }
        }

        return property.Subject is not ILuxtronikGatedSubject subject ||
            IsSupported(subject.MinimumFirmware, subject.Feature, firmwareVersion, configuredFeatures);
    }

    /// <summary>
    /// Checks a firmware and feature requirement. <c>null</c> configured features means the controller does not report them, so feature gates pass.
    /// </summary>
    public static bool IsSupported(
        string? minimumFirmware, LuxtronikFeature feature, Version firmwareVersion, IReadOnlySet<LuxtronikFeature>? configuredFeatures)
        => IsSupportedCore(minimumFirmware is null ? null : Version.Parse(minimumFirmware), feature, firmwareVersion, configuredFeatures);

    private static bool IsSupportedCore(
        Version? minimumFirmware, LuxtronikFeature feature, Version firmwareVersion, IReadOnlySet<LuxtronikFeature>? configuredFeatures)
    {
        if (minimumFirmware is not null && firmwareVersion < minimumFirmware)
        {
            return false;
        }

        return feature == LuxtronikFeature.None || configuredFeatures is null || configuredFeatures.Contains(feature);
    }

    public static HashSet<LuxtronikFeature> GetConfiguredFeatures(bool[] flags)
    {
        var features = new HashSet<LuxtronikFeature>();
        for (var index = 0; index < Math.Min(flags.Length, FeatureFlagCount); index++)
        {
            if (flags[index])
            {
                features.Add((LuxtronikFeature)index);
            }
        }

        return features;
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Luxtronik.Tests --filter "FullyQualifiedName~LuxtronikGatingTests"`
Expected: PASS, 8 tests.

- [ ] **Step 8: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Luxtronik src/HomeBlaze/Namotion.Devices.Luxtronik.Tests
git commit -m "feat: add Luxtronik enums, register attributes and firmware gating"
```

---
## Task 5: Input register model

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik/Model/LuxtronikFeatures.cs`, `LuxtronikOperatingStatus.cs`, `LuxtronikTemperatureSensor.cs`, `LuxtronikTemperatures.cs`, `LuxtronikEnergy.cs`, `LuxtronikOutputs.cs`, `LuxtronikSmartGrid.cs`, `LuxtronikRuntime.cs`, `LuxtronikExtraHotWater.cs`
- Test: `src/HomeBlaze/Namotion.Devices.Luxtronik.Tests/LuxtronikModelTests.cs`

Addresses in attributes are offsets from the class's `BaseAddress`. Groups that exist only from firmware 3.92 gate the whole subject through `ILuxtronikGatedSubject`; single registers gate through the attribute's `MinimumFirmware`/`Feature`. Every file starts with the source reference required by spec 5.1 and the common usings:

```csharp
// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Modbus.Attributes;

namespace Namotion.Devices.Luxtronik;
```

- [ ] **Step 1: Write the failing model tests**

```csharp
namespace Namotion.Devices.Luxtronik.Tests;

public class LuxtronikModelTests
{
    [Fact]
    public void WhenCompressorFlagIsSet_ThenCompressorIsRunning()
    {
        // Arrange
        var status = new LuxtronikOperatingStatus();

        // Act
        status.HeatPumpStatus = LuxtronikHeatPumpStatus.Compressor2;

        // Assert
        Assert.True(status.IsCompressorRunning);
        Assert.False(status.IsAuxiliaryHeaterRunning);
    }

    [Fact]
    public void WhenHeatPumpStatusIsUnknown_ThenRunningFlagsAreNull()
    {
        // Act
        var status = new LuxtronikOperatingStatus();

        // Assert
        Assert.Null(status.IsCompressorRunning);
        Assert.Null(status.IsAuxiliaryHeaterRunning);
    }

    [Theory]
    [InlineData(true, false, LuxtronikSmartGridState.Locked)]
    [InlineData(false, false, LuxtronikSmartGridState.Reduced)]
    [InlineData(false, true, LuxtronikSmartGridState.Normal)]
    [InlineData(true, true, LuxtronikSmartGridState.Increased)]
    public void WhenEvuSignalsAreSet_ThenSmartGridStateFollowsTheManual(bool evu1, bool evu2, LuxtronikSmartGridState expected)
    {
        // Arrange
        var smartGrid = new LuxtronikSmartGrid();

        // Act
        smartGrid.Evu1 = evu1;
        smartGrid.Evu2 = evu2;

        // Assert
        Assert.Equal(expected, smartGrid.State);
    }

    [Fact]
    public void WhenTemperaturesAreConstructed_ThenSensorsUseTheirRegisterAddresses()
    {
        // Act
        var temperatures = new LuxtronikTemperatures();

        // Assert
        Assert.Equal(10108, temperatures.Outside.BaseAddress);
        Assert.Equal(10120, temperatures.HotWater.BaseAddress);
        Assert.Null(((ILuxtronikGatedSubject)temperatures.Outside).MinimumFirmware);
        Assert.Equal("3.92.0", ((ILuxtronikGatedSubject)temperatures.HeatSourceInlet).MinimumFirmware);
        Assert.Equal("Outside", temperatures.Outside.Title);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Luxtronik.Tests --filter "FullyQualifiedName~LuxtronikModelTests"`
Expected: build error, the model types do not exist.

- [ ] **Step 3: Configured features (discrete inputs)**

`Model/LuxtronikFeatures.cs`:

```csharp
/// <summary>
/// Which controller functions are configured (discrete inputs 10000 to 10011).
/// </summary>
[InterceptorSubject]
public partial class LuxtronikFeatures : IModbusBaseAddressProvider
{
    public LuxtronikFeatures()
    {
        Heating = null;
        HotWater = null;
        Cooling = null;
        Pool = null;
        Solar = null;
        RoomControlUnit = null;
        MixingCircuit1Heating = null;
        MixingCircuit1Cooling = null;
        MixingCircuit2Heating = null;
        MixingCircuit2Cooling = null;
        MixingCircuit3Heating = null;
        MixingCircuit3Cooling = null;
    }

    public int BaseAddress => 10000;

    [ModbusRegister(0, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 1)]
    public partial bool? Heating { get; internal set; }

    [ModbusRegister(1, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 2)]
    public partial bool? HotWater { get; internal set; }

    [ModbusRegister(2, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 3)]
    public partial bool? Cooling { get; internal set; }

    [ModbusRegister(3, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 4)]
    public partial bool? Pool { get; internal set; }

    [ModbusRegister(4, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 5)]
    public partial bool? Solar { get; internal set; }

    [ModbusRegister(5, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 6)]
    public partial bool? RoomControlUnit { get; internal set; }

    [ModbusRegister(6, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 7)]
    public partial bool? MixingCircuit1Heating { get; internal set; }

    [ModbusRegister(7, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 8)]
    public partial bool? MixingCircuit1Cooling { get; internal set; }

    [ModbusRegister(8, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 9)]
    public partial bool? MixingCircuit2Heating { get; internal set; }

    [ModbusRegister(9, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 10)]
    public partial bool? MixingCircuit2Cooling { get; internal set; }

    [ModbusRegister(10, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 11)]
    public partial bool? MixingCircuit3Heating { get; internal set; }

    [ModbusRegister(11, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 12)]
    public partial bool? MixingCircuit3Cooling { get; internal set; }
}
```

- [ ] **Step 4: Operating status**

`Model/LuxtronikOperatingStatus.cs`:

```csharp
/// <summary>
/// Operating state of the heat pump (inputs 10000 to 10007 and 10201 to 10207).
/// </summary>
[InterceptorSubject]
public partial class LuxtronikOperatingStatus : IModbusBaseAddressProvider
{
    public LuxtronikOperatingStatus()
    {
        HeatPumpStatus = null;
        OperationMode = null;
        HeatingStatus = null;
        HotWaterStatus = null;
        CoolingStatus = null;
        PoolHeatingStatus = null;
        ErrorCode = null;
        BufferType = null;
        MinimumOffTime = null;
        MinimumRunTime = null;
        CoolingReleased = null;
    }

    public int BaseAddress => 10000;

    [LuxtronikInputRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikHeatPumpStatus? HeatPumpStatus { get; internal set; }

    [LuxtronikInputRegister(2, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 2)]
    public partial LuxtronikOperationMode? OperationMode { get; internal set; }

    [LuxtronikInputRegister(3, ModbusDataType.U16, Feature = LuxtronikFeature.Heating)]
    [State(IsDiscrete = true, Position = 3)]
    public partial LuxtronikModeStatus? HeatingStatus { get; internal set; }

    [LuxtronikInputRegister(4, ModbusDataType.U16, Feature = LuxtronikFeature.HotWater)]
    [State(IsDiscrete = true, Position = 4)]
    public partial LuxtronikModeStatus? HotWaterStatus { get; internal set; }

    [LuxtronikInputRegister(6, ModbusDataType.U16, Feature = LuxtronikFeature.Cooling)]
    [State(IsDiscrete = true, Position = 5)]
    public partial LuxtronikModeStatus? CoolingStatus { get; internal set; }

    [LuxtronikInputRegister(7, ModbusDataType.U16, Feature = LuxtronikFeature.Pool)]
    [State(IsDiscrete = true, Position = 6)]
    public partial LuxtronikModeStatus? PoolHeatingStatus { get; internal set; }

    /// <summary>
    /// Gets the controller error number, 0 when there is no error.
    /// </summary>
    [LuxtronikInputRegister(201, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 7)]
    public partial ushort? ErrorCode { get; internal set; }

    [LuxtronikInputRegister(202, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 8)]
    public partial LuxtronikBufferType? BufferType { get; internal set; }

    [LuxtronikInputRegister(203, ModbusDataType.U16)]
    [State(Unit = StateUnit.Minute, Position = 9)]
    public partial int? MinimumOffTime { get; internal set; }

    [LuxtronikInputRegister(204, ModbusDataType.U16)]
    [State(Unit = StateUnit.Minute, Position = 10)]
    public partial int? MinimumRunTime { get; internal set; }

    [LuxtronikInputRegister(207, ModbusDataType.U16, Feature = LuxtronikFeature.Cooling)]
    [State(IsDiscrete = true, Position = 11)]
    public partial bool? CoolingReleased { get; internal set; }

    [Derived]
    [State(IsDiscrete = true, Position = 20)]
    public bool? IsCompressorRunning => HeatPumpStatus is { } status
        ? (status & (LuxtronikHeatPumpStatus.Compressor1 | LuxtronikHeatPumpStatus.Compressor2)) != 0
        : null;

    [Derived]
    [State(IsDiscrete = true, Position = 21)]
    public bool? IsAuxiliaryHeaterRunning => HeatPumpStatus is { } status
        ? (status & (LuxtronikHeatPumpStatus.AuxiliaryHeater1 | LuxtronikHeatPumpStatus.AuxiliaryHeater2 | LuxtronikHeatPumpStatus.AuxiliaryHeater3)) != 0
        : null;
}
```

- [ ] **Step 5: Temperature sensor and temperatures**

`Model/LuxtronikTemperatureSensor.cs` (own usings, same source reference line):

```csharp
// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// One measured temperature. Its <see cref="BaseAddress"/> is the input register address itself.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikTemperatureSensor : ITemperatureSensor, ITitleProvider, IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    private readonly string? _minimumFirmware;
    private readonly LuxtronikFeature _feature;

    public LuxtronikTemperatureSensor(int address, string title, string? minimumFirmware = null, LuxtronikFeature feature = LuxtronikFeature.None)
    {
        BaseAddress = address;
        Title = title;
        _minimumFirmware = minimumFirmware;
        _feature = feature;
        Temperature = null;
    }

    public int BaseAddress { get; }

    public string? Title { get; }

    // S16 for every temperature: the registers the manual types as UINT16 decode the same below 3276.7 degrees.
    [LuxtronikInputRegister(0, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; internal set; }

    string? ILuxtronikGatedSubject.MinimumFirmware => _minimumFirmware;

    LuxtronikFeature ILuxtronikGatedSubject.Feature => _feature;
}
```

`Model/LuxtronikTemperatures.cs`:

```csharp
/// <summary>
/// Temperatures (inputs 10100 to 10124). Measured values are sensor children; targets and limits are plain values.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikTemperatures : IModbusBaseAddressProvider
{
    public LuxtronikTemperatures()
    {
        Return = new LuxtronikTemperatureSensor(10100, "Return");
        ExternalReturn = new LuxtronikTemperatureSensor(10102, "External return");
        Flow = new LuxtronikTemperatureSensor(10105, "Flow");
        Room = new LuxtronikTemperatureSensor(10106, "Room");
        Outside = new LuxtronikTemperatureSensor(10108, "Outside");
        OutsideAverage = new LuxtronikTemperatureSensor(10109, "Outside average", "3.92.0");
        HeatSourceInlet = new LuxtronikTemperatureSensor(10110, "Heat source inlet", "3.92.0");
        HeatSourceOutlet = new LuxtronikTemperatureSensor(10111, "Heat source outlet", "3.92.0");
        HotWater = new LuxtronikTemperatureSensor(10120, "Hot water");

        ReturnTarget = null;
        ReturnLimit = null;
        ReturnMinimumTarget = null;
        HeatingLimit = null;
        MaximumFlow = null;
        CalculatedFlow = null;
        HotWaterTarget = null;
        HotWaterMinimum = null;
        HotWaterMaximum = null;
        HotWaterLimit = null;
    }

    public int BaseAddress => 10100;

    [State(Position = 1)]
    public partial LuxtronikTemperatureSensor Return { get; internal set; }

    [State(Position = 2)]
    public partial LuxtronikTemperatureSensor ExternalReturn { get; internal set; }

    [State(Position = 3)]
    public partial LuxtronikTemperatureSensor Flow { get; internal set; }

    [State(Position = 4)]
    public partial LuxtronikTemperatureSensor Room { get; internal set; }

    [State(Position = 5)]
    public partial LuxtronikTemperatureSensor Outside { get; internal set; }

    [State(Position = 6)]
    public partial LuxtronikTemperatureSensor OutsideAverage { get; internal set; }

    [State(Position = 7)]
    public partial LuxtronikTemperatureSensor HeatSourceInlet { get; internal set; }

    [State(Position = 8)]
    public partial LuxtronikTemperatureSensor HeatSourceOutlet { get; internal set; }

    [State(Position = 9)]
    public partial LuxtronikTemperatureSensor HotWater { get; internal set; }

    [LuxtronikInputRegister(1, ModbusDataType.U16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 20)]
    public partial decimal? ReturnTarget { get; internal set; }

    [LuxtronikInputRegister(3, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 21)]
    public partial decimal? ReturnLimit { get; internal set; }

    [LuxtronikInputRegister(4, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 22)]
    public partial decimal? ReturnMinimumTarget { get; internal set; }

    [LuxtronikInputRegister(7, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 23)]
    public partial decimal? HeatingLimit { get; internal set; }

    [LuxtronikInputRegister(12, ModbusDataType.U16, Scale = 0.1, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.DegreeCelsius, Position = 24)]
    public partial decimal? MaximumFlow { get; internal set; }

    [LuxtronikInputRegister(13, ModbusDataType.S16, Scale = 0.1, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.DegreeCelsius, Position = 25)]
    public partial decimal? CalculatedFlow { get; internal set; }

    [LuxtronikInputRegister(21, ModbusDataType.U16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 26)]
    public partial decimal? HotWaterTarget { get; internal set; }

    [LuxtronikInputRegister(22, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 27)]
    public partial decimal? HotWaterMinimum { get; internal set; }

    [LuxtronikInputRegister(23, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 28)]
    public partial decimal? HotWaterMaximum { get; internal set; }

    [LuxtronikInputRegister(24, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 29)]
    public partial decimal? HotWaterLimit { get; internal set; }
}
```

- [ ] **Step 6: Energy**

`Model/LuxtronikEnergy.cs`:

```csharp
/// <summary>
/// Power and energy (inputs 10300 to 10329) in watts and watt-hours. The controller reports kW and kWh in tenths.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikEnergy : IModbusBaseAddressProvider
{
    public LuxtronikEnergy()
    {
        HeatingPower = null;
        ElectricalPower = null;
        MinimumPredictedElectricalPower = null;
        TotalElectricalEnergy = null;
        HeatingElectricalEnergy = null;
        HotWaterElectricalEnergy = null;
        CoolingElectricalEnergy = null;
        PoolElectricalEnergy = null;
        TotalThermalEnergy = null;
        HeatingThermalEnergy = null;
        HotWaterThermalEnergy = null;
        CoolingThermalEnergy = null;
        PoolThermalEnergy = null;
    }

    public int BaseAddress => 10300;

    /// <summary>
    /// Gets the thermal power currently produced.
    /// </summary>
    [LuxtronikInputRegister(0, ModbusDataType.S16, Scale = 100)]
    [State(Unit = StateUnit.Watt, Position = 1)]
    public partial decimal? HeatingPower { get; internal set; }

    [LuxtronikInputRegister(1, ModbusDataType.U16, Scale = 100)]
    [State(Unit = StateUnit.Watt, Position = 2)]
    public partial decimal? ElectricalPower { get; internal set; }

    [LuxtronikInputRegister(2, ModbusDataType.U16, Scale = 100)]
    [State(Unit = StateUnit.Watt, Position = 3)]
    public partial decimal? MinimumPredictedElectricalPower { get; internal set; }

    [LuxtronikInputRegister(10, ModbusDataType.S32, Scale = 100)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 10)]
    public partial decimal? TotalElectricalEnergy { get; internal set; }

    [LuxtronikInputRegister(12, ModbusDataType.S32, Scale = 100)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 11)]
    public partial decimal? HeatingElectricalEnergy { get; internal set; }

    [LuxtronikInputRegister(14, ModbusDataType.S32, Scale = 100)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 12)]
    public partial decimal? HotWaterElectricalEnergy { get; internal set; }

    [LuxtronikInputRegister(16, ModbusDataType.S32, Scale = 100, Feature = LuxtronikFeature.Cooling)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 13)]
    public partial decimal? CoolingElectricalEnergy { get; internal set; }

    [LuxtronikInputRegister(18, ModbusDataType.S32, Scale = 100, Feature = LuxtronikFeature.Pool)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 14)]
    public partial decimal? PoolElectricalEnergy { get; internal set; }

    [LuxtronikInputRegister(20, ModbusDataType.S32, Scale = 100, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 20)]
    public partial decimal? TotalThermalEnergy { get; internal set; }

    [LuxtronikInputRegister(22, ModbusDataType.S32, Scale = 100, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 21)]
    public partial decimal? HeatingThermalEnergy { get; internal set; }

    [LuxtronikInputRegister(24, ModbusDataType.S32, Scale = 100, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 22)]
    public partial decimal? HotWaterThermalEnergy { get; internal set; }

    [LuxtronikInputRegister(26, ModbusDataType.S32, Scale = 100, MinimumFirmware = "3.92.0", Feature = LuxtronikFeature.Cooling)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 23)]
    public partial decimal? CoolingThermalEnergy { get; internal set; }

    [LuxtronikInputRegister(28, ModbusDataType.S32, Scale = 100, MinimumFirmware = "3.92.0", Feature = LuxtronikFeature.Pool)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 24)]
    public partial decimal? PoolThermalEnergy { get; internal set; }
}
```

- [ ] **Step 7: Outputs, Smart Grid, runtime and extra hot water (3.92 groups)**

`Model/LuxtronikOutputs.cs`:

```csharp
/// <summary>
/// Pump outputs (inputs 10350 to 10356), firmware 3.92 and later.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikOutputs : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    public LuxtronikOutputs()
    {
        BrineCirculationPump = null;
        MixingCircuit1Pump = null;
        MixingCircuit2Pump = null;
        MixingCircuit3Pump = null;
        HeatingCirculationPump = null;
        HotWaterLoadingPump = null;
        CirculationPump = null;
    }

    public int BaseAddress => 10350;

    [LuxtronikInputRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial bool? BrineCirculationPump { get; internal set; }

    [LuxtronikInputRegister(1, ModbusDataType.U16, Feature = LuxtronikFeature.MixingCircuit1Heating)]
    [State(IsDiscrete = true, Position = 2)]
    public partial bool? MixingCircuit1Pump { get; internal set; }

    [LuxtronikInputRegister(2, ModbusDataType.U16, Feature = LuxtronikFeature.MixingCircuit2Heating)]
    [State(IsDiscrete = true, Position = 3)]
    public partial bool? MixingCircuit2Pump { get; internal set; }

    [LuxtronikInputRegister(3, ModbusDataType.U16, Feature = LuxtronikFeature.MixingCircuit3Heating)]
    [State(IsDiscrete = true, Position = 4)]
    public partial bool? MixingCircuit3Pump { get; internal set; }

    [LuxtronikInputRegister(4, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 5)]
    public partial bool? HeatingCirculationPump { get; internal set; }

    [LuxtronikInputRegister(5, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 6)]
    public partial bool? HotWaterLoadingPump { get; internal set; }

    [LuxtronikInputRegister(6, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 7)]
    public partial bool? CirculationPump { get; internal set; }

    string? ILuxtronikGatedSubject.MinimumFirmware => "3.92.0";

    LuxtronikFeature ILuxtronikGatedSubject.Feature => LuxtronikFeature.None;
}
```

`Model/LuxtronikSmartGrid.cs`:

```csharp
/// <summary>
/// Smart Grid signals from the utility (inputs 10360 and 10361), firmware 3.92 and later.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikSmartGrid : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    public LuxtronikSmartGrid()
    {
        Evu1 = null;
        Evu2 = null;
    }

    public int BaseAddress => 10360;

    [LuxtronikInputRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial bool? Evu1 { get; internal set; }

    [LuxtronikInputRegister(1, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 2)]
    public partial bool? Evu2 { get; internal set; }

    [Derived]
    [State(IsDiscrete = true, Position = 3)]
    public LuxtronikSmartGridState? State => (Evu1, Evu2) switch
    {
        (true, false) => LuxtronikSmartGridState.Locked,
        (false, false) => LuxtronikSmartGridState.Reduced,
        (false, true) => LuxtronikSmartGridState.Normal,
        (true, true) => LuxtronikSmartGridState.Increased,
        _ => null
    };

    string? ILuxtronikGatedSubject.MinimumFirmware => "3.92.0";

    LuxtronikFeature ILuxtronikGatedSubject.Feature => LuxtronikFeature.None;
}
```

`Model/LuxtronikRuntime.cs`:

```csharp
/// <summary>
/// Operating hours since installation (inputs 10404 to 10417), firmware 3.92 and later.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikRuntime : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    public LuxtronikRuntime()
    {
        HeatPump = null;
        Heating = null;
        HotWater = null;
        Cooling = null;
        Pool = null;
        Solar = null;
    }

    public int BaseAddress => 10404;

    [LuxtronikInputRegister(0, ModbusDataType.U32)]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 1)]
    public partial decimal? HeatPump { get; internal set; }

    [LuxtronikInputRegister(2, ModbusDataType.U32, Feature = LuxtronikFeature.Heating)]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 2)]
    public partial decimal? Heating { get; internal set; }

    [LuxtronikInputRegister(4, ModbusDataType.U32, Feature = LuxtronikFeature.HotWater)]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 3)]
    public partial decimal? HotWater { get; internal set; }

    [LuxtronikInputRegister(6, ModbusDataType.U32, Feature = LuxtronikFeature.Cooling)]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 4)]
    public partial decimal? Cooling { get; internal set; }

    [LuxtronikInputRegister(8, ModbusDataType.U32, Feature = LuxtronikFeature.Pool)]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 5)]
    public partial decimal? Pool { get; internal set; }

    [LuxtronikInputRegister(12, ModbusDataType.U32, Feature = LuxtronikFeature.Solar)]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 6)]
    public partial decimal? Solar { get; internal set; }

    string? ILuxtronikGatedSubject.MinimumFirmware => "3.92.0";

    LuxtronikFeature ILuxtronikGatedSubject.Feature => LuxtronikFeature.None;
}
```

`Model/LuxtronikExtraHotWater.cs`:

```csharp
/// <summary>
/// Extra hot water request state (inputs 10500 to 10502), firmware 3.92 and later.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikExtraHotWater : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    public LuxtronikExtraHotWater()
    {
        Setpoint = null;
        Duration = null;
        RemainingDuration = null;
    }

    public int BaseAddress => 10500;

    [LuxtronikInputRegister(0, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 1)]
    public partial decimal? Setpoint { get; internal set; }

    [LuxtronikInputRegister(1, ModbusDataType.S16)]
    [State(Unit = StateUnit.Minute, Position = 2)]
    public partial int? Duration { get; internal set; }

    [LuxtronikInputRegister(2, ModbusDataType.S16)]
    [State(Unit = StateUnit.Minute, Position = 3)]
    public partial int? RemainingDuration { get; internal set; }

    string? ILuxtronikGatedSubject.MinimumFirmware => "3.92.0";

    LuxtronikFeature ILuxtronikGatedSubject.Feature => LuxtronikFeature.HotWater;
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Luxtronik.Tests --filter "FullyQualifiedName~LuxtronikModelTests"`
Expected: PASS, 7 tests.

- [ ] **Step 9: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Luxtronik src/HomeBlaze/Namotion.Devices.Luxtronik.Tests
git commit -m "feat: model the Luxtronik SHI input registers"
```

---

## Task 6: Holding register model (controls, read only)

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik/Model/LuxtronikControl.cs`, `LuxtronikCoolingControl.cs`, `LuxtronikMixingCircuitSetpoints.cs`, `LuxtronikMixingCircuit.cs`, `LuxtronikPowerLimit.cs`, `LuxtronikLocks.cs`, `LuxtronikRoomControl.cs`, `LuxtronikOverallHeating.cs`, `LuxtronikHotWaterRequests.cs`
- Modify: `src/HomeBlaze/Namotion.Devices.Luxtronik.Tests/LuxtronikModelTests.cs`

Same file header as Task 5 (source reference line and common usings).

- [ ] **Step 1: Write the failing tests**

Add to `LuxtronikModelTests`:

```csharp
    [Theory]
    [InlineData(1, 10140, 10141, 10010, 10015, LuxtronikFeature.MixingCircuit1Heating, LuxtronikFeature.MixingCircuit1Cooling)]
    [InlineData(2, 10150, 10151, 10020, 10025, LuxtronikFeature.MixingCircuit2Heating, LuxtronikFeature.MixingCircuit2Cooling)]
    [InlineData(3, 10160, 10161, 10030, 10035, LuxtronikFeature.MixingCircuit3Heating, LuxtronikFeature.MixingCircuit3Cooling)]
    public void WhenMixingCircuitIsConstructed_ThenChildrenUseItsAddressesAndFeatures(
        int index, int temperatureAddress, int setpointsAddress, int heatingAddress, int coolingAddress,
        LuxtronikFeature heatingFeature, LuxtronikFeature coolingFeature)
    {
        // Act
        var circuit = new LuxtronikMixingCircuit(index);

        // Assert
        Assert.Equal(temperatureAddress, circuit.Temperature.BaseAddress);
        Assert.Equal(setpointsAddress, circuit.Setpoints.BaseAddress);
        Assert.Equal(heatingAddress, circuit.Heating.BaseAddress);
        Assert.Equal(coolingAddress, circuit.Cooling.BaseAddress);
        Assert.Equal(heatingFeature, ((ILuxtronikGatedSubject)circuit.Heating).Feature);
        Assert.Equal(coolingFeature, ((ILuxtronikGatedSubject)circuit.Cooling).Feature);
        Assert.Equal(heatingFeature, ((ILuxtronikGatedSubject)circuit.Temperature).Feature);
    }

    [Fact]
    public void WhenMixingCircuitIndexIsOutOfRange_ThenArgumentOutOfRangeExceptionIsThrown()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuxtronikMixingCircuit(4));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Luxtronik.Tests --filter "FullyQualifiedName~LuxtronikModelTests"`
Expected: build error, `LuxtronikMixingCircuit` does not exist.

- [ ] **Step 3: Controls**

`Model/LuxtronikControl.cs`:

```csharp
/// <summary>
/// A heating or hot water control block (mode, setpoint, offset, level), reused at several holding addresses.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikControl : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    private readonly LuxtronikFeature _feature;

    public LuxtronikControl(int baseAddress, LuxtronikFeature feature)
    {
        BaseAddress = baseAddress;
        _feature = feature;
        Mode = null;
        Setpoint = null;
        Offset = null;
        Level = null;
    }

    public int BaseAddress { get; }

    [LuxtronikHoldingRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikControlMode? Mode { get; internal set; }

    /// <summary>
    /// Gets the setpoint, which only applies when <see cref="Mode"/> is <see cref="LuxtronikControlMode.Setpoint"/>.
    /// </summary>
    [LuxtronikHoldingRegister(1, ModbusDataType.U16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 2)]
    public partial decimal? Setpoint { get; internal set; }

    /// <summary>
    /// Gets the offset, which only applies when <see cref="Mode"/> is <see cref="LuxtronikControlMode.Offset"/>.
    /// </summary>
    [LuxtronikHoldingRegister(2, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.Kelvin, Position = 3)]
    public partial decimal? Offset { get; internal set; }

    [LuxtronikHoldingRegister(3, ModbusDataType.U16, MinimumFirmware = "3.92.0")]
    [State(IsDiscrete = true, Position = 4)]
    public partial LuxtronikLevelMode? Level { get; internal set; }

    string? ILuxtronikGatedSubject.MinimumFirmware => null;

    LuxtronikFeature ILuxtronikGatedSubject.Feature => _feature;
}
```

`Model/LuxtronikCoolingControl.cs`:

```csharp
/// <summary>
/// A mixing circuit cooling control block (mode, setpoint, offset).
/// </summary>
[InterceptorSubject]
public partial class LuxtronikCoolingControl : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    private readonly LuxtronikFeature _feature;

    public LuxtronikCoolingControl(int baseAddress, LuxtronikFeature feature)
    {
        BaseAddress = baseAddress;
        _feature = feature;
        Mode = null;
        Setpoint = null;
        Offset = null;
    }

    public int BaseAddress { get; }

    [LuxtronikHoldingRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikControlMode? Mode { get; internal set; }

    [LuxtronikHoldingRegister(1, ModbusDataType.U16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 2)]
    public partial decimal? Setpoint { get; internal set; }

    [LuxtronikHoldingRegister(2, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.Kelvin, Position = 3)]
    public partial decimal? Offset { get; internal set; }

    string? ILuxtronikGatedSubject.MinimumFirmware => null;

    LuxtronikFeature ILuxtronikGatedSubject.Feature => _feature;
}
```

`Model/LuxtronikMixingCircuitSetpoints.cs`:

```csharp
/// <summary>
/// Target and limits of a mixing circuit (inputs 10141 to 10143, 10151 to 10153, 10161 to 10163).
/// </summary>
[InterceptorSubject]
public partial class LuxtronikMixingCircuitSetpoints : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    private readonly LuxtronikFeature _feature;

    public LuxtronikMixingCircuitSetpoints(int baseAddress, LuxtronikFeature feature)
    {
        BaseAddress = baseAddress;
        _feature = feature;
        Target = null;
        Minimum = null;
        Maximum = null;
    }

    public int BaseAddress { get; }

    [LuxtronikInputRegister(0, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 1)]
    public partial decimal? Target { get; internal set; }

    [LuxtronikInputRegister(1, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 2)]
    public partial decimal? Minimum { get; internal set; }

    [LuxtronikInputRegister(2, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 3)]
    public partial decimal? Maximum { get; internal set; }

    string? ILuxtronikGatedSubject.MinimumFirmware => null;

    LuxtronikFeature ILuxtronikGatedSubject.Feature => _feature;
}
```

`Model/LuxtronikMixingCircuit.cs` (own usings, same source reference line):

```csharp
// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Mixing circuit 1, 2 or 3. Registers of circuit n are 10 addresses after those of circuit n - 1.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikMixingCircuit : ITitleProvider
{
    public LuxtronikMixingCircuit(int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 3);

        Index = index;
        var offset = (index - 1) * 10;
        var heatingFeature = LuxtronikFeature.MixingCircuit1Heating + (index - 1) * 2;
        var coolingFeature = heatingFeature + 1;

        Temperature = new LuxtronikTemperatureSensor(10140 + offset, $"Mixing circuit {index}", feature: heatingFeature);
        Setpoints = new LuxtronikMixingCircuitSetpoints(10141 + offset, heatingFeature);
        Heating = new LuxtronikControl(10010 + offset, heatingFeature);
        Cooling = new LuxtronikCoolingControl(10015 + offset, coolingFeature);
    }

    public int Index { get; }

    public string? Title => $"Mixing circuit {Index}";

    [State(Position = 1)]
    public partial LuxtronikTemperatureSensor Temperature { get; internal set; }

    [State(Position = 2)]
    public partial LuxtronikMixingCircuitSetpoints Setpoints { get; internal set; }

    [State(Position = 3)]
    public partial LuxtronikControl Heating { get; internal set; }

    [State(Position = 4)]
    public partial LuxtronikCoolingControl Cooling { get; internal set; }
}
```

- [ ] **Step 4: Power limit, locks, room control, overall heating and hot water requests**

`Model/LuxtronikPowerLimit.cs`:

```csharp
/// <summary>
/// Power consumption limitation (holding 10040 and 10041).
/// </summary>
[InterceptorSubject]
public partial class LuxtronikPowerLimit : IModbusBaseAddressProvider
{
    public LuxtronikPowerLimit()
    {
        Mode = null;
        Limit = null;
    }

    public int BaseAddress => 10040;

    [LuxtronikHoldingRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikPowerLimitMode? Mode { get; internal set; }

    [LuxtronikHoldingRegister(1, ModbusDataType.U16, Scale = 100)]
    [State(Unit = StateUnit.Watt, Position = 2)]
    public partial decimal? Limit { get; internal set; }
}
```

`Model/LuxtronikLocks.cs`:

```csharp
/// <summary>
/// Operating mode locks (holding 10050 to 10053).
/// </summary>
[InterceptorSubject]
public partial class LuxtronikLocks : IModbusBaseAddressProvider
{
    public LuxtronikLocks()
    {
        Heating = null;
        HotWater = null;
        Cooling = null;
        Pool = null;
    }

    public int BaseAddress => 10050;

    [LuxtronikHoldingRegister(0, ModbusDataType.U16, MinimumFirmware = "3.92.0", Feature = LuxtronikFeature.Heating)]
    [State(IsDiscrete = true, Position = 1)]
    public partial bool? Heating { get; internal set; }

    [LuxtronikHoldingRegister(1, ModbusDataType.U16, MinimumFirmware = "3.92.0", Feature = LuxtronikFeature.HotWater)]
    [State(IsDiscrete = true, Position = 2)]
    public partial bool? HotWater { get; internal set; }

    [LuxtronikHoldingRegister(2, ModbusDataType.U16, Feature = LuxtronikFeature.Cooling)]
    [State(IsDiscrete = true, Position = 3)]
    public partial bool? Cooling { get; internal set; }

    [LuxtronikHoldingRegister(3, ModbusDataType.U16, Feature = LuxtronikFeature.Pool)]
    [State(IsDiscrete = true, Position = 4)]
    public partial bool? Pool { get; internal set; }
}
```

`Model/LuxtronikRoomControl.cs`:

```csharp
/// <summary>
/// Room temperature setpoint override (holding 10060), firmware 3.92.1 and later with a room control unit.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikRoomControl : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    public LuxtronikRoomControl()
    {
        TemperatureSetpoint = null;
    }

    public int BaseAddress => 10060;

    [LuxtronikHoldingRegister(0, ModbusDataType.U16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 1)]
    public partial decimal? TemperatureSetpoint { get; internal set; }

    string? ILuxtronikGatedSubject.MinimumFirmware => "3.92.1";

    LuxtronikFeature ILuxtronikGatedSubject.Feature => LuxtronikFeature.RoomControlUnit;
}
```

`Model/LuxtronikOverallHeating.cs`:

```csharp
/// <summary>
/// Global heating control for all heating circuits (holding 10065 to 10067), firmware 3.92 and later.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikOverallHeating : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    public LuxtronikOverallHeating()
    {
        Mode = null;
        Offset = null;
        Level = null;
    }

    public int BaseAddress => 10065;

    [LuxtronikHoldingRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikOverallHeatingMode? Mode { get; internal set; }

    [LuxtronikHoldingRegister(1, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.Kelvin, Position = 2)]
    public partial decimal? Offset { get; internal set; }

    [LuxtronikHoldingRegister(2, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 3)]
    public partial LuxtronikLevelMode? Level { get; internal set; }

    string? ILuxtronikGatedSubject.MinimumFirmware => "3.92.0";

    LuxtronikFeature ILuxtronikGatedSubject.Feature => LuxtronikFeature.Heating;
}
```

`Model/LuxtronikHotWaterRequests.cs`:

```csharp
/// <summary>
/// Circulation and extra hot water requests (holding 10070 and 10071), firmware 3.92 and later.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikHotWaterRequests : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    public LuxtronikHotWaterRequests()
    {
        Circulation = null;
        ExtraHotWater = null;
    }

    public int BaseAddress => 10070;

    [LuxtronikHoldingRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial bool? Circulation { get; internal set; }

    [LuxtronikHoldingRegister(1, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 2)]
    public partial bool? ExtraHotWater { get; internal set; }

    string? ILuxtronikGatedSubject.MinimumFirmware => "3.92.0";

    LuxtronikFeature ILuxtronikGatedSubject.Feature => LuxtronikFeature.HotWater;
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Luxtronik.Tests --filter "FullyQualifiedName~LuxtronikModelTests"`
Expected: PASS, 11 tests.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Luxtronik src/HomeBlaze/Namotion.Devices.Luxtronik.Tests
git commit -m "feat: model the Luxtronik SHI holding registers as read-only controls"
```

---

## Task 7: The heat pump device

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik/LuxtronikHeatPump.cs`
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik.Tests/Testing/TestHost.cs`
- Test: `src/HomeBlaze/Namotion.Devices.Luxtronik.Tests/LuxtronikHeatPumpDeviceTests.cs`

The device follows `src/HomeBlaze/HomeBlaze.OpcUa/OpcUaClient.cs` (source creation, attach, detach, dispose) and `src/HomeBlaze/Namotion.Devices.Ecowitt/EcowittGateway.cs` (configuration properties, status properties, `ApplyConfigurationAsync` via a semaphore).

- [ ] **Step 1: Add the test host**

The heat pump has a logger constructor, so the generator emits no context constructor for it. Tests attach it under a host subject, as the HomeBlaze graph does.

`Testing/TestHost.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.Luxtronik.Tests.Testing;

[InterceptorSubject]
public partial class TestHost
{
    public TestHost()
    {
        HeatPump = null;
    }

    public partial LuxtronikHeatPump? HeatPump { get; set; }

    public static (LuxtronikHeatPump HeatPump, IInterceptorSubjectContext Context) CreateAttachedHeatPump()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        var heatPump = new LuxtronikHeatPump(NullLogger<LuxtronikHeatPump>.Instance);
        _ = new TestHost(context) { HeatPump = heatPump };
        return (heatPump, context);
    }
}
```

- [ ] **Step 2: Write the failing device tests**

`LuxtronikHeatPumpDeviceTests.cs`:

```csharp
using System.Reactive.Concurrency;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Devices.Luxtronik.Tests.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.Luxtronik.Tests;

public class LuxtronikHeatPumpDeviceTests
{
    [Fact]
    public void WhenElectricalValuesAreSet_ThenPowerSensorReportsThem()
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();

        // Act
        heatPump.Energy.ElectricalPower = 1500m;
        heatPump.Energy.TotalElectricalEnergy = 12345600m;

        // Assert
        IPowerSensor powerSensor = heatPump;
        Assert.Equal(1500m, powerSensor.Power);
        Assert.Equal(12345600m, powerSensor.EnergyConsumed);
    }

    [Fact]
    public void WhenThermalValuesAreSet_ThenThermalPowerSensorReportsThem()
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();

        // Act
        heatPump.Energy.HeatingPower = 6500m;
        heatPump.Energy.TotalThermalEnergy = 45678900m;

        // Assert
        IThermalPowerSensor thermalPowerSensor = heatPump;
        Assert.Equal(6500m, thermalPowerSensor.ThermalPower);
        Assert.Equal(45678900m, thermalPowerSensor.ThermalEnergyProduced);
    }

    [Fact]
    public void WhenChildValueChanges_ThenDerivedPowerChangeIsPublished()
    {
        // Arrange
        var (heatPump, context) = TestHost.CreateAttachedHeatPump();
        var changedProperties = new List<string>();
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changedProperties.Add(change.Property.Name));

        // Act
        heatPump.Energy.ElectricalPower = 2000m;

        // Assert
        Assert.Contains(nameof(LuxtronikHeatPump.Power), changedProperties);
    }

    [Fact]
    public void WhenNameIsEmpty_ThenDefaultTitleIsUsed()
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();

        // Act
        heatPump.Name = "";

        // Assert
        Assert.Equal("Luxtronik Heat Pump", heatPump.Title);
    }

    [Fact]
    public void WhenConstructed_ThenDefaultsAreSet()
    {
        // Act
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();

        // Assert
        Assert.Equal(502, heatPump.Port);
        Assert.Equal(TimeSpan.FromSeconds(5), heatPump.PollingInterval);
        Assert.Equal(10000, heatPump.Heating.BaseAddress);
        Assert.Equal(10005, heatPump.HotWater.BaseAddress);
        Assert.Equal(2, heatPump.MixingCircuit2.Index);
    }
}
```

`GetPropertyChangeObservable()` without a scheduler observes on `Scheduler.Default`, which delivers asynchronously, so the assertion could run before the change arrives. `ImmediateScheduler.Instance` (from `System.Reactive.Concurrency`) delivers the change synchronously inside the write. `Subscribe(Action<T>)` comes from System.Reactive's `ObservableExtensions` in the `System` namespace (implicit usings cover it).

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Luxtronik.Tests --filter "FullyQualifiedName~LuxtronikHeatPumpDeviceTests"`
Expected: build error, `LuxtronikHeatPump` does not exist.

- [ ] **Step 4: Implement the device**

`LuxtronikHeatPump.cs`:

```csharp
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Networking;
using HomeBlaze.Abstractions.Sensors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// A Luxtronik 2.1 heat pump read through the Smart Home Interface (Modbus TCP, port 502). Read only.
/// </summary>
[Category("Devices")]
[Description("Luxtronik 2.1 heat pump (Alpha Innotec, Novelan) via the Smart Home Interface, read only")]
[InterceptorSubject]
public partial class LuxtronikHeatPump : BackgroundService,
    IModbusDiscovery,
    IPowerSensor,
    IThermalPowerSensor,
    IConnectionState,
    ISoftwareState,
    IConfigurable,
    IMonitoredService,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    private const int FirmwareAddress = 10400;
    private const int FeatureFlagsAddress = 10000;

    private readonly ILogger<LuxtronikHeatPump> _logger;
    private readonly SemaphoreSlim _configurationChanged = new(0, 1);

    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial string? HostAddress { get; set; }

    [Configuration]
    public partial int Port { get; set; }

    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    [State(IsDiscrete = true)]
    public partial bool IsConnected { get; internal set; }

    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    /// <summary>
    /// Gets the controller firmware, such as "3.92.3", read by the discovery on every connect.
    /// </summary>
    [State]
    public partial string? SoftwareVersion { get; internal set; }

    [State(Position = 10)]
    public partial LuxtronikOperatingStatus OperatingStatus { get; internal set; }

    [State(Position = 11)]
    public partial LuxtronikTemperatures Temperatures { get; internal set; }

    [State(Position = 12)]
    public partial LuxtronikEnergy Energy { get; internal set; }

    [State(Position = 13)]
    public partial LuxtronikRuntime Runtime { get; internal set; }

    [State(Position = 14)]
    public partial LuxtronikOutputs Outputs { get; internal set; }

    [State(Position = 15)]
    public partial LuxtronikSmartGrid SmartGrid { get; internal set; }

    [State(Position = 16)]
    public partial LuxtronikExtraHotWater ExtraHotWater { get; internal set; }

    [State(Position = 17)]
    public partial LuxtronikFeatures Features { get; internal set; }

    [State(Position = 20)]
    public partial LuxtronikControl Heating { get; internal set; }

    [State(Position = 21)]
    public partial LuxtronikControl HotWater { get; internal set; }

    [State(Position = 22)]
    public partial LuxtronikMixingCircuit MixingCircuit1 { get; internal set; }

    [State(Position = 23)]
    public partial LuxtronikMixingCircuit MixingCircuit2 { get; internal set; }

    [State(Position = 24)]
    public partial LuxtronikMixingCircuit MixingCircuit3 { get; internal set; }

    [State(Position = 25)]
    public partial LuxtronikPowerLimit PowerLimit { get; internal set; }

    [State(Position = 26)]
    public partial LuxtronikLocks Locks { get; internal set; }

    [State(Position = 27)]
    public partial LuxtronikRoomControl RoomControl { get; internal set; }

    [State(Position = 28)]
    public partial LuxtronikOverallHeating OverallHeating { get; internal set; }

    [State(Position = 29)]
    public partial LuxtronikHotWaterRequests HotWaterRequests { get; internal set; }

    [Derived]
    public decimal? Power => Energy.ElectricalPower;

    [Derived]
    public decimal? EnergyConsumed => Energy.TotalElectricalEnergy;

    [Derived]
    public decimal? ThermalPower => Energy.HeatingPower;

    [Derived]
    public decimal? ThermalEnergyProduced => Energy.TotalThermalEnergy;

    [Derived]
    [State]
    public string? AvailableSoftwareUpdate => null;

    [Derived]
    public string? Title => string.IsNullOrEmpty(Name) ? "Luxtronik Heat Pump" : Name;

    public string? IconName => "HeatPump";

    // A zero interval is rejected by the source configuration; the wait loops still need a positive delay.
    private TimeSpan EffectivePollingInterval => PollingInterval > TimeSpan.Zero ? PollingInterval : TimeSpan.FromSeconds(5);

    [Derived]
    public string? IconColor => IsConnected ? "Success" : null;

    public LuxtronikHeatPump(ILogger<LuxtronikHeatPump> logger)
    {
        _logger = logger;

        Name = string.Empty;
        HostAddress = null;
        Port = 502;
        PollingInterval = TimeSpan.FromSeconds(5);

        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        LastUpdated = null;
        SoftwareVersion = null;

        OperatingStatus = new LuxtronikOperatingStatus();
        Temperatures = new LuxtronikTemperatures();
        Energy = new LuxtronikEnergy();
        Runtime = new LuxtronikRuntime();
        Outputs = new LuxtronikOutputs();
        SmartGrid = new LuxtronikSmartGrid();
        ExtraHotWater = new LuxtronikExtraHotWater();
        Features = new LuxtronikFeatures();
        Heating = new LuxtronikControl(10000, LuxtronikFeature.Heating);
        HotWater = new LuxtronikControl(10005, LuxtronikFeature.HotWater);
        MixingCircuit1 = new LuxtronikMixingCircuit(1);
        MixingCircuit2 = new LuxtronikMixingCircuit(2);
        MixingCircuit3 = new LuxtronikMixingCircuit(3);
        PowerLimit = new LuxtronikPowerLimit();
        Locks = new LuxtronikLocks();
        RoomControl = new LuxtronikRoomControl();
        OverallHeating = new LuxtronikOverallHeating();
        HotWaterRequests = new LuxtronikHotWaterRequests();
    }

    /// <summary>
    /// Reads the firmware version and configured functions, and excludes the registers the controller does not provide.
    /// </summary>
    public async Task DiscoverAsync(ModbusDiscoveryContext context, CancellationToken cancellationToken)
    {
        var versionRegisters = await context.ReadInputRegistersAsync(FirmwareAddress, 3, cancellationToken: cancellationToken).ConfigureAwait(false);
        var firmwareVersion = new Version(versionRegisters[0], versionRegisters[1], versionRegisters[2]);
        new PropertyReference(this, nameof(SoftwareVersion))
            .SetValueFromSource(context.Source, null, null, firmwareVersion.ToString());

        IReadOnlySet<LuxtronikFeature>? configuredFeatures = null;
        try
        {
            var flags = await context.ReadDiscreteInputsAsync(FeatureFlagsAddress, LuxtronikGating.FeatureFlagCount, cancellationToken: cancellationToken).ConfigureAwait(false);
            configuredFeatures = LuxtronikGating.GetConfiguredFeatures(flags);
        }
        catch (ModbusResponseException exception)
        {
            _logger.LogInformation(
                "Luxtronik {HostAddress} does not report its configured functions (exception code {ExceptionCode}); values of unconfigured functions are shown as unavailable instead.",
                HostAddress, exception.ExceptionCode);
        }

        var registeredSubject = this.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("The heat pump is not registered. Attach it to a subject graph with a registry.");

        foreach (var property in registeredSubject.GetAllProperties())
        {
            var isUnreadableFeatureFlag = configuredFeatures is null && ReferenceEquals(property.Subject, Features);
            if (isUnreadableFeatureFlag || !LuxtronikGating.IsSupported(property, firmwareVersion, configuredFeatures))
            {
                context.ExcludeProperty(property.Reference);
            }
        }

        _logger.LogInformation("Luxtronik {HostAddress} runs firmware {FirmwareVersion}.", HostAddress, firmwareVersion);
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            _configurationChanged.Release();
        }
        catch (SemaphoreFullException)
        {
        }

        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(HostAddress))
            {
                Status = ServiceStatus.Stopped;
                StatusMessage = "No host address configured";
                await WaitForConfigurationChangeAsync(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
                continue;
            }

            await RunSourceAsync(HostAddress, stoppingToken).ConfigureAwait(false);
        }

        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    internal void UpdateStatus(ModbusClientDiagnostics diagnostics)
    {
        IsConnected = diagnostics.IsOperational == true;
        LastUpdated = diagnostics.Polling.LastPollTime;

        if (IsConnected)
        {
            var errorCode = OperatingStatus.ErrorCode;
            Status = ServiceStatus.Running;
            StatusMessage = errorCode is > 0 ? $"Heat pump error {errorCode}" : null;
        }
        else
        {
            Status = diagnostics.LastError is null ? ServiceStatus.Starting : ServiceStatus.Error;
            StatusMessage = diagnostics.LastError?.Message ?? "Connecting...";
        }
    }

    private async Task RunSourceAsync(string hostAddress, CancellationToken stoppingToken)
    {
        ModbusSubjectClientSource? source = null;
        try
        {
            Status = ServiceStatus.Starting;
            StatusMessage = "Connecting...";

            source = this.CreateModbusClientSource(
                new ModbusClientConfiguration { Host = hostAddress, Port = Port, PollingInterval = PollingInterval },
                _logger);
            await this.AttachHostedServiceAsync(source, stoppingToken).ConfigureAwait(false);

            // Source diagnostics are not tracked properties, so they are mirrored into this device's state.
            while (!stoppingToken.IsCancellationRequested)
            {
                UpdateStatus(source.Diagnostics);
                if (await WaitForConfigurationChangeAsync(EffectivePollingInterval, stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Luxtronik heat pump {HostAddress} failed.", hostAddress);
            Status = ServiceStatus.Error;
            StatusMessage = exception.Message;
            await WaitForConfigurationChangeAsync(EffectivePollingInterval, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            if (source is not null)
            {
                try
                {
                    await this.DetachHostedServiceAsync(source, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Failed to detach the Modbus source of {HostAddress}.", hostAddress);
                }

                await source.DisposeAsync().ConfigureAwait(false);
            }

            IsConnected = false;
        }
    }

    private async Task<bool> WaitForConfigurationChangeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _configurationChanged.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public override void Dispose()
    {
        _configurationChanged.Dispose();
        base.Dispose();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Luxtronik.Tests --filter "FullyQualifiedName~LuxtronikHeatPumpDeviceTests"`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Luxtronik src/HomeBlaze/Namotion.Devices.Luxtronik.Tests
git commit -m "feat: add the Luxtronik heat pump device"
```

---
## Task 8: Luxtronik test server and end-to-end tests

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik.Tests/Testing/LuxtronikTestServer.cs`
- Test: `src/HomeBlaze/Namotion.Devices.Luxtronik.Tests/LuxtronikHeatPumpTests.cs`
- Test: `src/HomeBlaze/Namotion.Devices.Luxtronik.Tests/LuxtronikHeatPumpLifecycleTests.cs`

Both test classes join the `LuxtronikIntegrationCollection` from Task 3, which disables parallel execution.

The test server imitates the controller: a request that touches any unmapped address, or a 3.92 address on older firmware, is rejected with "illegal data address", as python-luxtronik documents for the real device. The tests therefore also prove that the model never plans a read across unmapped registers.

- [ ] **Step 1: Write the test server**

`Testing/LuxtronikTestServer.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentModbus;

namespace Namotion.Devices.Luxtronik.Tests.Testing;

/// <summary>
/// In-process Modbus server imitating a Luxtronik 2.1 Smart Home Interface on unit 1.
/// </summary>
internal sealed class LuxtronikTestServer : IDisposable
{
    private const byte UnitId = 1;

    // Mapped registers per the AIT manual and python-luxtronik; Requires392 marks registers added in firmware 3.92.
    internal static readonly (int Start, int End, bool Requires392)[] InputRanges =
    [
        (10000, 10000, false), (10002, 10004, false), (10006, 10007, false),
        (10100, 10108, false), (10109, 10113, true), (10120, 10124, false),
        (10140, 10143, false), (10150, 10153, false), (10160, 10163, false),
        (10201, 10207, false), (10300, 10302, false), (10310, 10319, false), (10320, 10329, true),
        (10350, 10356, true), (10360, 10361, true), (10400, 10402, false),
        (10404, 10413, true), (10416, 10417, true), (10500, 10502, true)
    ];

    internal static readonly (int Start, int End, bool Requires392)[] HoldingRanges =
    [
        (10000, 10002, false), (10003, 10003, true), (10005, 10007, false), (10008, 10008, true),
        (10010, 10012, false), (10013, 10013, true), (10015, 10017, false),
        (10020, 10022, false), (10023, 10023, true), (10025, 10027, false),
        (10030, 10032, false), (10033, 10033, true), (10035, 10037, false),
        (10040, 10041, false), (10050, 10051, true), (10052, 10053, false),
        (10060, 10060, true), (10065, 10067, true), (10070, 10071, true)
    ];

    private readonly Version _firmware;
    private readonly bool _supportsDiscreteInputs;
    private ModbusTcpServer? _server;

    public LuxtronikTestServer(Version firmware, bool supportsDiscreteInputs = true)
    {
        _firmware = firmware;
        _supportsDiscreteInputs = supportsDiscreteInputs;
        Port = GetFreeTcpPort();
    }

    public int Port { get; }

    public void Start()
    {
        var server = new ModbusTcpServer(true);
        server.AddUnit(UnitId);
        server.RequestValidator = ValidateRequest;
        server.Start(new IPEndPoint(IPAddress.Loopback, Port));
        _server = server;

        SetInput<ushort>(10400, (ushort)_firmware.Major);
        SetInput<ushort>(10401, (ushort)_firmware.Minor);
        SetInput<ushort>(10402, (ushort)Math.Max(_firmware.Build, 0));
        SetFeatures(Enum.GetValues<LuxtronikFeature>().Where(feature => feature != LuxtronikFeature.None).ToArray());
    }

    public void SeedTypicalValues()
    {
        SetInput<ushort>(10000, 1);        // compressor 1 running
        SetInput<ushort>(10002, 0);        // operation mode: heating
        SetInput<ushort>(10003, 3);        // heating: running
        SetInput<short>(10105, 352);       // flow 35.2 degrees
        SetInput<short>(10108, -45);       // outside -4.5 degrees
        SetInput<short>(10110, 81);        // heat source inlet 8.1 degrees
        SetInput<short>(10120, 482);       // hot water 48.2 degrees
        SetInput<short>(10300, 65);        // heating power 6.5 kW
        SetInput<ushort>(10301, 15);       // electrical power 1.5 kW
        SetInput<int>(10310, 123456);      // electrical energy 12345.6 kWh
        SetInput<int>(10320, 456789);      // thermal energy 45678.9 kWh
        SetInput<uint>(10404, 12345);      // heat pump runtime hours
        SetHolding<ushort>(10001, 350);    // heating setpoint 35.0 degrees
        SetHolding<ushort>(10011, 280);    // mixing circuit 1 heating setpoint 28.0 degrees
        SetHolding<ushort>(10041, 300);    // power limit 30.0 kW
    }

    public void SetInput<T>(int address, T value) where T : unmanaged
    {
        var server = GetServer();
        lock (server.Lock)
        {
            server.GetInputRegisters(UnitId).SetBigEndian(address, value);
        }
    }

    public void SetHolding<T>(int address, T value) where T : unmanaged
    {
        var server = GetServer();
        lock (server.Lock)
        {
            server.GetHoldingRegisters(UnitId).SetBigEndian(address, value);
        }
    }

    public void SetFeatures(params LuxtronikFeature[] configuredFeatures)
    {
        var server = GetServer();
        lock (server.Lock)
        {
            var discreteInputs = server.GetDiscreteInputs(UnitId);
            for (var index = 0; index < 12; index++)
            {
                discreteInputs.Set(10000 + index, configuredFeatures.Contains((LuxtronikFeature)index));
            }
        }
    }

    /// <summary>
    /// Loads a raw register dump in the format written by the hardware test.
    /// </summary>
    public void LoadDump(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        foreach (var register in root.GetProperty("inputRegisters").EnumerateObject())
        {
            SetInput(int.Parse(register.Name), register.Value.GetUInt16());
        }

        foreach (var register in root.GetProperty("holdingRegisters").EnumerateObject())
        {
            SetHolding(int.Parse(register.Name), register.Value.GetUInt16());
        }

        if (root.TryGetProperty("discreteInputs", out var discreteInputs))
        {
            var server = GetServer();
            lock (server.Lock)
            {
                foreach (var input in discreteInputs.EnumerateObject())
                {
                    server.GetDiscreteInputs(UnitId).Set(int.Parse(input.Name), input.Value.GetBoolean());
                }
            }
        }
    }

    public void Dispose()
    {
        var server = _server;
        _server = null;
        if (server is not null)
        {
            server.Stop();
            server.Dispose();
        }
    }

    private ModbusTcpServer GetServer() => _server ?? throw new InvalidOperationException("The test server is not started.");

    private ModbusExceptionCode ValidateRequest(byte unitId, ModbusFunctionCode functionCode, ushort address, ushort quantity)
    {
        var isMapped = functionCode switch
        {
            ModbusFunctionCode.ReadInputRegisters => IsMapped(InputRanges, address, quantity),
            ModbusFunctionCode.ReadHoldingRegisters => IsMapped(HoldingRanges, address, quantity),
            ModbusFunctionCode.ReadDiscreteInputs => _supportsDiscreteInputs && address >= 10000 && address + quantity <= 10012,
            _ => false
        };

        return isMapped ? ModbusExceptionCode.OK : ModbusExceptionCode.IllegalDataAddress;
    }

    private bool IsMapped((int Start, int End, bool Requires392)[] ranges, int address, int quantity)
    {
        var supports392 = _firmware >= new Version(3, 92, 0);
        for (var current = address; current < address + quantity; current++)
        {
            var isKnown = false;
            foreach (var range in ranges)
            {
                if (current >= range.Start && current <= range.End && (supports392 || !range.Requires392))
                {
                    isKnown = true;
                    break;
                }
            }

            if (!isKnown)
            {
                return false;
            }
        }

        return true;
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

- [ ] **Step 2: Write the end-to-end tests**

`LuxtronikHeatPumpTests.cs`:

```csharp
using HomeBlaze.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Luxtronik.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Testing;

namespace Namotion.Devices.Luxtronik.Tests;

[Trait("Category", "Integration")]
[Collection(LuxtronikIntegrationCollection.Name)]
public class LuxtronikHeatPumpTests
{
    private static async Task<(LuxtronikHeatPump HeatPump, ModbusSubjectClientSource Source, SourceStateRecorder Recorder)> StartAsync(
        LuxtronikTestServer server)
    {
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();
        var source = heatPump.CreateModbusClientSource(
            new ModbusClientConfiguration
            {
                Host = "127.0.0.1",
                Port = server.Port,
                PollingInterval = TimeSpan.FromMilliseconds(100),
                RetryTime = TimeSpan.FromMilliseconds(200)
            },
            NullLogger.Instance);

        var recorder = SourceStateRecorder.SubscribeTo(source);
        await source.StartAsync(CancellationToken.None);
        await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The heat pump should synchronize.", SourceState.Synchronized);
        return (heatPump, source, recorder);
    }

    private static bool IsClaimed(IInterceptorSubject subject, string propertyName)
        => new PropertyReference(subject, propertyName).TryGetSource(out _);

    [Fact]
    public async Task WhenControllerRunsFirmware392_ThenTheModelIsRead()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();

        // Act
        var (heatPump, source, recorder) = await StartAsync(server);
        try
        {
            heatPump.UpdateStatus(source.Diagnostics);

            // Assert
            Assert.Equal("3.92.3", heatPump.SoftwareVersion);
            Assert.True(heatPump.OperatingStatus.IsCompressorRunning);
            Assert.Equal(LuxtronikOperationMode.Heating, heatPump.OperatingStatus.OperationMode);
            Assert.Equal(LuxtronikModeStatus.Running, heatPump.OperatingStatus.HeatingStatus);
            Assert.Equal(35.2m, heatPump.Temperatures.Flow.Temperature);
            Assert.Equal(-4.5m, heatPump.Temperatures.Outside.Temperature);
            Assert.Equal(8.1m, heatPump.Temperatures.HeatSourceInlet.Temperature);
            Assert.Equal(48.2m, heatPump.Temperatures.HotWater.Temperature);
            Assert.Equal(6500m, heatPump.Energy.HeatingPower);
            Assert.Equal(1500m, heatPump.Power);
            Assert.Equal(12345600m, heatPump.EnergyConsumed);
            Assert.Equal(45678900m, heatPump.ThermalEnergyProduced);
            Assert.Equal(12345m, heatPump.Runtime.HeatPump);
            Assert.Equal(35.0m, heatPump.Heating.Setpoint);
            Assert.Equal(28.0m, heatPump.MixingCircuit1.Heating.Setpoint);
            Assert.Equal(30000m, heatPump.PowerLimit.Limit);
            Assert.True(heatPump.Features.Heating);
            Assert.Equal(0, source.Diagnostics.Polling.FailedBatches);
            Assert.Equal(0, source.Diagnostics.Polling.UnavailableProperties);
            Assert.True(heatPump.IsConnected);
            Assert.Equal(ServiceStatus.Running, heatPump.Status);
            Assert.Null(heatPump.StatusMessage);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenControllerRunsFirmware390_ThenNewerRegistersAreExcludedWithoutFailures()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 90, 1));
        server.Start();
        server.SeedTypicalValues();

        // Act
        var (heatPump, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            Assert.Equal("3.90.1", heatPump.SoftwareVersion);
            Assert.Equal(35.2m, heatPump.Temperatures.Flow.Temperature);
            Assert.Null(heatPump.Temperatures.HeatSourceInlet.Temperature);
            Assert.False(IsClaimed(heatPump.Temperatures.HeatSourceInlet, nameof(LuxtronikTemperatureSensor.Temperature)));
            Assert.Null(heatPump.Energy.TotalThermalEnergy);
            Assert.False(IsClaimed(heatPump.Runtime, nameof(LuxtronikRuntime.HeatPump)));
            Assert.False(IsClaimed(heatPump.Heating, nameof(LuxtronikControl.Level)));
            Assert.Equal(0, source.Diagnostics.Polling.FailedBatches);
            Assert.Equal(0, source.Diagnostics.Polling.UnavailableProperties);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenFunctionsAreNotConfigured_ThenTheirRegistersAreExcluded()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();
        server.SetFeatures(LuxtronikFeature.Heating, LuxtronikFeature.HotWater, LuxtronikFeature.MixingCircuit1Heating);

        // Act
        var (heatPump, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            Assert.False(IsClaimed(heatPump.OperatingStatus, nameof(LuxtronikOperatingStatus.PoolHeatingStatus)));
            Assert.False(IsClaimed(heatPump.OperatingStatus, nameof(LuxtronikOperatingStatus.CoolingStatus)));
            Assert.False(IsClaimed(heatPump.MixingCircuit2.Heating, nameof(LuxtronikControl.Mode)));
            Assert.True(IsClaimed(heatPump.MixingCircuit1.Heating, nameof(LuxtronikControl.Mode)));
            Assert.Equal(28.0m, heatPump.MixingCircuit1.Heating.Setpoint);
            Assert.Equal(0, source.Diagnostics.Polling.FailedBatches);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenDiscreteInputsAreRejected_ThenNotAvailableValuesMapToNull()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3), supportsDiscreteInputs: false);
        server.Start();
        server.SeedTypicalValues();
        server.SetInput<ushort>(10007, 0x7FFF);
        server.SetInput<int>(10318, 0x7FFFFFFF);

        // Act
        var (heatPump, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            Assert.True(IsClaimed(heatPump.OperatingStatus, nameof(LuxtronikOperatingStatus.PoolHeatingStatus)));
            Assert.Null(heatPump.OperatingStatus.PoolHeatingStatus);
            Assert.Null(heatPump.Energy.PoolElectricalEnergy);
            Assert.False(IsClaimed(heatPump.Features, nameof(LuxtronikFeatures.Heating)));
            Assert.Equal(0, source.Diagnostics.Polling.FailedBatches);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenElectricalPowerRegisterChanges_ThenDevicePowerFollows()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();
        var (heatPump, source, recorder) = await StartAsync(server);
        try
        {
            // Act
            server.SetInput<ushort>(10301, 20);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => heatPump.Power == 2000m, TimeSpan.FromSeconds(10), message: "Power should follow the register.");
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenDumpIsLoaded_ThenTheModelReadsIt()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.LoadDump("""
            {
              "inputRegisters": { "10105": 400, "10400": 3, "10401": 92, "10402": 3 },
              "holdingRegisters": { "10001": 330 },
              "discreteInputs": { "10000": true }
            }
            """);

        // Act
        var (heatPump, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            Assert.Equal(40.0m, heatPump.Temperatures.Flow.Temperature);
            Assert.Equal(33.0m, heatPump.Heating.Setpoint);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }
}
```

`TryGetSource` is `Namotion.Interceptor.Connectors.SourcePropertyExtensions`; `CreateModbusClientSource` is in `Microsoft.Extensions.DependencyInjection`.

- [ ] **Step 3: Write the device lifecycle test**

The tests above drive the source directly. This one runs the device as HomeBlaze does: the context's hosted service handler starts the attached heat pump, `ExecuteAsync` creates and attaches the source, and `UpdateStatus` mirrors its diagnostics. Modelled on `src/HomeBlaze/HomeBlaze.Services.Tests/Serialization/ConfigurableSubjectStartupTests.cs`.

`LuxtronikHeatPumpLifecycleTests.cs`:

```csharp
using HomeBlaze.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Luxtronik.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.Luxtronik.Tests;

[Trait("Category", "Integration")]
[Collection(LuxtronikIntegrationCollection.Name)]
public class LuxtronikHeatPumpLifecycleTests
{
    [Fact]
    public async Task WhenControllerStops_ThenHostedHeatPumpReportsError()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();

        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithHostedServices(services);
        await using var provider = services.BuildServiceProvider();
        var handler = Assert.Single(provider.GetServices<IHostedService>());
        await handler.StartAsync(CancellationToken.None);

        var heatPump = new LuxtronikHeatPump(NullLogger<LuxtronikHeatPump>.Instance)
        {
            HostAddress = "127.0.0.1",
            Port = server.Port,
            PollingInterval = TimeSpan.FromMilliseconds(200)
        };

        try
        {
            _ = new TestHost(context) { HeatPump = heatPump };
            await AsyncTestHelpers.WaitUntilAsync(
                () => heatPump.IsConnected && heatPump.Status == ServiceStatus.Running && heatPump.LastUpdated is not null,
                TimeSpan.FromSeconds(30),
                message: "The hosted heat pump should connect and poll.");

            // Act
            server.Dispose();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => !heatPump.IsConnected && heatPump.Status == ServiceStatus.Error,
                TimeSpan.FromSeconds(30),
                message: "The heat pump should report the lost controller.");
        }
        finally
        {
            await handler.StopAsync(CancellationToken.None);
        }
    }
}
```

`WithHostedServices` registers the handler as the collection's only `IHostedService` and adds `WithLifecycle`; the handler needs an `ILogger<HostedServiceHandler>`, hence the open generic `NullLogger<>` registration. `LuxtronikTestServer.Dispose` is idempotent, so the explicit call and the `using` do not conflict.

- [ ] **Step 4: Run the tests**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Luxtronik.Tests --filter "FullyQualifiedName~LuxtronikHeatPumpTests|FullyQualifiedName~LuxtronikHeatPumpLifecycleTests"`
Expected: PASS, 7 tests. A non-zero `FailedBatches` means the model plans a read across an unmapped register: find the property whose address is missing from the ranges, and fix either the model address or the gate.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Luxtronik.Tests
git commit -m "test: verify the Luxtronik model against a simulated controller"
```

---

## Task 9: Hardware register dump

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik.Tests/Testing/LuxtronikHardwareFactAttribute.cs`
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik.Tests/LuxtronikHardwareTests.cs`

The test reads the real controller and writes the raw registers to JSON. It uses only read function codes (1 to 4) through FluentModbus directly, independent of the model, so a mapping bug cannot hide a register. It is skipped (not passed) unless `LUXTRONIK_HOST` is set, and it carries the Integration trait so default unit runs never touch it.

- [ ] **Step 1: Add the skip attribute**

`Testing/LuxtronikHardwareFactAttribute.cs`:

```csharp
namespace Namotion.Devices.Luxtronik.Tests.Testing;

/// <summary>
/// A fact that runs only when LUXTRONIK_HOST names a real Luxtronik 2.1 controller with the Smart Home Interface enabled.
/// </summary>
public sealed class LuxtronikHardwareFactAttribute : FactAttribute
{
    public LuxtronikHardwareFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LUXTRONIK_HOST")))
        {
            Skip = "Set LUXTRONIK_HOST to the controller's IP address to run hardware tests.";
        }
    }
}
```

- [ ] **Step 2: Add the dump test**

`LuxtronikHardwareTests.cs`:

```csharp
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using FluentModbus;
using Namotion.Devices.Luxtronik.Tests.Testing;
using Xunit.Abstractions;

namespace Namotion.Devices.Luxtronik.Tests;

[Trait("Category", "Integration")]
public class LuxtronikHardwareTests
{
    private const int Port = 502;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;
    private TcpClient? _tcpClient;
    private ModbusTcpClient? _client;

    public LuxtronikHardwareTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [LuxtronikHardwareFact]
    public async Task WhenReadingTheController_ThenRawRegistersAreDumped()
    {
        // Arrange
        var host = Environment.GetEnvironmentVariable("LUXTRONIK_HOST")!;
        var dumpPath = Environment.GetEnvironmentVariable("LUXTRONIK_DUMP_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "luxtronik-dump.json");
        var inputRegisters = new SortedDictionary<string, ushort>(StringComparer.Ordinal);
        var holdingRegisters = new SortedDictionary<string, ushort>(StringComparer.Ordinal);
        var discreteInputs = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        var unmappedReadBehavior = "not read";

        // Act (reads only: this test must never call a FluentModbus write method)
        try
        {
            await ConnectAsync(host);

            foreach (var (start, end, _) in LuxtronikTestServer.InputRanges)
            {
                var data = await TryReadAsync(host, $"input {start}", failures,
                    client => client.ReadInputRegistersAsync((byte)1, (ushort)start, (ushort)(end - start + 1), CancellationToken.None));
                AddRegisters(data, start, inputRegisters);
            }

            foreach (var (start, end, _) in LuxtronikTestServer.HoldingRanges)
            {
                var data = await TryReadAsync(host, $"holding {start}", failures,
                    client => client.ReadHoldingRegistersAsync((byte)1, (ushort)start, (ushort)(end - start + 1), CancellationToken.None));
                AddRegisters(data, start, holdingRegisters);
            }

            var bits = await TryReadAsync(host, "discrete 10000", failures,
                client => client.ReadDiscreteInputsAsync(1, 10000, 12, CancellationToken.None));
            if (bits is not null)
            {
                for (var index = 0; index < 12; index++)
                {
                    discreteInputs[(10000 + index).ToString()] = ((bits[index / 8] >> (index % 8)) & 1) != 0;
                }
            }

            // Input 10001 is not mapped; how the controller answers decides how the connector's split-on-failure behaves.
            try
            {
                var value = (await _client!.ReadInputRegistersAsync((byte)1, (ushort)10001, (ushort)1, CancellationToken.None).WaitAsync(RequestTimeout)).ToArray();
                unmappedReadBehavior = $"answered with value {BinaryPrimitives.ReadUInt16BigEndian(value)}";
            }
            catch (ModbusException exception)
            {
                unmappedReadBehavior = $"exception response {exception.ExceptionCode}";
            }
            catch (TimeoutException)
            {
                unmappedReadBehavior = "no response";
            }
            catch (IOException exception)
            {
                unmappedReadBehavior = $"connection error: {exception.Message}";
            }
        }
        finally
        {
            Disconnect();

            var dump = new
            {
                capturedAt = DateTimeOffset.UtcNow,
                unmappedReadBehavior,
                failures,
                inputRegisters,
                holdingRegisters,
                discreteInputs
            };
            await File.WriteAllTextAsync(dumpPath, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));
            _output.WriteLine($"Dump written to {dumpPath}");
        }

        // Assert
        Assert.True(inputRegisters.ContainsKey("10400"), "The firmware registers must be readable.");
        _output.WriteLine($"Firmware {inputRegisters["10400"]}.{inputRegisters["10401"]}.{inputRegisters["10402"]}");
        _output.WriteLine($"Unmapped read: {unmappedReadBehavior}");
        _output.WriteLine($"Failures: {(failures.Count == 0 ? "none" : string.Join(", ", failures))}");
    }

    private async Task<byte[]?> TryReadAsync(
        string host, string description, List<string> failures, Func<ModbusTcpClient, Task<Memory<byte>>> read)
    {
        try
        {
            return (await read(_client!).WaitAsync(RequestTimeout)).ToArray();
        }
        catch (ModbusException exception)
        {
            failures.Add($"{description}: {exception.ExceptionCode}");
        }
        catch (TimeoutException)
        {
            failures.Add($"{description}: timeout");
            await ConnectAsync(host);
        }
        catch (IOException exception)
        {
            failures.Add($"{description}: error: {exception.Message}");
            await ConnectAsync(host);
        }

        return null;
    }

    // A timed-out request may still be answered later, and that late response would be taken as the
    // answer to the next request, so every timeout or connection error continues on a fresh connection.
    private async Task ConnectAsync(string host)
    {
        Disconnect();

        var tcpClient = new TcpClient();
        try
        {
            await tcpClient.ConnectAsync(host, Port).WaitAsync(RequestTimeout);
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }

        var client = new ModbusTcpClient();
        client.Initialize(tcpClient, ModbusEndianness.BigEndian);
        _tcpClient = tcpClient;
        _client = client;
    }

    // A ModbusTcpClient initialized with an external TcpClient does not dispose it, so both are disposed.
    private void Disconnect()
    {
        _client?.Dispose();
        _tcpClient?.Dispose();
        _client = null;
        _tcpClient = null;
    }

    private static void AddRegisters(byte[]? data, int start, SortedDictionary<string, ushort> target)
    {
        if (data is null)
        {
            return;
        }

        for (var index = 0; index < data.Length / 2; index++)
        {
            target[(start + index).ToString()] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index * 2));
        }
    }
}
```

The anonymous object serializes with camel-case names already (`inputRegisters`, `holdingRegisters`, `discreteInputs`), which is the shape `LuxtronikTestServer.LoadDump` reads. Each block read tolerates a Modbus exception response, a timeout and a connection error (`IOException`), records it in `failures`, and the dump is written in the `finally` block so a partial run still leaves its evidence on disk.

- [ ] **Step 3: Verify the test is skipped without hardware**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Luxtronik.Tests --filter "FullyQualifiedName~LuxtronikHardwareTests"`
Expected: 1 skipped, 0 failed.

- [ ] **Step 4: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Luxtronik.Tests
git commit -m "test: add a read-only Luxtronik hardware register dump"
```

The hardware run itself is a manual step for the user (Task 13, Step 5).

---

## Task 10: HomeBlaze UI

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik.HomeBlaze/Namotion.Devices.Luxtronik.HomeBlaze.csproj`
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik.HomeBlaze/_Imports.razor`
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik.HomeBlaze/LuxtronikHeatPumpWidget.razor`
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik.HomeBlaze/LuxtronikHeatPumpEditComponent.razor`
- Modify: `src/Namotion.Interceptor.slnx`

Modelled on `Namotion.Devices.Ecowitt.HomeBlaze` (edit and widget, no setup component).

- [ ] **Step 1: Project and imports**

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>preview</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="MudBlazor" Version="9.2.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Namotion.Devices.Luxtronik\Namotion.Devices.Luxtronik.csproj" />
    <ProjectReference Include="..\HomeBlaze.Components.Abstractions\HomeBlaze.Components.Abstractions.csproj" />
  </ItemGroup>

</Project>
```

`_Imports.razor`:

```razor
@using Microsoft.AspNetCore.Components
@using MudBlazor
@using Namotion.Interceptor
@using Namotion.Devices.Luxtronik
@using global::HomeBlaze.Components.Abstractions
@using global::HomeBlaze.Components.Abstractions.Attributes
```

- [ ] **Step 2: Widget**

`LuxtronikHeatPumpWidget.razor`:

```razor
@attribute [SubjectComponent(SubjectComponentType.Widget, typeof(LuxtronikHeatPump))]
@implements ISubjectComponent

<MudPaper Class="pa-4">
    <MudStack Spacing="2">
        <MudStack Row="true" AlignItems="AlignItems.Center">
            <MudIcon Icon="@Icons.Material.Filled.HeatPump" Class="mr-2" />
            <MudText Typo="Typo.h6">@(HeatPump?.Title ?? "Luxtronik Heat Pump")</MudText>
            <MudSpacer />
            <MudChip T="string"
                     Size="Size.Small"
                     Color="@(HeatPump?.IsConnected == true ? Color.Success : Color.Error)"
                     Variant="Variant.Filled">
                @(HeatPump?.IsConnected == true ? "Connected" : "Disconnected")
            </MudChip>
        </MudStack>

        <MudDivider />

        @if (HeatPump is not null)
        {
            <MudStack Row="true" Spacing="4" Wrap="Wrap.Wrap">
                @Value("Mode", HeatPump.OperatingStatus.OperationMode?.ToString())
                @Value("Outside", Format(HeatPump.Temperatures.Outside.Temperature, "°C"))
                @Value("Flow", Format(HeatPump.Temperatures.Flow.Temperature, "°C"))
                @Value("Hot water", Format(HeatPump.Temperatures.HotWater.Temperature, "°C"))
                @Value("Electrical", Format(HeatPump.Energy.ElectricalPower, "W"))
                @Value("Heating", Format(HeatPump.Energy.HeatingPower, "W"))
            </MudStack>

            @if (HeatPump.OperatingStatus.ErrorCode is > 0)
            {
                <MudAlert Severity="Severity.Error" Dense="true">Heat pump error @HeatPump.OperatingStatus.ErrorCode</MudAlert>
            }
        }
    </MudStack>
</MudPaper>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    private LuxtronikHeatPump? HeatPump => Subject as LuxtronikHeatPump;

    private static string? Format(decimal? value, string unit) => value is null ? null : $"{value:0.#} {unit}";

    private static RenderFragment Value(string caption, string? value) =>
        @<MudStack>
            <MudText Typo="Typo.caption">@caption</MudText>
            <MudText Typo="Typo.h5">@(value ?? "-")</MudText>
        </MudStack>;
}
```

If `Icons.Material.Filled.HeatPump` does not exist in the referenced MudBlazor version (build error CS0117), use `Icons.Material.Filled.Thermostat` here and `"Thermostat"` for `LuxtronikHeatPump.IconName`.

- [ ] **Step 3: Edit component**

`LuxtronikHeatPumpEditComponent.razor`:

```razor
@attribute [SubjectComponent(SubjectComponentType.Edit, typeof(LuxtronikHeatPump))]
@implements ISubjectEditComponent

<MudForm>
    <MudTextField @bind-Value="_name"
                  Label="Name"
                  Immediate="true"
                  OnKeyUp="OnFieldChanged" />

    <MudTextField @bind-Value="_hostAddress"
                  Label="Host Address (IP or hostname)"
                  Required="true"
                  RequiredError="Host address is required"
                  Immediate="true"
                  OnKeyUp="OnFieldChanged"
                  Class="mt-4" />

    <MudNumericField Value="_port"
                     ValueChanged="@((int value) => { _port = value; OnFieldChanged(); })"
                     Label="Port"
                     Min="1"
                     Max="65535"
                     Class="mt-4" />

    <MudNumericField Value="_pollingIntervalSeconds"
                     ValueChanged="@((int value) => { _pollingIntervalSeconds = value; OnFieldChanged(); })"
                     Label="Polling Interval (seconds)"
                     Min="1"
                     Max="300"
                     Class="mt-4" />

    <MudAlert Severity="Severity.Info" Dense="true" Class="mt-4">
        Read only. Enable the Smart Home Interface (ModBus TCP) on the controller and keep port 502 inside your network.
    </MudAlert>

    @if (!IsCreating && HeatPump is not null)
    {
        <MudDivider Class="my-4" />
        <MudText Typo="Typo.subtitle2" Class="mb-2">Current State</MudText>
        <MudText>Status: @HeatPump.Status @HeatPump.StatusMessage</MudText>
        @if (HeatPump.SoftwareVersion is not null)
        {
            <MudText>Firmware: @HeatPump.SoftwareVersion</MudText>
        }
    }
</MudForm>

@code {
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    private LuxtronikHeatPump? HeatPump => Subject as LuxtronikHeatPump;

    private string _name = string.Empty;
    private string? _hostAddress;
    private int _port = 502;
    private int _pollingIntervalSeconds = 5;

    private string _originalName = string.Empty;
    private string? _originalHostAddress;
    private int _originalPort = 502;
    private int _originalPollingIntervalSeconds = 5;

    public bool IsValid => !string.IsNullOrWhiteSpace(_hostAddress) && _port is >= 1 and <= 65535 && _pollingIntervalSeconds >= 1;

    public bool IsDirty => _name != _originalName ||
                           _hostAddress != _originalHostAddress ||
                           _port != _originalPort ||
                           _pollingIntervalSeconds != _originalPollingIntervalSeconds;

    public event Action<bool>? IsValidChanged;

    public event Action<bool>? IsDirtyChanged;

    protected override void OnInitialized()
    {
        if (HeatPump is not null)
        {
            _name = HeatPump.Name;
            _hostAddress = HeatPump.HostAddress;
            _port = HeatPump.Port;
            _pollingIntervalSeconds = (int)HeatPump.PollingInterval.TotalSeconds;
            StoreOriginalValues();
        }
    }

    private void StoreOriginalValues()
    {
        _originalName = _name;
        _originalHostAddress = _hostAddress;
        _originalPort = _port;
        _originalPollingIntervalSeconds = _pollingIntervalSeconds;
    }

    private void OnFieldChanged()
    {
        IsValidChanged?.Invoke(IsValid);
        IsDirtyChanged?.Invoke(IsDirty);
    }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        if (HeatPump is not null && IsValid)
        {
            HeatPump.Name = _name;
            HeatPump.HostAddress = _hostAddress;
            HeatPump.Port = _port;
            HeatPump.PollingInterval = TimeSpan.FromSeconds(_pollingIntervalSeconds);
            StoreOriginalValues();
            IsDirtyChanged?.Invoke(false);
        }

        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Add the UI project to the solution**

In `src/Namotion.Interceptor.slnx`, after the Luxtronik lines of Task 3:

```xml
    <Project Path="HomeBlaze/Namotion.Devices.Luxtronik.HomeBlaze/Namotion.Devices.Luxtronik.HomeBlaze.csproj" />
```

- [ ] **Step 5: Build**

Run: `dotnet build src/HomeBlaze/Namotion.Devices.Luxtronik.HomeBlaze`
Expected: `Build succeeded`, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Luxtronik.HomeBlaze src/Namotion.Interceptor.slnx src/HomeBlaze/Namotion.Devices.Luxtronik
git commit -m "feat: add the Luxtronik heat pump widget and editor"
```

---

## Task 11: HomeBlaze registration and sample configuration

**Files:**
- Create: `src/HomeBlaze/Namotion.Devices.Luxtronik/LuxtronikServiceCollectionExtensions.cs`
- Modify: `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj` (project references, after the Ecowitt lines)
- Modify: `src/HomeBlaze/HomeBlaze/Program.cs` (usings near line 23, `typeProvider` chain near line 88)
- Create: `src/HomeBlaze/HomeBlaze/Data/Devices/Luxtronik.json`

- [ ] **Step 1: DI extension**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;

namespace Namotion.Devices.Luxtronik;

public static class LuxtronikServiceCollectionExtensions
{
    public static IServiceCollection AddLuxtronikHeatPump(
        this IServiceCollection services,
        Action<LuxtronikHeatPump>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        => services.AddHostedSubject(configure, contextResolver);
}
```

- [ ] **Step 2: Register the assemblies**

`HomeBlaze.csproj`, after the two Ecowitt references:

```xml
        <ProjectReference Include="..\Namotion.Devices.Luxtronik\Namotion.Devices.Luxtronik.csproj" />
        <ProjectReference Include="..\Namotion.Devices.Luxtronik.HomeBlaze\Namotion.Devices.Luxtronik.HomeBlaze.csproj" />
```

`Program.cs`, after `using Namotion.Devices.Ecowitt.HomeBlaze;`:

```csharp
using Namotion.Devices.Luxtronik;
using Namotion.Devices.Luxtronik.HomeBlaze;
```

and after `.AddAssembly(typeof(EcowittGatewayWidget).Assembly)`:

```csharp
    .AddAssembly(typeof(LuxtronikHeatPump).Assembly)
    .AddAssembly(typeof(LuxtronikHeatPumpWidget).Assembly)
```

The Razor component's namespace is `Namotion.Devices.Luxtronik.HomeBlaze` (root namespace of the UI project), matching the Ecowitt pattern.

- [ ] **Step 3: Sample configuration**

`Data/Devices/Luxtronik.json`:

```json
{
  "$type": "Namotion.Devices.Luxtronik.LuxtronikHeatPump",
  "name": "Heat Pump",
  "hostAddress": "",
  "port": 502,
  "pollingInterval": "00:00:05"
}
```

The empty host address keeps the device idle in stock HomeBlaze: `ExecuteAsync` reports `Stopped` with "No host address configured" until a host is entered in the editor, instead of retrying a placeholder address.

- [ ] **Step 4: Build HomeBlaze**

Run: `dotnet build src/HomeBlaze/HomeBlaze`
Expected: `Build succeeded`, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/HomeBlaze/Namotion.Devices.Luxtronik/LuxtronikServiceCollectionExtensions.cs src/HomeBlaze/HomeBlaze/HomeBlaze.csproj src/HomeBlaze/HomeBlaze/Program.cs src/HomeBlaze/HomeBlaze/Data/Devices/Luxtronik.json
git commit -m "feat: register the Luxtronik heat pump in HomeBlaze"
```

---

## Task 12: Device documentation

**Files:**
- Create: `src/HomeBlaze/HomeBlaze/Data/Docs/devices/Luxtronik.md`

- [ ] **Step 1: Write the page**

````markdown
---
title: Luxtronik Heat Pump
icon: HeatPump
---

# Luxtronik Heat Pump

Reads Luxtronik 2.1 heat pumps (Alpha Innotec, Novelan and other ait-deutschland brands) through the Smart Home Interface (SHI), which is Modbus TCP on port 502. This integration is read only: it never sends a value to the controller.

## Supported Devices

- Luxtronik 2.1 controllers with the Smart Home Interface, firmware 3.90.1 or later
- Registers added in firmware 3.92 (thermal energy, operating hours, pump outputs, Smart Grid state, levels, locks, hot water requests) are read only when the controller runs 3.92 or later; tested against 3.92.3

## Safety and Prerequisites

Reading cannot change how the heat pump runs:

- Only read requests are sent. Reads change no controller state and write nothing to flash.
- Every SHI control starts at "no influence" and only takes effect once a controller writes its mode, which this integration never does.
- After enabling SHI, the controller's "Empfangene Daten" (received data) menu keeps showing "---" and no SHI symbol appears on the navigation screen. That is visible proof nothing was written.

Before enabling SHI:

1. Enabling SHI is a service-menu setting: Service > Systemsteuerung > Konnektivität > Smart Home Interface > ModBus TCP. The manufacturer reserves controller settings for authorised personnel.
2. Note the current Smart Grid setting and leave it unchanged for a read-only test. The manual warns that SHI and Smart Grid can influence each other; with no SHI writes there is no influence.
3. Keep port 502 inside your network. Modbus TCP has no authentication, so never forward the port on your router, even though the controller manual mentions it.
4. Make sure no other tool writes to SHI (for example evcc or SolarManager). A second writer raises controller error 816.
5. Switch SHI off again if you no longer need it.

The controller shows SHI as "Standby" after 10 minutes without requests and resets values received from a writer after 15 minutes without requests. The default polling interval of 5 seconds keeps the interface active.

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | "" | Display name |
| `HostAddress` | string | - | Controller IP address or host name |
| `Port` | int | 502 | Modbus TCP port |
| `PollingInterval` | TimeSpan | 5 seconds | Time between reads |

## State Properties

Temperatures are °C, power W, energy Wh. A value is empty when the controller does not provide it (older firmware, function not configured) or reports it as not available.

| Group | Properties |
|-------|-----------|
| Device | `IsConnected`, `Status`, `StatusMessage` (includes the heat pump error number), `LastUpdated`, `SoftwareVersion`, `Power`, `EnergyConsumed`, `ThermalPower`, `ThermalEnergyProduced` |
| `OperatingStatus` | `HeatPumpStatus`, `IsCompressorRunning`, `IsAuxiliaryHeaterRunning`, `OperationMode`, `HeatingStatus`, `HotWaterStatus`, `CoolingStatus`, `PoolHeatingStatus`, `ErrorCode`, `BufferType`, `MinimumOffTime`, `MinimumRunTime` (minutes), `CoolingReleased` |
| `Temperatures` | Sensors: `Return`, `ExternalReturn`, `Flow`, `Room`, `Outside`, `OutsideAverage`, `HeatSourceInlet`, `HeatSourceOutlet`, `HotWater`. Values: `ReturnTarget`, `ReturnLimit`, `ReturnMinimumTarget`, `HeatingLimit`, `MaximumFlow`, `CalculatedFlow`, `HotWaterTarget`, `HotWaterMinimum`, `HotWaterMaximum`, `HotWaterLimit` |
| `Energy` | `HeatingPower`, `ElectricalPower`, `MinimumPredictedElectricalPower`; electrical and thermal energy totals for all, heating, hot water, cooling and pool |
| `Runtime` | Operating hours: `HeatPump`, `Heating`, `HotWater`, `Cooling`, `Pool`, `Solar` |
| `Outputs` | Pump states: brine, mixing circuits 1 to 3, heating, hot water, circulation |
| `SmartGrid` | `Evu1`, `Evu2`, `State` (Locked, Reduced, Normal, Increased) |
| `ExtraHotWater` | `Setpoint`, `Duration`, `RemainingDuration` (minutes) |
| `Features` | Which functions are configured on the controller |
| `Heating`, `HotWater`, `MixingCircuit1..3` | Current SHI control values: `Mode`, `Setpoint`, `Offset` (K), `Level`; mixing circuits also have a temperature sensor, setpoints and a cooling control |
| `PowerLimit`, `Locks`, `RoomControl`, `OverallHeating`, `HotWaterRequests` | Current SHI control values |

## Interfaces

- `IPowerSensor` - electrical power and consumed energy
- `IThermalPowerSensor` - heating power and produced thermal energy
- `ITemperatureSensor` - each measured temperature is its own child sensor
- `IConnectionState` - SHI connection state
- `ISoftwareState` - controller firmware version
- `IMonitoredService` - service status and heat pump error number

## JSON Configuration Example

```json
{
  "$type": "Namotion.Devices.Luxtronik.LuxtronikHeatPump",
  "name": "Heat Pump",
  "hostAddress": "192.168.1.50",
  "port": 502,
  "pollingInterval": "00:00:05"
}
```

## Troubleshooting

- **Connection refused:** SHI is not enabled on the controller, or a firewall blocks port 502.
- **Values stay empty:** the function is not configured on the controller, or the firmware is older than 3.92 for that value.
- **Controller error 816:** more than one tool writes to SHI. This integration does not write; check other integrations.
- **Status "Standby" on the controller:** no requests for 10 minutes; check that HomeBlaze is running and connected.
- **Hot water temperature shows exactly 75.0 °C:** the controller reports this substitute value (input 10120) when the hot water sensor is faulty. It is passed through unfiltered; check the sensor.
- **A value keeps showing after a function was switched off:** functions and firmware are checked on every reconnect, and a value excluded on a later reconnect keeps its last reading until HomeBlaze restarts.

## Modbus Register Map

Addresses are raw Modbus addresses (no +1). Input registers are read with function code 4, holding registers with 3, discrete inputs with 2.

| Address | Space | Content |
|---------|-------|---------|
| 10000 | Input | Heat pump status bits (compressors, auxiliary heaters) |
| 10002 to 10007 | Input | Operation mode, heating, hot water, cooling and pool status |
| 10100 to 10113 | Input | Return, flow, room, outside temperatures and limits (0.1 °C); 10109 to 10113 from 3.92 |
| 10120 to 10124 | Input | Hot water temperature, target and limits (0.1 °C) |
| 10140 to 10163 | Input | Mixing circuit 1 to 3 temperature, target, minimum, maximum (0.1 °C) |
| 10201 to 10207 | Input | Error number, buffer type, minimum off and run times, cooling released |
| 10300 to 10302 | Input | Heating power, electrical power, minimum predicted power (0.1 kW) |
| 10310 to 10329 | Input | Electrical and thermal energy, 32-bit high word first (0.1 kWh); thermal from 3.92 |
| 10350 to 10361 | Input | Pump outputs and Smart Grid signals (3.92) |
| 10400 to 10402 | Input | Firmware major, minor, patch |
| 10404 to 10417 | Input | Operating hours, 32-bit (3.92) |
| 10500 to 10502 | Input | Extra hot water setpoint, duration, remaining time (3.92) |
| 10000 to 10008 | Holding | Heating and hot water control: mode, setpoint, offset, level |
| 10010 to 10037 | Holding | Mixing circuit 1 to 3 heating and cooling control |
| 10040 to 10041 | Holding | Power limit mode and value (0.1 kW) |
| 10050 to 10053 | Holding | Heating, hot water, cooling and pool locks |
| 10060 | Holding | Room temperature setpoint (3.92.1, room control unit) |
| 10065 to 10071 | Holding | Overall heating control, circulation and extra hot water requests (3.92) |
| 10000 to 10011 | Discrete input | Configured functions |

Since firmware 3.92 a configured-off value reads as 0x7FFF (16-bit) or 0x7FFFFFFF (32-bit) and is shown as empty.

## References

- [AIT, Betriebsanleitung Smart Home Interface Modbus TCP (83026900aDE)](https://files.ait-group.net/FILES/Alpha-InnoTec/Betriebsanleitungen/01%20Waermepumpen/05%20Regler/Zubehoer/83026900aDE_SHI.pdf)
- [AIT, Betriebsanleitung Luxtronik 2.1 Teil 2 (83055400oDE)](https://www.alpha-innotec.com/download/18.682db7e519782568f993847/1752825715519/Betriebsanleitung_Regler_Luxtronik_2.1_Teil_2_83055400oDE.pdf)
- [python-luxtronik SHI README](https://github.com/Bouni/python-luxtronik/blob/02afea84bd5bf3ee87445de6f2a42b8029983169/luxtronik/shi/README.md)
- [python-luxtronik input definitions](https://github.com/Bouni/python-luxtronik/blob/02afea84bd5bf3ee87445de6f2a42b8029983169/luxtronik/definitions/inputs.py)
- [python-luxtronik holding definitions](https://github.com/Bouni/python-luxtronik/blob/02afea84bd5bf3ee87445de6f2a42b8029983169/luxtronik/definitions/holdings.py)
- [python-luxtronik constants (not-available values)](https://github.com/Bouni/python-luxtronik/blob/02afea84bd5bf3ee87445de6f2a42b8029983169/luxtronik/constants.py)
- [python-luxtronik PR 213, update from the official documentation](https://github.com/Bouni/python-luxtronik/pull/213)
- [raibisch LuxModbusSHI how-to (its register table has known errors)](https://github.com/raibisch/mylibs/blob/main/LuxModbusSHI/LuxtronikSHI.md)
- [evcc Luxtronik support, PR 21516](https://github.com/evcc-io/evcc/pull/21516)
- [haustechnikdialog forum thread on the Luxtronik 2.1 SHI](https://www.haustechnikdialog.de/Forum/t/284442/Eigene-Regelung-PV-Luxtronik-2-1-Smart-Home-Interface-SHI)
````

- [ ] **Step 2: Check for em dashes**

Run: `grep -nP "\x{2014}" src/HomeBlaze/HomeBlaze/Data/Docs/devices/Luxtronik.md`
Expected: no output.

- [ ] **Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze/Data/Docs/devices/Luxtronik.md
git commit -m "docs: document the Luxtronik heat pump device"
```

---

## Task 13: Final verification and hardware run

- [ ] **Step 1: Build the solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: `Build succeeded`, 0 warnings.

- [ ] **Step 2: Run the Luxtronik tests**

Run: `dotnet test src/HomeBlaze/Namotion.Devices.Luxtronik.Tests`
Expected: all pass, the hardware test skipped.

- [ ] **Step 3: Run the affected HomeBlaze tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Host.Services.Tests`
Expected: all pass.

- [ ] **Step 4: Run the unit test suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: all pass (see the flaky `ContextRetentionLeakTests` note in the connector plan).

- [ ] **Step 5: Hand the hardware run to the user**

The user runs, after enabling SHI per the device doc's checklist:

```bash
LUXTRONIK_HOST=<controller-ip> dotnet test src/HomeBlaze/Namotion.Devices.Luxtronik.Tests --filter "FullyQualifiedName~LuxtronikHardwareTests" --logger "console;verbosity=detailed"
```

Then, together with the user:
1. Compare the dump with the controller display: 10100 return actual vs 10101 target, 10301 electrical power, 10310/10311 electrical energy, 10320/10321 thermal energy, 10404 operating hours.
2. Check `unmappedReadBehavior`. If it is "no response" rather than an exception response, raise it before release: split-on-failure would then see timeouts and reconnect instead of isolating a register.
3. Fix any mapping the dump contradicts, rerun the tests.
4. Check the dump in as `src/HomeBlaze/Namotion.Devices.Luxtronik.Tests/Fixtures/luxtronik-3.92.3.json` (remove nothing; it contains no host or personal data) and add a test that loads it with `LuxtronikTestServer.LoadDump` and asserts the values the user confirmed on the display.

- [ ] **Step 6: Hand over**

Report test counts and the hardware findings. Do not push or open a pull request unless the user asks.
