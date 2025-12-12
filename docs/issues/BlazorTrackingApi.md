# Blazor Feature Request: Preserve AsyncLocal/ExecutionContext for Reactive Dependency Tracking

## Problem Statement

We are building a reactive UI library ([Namotion.Interceptor](https://github.com/RicoSuter/Namotion.Interceptor)) that automatically tracks property reads during Blazor component rendering to enable fine-grained re-rendering - similar to how Vue.js, MobX, or Solid.js track dependencies.

**Goal**: Only re-render a component when properties it actually uses change, not when any property in the application state changes.

### Example Use Case

```razor
<TrackingScope Context="@appState">
    <h1>@appState.User.Name</h1>
    <p>Temperature: @appState.Sensor.Temperature</p>
</TrackingScope>
```

The `TrackingScope` should:
1. Detect that `User.Name` and `Sensor.Temperature` were read during render
2. Subscribe only to changes on those specific properties
3. Re-render only when those properties change (not when `User.Email` or `Sensor.Humidity` changes)

This is achieved through property interception - our source generator creates intercepted properties that notify when read/written.

## The Challenge

To associate a property read with the correct `TrackingScope`, we need to know **which component's render is currently executing** at the moment the property getter runs.

The natural .NET approach is `AsyncLocal<T>`:

```csharp
public class TrackingScope : ComponentBase
{
    private static readonly AsyncLocal<TrackingScope?> Current = new();

    private RenderFragment WrapChildContent => builder =>
    {
        var previous = Current.Value;
        Current.Value = this;
        try
        {
            ChildContent?.Invoke(builder);
        }
        finally
        {
            Current.Value = previous;
        }
    };
}

// In property interceptor
public TProperty ReadProperty<TProperty>(...)
{
    var result = next(ref context);
    TrackingScope.Current?.RecordPropertyRead(context.Property);
    return result;
}
```

**The problem**: `AsyncLocal` values are lost when RenderFragments are invoked in a different async context.

### What We Tried

#### 1. Lifecycle Methods (OnParametersSet / OnAfterRender)

```csharp
protected override void OnParametersSet()
{
    StartRecording();
}

protected override void OnAfterRender(bool firstRender)
{
    StopRecording();
}
```

**Problem**: Multiple components' lifecycle methods interleave. When component A and B render in the same batch, their OnParametersSet/OnAfterRender calls don't nest cleanly around their own render execution.

#### 2. Wrapping ChildContent RenderFragment

```csharp
private RenderFragment WrapChildContent => builder =>
{
    Current.Value = this;
    try
    {
        ChildContent?.Invoke(builder);
    }
    finally
    {
        Current.Value = null;
    }
};
```

**Problem**: Works for synchronous rendering in the same batch, but fails for:
- **Virtualized components** (e.g., DataGrid): Cell templates execute in later render batches when user scrolls
- **Deferred rendering**: Components that capture RenderFragments and invoke them later

When a scroll event triggers a new cell to render, it's a new async flow - our `AsyncLocal` value is gone.

#### 3. CascadingParameter + Component Base Class

```csharp
public class TrackedComponentBase : ComponentBase
{
    [CascadingParameter]
    public TrackingScope? Scope { get; set; }

    protected override void OnParametersSet()
    {
        Scope?.RegisterSubject(Subject);
    }
}
```

**Problem**:
- Invasive - requires all components to inherit from a specific base class
- Doesn't work for implicit property reads (e.g., `@model.Property` in templates)
- Third-party components won't cooperate

#### 4. Global Context + Subject Hierarchy Filtering

Subscribe to all property changes on a shared context, filter by checking if the subject belongs to this scope's hierarchy.

**Problem**: In our architecture, all subjects share the same root context through fallback chains, so hierarchy filtering doesn't distinguish between scopes.

## Root Cause Analysis

The fundamental issue is that **Blazor does not preserve ExecutionContext/AsyncLocal when invoking deferred RenderFragments**.

When a RenderFragment is captured (e.g., a DataGrid cell template) and later invoked (e.g., on scroll), it runs in a new async context without the AsyncLocal values from the original render.

This differs from standard .NET async behavior where `ExecutionContext` flows through `async/await` and APIs like `Task.Run`.

## Proposed Blazor Changes

### Primary Proposal: Preserve ExecutionContext Through RenderFragment Invocations

When Blazor invokes a RenderFragment (including deferred ones), it should capture and restore the ExecutionContext from when the RenderFragment was created/captured.

Conceptually:
```csharp
// When capturing a RenderFragment (e.g., in a DataGrid)
var capturedContext = ExecutionContext.Capture();

// Later, when invoking it (e.g., on scroll)
ExecutionContext.Run(capturedContext, _ => renderFragment(builder), null);
```

**Benefits:**
- **No new API surface** - behavioral change only
- **Aligns with .NET conventions** - AsyncLocal is the standard way to flow context
- **Enables many scenarios** - logging, tracing, diagnostics, dependency tracking
- **Already expected behavior** - developers expect AsyncLocal to flow through async operations

### Alternative Proposal: Ambient Render Data on RenderTreeBuilder

If preserving full ExecutionContext has performance concerns, a more targeted API:

```csharp
public class RenderTreeBuilder
{
    /// <summary>
    /// Pushes ambient data that flows to all nested RenderFragment invocations,
    /// including deferred ones.
    /// </summary>
    public void PushAmbientValue<T>(T value);

    /// <summary>
    /// Pops the most recent ambient value of type T.
    /// </summary>
    public void PopAmbientValue<T>();

    /// <summary>
    /// Gets the current ambient value of type T.
    /// Accessible from anywhere during render execution.
    /// </summary>
    public static T? GetAmbientValue<T>();
}
```

Usage:
```csharp
// TrackingScope's render
builder.PushAmbientValue(this);
try
{
    ChildContent?.Invoke(builder);
}
finally
{
    builder.PopAmbientValue<TrackingScope>();
}

// In property interceptor (anywhere)
var scope = RenderTreeBuilder.GetAmbientValue<TrackingScope>();
scope?.RecordPropertyRead(property);
```

This is similar to React's Context or Vue's provide/inject, but accessible from non-component code.

## Why This Matters

Reactive UI patterns are increasingly popular (Vue, Solid, MobX, Svelte). Enabling them in Blazor would:

1. **Improve performance**: Fine-grained re-rendering instead of component-tree-based
2. **Simplify state management**: No manual `StateHasChanged()` calls needed
3. **Enable new libraries**: Reactive data binding libraries could be built for Blazor
4. **Better DX**: Developers don't need to manually track dependencies

The primary proposal (ExecutionContext preservation) would benefit the broader .NET ecosystem. Any code that uses `AsyncLocal` for ambient context (logging correlation IDs, transaction contexts, user identity, etc.) would work correctly during Blazor rendering.

## Current Workaround

Our current workaround combines multiple approaches:

1. **AsyncLocal + ChildContent wrapping**: Works for same-batch rendering
2. **Subject-level tracking with BFS parent walking**: Instead of tracking specific properties, we track which *subjects* (objects) were accessed during render. When any property changes, we use BFS to walk up the parent hierarchy and check if the changed subject is a descendant of any tracked subject.
3. **Observable subscription**: Re-render when any tracked property or descendant changes

**Implementation details:**
- Uses `AsyncLocal<T>` for session isolation (no cross-client interference in server-side Blazor)
- Thread-safe with proper locking and `Interlocked` operations for rerender coalescing
- Conservative caching (only caches positive ancestor lookups to guarantee at-least-once rerender)
- Handles multi-parent object graphs and cycles via BFS with visited set

This works for most cases but **causes unnecessary re-renders**:
- If a component reads `motor.Speed`, it will also re-render when `motor.Temperature` changes
- Any child subject property change triggers parent re-render, even if that property wasn't displayed
- The guarantee is "at-least-once" not "exactly-once" - we prioritize correctness over minimal renders

## Related Issues

- [#51140 - Override BuildRenderTree for ambient context](https://github.com/dotnet/aspnetcore/issues/51140) - Closed without solution
- [#18500 - Expose Renderer Instance](https://github.com/dotnet/aspnetcore/issues/18500) - Closed; team decided not to expose Renderer
- [#45741 - AsyncLocal is null on first re-render following hot reload](https://github.com/dotnet/aspnetcore/issues/45741) - Related AsyncLocal issue

## Summary

Preserving `ExecutionContext`/`AsyncLocal` through RenderFragment invocations would:

1. **Be minimal** - Behavioral change, no new API surface
2. **Be non-breaking** - Existing code continues to work
3. **Align with .NET** - Standard ExecutionContext flow behavior
4. **Enable reactive patterns** - Standard in other modern UI frameworks

We would be happy to contribute to the implementation or provide more details about our use case.
