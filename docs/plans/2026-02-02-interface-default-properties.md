# Interface Default Implementation Properties - Feature Plan

## Overview

Add support for interface default implementation properties to be automatically included in the generated property metadata (`DefaultProperties`), enabling full tracking, registry, and derived property handling for properties defined in interfaces with default implementations.

**Depends on:** Issue #181 bug fix (see `2026-02-02-issue-181-initializer-bug-fix.md`)

## Goals

1. Interface properties with default implementations are automatically registered in `DefaultProperties`
2. `[Derived]` properties from interfaces work with dependency tracking
3. All attributes from interface properties are preserved
4. Generator code is refactored for maintainability
5. Documentation explains supported scenarios

## Non-Goals

- Generating property implementations (C# default impl handles this)
- Supporting abstract interface properties (no implementation to call)
- Modifying explicit interface implementation behavior (C# limitation)

---

## Phase 1: Refactor Generator into Multiple Classes

**Goal:** Split the 544-line generator into focused, maintainable classes.

### Task 1.1: Create Model Classes

**File:** `src/Namotion.Interceptor.Generator/Models/SubjectMetadata.cs`

```csharp
namespace Namotion.Interceptor.Generator.Models;

/// <summary>
/// Metadata about a class marked with [InterceptorSubject].
/// </summary>
internal sealed record SubjectMetadata(
    string ClassName,
    string NamespaceName,
    string FullTypeName,
    bool HasParameterlessConstructor,
    string? BaseClassTypeName,
    bool BaseClassHasInpc,
    IReadOnlyList<PropertyMetadata> Properties,
    IReadOnlyList<MethodMetadata> Methods);
```

**File:** `src/Namotion.Interceptor.Generator/Models/PropertyMetadata.cs`

```csharp
namespace Namotion.Interceptor.Generator.Models;

/// <summary>
/// Metadata about a property to be included in DefaultProperties.
/// </summary>
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

**File:** `src/Namotion.Interceptor.Generator/Models/MethodMetadata.cs`

```csharp
namespace Namotion.Interceptor.Generator.Models;

/// <summary>
/// Metadata about an intercepted method.
/// </summary>
internal sealed record MethodMetadata(
    string Name,
    string FullMethodName,
    string ReturnType,
    IReadOnlyList<ParameterMetadata> Parameters);

internal sealed record ParameterMetadata(string Name, string Type);
```

### Task 1.2: Create SubjectMetadataExtractor

**File:** `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`

This class extracts all metadata from Roslyn symbols. Key responsibilities:
- Extract class-level info (name, namespace, base class)
- Extract properties from class declarations
- **NEW**: Extract properties from interface default implementations
- Extract intercepted methods

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator;

internal static class SubjectMetadataExtractor
{
    private const string InterceptedMethodPostfix = "WithoutInterceptor";
    private const string DerivedAttributeName = "Namotion.Interceptor.Attributes.DerivedAttribute";
    private const string InterceptorSubjectAttributeName = "Namotion.Interceptor.Attributes.InterceptorSubjectAttribute";

    public static SubjectMetadata? Extract(
        ClassDeclarationSyntax classDeclaration,
        INamedTypeSymbol typeSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var className = classDeclaration.Identifier.ValueText;
        var namespaceName = GetNamespace(classDeclaration);
        var fullTypeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Extract base class info
        var (baseClassTypeName, baseClassHasInpc) = ExtractBaseClassInfo(classDeclaration, semanticModel);

        // Extract properties from class
        var classProperties = ExtractClassProperties(typeSymbol, semanticModel, cancellationToken);
        var classPropertyNames = classProperties.Select(p => p.Name).ToHashSet();

        // Extract properties from interface default implementations
        var interfaceProperties = ExtractInterfaceDefaultProperties(typeSymbol, classPropertyNames, cancellationToken);

        // Combine all properties
        var allProperties = classProperties.Concat(interfaceProperties).ToList();

        // Extract methods
        var methods = ExtractMethods(typeSymbol, semanticModel, cancellationToken);

        // Check for parameterless constructor
        var hasParameterlessConstructor = HasParameterlessConstructor(typeSymbol, cancellationToken);

        return new SubjectMetadata(
            className,
            namespaceName,
            fullTypeName,
            hasParameterlessConstructor,
            baseClassTypeName,
            baseClassHasInpc,
            allProperties,
            methods);
    }

    private static IReadOnlyList<PropertyMetadata> ExtractInterfaceDefaultProperties(
        INamedTypeSymbol classSymbol,
        HashSet<string> classPropertyNames,
        CancellationToken cancellationToken)
    {
        var properties = new List<PropertyMetadata>();

        // Walk all implemented interfaces (including inherited)
        foreach (var iface in classSymbol.AllInterfaces)
        {
            foreach (var member in iface.GetMembers().OfType<IPropertySymbol>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip if class already declares this property
                if (classPropertyNames.Contains(member.Name))
                    continue;

                // Check if property has default implementation (not abstract)
                if (!HasDefaultImplementation(member))
                    continue;

                // Check if has [Derived] attribute
                var isDerived = member.GetAttributes()
                    .Any(a => a.AttributeClass?.ToDisplayString() == DerivedAttributeName);

                var fullTypeName = member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                properties.Add(new PropertyMetadata(
                    Name: member.Name,
                    FullTypeName: fullTypeName,
                    AccessModifier: "public", // Interface members are public
                    IsPartial: false,
                    IsVirtual: false,
                    IsOverride: false,
                    IsDerived: isDerived,
                    IsRequired: false,
                    HasGetter: member.GetMethod is not null,
                    HasSetter: member.SetMethod is not null,
                    HasInit: member.SetMethod?.IsInitOnly == true,
                    IsFromInterface: true,
                    GetterAccessModifier: null,
                    SetterAccessModifier: null));
            }
        }

        return properties;
    }

    private static bool HasDefaultImplementation(IPropertySymbol property)
    {
        // Property has default implementation if getter/setter is not abstract
        return (property.GetMethod is { IsAbstract: false }) ||
               (property.SetMethod is { IsAbstract: false });
    }

    // ... (move existing extraction methods here from InterceptorSubjectGenerator.cs)
}
```

### Task 1.3: Create SubjectCodeEmitter

**File:** `src/Namotion.Interceptor.Generator/SubjectCodeEmitter.cs`

This class generates code from metadata. No Roslyn dependencies - just string building from data.

```csharp
using System.Text;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator;

internal static class SubjectCodeEmitter
{
    public static string Emit(SubjectMetadata metadata)
    {
        var sb = new StringBuilder();

        EmitHeader(sb);
        EmitNamespaceAndClassStart(sb, metadata);
        EmitNotifyPropertyChanged(sb, metadata);
        EmitContextAndProperties(sb, metadata);
        EmitDefaultProperties(sb, metadata);
        EmitConstructors(sb, metadata);
        EmitPartialProperties(sb, metadata);
        EmitMethods(sb, metadata);
        EmitHelperMethods(sb, metadata);
        EmitClassEnd(sb);

        return sb.ToString();
    }

    private static void EmitDefaultProperties(StringBuilder sb, SubjectMetadata metadata)
    {
        var defaultPropertiesNewModifier = metadata.BaseClassTypeName is not null ? "new " : "";

        sb.AppendLine($"        public {defaultPropertiesNewModifier}static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties {{ get; }} =");
        sb.AppendLine("            new Dictionary<string, SubjectPropertyMetadata>");
        sb.AppendLine("            {");

        foreach (var property in metadata.Properties)
        {
            EmitPropertyMetadataEntry(sb, metadata.ClassName, property);
        }

        sb.AppendLine("            }");

        if (metadata.BaseClassTypeName is not null)
        {
            sb.AppendLine($"            .Concat({metadata.BaseClassTypeName}.DefaultProperties)");
        }

        sb.AppendLine("            .ToFrozenDictionary();");
    }

    private static void EmitPropertyMetadataEntry(StringBuilder sb, string className, PropertyMetadata property)
    {
        // Interface properties use reflection to get property info from the interface
        // Class properties use the class type
        var getterCode = property.HasGetter
            ? $"(o) => (({className})o).{property.Name}"
            : "null";
        var setterCode = property.HasSetter && !property.IsFromInterface
            ? $"(o, v) => (({className})o).{property.Name} = ({property.FullTypeName})v"
            : "null";

        // Interface default impl properties are NOT intercepted (no backing field)
        // but they ARE tracked in the metadata
        var isIntercepted = property.IsPartial && !property.IsFromInterface;

        sb.AppendLine($@"                {{
                    ""{property.Name}"",
                    new SubjectPropertyMetadata(
                        typeof({className}).GetProperty(nameof({property.Name}), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!,
                        {getterCode},
                        {setterCode},
                        isIntercepted: {(isIntercepted ? "true" : "false")},
                        isDynamic: false)
                }},");
    }

    // ... (move existing emit methods here)
}
```

### Task 1.4: Update InterceptorSubjectGenerator

**File:** `src/Namotion.Interceptor.Generator/InterceptorSubjectGenerator.cs`

Simplify to just pipeline orchestration:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace Namotion.Interceptor.Generator;

[Generator]
public class InterceptorSubjectGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var provider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: TransformToMetadata)
            .Where(m => m is not null)
            .Collect()
            .SelectMany((items, _) => items
                .GroupBy(x => x!.FullTypeName)
                .Select(g => g.First()));

        context.RegisterSourceOutput(provider, GenerateSource);
    }

    private static SubjectMetadata? TransformToMetadata(
        GeneratorSyntaxContext ctx,
        CancellationToken ct)
    {
        var classDeclaration = (ClassDeclarationSyntax)ctx.Node;
        var typeSymbol = ctx.SemanticModel.GetDeclaredSymbol(classDeclaration, ct);

        if (typeSymbol is null || !HasInterceptorSubjectAttribute(typeSymbol))
            return null;

        return SubjectMetadataExtractor.Extract(
            classDeclaration,
            typeSymbol,
            ctx.SemanticModel,
            ct);
    }

    private static void GenerateSource(
        SourceProductionContext spc,
        SubjectMetadata? metadata)
    {
        if (metadata is null) return;

        var fileName = $"{metadata.NamespaceName}.{metadata.ClassName}.g.cs";

        try
        {
            var code = SubjectCodeEmitter.Emit(metadata);
            spc.AddSource(fileName, SourceText.From(code, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            spc.AddSource(fileName, SourceText.From($"/* {ex} */", Encoding.UTF8));
        }
    }

    private static bool HasInterceptorSubjectAttribute(INamedTypeSymbol type)
    {
        return type.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() ==
                "Namotion.Interceptor.Attributes.InterceptorSubjectAttribute");
    }
}
```

### Task 1.5: Add Snapshot Tests for Refactoring

Verify no behavior change by running existing snapshot tests and adding new ones.

**Verification:** Run all generator tests - snapshots should match exactly.

---

## Phase 2: Add Interface Default Implementation Support

**Goal:** Interface properties with default implementations appear in `DefaultProperties`.

### Task 2.1: Create Test Models with Interface Default Implementations

**File:** `src/Namotion.Interceptor.Generator.Tests/Models/IPersonInterface.cs`

```csharp
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

