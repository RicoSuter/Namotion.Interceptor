# Namotion.Devices.Gpio Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Create a trackable object model for Raspberry Pi GPIO with bidirectional hardware sync, plus HomeBlaze UI components.

**Architecture:** Two packages - `Namotion.Devices.Gpio` (core subjects using System.Device.Gpio) and `Namotion.Devices.Gpio.HomeBlaze` (MudBlazor widget/editor). Pins auto-discovered, ADC optional via configuration.

**Tech Stack:** .NET 9, System.Device.Gpio, Iot.Device.Bindings, MudBlazor, xUnit

**Design Document:** `docs/plans/2025-12-19-namotion-devices-gpio-design.md`

---

## Part 1: Serialization Enhancement

### Task 1: Create ConfigurationJsonTypeInfoResolver

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Services/Serialization/ConfigurationJsonTypeInfoResolver.cs`
- Test: `src/HomeBlaze/HomeBlaze.Services.Tests/Serialization/ConfigurationJsonTypeInfoResolverTests.cs`

**Step 1: Create test file with basic subject serialization test**

```csharp
using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Serialization;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Xunit;

namespace HomeBlaze.Services.Tests.Serialization;

public class ConfigurationJsonTypeInfoResolverTests
{
    private readonly JsonSerializerOptions _options;

