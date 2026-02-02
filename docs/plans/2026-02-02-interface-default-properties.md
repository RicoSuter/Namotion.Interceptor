# Interface Default Implementation Properties - Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add support for interface default implementation properties to be automatically included in `DefaultProperties`, enabling tracking and derived property handling.

**Architecture:** Refactor the ~600-line generator into separate metadata extraction and code emission classes, then add interface property detection. Interface properties with default implementations (expression-bodied or with bodies) will be included in `DefaultProperties` with getter delegates.

**Tech Stack:** C# 13, Roslyn Source Generators, xUnit, Verify (snapshot testing)

---

## PR Summary

This PR adds support for C# interface default implementation properties in the source generator:

- **Refactors** the generator into separate metadata extraction and code emission classes for maintainability
- **Adds** automatic detection of interface properties with default implementations
- **Includes** interface default properties in `DefaultProperties` with proper getter delegates
- **Supports** `[Derived]` attribute on interface properties for dependency tracking
- **Handles** interface hierarchies (inherited interfaces) and multiple interface implementations

### Example

```csharp
public interface ITemperatureSensor
{
    double TemperatureCelsius { get; set; }

    [Derived]
    double TemperatureFahrenheit => TemperatureCelsius * 9 / 5 + 32;

    [Derived]
    bool IsFreezing => TemperatureCelsius <= 0;
}

[InterceptorSubject]
public partial class TemperatureSensor : ITemperatureSensor
{
    public partial double TemperatureCelsius { get; set; }
    // TemperatureFahrenheit and IsFreezing automatically appear in DefaultProperties
}
```

---

## Phase 1: Refactor Generator into Multiple Classes

**Goal:** Split the generator into focused, maintainable classes without changing behavior.

### Task 1: Create Model Records

**Files:**
- Create: `src/Namotion.Interceptor.Generator/Models/SubjectMetadata.cs`
- Create: `src/Namotion.Interceptor.Generator/Models/PropertyMetadata.cs`
- Create: `src/Namotion.Interceptor.Generator/Models/MethodMetadata.cs`

**Step 1: Create the Models directory and SubjectMetadata.cs**

```csharp
namespace Namotion.Interceptor.Generator.Models;

internal sealed record SubjectMetadata(
    string ClassName,
    string NamespaceName,
    string FullTypeName,
    string[] ContainingTypes,
    bool HasParameterlessConstructor,
    string? BaseClassTypeName,
    bool BaseClassHasInpc,
    IReadOnlyList<PropertyMetadata> Properties,
    IReadOnlyList<MethodMetadata> Methods);
```

**Step 2: Create PropertyMetadata.cs**

```csharp
namespace Namotion.Interceptor.Generator.Models;

internal sealed record PropertyMetadata(
    string Name,
    string FullTypeName,
    string AccessModifier,
    bool IsPartial,
    bool IsVirtual,
    bool IsOverride,
    bool IsDerived,
    bool IsRequired,
    bool HasGetter,
    bool HasSetter,
    bool HasInit,
    bool IsFromInterface,
    string? GetterAccessModifier,
    string? SetterAccessModifier);
```

**Step 3: Create MethodMetadata.cs**

```csharp
namespace Namotion.Interceptor.Generator.Models;

internal sealed record MethodMetadata(
    string Name,
    string FullMethodName,
    string ReturnType,
    IReadOnlyList<ParameterMetadata> Parameters);

internal sealed record ParameterMetadata(string Name, string Type);
```

**Step 4: Build to verify no syntax errors**

Run: `dotnet build src/Namotion.Interceptor.Generator`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Generator/Models/
git commit -m "refactor: Add model records for generator metadata"
```

---

### Task 2: Run Existing Tests as Baseline

**Step 1: Run all generator tests to establish baseline**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests`
Expected: All tests pass (save output for comparison)

---

### Task 3: Create SubjectMetadataExtractor

**Files:**
- Create: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`
- Reference: `src/Namotion.Interceptor.Generator/InterceptorSubjectGenerator.cs` (extract from)

**Step 1: Create SubjectMetadataExtractor with class extraction logic**

Extract the metadata gathering logic from `InterceptorSubjectGenerator.cs` into a new static class. This includes:
- Namespace extraction
- Base class detection
- Property collection from class declarations
- Method collection
- Constructor detection

The extractor should return a `SubjectMetadata` record.

**Step 2: Build to verify compilation**

Run: `dotnet build src/Namotion.Interceptor.Generator`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs
git commit -m "refactor: Extract metadata gathering into SubjectMetadataExtractor"
```

---

### Task 4: Create SubjectCodeEmitter

**Files:**
- Create: `src/Namotion.Interceptor.Generator/SubjectCodeEmitter.cs`
- Reference: `src/Namotion.Interceptor.Generator/InterceptorSubjectGenerator.cs` (extract from)