public interface IPersonInterface
{
    string FirstName { get; set; }
    string LastName { get; set; }

    [Derived]
    string FullName => $"{FirstName} {LastName}";

    string Greeting => $"Hello, {FirstName}!";
}
```

**File:** `src/Namotion.Interceptor.Generator.Tests/Models/PersonWithInterfaceDefaults.cs`

```csharp
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class PersonWithInterfaceDefaults : IPersonInterface
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }

    // FullName and Greeting come from interface default implementation

    public PersonWithInterfaceDefaults()
    {
        FirstName = string.Empty;
        LastName = string.Empty;
    }
}
```

### Task 2.2: Create Generator Unit Tests

**File:** `src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs`

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

public interface IGreetable
{
    string Name { get; set; }
    string Greeting => $""Hello, {Name}!"";
}

[InterceptorSubject]
public partial class Greeter : IGreetable
{
    public partial string Name { get; set; }
}";

        var generated = GenerateCode(source);
        var generatedSource = generated.Single().SourceText.ToString();

        // Assert Greeting is in DefaultProperties
        Assert.Contains(@"""Greeting""", generatedSource);
        return Verify(generatedSource).UseDirectory("Snapshots");
    }

    [Fact]
    public Task InterfaceDerivedProperty_IncludedInDefaultProperties()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface IPerson
{
    string FirstName { get; set; }
    string LastName { get; set; }

    [Derived]
    string FullName => $""{FirstName} {LastName}"";
}

[InterceptorSubject]
public partial class Person : IPerson
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }
}";

        var generated = GenerateCode(source);
        var generatedSource = generated.Single().SourceText.ToString();

        Assert.Contains(@"""FullName""", generatedSource);
        return Verify(generatedSource).UseDirectory("Snapshots");
    }

    [Fact]
    public Task InterfaceHierarchy_AllPropertiesIncluded()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface INameable
{
    string DisplayName => ""Unknown"";
}

public interface IPerson : INameable
{
    string FirstName { get; set; }
    string FullName => FirstName;
}

[InterceptorSubject]
public partial class Person : IPerson
{
    public partial string FirstName { get; set; }
}";

        var generated = GenerateCode(source);
        var generatedSource = generated.Single().SourceText.ToString();

        // Both DisplayName and FullName should be included
        Assert.Contains(@"""DisplayName""", generatedSource);
        Assert.Contains(@"""FullName""", generatedSource);
        return Verify(generatedSource).UseDirectory("Snapshots");
    }

    [Fact]
    public Task ClassOverridesInterfaceProperty_ClassWins()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface IPerson
{
    string Name => ""Default"";
}

[InterceptorSubject]
public partial class Person : IPerson
{
    // Class overrides interface default
    public partial string Name { get; set; }
}";

        var generated = GenerateCode(source);
        var generatedSource = generated.Single().SourceText.ToString();

        // Name should be intercepted (from class), not from interface
        Assert.Contains("isIntercepted: true", generatedSource);
        return Verify(generatedSource).UseDirectory("Snapshots");
    }

    [Fact]
    public Task MultipleInterfaces_AllPropertiesIncluded()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface IHasFirstName
{
    string FirstName { get; set; }
    string FirstGreeting => $""Hi {FirstName}"";
}

public interface IHasLastName
{
    string LastName { get; set; }
    string LastGreeting => $""Hi {LastName}"";
}

[InterceptorSubject]
public partial class Person : IHasFirstName, IHasLastName
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }
}";

        var generated = GenerateCode(source);
        var generatedSource = generated.Single().SourceText.ToString();

        Assert.Contains(@"""FirstGreeting""", generatedSource);
        Assert.Contains(@"""LastGreeting""", generatedSource);
        return Verify(generatedSource).UseDirectory("Snapshots");
    }

    [Fact]
    public void DiamondInheritance_HandledGracefully()
    {
        // C# resolves diamond inheritance for default implementations
        // We just need to not crash and pick one
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

        // Should not throw
        var generated = GenerateCode(source);
        Assert.NotEmpty(generated);
    }

    [Fact]
    public Task InterfacePropertyWithAttributes_AttributesPreserved()
    {
        const string source = @"
using Namotion.Interceptor.Attributes;
using System.ComponentModel.DataAnnotations;

public interface IPerson
{
    string FirstName { get; set; }

    [Derived]
    string FullName => FirstName;
}

[InterceptorSubject]
public partial class Person : IPerson
{
    public partial string FirstName { get; set; }
}";

        var generated = GenerateCode(source);
        var generatedSource = generated.Single().SourceText.ToString();

        return Verify(generatedSource).UseDirectory("Snapshots");
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

### Task 2.3: Create Behavioral Tests

**File:** `src/Namotion.Interceptor.Registry.Tests/InterfaceDefaultPropertyBehaviorTests.cs`

```csharp
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Registry.Tests;