    public ConfigurationJsonTypeInfoResolverTests()
    {
        _options = new JsonSerializerOptions
        {
            TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(),
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    [Fact]
    public void Serialize_Subject_OnlyIncludesConfigurationProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new TestSubject(context)
        {
            ConfigProperty = "saved",
            StateProperty = "not-saved"
        };

        // Act
        var json = JsonSerializer.Serialize(subject, subject.GetType(), _options);

        // Assert
        Assert.Contains("configProperty", json);
        Assert.Contains("saved", json);
        Assert.DoesNotContain("stateProperty", json);
        Assert.DoesNotContain("not-saved", json);
    }

    [InterceptorSubject]
    private partial class TestSubject
    {
        [Configuration]
        public partial string ConfigProperty { get; set; }

        public partial string StateProperty { get; set; }

        public TestSubject()
        {
            ConfigProperty = string.Empty;
            StateProperty = string.Empty;
        }
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "ConfigurationJsonTypeInfoResolverTests.Serialize_Subject_OnlyIncludesConfigurationProperties"`

Expected: FAIL - ConfigurationJsonTypeInfoResolver does not exist

**Step 3: Create minimal ConfigurationJsonTypeInfoResolver**

```csharp
using System.Text.Json.Serialization.Metadata;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Serialization;

/// <summary>
/// JSON type info resolver that filters properties based on serialization rules:
/// - IInterceptorSubject types: only [Configuration] properties
/// - Plain objects (value objects): all properties
/// </summary>
public class ConfigurationJsonTypeInfoResolver : DefaultJsonTypeInfoResolver
{
    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = base.GetTypeInfo(type, options);

        if (typeof(IInterceptorSubject).IsAssignableFrom(type) &&
            typeInfo.Kind == JsonTypeInfoKind.Object)
        {
            foreach (var property in typeInfo.Properties)
            {
                var hasConfigurationAttribute = property.AttributeProvider?
                    .GetCustomAttributes(typeof(ConfigurationAttribute), true)
                    .Any() ?? false;

                if (!hasConfigurationAttribute)
                {
                    property.ShouldSerialize = static (_, _) => false;
                }
            }
        }

        return typeInfo;
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "ConfigurationJsonTypeInfoResolverTests.Serialize_Subject_OnlyIncludesConfigurationProperties"`

Expected: PASS

**Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/Serialization/ConfigurationJsonTypeInfoResolver.cs src/HomeBlaze/HomeBlaze.Services.Tests/Serialization/ConfigurationJsonTypeInfoResolverTests.cs
git commit -m "feat: add ConfigurationJsonTypeInfoResolver for subject serialization"
```

---

### Task 2: Add value object serialization test

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Services.Tests/Serialization/ConfigurationJsonTypeInfoResolverTests.cs`

**Step 1: Add test for value object (all properties serialized)**

```csharp
[Fact]
public void Serialize_ValueObject_IncludesAllProperties()
{
    // Arrange
    var valueObject = new TestValueObject
    {
        PropertyOne = "one",
        PropertyTwo = "two"
    };

    // Act
    var json = JsonSerializer.Serialize(valueObject, _options);

    // Assert
    Assert.Contains("propertyOne", json);
    Assert.Contains("one", json);
    Assert.Contains("propertyTwo", json);
    Assert.Contains("two", json);
}

private class TestValueObject
{
    public string PropertyOne { get; set; } = string.Empty;
    public string PropertyTwo { get; set; } = string.Empty;
}
```

**Step 2: Run test to verify it passes (no implementation needed)**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "ConfigurationJsonTypeInfoResolverTests.Serialize_ValueObject_IncludesAllProperties"`

Expected: PASS (value objects already serialize all properties by default)

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services.Tests/Serialization/ConfigurationJsonTypeInfoResolverTests.cs
git commit -m "test: add value object serialization test"
```

---

### Task 3: Add nested subject and value object tests

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Services.Tests/Serialization/ConfigurationJsonTypeInfoResolverTests.cs`

**Step 1: Add test for subject with nested value object**

```csharp
[Fact]
public void Serialize_SubjectWithNestedValueObject_ValueObjectFullySerialized()
{
    // Arrange
    var context = InterceptorSubjectContext.Create();
    var subject = new ParentSubject(context)
    {
        Name = "parent",
        Config = new NestedValueObject { SettingA = "a", SettingB = "b" }
    };

    // Act
    var json = JsonSerializer.Serialize(subject, subject.GetType(), _options);

    // Assert
    Assert.Contains("name", json);
    Assert.Contains("config", json);
    Assert.Contains("settingA", json);
    Assert.Contains("settingB", json);
}

[Fact]
public void Serialize_SubjectWithNestedSubject_NestedSubjectFiltered()
{
    // Arrange
    var context = InterceptorSubjectContext.Create();
    var child = new ChildSubject(context) { ChildConfig = "saved", ChildState = "not-saved" };
    var parent = new ParentWithChildSubject(context) { Name = "parent", Child = child };

    // Act
    var json = JsonSerializer.Serialize(parent, parent.GetType(), _options);

    // Assert
    Assert.Contains("name", json);
    Assert.Contains("child", json);
    Assert.Contains("childConfig", json);
    Assert.DoesNotContain("childState", json);
}

[InterceptorSubject]
private partial class ParentSubject
{
    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial NestedValueObject? Config { get; set; }

    public ParentSubject()
    {
        Name = string.Empty;
    }
}

private class NestedValueObject
{
    public string SettingA { get; set; } = string.Empty;
    public string SettingB { get; set; } = string.Empty;
}

[InterceptorSubject]
private partial class ParentWithChildSubject
{
    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial ChildSubject? Child { get; set; }

    public ParentWithChildSubject()
    {
        Name = string.Empty;
    }
}

[InterceptorSubject]
private partial class ChildSubject
{
    [Configuration]
    public partial string ChildConfig { get; set; }

    public partial string ChildState { get; set; }

    public ChildSubject()
    {
        ChildConfig = string.Empty;
        ChildState = string.Empty;
    }
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "ConfigurationJsonTypeInfoResolverTests"`

Expected: PASS

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services.Tests/Serialization/ConfigurationJsonTypeInfoResolverTests.cs
git commit -m "test: add nested subject and value object serialization tests"
```

---

### Task 4: Add collection serialization tests

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Services.Tests/Serialization/ConfigurationJsonTypeInfoResolverTests.cs`

**Step 1: Add tests for collections**

```csharp
[Fact]
public void Serialize_ListOfSubjects_EachSubjectFiltered()
{
    // Arrange
    var context = InterceptorSubjectContext.Create();
    var parent = new SubjectWithList(context)
    {
        Items =
        [
            new ChildSubject(context) { ChildConfig = "a", ChildState = "x" },
            new ChildSubject(context) { ChildConfig = "b", ChildState = "y" }
        ]
    };

    // Act
    var json = JsonSerializer.Serialize(parent, parent.GetType(), _options);

    // Assert
    Assert.Contains("childConfig", json);
    Assert.Contains("\"a\"", json);
    Assert.Contains("\"b\"", json);
    Assert.DoesNotContain("childState", json);
}

[Fact]
public void Serialize_DictionaryOfSubjects_EachSubjectFiltered()
{
    // Arrange
    var context = InterceptorSubjectContext.Create();
    var parent = new SubjectWithDictionary(context)
    {
        Items = new Dictionary<string, ChildSubject>
        {
            ["first"] = new ChildSubject(context) { ChildConfig = "a", ChildState = "x" },
            ["second"] = new ChildSubject(context) { ChildConfig = "b", ChildState = "y" }
        }
    };

    // Act
    var json = JsonSerializer.Serialize(parent, parent.GetType(), _options);

    // Assert
    Assert.Contains("first", json);
    Assert.Contains("second", json);
    Assert.Contains("childConfig", json);
    Assert.DoesNotContain("childState", json);
}

[Fact]
public void Serialize_ListOfValueObjects_AllPropertiesIncluded()
{
    // Arrange
    var context = InterceptorSubjectContext.Create();
    var parent = new SubjectWithValueObjectList(context)
    {
        Configs =
        [
            new NestedValueObject { SettingA = "a1", SettingB = "b1" },
            new NestedValueObject { SettingA = "a2", SettingB = "b2" }
        ]
    };

    // Act
    var json = JsonSerializer.Serialize(parent, parent.GetType(), _options);

    // Assert
    Assert.Contains("settingA", json);
    Assert.Contains("settingB", json);
    Assert.Contains("a1", json);
    Assert.Contains("b2", json);
}

[InterceptorSubject]
private partial class SubjectWithList
{
    [Configuration]
    public partial List<ChildSubject> Items { get; set; }

    public SubjectWithList()
    {
        Items = [];
    }
}

[InterceptorSubject]
private partial class SubjectWithDictionary
{
    [Configuration]
    public partial Dictionary<string, ChildSubject> Items { get; set; }

    public SubjectWithDictionary()
    {
        Items = new Dictionary<string, ChildSubject>();
    }
}

[InterceptorSubject]
private partial class SubjectWithValueObjectList
{
    [Configuration]
    public partial List<NestedValueObject> Configs { get; set; }

    public SubjectWithValueObjectList()
    {
        Configs = [];
    }
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests --filter "ConfigurationJsonTypeInfoResolverTests"`

Expected: PASS

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services.Tests/Serialization/ConfigurationJsonTypeInfoResolverTests.cs
git commit -m "test: add collection serialization tests"
```

---

### Task 5: Update ConfigurableSubjectSerializer to use resolver

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Services/ConfigurableSubjectSerializer.cs`
- Test: `src/HomeBlaze/HomeBlaze.Services.Tests/ConfigurableSubjectSerializerTests.cs`

**Step 1: Review current ConfigurableSubjectSerializer**

Read: `src/HomeBlaze/HomeBlaze.Services/ConfigurableSubjectSerializer.cs`

**Step 2: Update to use ConfigurationJsonTypeInfoResolver**

Replace the manual serialization with JsonSerializer using the resolver:

```csharp
// In constructor, update _jsonOptions:
_jsonOptions = new JsonSerializerOptions
{
    TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(),
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

// Simplify Serialize method:
public string Serialize(IInterceptorSubject subject)
{
    return JsonSerializer.Serialize(subject, subject.GetType(), _jsonOptions);
}
```

**Step 3: Run all serializer tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.Services.Tests`

Expected: PASS

**Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Services/ConfigurableSubjectSerializer.cs
git commit -m "refactor: use ConfigurationJsonTypeInfoResolver in ConfigurableSubjectSerializer"
```

---

## Part 2: Namotion.Devices.Gpio Core Library

### Task 6: Create project structure

**Files:**
- Create: `src/Namotion.Devices.Gpio/Namotion.Devices.Gpio.csproj`
- Create: `src/Namotion.Devices.Gpio.Tests/Namotion.Devices.Gpio.Tests.csproj`

**Step 1: Create core project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>preview</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="System.Device.Gpio" Version="3.*" />
    <PackageReference Include="Iot.Device.Bindings" Version="3.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Namotion.Interceptor\Namotion.Interceptor.csproj" />
    <ProjectReference Include="..\HomeBlaze\HomeBlaze.Abstractions\HomeBlaze.Abstractions.csproj" />
  </ItemGroup>

</Project>
```

**Step 2: Create test project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>preview</LangVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Namotion.Devices.Gpio\Namotion.Devices.Gpio.csproj" />
  </ItemGroup>

</Project>
```

**Step 3: Add projects to solution**

Run: `dotnet sln src/Namotion.Interceptor.slnx add src/Namotion.Devices.Gpio/Namotion.Devices.Gpio.csproj src/Namotion.Devices.Gpio.Tests/Namotion.Devices.Gpio.Tests.csproj`

**Step 4: Verify build**

Run: `dotnet build src/Namotion.Devices.Gpio`

Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Namotion.Devices.Gpio src/Namotion.Devices.Gpio.Tests src/Namotion.Interceptor.slnx
git commit -m "feat: add Namotion.Devices.Gpio project structure"
```

---

### Task 7: Create GpioPinMode enum

**Files:**
- Create: `src/Namotion.Devices.Gpio/GpioPinMode.cs`

**Step 1: Create enum**

```csharp
namespace Namotion.Devices.Gpio;

/// <summary>
/// GPIO pin operating modes.
/// </summary>
public enum GpioPinMode
{
    /// <summary>
    /// Digital input (floating).
    /// </summary>
    Input,

    /// <summary>
    /// Digital input with internal pull-up resistor.
    /// </summary>
    InputPullUp,

    /// <summary>
    /// Digital input with internal pull-down resistor.
    /// </summary>
    InputPullDown,

    /// <summary>
    /// Digital output.
    /// </summary>
    Output
}
```

**Step 2: Verify build**

Run: `dotnet build src/Namotion.Devices.Gpio`

Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Devices.Gpio/GpioPinMode.cs
git commit -m "feat: add GpioPinMode enum"
```

---

### Task 8: Create GpioPin subject

**Files:**
- Create: `src/Namotion.Devices.Gpio/GpioPin.cs`
- Create: `src/Namotion.Devices.Gpio.Tests/GpioPinTests.cs`

**Step 1: Create test file**

```csharp
using Namotion.Interceptor;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class GpioPinTests
{
    [Fact]
    public void GpioPin_InitializesWithDefaults()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        // Act
        var pin = new GpioPin(context);

        // Assert
        Assert.Equal(0, pin.PinNumber);
        Assert.Equal(GpioPinMode.Input, pin.Mode);
        Assert.False(pin.Value);
    }

    [Fact]
    public void GpioPin_CanSetProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var pin = new GpioPin(context);

        // Act
        pin.PinNumber = 17;
        pin.Mode = GpioPinMode.Output;
        pin.Value = true;

        // Assert
        Assert.Equal(17, pin.PinNumber);
        Assert.Equal(GpioPinMode.Output, pin.Mode);
        Assert.True(pin.Value);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Devices.Gpio.Tests`

Expected: FAIL - GpioPin does not exist

**Step 3: Create GpioPin subject**

```csharp
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Represents a single GPIO pin with mode and value.
/// </summary>
[InterceptorSubject]
public partial class GpioPin
{
    /// <summary>
    /// The GPIO pin number (BCM numbering).
    /// </summary>
    public partial int PinNumber { get; set; }

    /// <summary>
    /// The pin operating mode.
    /// </summary>
    [Configuration]
    public partial GpioPinMode Mode { get; set; }

    /// <summary>
    /// The current pin value (true = high, false = low).
    /// </summary>
    [State]
    public partial bool Value { get; set; }

    public GpioPin()
    {
        PinNumber = 0;
        Mode = GpioPinMode.Input;
        Value = false;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Devices.Gpio.Tests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/Namotion.Devices.Gpio/GpioPin.cs src/Namotion.Devices.Gpio.Tests/GpioPinTests.cs
git commit -m "feat: add GpioPin subject"
```

---

### Task 9: Create AnalogChannel subject

**Files:**
- Create: `src/Namotion.Devices.Gpio/AnalogChannel.cs`
- Modify: `src/Namotion.Devices.Gpio.Tests/GpioPinTests.cs` → rename to `SubjectTests.cs`

**Step 1: Add test for AnalogChannel**

```csharp
[Fact]
public void AnalogChannel_InitializesWithDefaults()
{
    // Arrange
    var context = InterceptorSubjectContext.Create();

    // Act
    var channel = new AnalogChannel(context);

    // Assert
    Assert.Equal(0, channel.ChannelNumber);
    Assert.Equal(0.0, channel.Value);
    Assert.Equal(0, channel.RawValue);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Devices.Gpio.Tests --filter "AnalogChannel_InitializesWithDefaults"`

Expected: FAIL - AnalogChannel does not exist

**Step 3: Create AnalogChannel subject**

```csharp
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Represents an analog input channel from an ADC.
/// </summary>
[InterceptorSubject]
public partial class AnalogChannel
{
    /// <summary>
    /// The ADC channel number.
    /// </summary>
    public partial int ChannelNumber { get; set; }

    /// <summary>
    /// The normalized value (0.0 to 1.0).
    /// </summary>
    [State]
    public partial double Value { get; set; }

    /// <summary>
    /// The raw ADC value (e.g., 0-1023 for 10-bit).
    /// </summary>
    [State]
    public partial int RawValue { get; set; }

    public AnalogChannel()
    {
        ChannelNumber = 0;
        Value = 0.0;
        RawValue = 0;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Devices.Gpio.Tests`

Expected: PASS

**Step 5: Commit**

```bash
git add src/Namotion.Devices.Gpio/AnalogChannel.cs src/Namotion.Devices.Gpio.Tests/
git commit -m "feat: add AnalogChannel subject"
```

---

### Task 10: Create configuration classes

**Files:**
- Create: `src/Namotion.Devices.Gpio/Configuration/Mcp3008Configuration.cs`
- Create: `src/Namotion.Devices.Gpio/Configuration/Ads1115Configuration.cs`

**Step 1: Create Mcp3008Configuration**

```csharp
namespace Namotion.Devices.Gpio.Configuration;

/// <summary>
/// Configuration for MCP3008 10-bit SPI ADC.
/// </summary>
public class Mcp3008Configuration
{
    /// <summary>
    /// SPI clock pin (BCM numbering).
    /// </summary>
    public int ClockPin { get; set; } = 11;

    /// <summary>
    /// SPI MOSI pin (BCM numbering).
    /// </summary>
    public int MosiPin { get; set; } = 10;

    /// <summary>
    /// SPI MISO pin (BCM numbering).
    /// </summary>
    public int MisoPin { get; set; } = 9;

    /// <summary>
    /// SPI chip select pin (BCM numbering).
    /// </summary>
    public int ChipSelectPin { get; set; } = 8;
}
```

**Step 2: Create Ads1115Configuration**

```csharp
namespace Namotion.Devices.Gpio.Configuration;

/// <summary>
/// Configuration for ADS1115 16-bit I2C ADC.
/// </summary>
public class Ads1115Configuration
{
    /// <summary>
    /// I2C bus number.
    /// </summary>
    public int I2cBus { get; set; } = 1;

    /// <summary>
    /// I2C device address.
    /// </summary>
    public int Address { get; set; } = 0x48;
}
```

**Step 3: Verify build**

Run: `dotnet build src/Namotion.Devices.Gpio`

Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Namotion.Devices.Gpio/Configuration/
git commit -m "feat: add ADC configuration classes"
```

---

### Task 11: Create GpioSubject

**Files:**
- Create: `src/Namotion.Devices.Gpio/GpioSubject.cs`
- Create: `src/Namotion.Devices.Gpio.Tests/GpioSubjectTests.cs`

**Step 1: Create test file**

```csharp
using Namotion.Interceptor;
using Xunit;

namespace Namotion.Devices.Gpio.Tests;

public class GpioSubjectTests
{
    [Fact]
    public void GpioSubject_InitializesWithEmptyCollections()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        // Act
        var subject = new GpioSubject(context);

        // Assert
        Assert.NotNull(subject.Pins);
        Assert.Empty(subject.Pins);
        Assert.NotNull(subject.AnalogChannels);
        Assert.Empty(subject.AnalogChannels);
        Assert.Null(subject.Mcp3008);
        Assert.Null(subject.Ads1115);
    }

    [Fact]
    public void GpioSubject_TitleAndIcon_ReturnExpectedValues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new GpioSubject(context);

        // Act & Assert
        Assert.Equal("GPIO", subject.Title);
        Assert.Equal("Memory", subject.Icon);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Devices.Gpio.Tests --filter "GpioSubjectTests"`

Expected: FAIL - GpioSubject does not exist

**Step 3: Create GpioSubject**

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using Namotion.Devices.Gpio.Configuration;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Represents a Raspberry Pi GPIO controller with auto-discovered pins
/// and optional ADC support.
/// </summary>
[InterceptorSubject]
public partial class GpioSubject : BackgroundService, IConfigurableSubject, ITitleProvider, IIconProvider
{
    /// <summary>
    /// Auto-discovered GPIO pins indexed by pin number.
    /// </summary>
    public partial Dictionary<int, GpioPin> Pins { get; set; }

    /// <summary>
    /// Optional MCP3008 ADC configuration.
    /// </summary>
    [Configuration]
    public partial Mcp3008Configuration? Mcp3008 { get; set; }

    /// <summary>
    /// Optional ADS1115 ADC configuration.
    /// </summary>
    [Configuration]
    public partial Ads1115Configuration? Ads1115 { get; set; }

    /// <summary>
    /// Analog channels from configured ADC (empty if no ADC configured).
    /// </summary>
    public partial Dictionary<int, AnalogChannel> AnalogChannels { get; set; }

    /// <inheritdoc />
    public string? Title => "GPIO";

    /// <inheritdoc />
    public string? Icon => "Memory";

    public GpioSubject()
    {
        Pins = new Dictionary<int, GpioPin>();
        AnalogChannels = new Dictionary<int, AnalogChannel>();
    }

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        // React to configuration changes - initialize/dispose ADC hardware
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Discover pins from hardware
        // 2. Register pin change callbacks
        // 3. Poll ADC channels if configured
        return Task.CompletedTask;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Devices.Gpio.Tests --filter "GpioSubjectTests"`

Expected: PASS

**Step 5: Commit**

```bash
git add src/Namotion.Devices.Gpio/GpioSubject.cs src/Namotion.Devices.Gpio.Tests/GpioSubjectTests.cs
git commit -m "feat: add GpioSubject with empty collections"
```

---

## Part 3: Namotion.Devices.Gpio.HomeBlaze UI

### Task 12: Create HomeBlaze project structure

**Files:**
- Create: `src/Namotion.Devices.Gpio.HomeBlaze/Namotion.Devices.Gpio.HomeBlaze.csproj`
- Create: `src/Namotion.Devices.Gpio.HomeBlaze/_Imports.razor`

**Step 1: Create project file**

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>preview</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="MudBlazor" Version="7.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Namotion.Devices.Gpio\Namotion.Devices.Gpio.csproj" />
    <ProjectReference Include="..\HomeBlaze\HomeBlaze.Components.Abstractions\HomeBlaze.Components.Abstractions.csproj" />
  </ItemGroup>

</Project>
```

**Step 2: Create _Imports.razor**

```razor
@using Microsoft.AspNetCore.Components
@using MudBlazor
@using Namotion.Devices.Gpio
@using Namotion.Devices.Gpio.Configuration
@using HomeBlaze.Components.Abstractions
@using HomeBlaze.Components.Abstractions.Attributes
```

**Step 3: Add project to solution**

Run: `dotnet sln src/Namotion.Interceptor.slnx add src/Namotion.Devices.Gpio.HomeBlaze/Namotion.Devices.Gpio.HomeBlaze.csproj`

**Step 4: Verify build**

Run: `dotnet build src/Namotion.Devices.Gpio.HomeBlaze`

Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Namotion.Devices.Gpio.HomeBlaze src/Namotion.Interceptor.slnx
git commit -m "feat: add Namotion.Devices.Gpio.HomeBlaze project"
```

---

### Task 13: Create GpioSubjectWidget

**Files:**
- Create: `src/Namotion.Devices.Gpio.HomeBlaze/GpioSubjectWidget.razor`

**Step 1: Create widget component**

```razor
@using HomeBlaze.Components.Abstractions.Attributes

<SubjectComponent(typeof(GpioSubject), SubjectComponentType.Widget)>
<MudPaper Class="pa-4">
    <MudStack Spacing="2">
        <MudText Typo="Typo.h6">
            <MudIcon Icon="@Icons.Material.Filled.Memory" Class="mr-2" />
            GPIO
        </MudText>

        <MudDivider />

        <MudStack Row="true" Spacing="4">
            <MudStack>
                <MudText Typo="Typo.caption">Digital Pins</MudText>
                <MudText Typo="Typo.h5">@Subject?.Pins.Count</MudText>
            </MudStack>

            <MudStack>
                <MudText Typo="Typo.caption">Inputs</MudText>
                <MudText Typo="Typo.h5">@InputCount</MudText>
            </MudStack>

            <MudStack>
                <MudText Typo="Typo.caption">Outputs</MudText>
                <MudText Typo="Typo.h5">@OutputCount</MudText>
            </MudStack>
        </MudStack>

        @if (Subject?.AnalogChannels.Count > 0)
        {
            <MudDivider />
            <MudStack Row="true" Spacing="4">
                <MudStack>
                    <MudText Typo="Typo.caption">Analog Channels</MudText>
                    <MudText Typo="Typo.h5">@Subject.AnalogChannels.Count</MudText>
                </MudStack>
            </MudStack>
        }
    </MudStack>
</MudPaper>

@code {
    [Parameter]
    public GpioSubject? Subject { get; set; }

    private int InputCount => Subject?.Pins.Values
        .Count(p => p.Mode is GpioPinMode.Input or GpioPinMode.InputPullUp or GpioPinMode.InputPullDown) ?? 0;

    private int OutputCount => Subject?.Pins.Values
        .Count(p => p.Mode == GpioPinMode.Output) ?? 0;
}
```

**Step 2: Verify build**

Run: `dotnet build src/Namotion.Devices.Gpio.HomeBlaze`

Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Devices.Gpio.HomeBlaze/GpioSubjectWidget.razor
git commit -m "feat: add GpioSubjectWidget"
```

---

### Task 14: Create GpioSubjectEditComponent

**Files:**
- Create: `src/Namotion.Devices.Gpio.HomeBlaze/GpioSubjectEditComponent.razor`

**Step 1: Create edit component**

```razor
@using HomeBlaze.Components.Abstractions.Attributes

<SubjectComponent(typeof(GpioSubject), SubjectComponentType.Edit)>
<MudStack Spacing="4">
    <MudText Typo="Typo.h6">GPIO Configuration</MudText>

    @* Pin Configuration Section *@
    <MudExpansionPanels>
        <MudExpansionPanel Text="Digital Pins" IsInitiallyExpanded="true">
            @if (Subject?.Pins.Count > 0)
            {
                <MudTable Items="@Subject.Pins.Values.OrderBy(p => p.PinNumber)" Dense="true">
                    <HeaderContent>
                        <MudTh>Pin</MudTh>
                        <MudTh>Mode</MudTh>
                        <MudTh>Value</MudTh>
                    </HeaderContent>
                    <RowTemplate>
                        <MudTd>@context.PinNumber</MudTd>
                        <MudTd>
                            <MudSelect T="GpioPinMode" Value="@context.Mode" ValueChanged="@(v => OnModeChanged(context, v))" Dense="true">
                                @foreach (var mode in Enum.GetValues<GpioPinMode>())
                                {
                                    <MudSelectItem Value="@mode">@mode</MudSelectItem>
                                }
                            </MudSelect>
                        </MudTd>
                        <MudTd>
                            @if (context.Mode == GpioPinMode.Output)
                            {
                                <MudSwitch T="bool" Value="@context.Value" ValueChanged="@(v => OnValueChanged(context, v))" Color="Color.Primary" />
                            }
                            else
                            {
                                <MudChip T="string" Color="@(context.Value ? Color.Success : Color.Default)" Size="Size.Small">
                                    @(context.Value ? "HIGH" : "LOW")
                                </MudChip>
                            }
                        </MudTd>
                    </RowTemplate>
                </MudTable>
            }
            else
            {
                <MudAlert Severity="Severity.Info">No GPIO pins detected. Running on non-Pi hardware?</MudAlert>
            }
        </MudExpansionPanel>

        @* MCP3008 ADC Section *@
        <MudExpansionPanel Text="MCP3008 ADC (SPI)">
            <MudStack Spacing="2">
                <MudSwitch T="bool" Value="@(Subject?.Mcp3008 != null)" ValueChanged="@OnMcp3008EnabledChanged" Label="Enable MCP3008" Color="Color.Primary" />

                @if (Subject?.Mcp3008 != null)
                {
                    <MudNumericField T="int" Value="@Subject.Mcp3008.ClockPin" ValueChanged="@(v => Subject.Mcp3008.ClockPin = v)" Label="Clock Pin" />
                    <MudNumericField T="int" Value="@Subject.Mcp3008.MosiPin" ValueChanged="@(v => Subject.Mcp3008.MosiPin = v)" Label="MOSI Pin" />
                    <MudNumericField T="int" Value="@Subject.Mcp3008.MisoPin" ValueChanged="@(v => Subject.Mcp3008.MisoPin = v)" Label="MISO Pin" />
                    <MudNumericField T="int" Value="@Subject.Mcp3008.ChipSelectPin" ValueChanged="@(v => Subject.Mcp3008.ChipSelectPin = v)" Label="Chip Select Pin" />
                }
            </MudStack>
        </MudExpansionPanel>

        @* ADS1115 ADC Section *@
        <MudExpansionPanel Text="ADS1115 ADC (I2C)">
            <MudStack Spacing="2">
                <MudSwitch T="bool" Value="@(Subject?.Ads1115 != null)" ValueChanged="@OnAds1115EnabledChanged" Label="Enable ADS1115" Color="Color.Primary" />

                @if (Subject?.Ads1115 != null)
                {
                    <MudNumericField T="int" Value="@Subject.Ads1115.I2cBus" ValueChanged="@(v => Subject.Ads1115.I2cBus = v)" Label="I2C Bus" />
                    <MudNumericField T="int" Value="@Subject.Ads1115.Address" ValueChanged="@(v => Subject.Ads1115.Address = v)" Label="I2C Address" />
                }
            </MudStack>
        </MudExpansionPanel>

        @* Analog Channels Section *@
        @if (Subject?.AnalogChannels.Count > 0)
        {
            <MudExpansionPanel Text="Analog Channels">
                <MudTable Items="@Subject.AnalogChannels.Values.OrderBy(c => c.ChannelNumber)" Dense="true">
                    <HeaderContent>
                        <MudTh>Channel</MudTh>
                        <MudTh>Value</MudTh>
                        <MudTh>Raw</MudTh>
                    </HeaderContent>
                    <RowTemplate>
                        <MudTd>@context.ChannelNumber</MudTd>
                        <MudTd>@context.Value.ToString("P1")</MudTd>
                        <MudTd>@context.RawValue</MudTd>
                    </RowTemplate>
                </MudTable>
            </MudExpansionPanel>
        }
    </MudExpansionPanels>
</MudStack>

@code {
    [Parameter]
    public GpioSubject? Subject { get; set; }

    private void OnModeChanged(GpioPin pin, GpioPinMode mode)
    {
        pin.Mode = mode;
    }

    private void OnValueChanged(GpioPin pin, bool value)
    {
        pin.Value = value;
    }

    private void OnMcp3008EnabledChanged(bool enabled)
    {
        if (Subject == null) return;
        Subject.Mcp3008 = enabled ? new Mcp3008Configuration() : null;
    }

    private void OnAds1115EnabledChanged(bool enabled)
    {
        if (Subject == null) return;
        Subject.Ads1115 = enabled ? new Ads1115Configuration() : null;
    }
}
```

**Step 2: Verify build**

Run: `dotnet build src/Namotion.Devices.Gpio.HomeBlaze`

Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Devices.Gpio.HomeBlaze/GpioSubjectEditComponent.razor
git commit -m "feat: add GpioSubjectEditComponent"
```

---

## Part 4: Sample Configuration

### Task 15: Add sample Gpio.json

**Files:**
- Create: `src/HomeBlaze/HomeBlaze/Data/Gpio.json`

**Step 1: Create minimal configuration**

```json
{
    "type": "Namotion.Devices.Gpio.GpioSubject"
}
```

**Step 2: Commit**

```bash
git add src/HomeBlaze/HomeBlaze/Data/Gpio.json
git commit -m "feat: add sample Gpio.json configuration"
```

---

## Part 5: Documentation

### Task 16: Create serialization documentation

**Files:**
- Create: `docs/serialization.md`

**Step 1: Create documentation**

```markdown
# Configurable Subject Serialization

## Overview

The `ConfigurableSubjectSerializer` persists subject configuration to JSON.
It uses the `[Configuration]` attribute to determine which properties are saved.

## Serialization Rules

| Type | What Gets Serialized |
|------|---------------------|
| `IInterceptorSubject` | Only properties marked with `[Configuration]` |
| Plain classes (value objects) | All public properties |

## Why Two Rules?

**Subjects** have mixed concerns:
- `[Configuration]` - persisted settings (saved)
- `[State]` - runtime values (not saved)
- `[Derived]` - computed properties (not saved)

**Value objects** referenced from `[Configuration]` properties are
entirely configuration by definition - no need to annotate every field.

## Example

```csharp
[InterceptorSubject]
public partial class GpioSubject : IConfigurableSubject
{
    [Configuration]
    public partial Mcp3008Configuration? Mcp3008 { get; set; }  // Saved

    [State]
    public partial bool IsConnected { get; set; }               // Not saved

    public partial Dictionary<int, GpioPin> Pins { get; set; }  // Not saved
}

public class Mcp3008Configuration  // Value object - all saved
{
    public int ClockPin { get; set; }
    public int MosiPin { get; set; }
    public int MisoPin { get; set; }
    public int ChipSelectPin { get; set; }
}
```

**Resulting JSON:**

```json
{
    "type": "Namotion.Devices.Gpio.GpioSubject",
    "mcp3008": {
        "clockPin": 11,
        "mosiPin": 10,
        "misoPin": 9,
        "chipSelectPin": 8
    }
}
```

## Nested Subjects

If a `[Configuration]` property contains another `IInterceptorSubject`,
the same rules apply recursively - only its `[Configuration]` properties are saved.

## Implementation

Uses `System.Text.Json` with custom `ConfigurationJsonTypeInfoResolver`:

```csharp
var options = new JsonSerializerOptions
{
    TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(),
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};
```
```

**Step 2: Commit**

```bash
git add docs/serialization.md
git commit -m "docs: add serialization documentation"
```

---

## Final: Run full test suite

**Step 1: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx`

Expected: All tests pass

**Step 2: Final commit with all changes**

```bash
git status
# Verify clean working directory
```

---

## Summary

| Part | Tasks | Description |
|------|-------|-------------|
| 1 | 1-5 | Serialization enhancement with ConfigurationJsonTypeInfoResolver |
| 2 | 6-11 | Namotion.Devices.Gpio core library |
| 3 | 12-14 | Namotion.Devices.Gpio.HomeBlaze UI components |
| 4 | 15 | Sample configuration |
| 5 | 16 | Documentation |