**Step 1: Create SubjectCodeEmitter with code generation logic**

Extract the code generation logic from `InterceptorSubjectGenerator.cs` into a new static class. This class takes a `SubjectMetadata` and returns the generated C# code string.

**Step 2: Build to verify compilation**

Run: `dotnet build src/Namotion.Interceptor.Generator`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Generator/SubjectCodeEmitter.cs
git commit -m "refactor: Extract code generation into SubjectCodeEmitter"
```

---

### Task 5: Update InterceptorSubjectGenerator to Use New Classes

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/InterceptorSubjectGenerator.cs`

**Step 1: Simplify InterceptorSubjectGenerator**

Update the generator to:
1. Use `SubjectMetadataExtractor.Extract()` to gather metadata
2. Use `SubjectCodeEmitter.Emit()` to generate code
3. Remove the extracted logic (now in separate classes)

**Step 2: Build to verify compilation**

Run: `dotnet build src/Namotion.Interceptor.Generator`
Expected: Build succeeded

**Step 3: Run all generator tests**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests`
Expected: All tests pass (same as baseline)

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Generator/InterceptorSubjectGenerator.cs
git commit -m "refactor: Simplify generator to use extractor and emitter"
```

---

## Phase 2: Add Interface Default Implementation Support

**Goal:** Interface properties with default implementations appear in `DefaultProperties`.

### Task 6: Write Failing Test for Basic Interface Default Property

**Files:**
- Create: `src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs`

**Step 1: Create test file with first test**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Namotion.Interceptor.Generator.Tests;

public class InterfaceDefaultPropertyTests
{
    [Fact]
    public Task InterfaceDefaultProperty_IncludedInDefaultProperties()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface ISensor
{
    double Value { get; set; }
    string Status => Value > 0 ? ""Active"" : ""Inactive"";
}

[InterceptorSubject]
public partial class Sensor : ISensor
{
    public partial double Value { get; set; }
}";

        var generated = GenerateCode(source);
        var generatedSource = generated.Single().SourceText.ToString();