public class InterfaceDefaultPropertyBehaviorTests
{
    [Fact]
    public void InterfaceDefaultProperty_AppearsInRegistry()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var person = new PersonWithInterfaceDefaults(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        // Act
        var registered = person.TryGetRegisteredSubject();
        var fullNameProperty = registered?.TryGetProperty("FullName");

        // Assert
        Assert.NotNull(fullNameProperty);
        Assert.Equal("John Doe", fullNameProperty.GetValue());
    }

    [Fact]
    public void InterfaceDerivedProperty_TracksChanges()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeObservable()
            .WithDerivedPropertyChangeDetection()
            .WithRegistry();

        var person = new PersonWithInterfaceDefaults(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == "FullName")
            .Subscribe(changes.Add);

        // Act
        person.FirstName = "Jane";

        // Assert
        Assert.Contains(changes, c => c.GetNewValue<string>() == "Jane Doe");
    }

    [Fact]
    public void InterfaceDefaultProperty_InitializersAreCalled()
    {
        // Arrange
        var initializedProperties = new List<string>();
        var initializer = new TestPropertyInitializer(p => initializedProperties.Add(p.Name));

        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService<ISubjectPropertyInitializer>(initializer);

        // Act
        var person = new PersonWithInterfaceDefaults(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        // Assert
        Assert.Contains("FullName", initializedProperties);
        Assert.Contains("Greeting", initializedProperties);
    }

    private class TestPropertyInitializer : ISubjectPropertyInitializer
    {
        private readonly Action<RegisteredSubjectProperty> _onInitialize;

        public TestPropertyInitializer(Action<RegisteredSubjectProperty> onInitialize)
            => _onInitialize = onInitialize;

        public void InitializeProperty(RegisteredSubjectProperty property)
            => _onInitialize(property);
    }
}
```

---

## Phase 3: Create Documentation

**Goal:** Document what the generator supports and how it works.

### Task 3.1: Create docs/generator.md

**File:** `docs/generator.md`

```markdown
# Source Generator Reference

