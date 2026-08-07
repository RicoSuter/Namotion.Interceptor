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

Explicit interface implementations are also supported and are keyed by the member's simple name:

```csharp
public enum Gender { Male, Female }

public interface IHuman
{
    Gender Gender { get; }
}

public interface IMale : IHuman
{
    Gender IHuman.Gender => Gender.Male;
}

[InterceptorSubject]
public partial class John : IMale
{
    // "Gender" is included in DefaultProperties and reads as Gender.Male
}
```

The property is reached by casting to the interface that declares the member (`IHuman` here), so it always resolves through the normal dispatch rules for interface default implementations. A property reached this way is not intercepted, because an explicitly implemented member cannot be routed through the interception pipeline.

Attributes such as `[Derived]` must be declared on the interface member rather than on the explicit implementation. An attribute placed on the implementation is silently lost, and doing so reports NI0007 (see [Diagnostics](#diagnostics)).

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

The containing type does not need to be a class. It can also be a record, a record struct, a struct, or an interface, as long as every containing type is `partial`:

```csharp
public partial record Outer
{
    [InterceptorSubject]
    public partial class Nested
    {
        public partial string Name { get; set; }
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

### Namespaces and Accessibility

Subjects can be declared inside a namespace, inside a file-scoped namespace, or directly in the global namespace. The subject class does not need to be public either; the generator honors whatever accessibility it declares:

```csharp
[InterceptorSubject]
internal partial class InternalSubject
{
    public partial string Name { get; set; }
}
```

## Limitations

| Limitation | Workaround |
|------------|------------|
| Only partial properties are intercepted | Mark properties with `partial` keyword |
| Records cannot be subjects | Use a class. See NI0003 in [Diagnostics](#diagnostics) |
| Structs and interfaces cannot be subjects | Use a class. The compiler itself rejects a plain struct or interface as CS0592, because `InterceptorSubjectAttribute` only targets classes. A record struct is reported by NI0003 instead |
| Generic subjects, or subjects nested in a generic containing type, are not supported | Use non-generic types. See NI0009 in [Diagnostics](#diagnostics) |
| File-local subjects are not supported | Remove the `file` modifier. See NI0010 in [Diagnostics](#diagnostics) |
| Attributes on an explicit interface implementation are ignored | Declare the attribute on the interface member instead. See NI0007 in [Diagnostics](#diagnostics) |
| Abstract properties not supported | Use `virtual` instead |
| Init-only properties cannot be set after construction | Design constraint of C# |
| Partial properties cannot have field initializers | Initialize in constructor |

## Diagnostics

The generator reports the following diagnostics, all in the `Namotion.Interceptor` category:

| ID | Severity | Cause | Fix |
|----|----------|-------|-----|
| NI0001 | Error | The subject class is not declared `partial` | Add the `partial` modifier |
| NI0002 | Error | A containing type of the subject is not declared `partial` | Add `partial` to every containing type |
| NI0003 | Error | `[InterceptorSubject]` is placed on a record or a record struct. A plain struct or interface never reaches this diagnostic; the compiler already rejects those with CS0592, because the attribute only targets classes | Use a class |
| NI0004 | Error | The generator threw an unhandled exception while processing the subject | Report the issue. The generated file contains the full stack trace |
| NI0005 | Warning | A derived subject re-declares a property whose interface implementation is already provided by a base class, so reading through the subject and reading through the interface return different values | Rename the property, or suppress the warning if the divergence is intended |
| NI0006 | Warning | A member was skipped because it cannot be supported: an interface default property is an indexer or a static member, or is not accessible from generated code (only when neither accessor is reachable; a single inaccessible accessor keeps the property and drops just that accessor); or a `WithoutInterceptor` method has no name before the suffix, is static or generic, takes a `ref` or `out` parameter, or is itself an explicit interface implementation | Remove or rename the member, widen its accessibility, or adjust the method signature |
| NI0007 | Error | Any attribute, not only `[Derived]`, is placed on an explicit interface implementation. The emitted metadata reflects the interface member, so the attribute would be silently lost | Move the attribute to the interface member |
| NI0008 | Warning | Two interface members resolve to the same property name; the first one found wins and the rest are dropped | Rename one of the members, or suppress the warning to accept the first-wins behavior |
| NI0009 | Error | The subject itself is generic, or the subject is nested inside a generic containing type | Remove the type parameters from the subject or its containing type |
| NI0010 | Error | The subject is declared `file`-local | Remove the `file` modifier |

Suppress a rule at the point of use with `#pragma warning disable NI0005`, or project-wide through `<NoWarn>` in the project file.

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

1. Check the build output for an `NI####` diagnostic first. It names the cause directly; see [Diagnostics](#diagnostics)
2. Ensure you're using C# 13 or later
3. Check that property types are accessible from the generated code
4. Verify namespace imports are correct

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
