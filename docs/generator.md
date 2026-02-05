# Source Generator

The `Namotion.Interceptor.Generator` package is a C# 13 source generator that transforms classes marked with `[InterceptorSubject]` into fully trackable interceptor subjects. All interception logic is generated at compile-time, resulting in zero runtime reflection overhead.

## Getting Started

Add both packages to your project:

```xml
<ItemGroup>
    <PackageReference Include="Namotion.Interceptor" Version="*" />
    <PackageReference Include="Namotion.Interceptor.Generator" Version="*"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
```

Mark your class with `[InterceptorSubject]` and declare properties as `partial`:

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
}
```

See the [Subject Design Guidelines](subject-guidelines.md) for detailed patterns, best practices, and examples.

## What Gets Generated

For each `[InterceptorSubject]` class, the generator creates a partial class implementation with:

### Interface Implementations

- `IInterceptorSubject` - Core interception infrastructure
- `INotifyPropertyChanged` - Property change notifications (if not inherited from base class)
- `IRaisePropertyChanged` - Internal interface for raising change events

### Constructors

If no constructors exist, the generator creates:

```csharp
public Person() { }

public Person(IInterceptorSubjectContext context) : this()
{
    ((IInterceptorSubject)this).Context.AddFallbackContext(context);
}
```

If a parameterless constructor already exists, only the context constructor is generated.

### Property Implementations

For each partial property, the generator creates:

- A backing field (`_PropertyName`)
- Getter that routes through the interception pipeline
- Setter that routes through the interception pipeline, with hooks and change notification
- Partial method hooks for customization

### Static Metadata

A `DefaultProperties` dictionary containing metadata for all properties:

```csharp
public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties { get; }
```

> **Note:** `DefaultProperties` is used internally by other Namotion.Interceptor libraries and may change in future versions. For runtime property discovery, use `IInterceptorSubject.Properties` on the instance instead.

## Supported Features

### Partial Properties

Only properties marked with the `partial` keyword are intercepted:

```csharp
[InterceptorSubject]
public partial class Sensor
{
    public partial double Temperature { get; set; }  // Intercepted
    public string TransientData { get; set; }        // Not intercepted
}
```

### Property Hooks

The generator creates partial method hooks for each property with a setter:

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string Name { get; set; }

    // Called before the value is written - can cancel or modify the value
    partial void OnNameChanging(ref string newValue, ref bool cancel)
    {
        if (string.IsNullOrWhiteSpace(newValue))
        {
            cancel = true;  // Reject the change
            return;
        }
        newValue = newValue.Trim();  // Coerce the value
    }

    // Called after the value is written successfully
    partial void OnNameChanged(string newValue)
    {
        Console.WriteLine($"Name changed to: {newValue}");
    }
}
```

### Derived Properties

Properties marked with `[Derived]` are included in the metadata as calculated properties (can be read-only or writable):

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

When `Width` or `Height` changes, `Area` automatically raises a change notification (requires `WithDerivedPropertyChangeDetection()` on the context).

### Interface Default Properties

The generator automatically discovers and includes interface default implementations in the property metadata:

```csharp
public interface ITemperatureSensor
{
    double Celsius { get; set; }

    [Derived]
    double Fahrenheit => Celsius * 9.0 / 5.0 + 32;
}

[InterceptorSubject]
public partial class Sensor : ITemperatureSensor
{
    public partial double Celsius { get; set; }
    // Fahrenheit is automatically included in DefaultProperties
}
```

**Supported scenarios:**
- Read-only default properties (`string Status => ...`)
- Writable default properties (`string Label { get => ...; set { } }`)
- `[Derived]` attribute on interface properties
- Multiple interface inheritance
- Interface hierarchies (base interfaces)
- Generic interfaces
- Diamond inheritance (deduplicated)

**Note:** If a class implements a property that an interface also provides as a default, the class implementation takes precedence.

### Method Interception

Methods ending with `WithoutInterceptor` get public wrapper methods:

```csharp
[InterceptorSubject]
public partial class Calculator
{
    protected int SumWithoutInterceptor(int a, int b)
    {
        return a + b;
    }
}

// Generator creates:
// public int Sum(int a, int b) { ... }
```

The generated method routes through the interception pipeline, enabling cross-cutting concerns.

### Virtual and Override Properties

```csharp
[InterceptorSubject]
public partial class Animal
{
    public virtual partial string Name { get; set; }
}

[InterceptorSubject]
public partial class Dog : Animal
{
    public override partial string Name { get; protected set; }
}
```

### Access Modifiers

All C# access modifiers are supported:

```csharp
[InterceptorSubject]
public partial class Entity
{
    public partial string Public { get; set; }
    protected partial string Protected { get; set; }
    internal partial string Internal { get; set; }
    private partial string Private { get; set; }
    protected internal partial string ProtectedInternal { get; set; }
    private protected partial string PrivateProtected { get; set; }

    // Accessor-level modifiers
    public partial string Name { get; private set; }
}
```

### Init-Only and Required Properties

```csharp
[InterceptorSubject]
public partial class Config
{
    public required partial string ConnectionString { get; set; }
    public partial string Environment { get; init; }

    public Config()
    {
        Environment = "Development";
    }
}
```

### Nested Classes

```csharp
public partial class Outer
{
    [InterceptorSubject]
    public partial class Nested
    {
        public partial string Name { get; set; }
    }

    public partial class Level2
    {
        [InterceptorSubject]
        public partial class DeepNested
        {
            public partial int Value { get; set; }
        }
    }
}
```

### Inheritance

Child classes can also be `[InterceptorSubject]`:

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string Name { get; set; }
}

[InterceptorSubject]
public partial class Employee : Person
{
    public partial string Department { get; set; }
}
```

The `DefaultProperties` of `Employee` includes properties from both classes. Change notifications from the base class work correctly.

### Partial Class Spanning

Properties can be declared across multiple files:

```csharp
// Person.cs
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
}

// Person.Extended.cs
public partial class Person
{
    public partial string LastName { get; set; }
}
```

Both properties are included in the generated code.

## Limitations

| Limitation | Workaround |
|------------|------------|
| Only partial properties are intercepted | Mark properties with `partial` keyword |
| Explicit interface implementation not supported | Use implicit implementation |
| Abstract properties not supported | Use `virtual` instead |
| Init-only properties cannot be set after construction | Design constraint of C# |
| Partial properties cannot have field initializers | Initialize in constructor |

## Requirements

- **C# 13** with partial property support
- Class must be marked `partial`
- Properties to intercept must be marked `partial`
- IDE with source generator support (Visual Studio 2022, Rider, VS Code)

## Performance

The generator is optimized for performance:

- **Zero runtime reflection** - All metadata generated at compile-time
- **Static lambdas** - No closure allocations in property accessors
- **Fast-path optimization** - Direct field access when no context is set
- **FrozenDictionary** - Thread-safe, read-optimized property lookup
- **PropertyChangedEventArgs caching** - Avoids repeated allocations
- **AggressiveInlining** - Helper methods are inlined by the JIT

## Troubleshooting

### Generated code not appearing

1. Ensure the generator is added as an analyzer:
   ```xml
   <PackageReference Include="Namotion.Interceptor.Generator" Version="..."
                     OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
   ```

2. Rebuild the project completely

3. Check that your class is `partial` and has `[InterceptorSubject]`

### Compilation errors in generated code

1. Ensure you're using C# 13 or later
2. Check that property types are accessible from the generated code
3. Verify namespace imports are correct

### Changes not being tracked

1. Ensure properties are marked `partial`
2. Verify you're using a context with tracking enabled:
   ```csharp
   var context = InterceptorSubjectContext
       .Create()
       .WithFullPropertyTracking();
   ```

3. Create instances with the context:
   ```csharp
   var person = new Person(context);  // Not: new Person()
   ```