The Namotion.Interceptor source generator automatically creates interception logic for classes marked with `[InterceptorSubject]`.

## What Triggers Generation

A class is processed when:
1. It has the `[InterceptorSubject]` attribute (on any partial declaration)
2. It is declared as `partial`

## What Gets Generated

For each `[InterceptorSubject]` class, the generator creates:

### 1. IInterceptorSubject Implementation

- Context field and property
- Data dictionary for metadata storage
- Properties dictionary accessor
- `AddProperties()` method for dynamic property registration

### 2. INotifyPropertyChanged Implementation

- `PropertyChanged` event
- `RaisePropertyChanged()` method
- Inherited from base class if base has `[InterceptorSubject]`

### 3. Static Property Metadata (DefaultProperties)

A frozen dictionary containing metadata for all properties:
- Properties declared as `partial` in the class
- Properties with default implementations from interfaces

### 4. Partial Property Implementations

For each `partial` property:
- Private backing field (`_PropertyName`)
- Getter calling `GetPropertyValue<T>()`
- Setter calling `SetPropertyValue<T>()`
- Partial method hooks (`OnPropertyNameChanging`, `OnPropertyNameChanged`)

### 5. Constructors

- Parameterless constructor (if not defined)
- Context constructor: `ClassName(IInterceptorSubjectContext context)`

