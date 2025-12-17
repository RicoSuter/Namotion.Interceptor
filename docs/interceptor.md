# Interceptors and Contexts

The `InterceptorSubjectContext` is the central coordination hub in the core `Namotion.Interceptor` package. It manages service registration, resolution, and orchestrates the interception pipeline. Every interceptor subject requires a context to function.

## Creating a Context

```csharp
var context = InterceptorSubjectContext.Create();

var person = new Person(context);
```

The context is typically created once at application startup and shared across all subjects in an object graph.

## Adding Services

Services are registered using the fluent API. Services can be interceptors, handlers, or any custom service type:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithService<IMyService>(() => new MyService())
    .WithService(() => new MyWriteInterceptor());
```

**Common service interfaces:**

- `IReadInterceptor` - Intercepts property reads
- `IWriteInterceptor` - Intercepts property writes
- `IMethodInterceptor` - Intercepts method invocations
- `ILifecycleHandler` - Handles subject attach/detach events

Extension methods like `WithFullPropertyTracking()` or `WithRegistry()` register multiple related services at once.

## Service Resolution

Services are resolved by interface type. Multiple services of the same type are returned in registration order (unless ordering attributes are used):

```csharp
// Get all services of a type
var interceptors = context.GetServices<IWriteInterceptor>();

// Get a single service (throws if multiple exist)
var registry = context.TryGetService<SubjectRegistry>();
```

Services are cached after first resolution. The cache is invalidated when services or fallback contexts change.

## Fallback Contexts

Contexts can be linked in a hierarchy where child contexts inherit services from parent contexts:

```csharp
var parentContext = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking();

var childContext = InterceptorSubjectContext.Create();
childContext.AddFallbackContext(parentContext);

// childContext now has access to all services from parentContext
```

This is used internally by `WithContextInheritance()` to automatically assign the parent's context to child subjects.

**Resolution order:**
1. Services registered directly on the context
2. Services from fallback contexts (recursively)
3. Results are deduplicated and ordered

## Service Ordering

When multiple handlers or interceptors are registered, their execution order can be controlled using ordering attributes. This is important when services have dependencies on each other.

**Available Attributes:**

```csharp
using Namotion.Interceptor.Attributes;

// Run before specific types
[RunsBefore(typeof(OtherHandler))]
public class MyHandler : ILifecycleHandler { }

// Run after specific types
[RunsAfter(typeof(OtherHandler))]
public class MyHandler : ILifecycleHandler { }

// Run before all services without [RunsFirst]
[RunsFirst]
public class EarlyHandler : IWriteInterceptor { }

// Run after all services without [RunsLast]
[RunsLast]
public class LateHandler : IWriteInterceptor { }
```

**Ordering Rules:**

- Services are partitioned into three groups: `[RunsFirst]` → Middle → `[RunsLast]`
- Within each group, `[RunsBefore]` and `[RunsAfter]` define the topological order
- Without ordering attributes, registration order is preserved
- Missing dependency types are silently ignored (supports optional dependencies)
- Circular dependencies throw `InvalidOperationException`
- A service cannot have both `[RunsFirst]` and `[RunsLast]`
- A `[RunsFirst]` service cannot have `[RunsAfter]` referencing Middle or Last group services
- A `[RunsLast]` service cannot have `[RunsBefore]` referencing First or Middle group services

## Interceptor Pipeline

The context builds an interceptor chain for property operations. When a property is read or written, the chain executes in order:

```
Write: Interceptor1 → Interceptor2 → ... → Actual Write
Read:  Interceptor1 → Interceptor2 → ... → Actual Read
```

Each interceptor can:
- Modify the value before passing to the next interceptor
- Skip calling the next interceptor (blocking the operation)
- Perform side effects (logging, validation, change tracking)

```csharp
public class LoggingInterceptor : IWriteInterceptor
{
    public void WriteProperty<T>(ref PropertyWriteContext<T> context, WritePropertyDelegate<T> next)
    {
        Console.WriteLine($"Writing {context.Property.Name}");
        next(ref context); // Call next interceptor or actual write
    }
}
```

The pipeline is built once per property type and cached for performance.