        Assert.Contains(@"""Status""", generatedSource);
        return Verify(generatedSource);
    }

    private static IEnumerable<GeneratedSourceResult> GenerateCode(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new InterceptorSubjectGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        return driver.GetRunResult().Results.SelectMany(r => r.GeneratedSources);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter InterfaceDefaultProperty_IncludedInDefaultProperties`
Expected: FAIL - "Status" not found in generated source

**Step 3: Commit failing test**

```bash
git add src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs
git commit -m "test: Add failing test for interface default property support"
```

---

### Task 7: Implement Interface Default Property Extraction

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`

**Step 1: Add ExtractInterfaceDefaultProperties method**

Add logic to:
1. Iterate through `typeSymbol.AllInterfaces`
2. For each interface, get properties with default implementations
3. Skip properties already declared in the class
4. Create `PropertyMetadata` with `IsFromInterface = true`

Key detection: A property has a default implementation if `property.GetMethod?.IsAbstract == false` or `property.SetMethod?.IsAbstract == false`.

**Step 2: Update Extract method to include interface properties**

Combine class properties with interface default properties.

**Step 3: Build to verify compilation**

Run: `dotnet build src/Namotion.Interceptor.Generator`
Expected: Build succeeded

---

### Task 8: Update Code Emitter for Interface Properties

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeEmitter.cs`

**Step 1: Update EmitPropertyMetadataEntry for interface properties**

For interface properties:
- `isIntercepted: false` (no backing field)
- Getter delegates to the interface default implementation
- Setter is null (interface default properties are typically read-only)

**Step 2: Run the failing test**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter InterfaceDefaultProperty_IncludedInDefaultProperties`
Expected: PASS

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Generator/
git commit -m "feat: Add interface default property extraction and emission"
```

---

### Task 9: Add Test for Derived Attribute on Interface Property

**Files:**
- Modify: `src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs`

**Step 1: Add test for [Derived] attribute**

```csharp
[Fact]
public Task InterfaceDerivedProperty_IncludedInDefaultProperties()
{
    const string source = @"
using Namotion.Interceptor.Attributes;

public interface ITemperatureSensor
{
    double Celsius { get; set; }

    [Derived]
    double Fahrenheit => Celsius * 9 / 5 + 32;
}

[InterceptorSubject]
public partial class TemperatureSensor : ITemperatureSensor
{
    public partial double Celsius { get; set; }
}";

    var generated = GenerateCode(source);
    var generatedSource = generated.Single().SourceText.ToString();

    Assert.Contains(@"""Fahrenheit""", generatedSource);
    return Verify(generatedSource);
}
```

**Step 2: Run test**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter InterfaceDerivedProperty_IncludedInDefaultProperties`
Expected: PASS (should work with existing implementation)

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs
git commit -m "test: Add test for [Derived] attribute on interface properties"
```

---

### Task 10: Add Test for Interface Hierarchy

**Files:**
- Modify: `src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs`

**Step 1: Add test for inherited interfaces**

```csharp
[Fact]
public Task InterfaceHierarchy_AllDefaultPropertiesIncluded()
{
    const string source = @"
using Namotion.Interceptor.Attributes;

public interface IBase
{
    string BaseStatus => ""Base"";
}

public interface IDerived : IBase
{
    double Value { get; set; }
    string DerivedStatus => ""Derived"";
}

[InterceptorSubject]
public partial class Implementation : IDerived
{
    public partial double Value { get; set; }
}";

    var generated = GenerateCode(source);
    var generatedSource = generated.Single().SourceText.ToString();

    Assert.Contains(@"""BaseStatus""", generatedSource);
    Assert.Contains(@"""DerivedStatus""", generatedSource);
    return Verify(generatedSource);
}
```

**Step 2: Run test**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter InterfaceHierarchy_AllDefaultPropertiesIncluded`
Expected: PASS (AllInterfaces includes inherited)

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs
git commit -m "test: Add test for interface hierarchy support"
```

---

### Task 11: Add Test for Class Overriding Interface Property

**Files:**
- Modify: `src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs`

**Step 1: Add test for class override**

```csharp
[Fact]
public Task ClassOverridesInterfaceProperty_ClassWins()
{
    const string source = @"
using Namotion.Interceptor.Attributes;

public interface ISensor
{
    string Name => ""DefaultName"";
}

[InterceptorSubject]
public partial class Sensor : ISensor
{
    public partial string Name { get; set; }
}";

    var generated = GenerateCode(source);
    var generatedSource = generated.Single().SourceText.ToString();

    // Name should be intercepted (from class), not from interface
    Assert.Contains("isIntercepted: true", generatedSource);
    return Verify(generatedSource);
}
```

**Step 2: Run test**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter ClassOverridesInterfaceProperty_ClassWins`
Expected: PASS (class properties collected first, interface skips existing)

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs
git commit -m "test: Add test for class overriding interface property"
```

---

### Task 12: Add Test for Multiple Interfaces

**Files:**
- Modify: `src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs`

**Step 1: Add test for multiple interfaces**

```csharp
[Fact]
public Task MultipleInterfaces_AllDefaultPropertiesIncluded()
{
    const string source = @"
using Namotion.Interceptor.Attributes;

public interface IHasTemperature
{
    double Temperature { get; set; }
    bool IsHot => Temperature > 30;
}

public interface IHasHumidity
{
    double Humidity { get; set; }
    bool IsHumid => Humidity > 70;
}

[InterceptorSubject]
public partial class WeatherStation : IHasTemperature, IHasHumidity
{
    public partial double Temperature { get; set; }
    public partial double Humidity { get; set; }
}";

    var generated = GenerateCode(source);
    var generatedSource = generated.Single().SourceText.ToString();

    Assert.Contains(@"""IsHot""", generatedSource);
    Assert.Contains(@"""IsHumid""", generatedSource);
    return Verify(generatedSource);
}
```

**Step 2: Run test**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter MultipleInterfaces_AllDefaultPropertiesIncluded`
Expected: PASS

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs
git commit -m "test: Add test for multiple interface support"
```

---

### Task 13: Add Diamond Inheritance Test

**Files:**
- Modify: `src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs`

**Step 1: Add diamond inheritance test**

```csharp
[Fact]
public void DiamondInheritance_HandledGracefully()
{
    const string source = @"
using Namotion.Interceptor.Attributes;

public interface IBase
{
    string Shared => ""Base"";
}

public interface IA : IBase { }
public interface IB : IBase { }

[InterceptorSubject]
public partial class Diamond : IA, IB
{
}";

    // Should not throw, and should include Shared once
    var generated = GenerateCode(source);
    var generatedSource = generated.Single().SourceText.ToString();

    // Count occurrences of "Shared" in DefaultProperties
    var count = System.Text.RegularExpressions.Regex.Matches(
        generatedSource, @"""Shared""").Count;
    Assert.Equal(1, count);
}
```

**Step 2: Run test**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter DiamondInheritance_HandledGracefully`
Expected: PASS (deduplicate by property name)

If failing, update `ExtractInterfaceDefaultProperties` to track seen property names across all interfaces.

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs
git commit -m "test: Add diamond inheritance handling test"
```

---

### Task 14: Create Behavioral Test Models

**Files:**
- Create: `src/Namotion.Interceptor.Generator.Tests/Models/ITemperatureSensorInterface.cs`
- Create: `src/Namotion.Interceptor.Generator.Tests/Models/SensorWithInterfaceDefaults.cs`

**Step 1: Create interface**

```csharp
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

public interface ITemperatureSensorInterface
{
    double TemperatureCelsius { get; set; }

    [Derived]
    double TemperatureFahrenheit => TemperatureCelsius * 9 / 5 + 32;

    bool IsFreezing => TemperatureCelsius <= 0;
}
```

**Step 2: Create implementing class**

```csharp
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class SensorWithInterfaceDefaults : ITemperatureSensorInterface
{
    public partial double TemperatureCelsius { get; set; }
}
```

**Step 3: Build to verify generation works**

Run: `dotnet build src/Namotion.Interceptor.Generator.Tests`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Generator.Tests/Models/
git commit -m "test: Add test models for interface default properties"
```

---

### Task 15: Add Behavioral Tests

**Files:**
- Create: `src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyBehaviorTests.cs`

**Step 1: Create behavioral test file**

```csharp
using Namotion.Interceptor.Generator.Tests.Models;

namespace Namotion.Interceptor.Generator.Tests;

public class InterfaceDefaultPropertyBehaviorTests
{
    [Fact]
    public void InterfaceDefaultProperty_AppearsInDefaultProperties()
    {
        // Arrange & Act
        var properties = SensorWithInterfaceDefaults.DefaultProperties;

        // Assert
        Assert.True(properties.ContainsKey("TemperatureFahrenheit"));
        Assert.True(properties.ContainsKey("IsFreezing"));
    }

    [Fact]
    public void InterfaceDefaultProperty_GetterWorks()
    {
        // Arrange
        var sensor = new SensorWithInterfaceDefaults { TemperatureCelsius = 25.0 };
        var properties = SensorWithInterfaceDefaults.DefaultProperties;

        // Act
        var fahrenheitProp = properties["TemperatureFahrenheit"];
        var value = fahrenheitProp.Getter!(sensor);

        // Assert
        Assert.Equal(77.0, value);
    }

    [Fact]
    public void InterfaceDefaultProperty_IsNotIntercepted()
    {
        // Arrange & Act
        var fahrenheitProp = SensorWithInterfaceDefaults.DefaultProperties["TemperatureFahrenheit"];

        // Assert
        Assert.False(fahrenheitProp.IsIntercepted);
    }

    [Fact]
    public void InterfaceDefaultProperty_SetterIsNull()
    {
        // Arrange & Act
        var fahrenheitProp = SensorWithInterfaceDefaults.DefaultProperties["TemperatureFahrenheit"];

        // Assert
        Assert.Null(fahrenheitProp.Setter);
    }
}
```

**Step 2: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Generator.Tests --filter InterfaceDefaultPropertyBehaviorTests`
Expected: All pass

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyBehaviorTests.cs
git commit -m "test: Add behavioral tests for interface default properties"
```

---

## Phase 3: Final Verification

### Task 16: Run All Tests

**Step 1: Run complete test suite**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All tests pass

**Step 2: Update any failing snapshots if expected**

If snapshot tests fail due to expected changes, review and accept:
Run: Review `.received.` files and rename to `.verified.` if correct

---

### Task 17: Build and Verify Samples

**Step 1: Build entire solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded

**Step 2: Run console sample**

Run: `dotnet run --project src/Namotion.Interceptor.SampleConsole`
Expected: Runs without errors

---

### Task 18: Final Commit

**Step 1: Review all changes**

Run: `git status && git diff --stat`

**Step 2: Create final commit if any uncommitted changes**

```bash
git add -A
git commit -m "feat: Complete interface default property support"
```

---

## Summary

| Phase | Tasks | Focus |
|-------|-------|-------|
| 1. Refactor | Tasks 1-5 | Split generator into models, extractor, emitter |
| 2. Feature | Tasks 6-15 | Add interface property support with TDD |
| 3. Verify | Tasks 16-18 | Run all tests, verify samples |

## Files to Create/Modify

**New Files:**
- `src/Namotion.Interceptor.Generator/Models/SubjectMetadata.cs`
- `src/Namotion.Interceptor.Generator/Models/PropertyMetadata.cs`
- `src/Namotion.Interceptor.Generator/Models/MethodMetadata.cs`
- `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`
- `src/Namotion.Interceptor.Generator/SubjectCodeEmitter.cs`
- `src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs`
- `src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyBehaviorTests.cs`
- `src/Namotion.Interceptor.Generator.Tests/Models/ITemperatureSensorInterface.cs`
- `src/Namotion.Interceptor.Generator.Tests/Models/SensorWithInterfaceDefaults.cs`

**Modified Files:**
- `src/Namotion.Interceptor.Generator/InterceptorSubjectGenerator.cs` (simplified)