### 6. Intercepted Methods

For methods ending with `WithoutInterceptor`:
- Public wrapper method without the postfix
- Calls through `InvokeMethod()` for interception

## Supported Patterns

### Partial Properties

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string Name { get; set; }           // Standard
    public virtual partial string Title { get; set; } // Virtual
    public override partial string Title { get; set; } // Override
    public required partial string Id { get; set; }   // Required
    public partial string Code { get; init; }         // Init-only
    protected partial int Age { get; set; }           // Non-public
}
```

### Interface Default Implementations

Properties with default implementations in interfaces are automatically included in `DefaultProperties`:

```csharp
public interface IPerson
{
    string FirstName { get; set; }
    string LastName { get; set; }

    [Derived]
    string FullName => $"{FirstName} {LastName}";  // Included in DefaultProperties

    string Greeting => $"Hello!";  // Also included
}

[InterceptorSubject]
public partial class Person : IPerson
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }
    // FullName and Greeting automatically tracked
}
```

Interface properties:
- Are included in `DefaultProperties` with getters
- Support `[Derived]` attribute for dependency tracking
- Work with full interface hierarchy (inherited interfaces)
- Are overridden by class declarations (class wins)

### Inheritance

```csharp
[InterceptorSubject]
public partial class Animal
{
    public virtual partial string Name { get; set; }
}

[InterceptorSubject]
public partial class Dog : Animal
{
    public override partial string Name { get; set; }
    public partial string Breed { get; set; }
}
```

### Derived Properties

```csharp
[InterceptorSubject]
public partial class Rectangle
{
    public partial double Width { get; set; }
    public partial double Height { get; set; }

    [Derived]
    public double Area => Width * Height;
}
```

## Limitations

### Not Supported

| Pattern | Reason | Alternative |
|---------|--------|-------------|
| Explicit interface implementation | C# doesn't allow `partial` on explicit impl | Use implicit implementation |
| Abstract properties | Can't be partial | Use virtual |
| Field initializers on partial properties | C# limitation | Initialize in constructor |

### Interface Property Limitations

- Interface properties are read-only in the tracking system (no setter delegation)
- Abstract interface properties (without default impl) are not included
- Explicit interface implementations cannot be partial

## Troubleshooting

### Property not appearing in DefaultProperties

1. Check if property is `partial` (class properties) or has default implementation (interface properties)
2. Check if class has `[InterceptorSubject]` attribute
3. Check if class overrides interface property (class takes precedence)

### Derived property not tracking changes

1. Ensure `WithDerivedPropertyChangeDetection()` is configured on context
2. Check if `[Derived]` attribute is on the property
3. For interface properties, ensure the interface property has `[Derived]`
```

### Task 3.2: Update subject-guidelines.md

Add a section about interface default implementations to the existing guidelines document.

---

## Phase 4: Final Verification

### Task 4.1: Run All Tests

```bash
dotnet test src/Namotion.Interceptor.slnx
```

### Task 4.2: Update Snapshots (if needed)

Review any snapshot changes to ensure they're expected.

### Task 4.3: Build and Verify Samples

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet run --project src/Namotion.Interceptor.SampleConsole
```

---

## Summary

| Phase | Tasks | Focus |
|-------|-------|-------|
| 1. Refactor | Split generator into 3 files + models | Maintainability |
| 2. Interface Support | Add interface property extraction + tests | New feature |
| 3. Documentation | Create docs/generator.md | User guidance |
| 4. Verification | Run all tests, verify samples | Quality |

## Files to Create/Modify

**New Files:**
- `src/Namotion.Interceptor.Generator/Models/SubjectMetadata.cs`
- `src/Namotion.Interceptor.Generator/Models/PropertyMetadata.cs`
- `src/Namotion.Interceptor.Generator/Models/MethodMetadata.cs`
- `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`
- `src/Namotion.Interceptor.Generator/SubjectCodeEmitter.cs`
- `src/Namotion.Interceptor.Generator.Tests/InterfaceDefaultPropertyTests.cs`
- `src/Namotion.Interceptor.Generator.Tests/Models/IPersonInterface.cs`
- `src/Namotion.Interceptor.Generator.Tests/Models/PersonWithInterfaceDefaults.cs`
- `src/Namotion.Interceptor.Registry.Tests/InterfaceDefaultPropertyBehaviorTests.cs`
- `docs/generator.md`

**Modified Files:**
- `src/Namotion.Interceptor.Generator/InterceptorSubjectGenerator.cs` (simplified)
- `docs/subject-guidelines.md` (add interface section)
