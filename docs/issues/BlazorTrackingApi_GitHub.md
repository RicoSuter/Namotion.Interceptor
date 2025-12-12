# [Feature Request] Preserve AsyncLocal/ExecutionContext Through RenderFragment Invocations

## Is there an existing issue for this?

- [x] I have searched the existing issues

Related issues:
- #51140 - Override BuildRenderTree for ambient context (closed without solution)
- #18500 - Expose Renderer Instance (closed - team decided not to expose Renderer)
- #45741 - AsyncLocal is null on first re-render following hot reload

## Is your feature request related to a problem? Please describe the problem.

We are building a reactive UI library ([Namotion.Interceptor](https://github.com/RicoSuter/Namotion.Interceptor)) that automatically tracks property reads during Blazor component rendering to enable fine-grained re-rendering - similar to Vue.js, MobX, or Solid.js.

**Goal**: Only re-render a component when properties it actually uses change.

```razor
<TrackingScope Context="@appState">
    <h1>@appState.User.Name</h1>
    <p>Temperature: @appState.Sensor.Temperature</p>
</TrackingScope>
```

We use `AsyncLocal<T>` to track which `TrackingScope` is currently rendering:

```csharp
public class TrackingScope : ComponentBase
{
    private static readonly AsyncLocal<TrackingScope?> Current = new();

    // Set before rendering ChildContent, clear after
}
```

**The problem**: `AsyncLocal` values are lost when RenderFragments are invoked in a different async context, such as:

- **Virtualized components**: DataGrid cell templates execute in later render batches when user scrolls
- **Deferred rendering**: Components that capture RenderFragments and invoke them later via user interaction

When a scroll event triggers a new cell to render, it's a new async flow - our `AsyncLocal` value is gone, and we can't associate the property read with the correct `TrackingScope`.

## What we tried

1. **Lifecycle methods (OnParametersSet/OnAfterRender)**: Multiple components' lifecycle methods interleave unpredictably.

2. **Wrapping ChildContent RenderFragment**: Works for same-batch rendering, fails for virtualized/deferred scenarios.

3. **CascadingParameter + base class**: Invasive, requires all components to cooperate, doesn't work for implicit property reads like `@model.Property`.

4. **Subject hierarchy filtering**: All subjects share the same root context in our architecture.

5. **Current workaround - Subject-level tracking with BFS parent walking**: Instead of tracking specific properties, we track which *subjects* (objects) were accessed. When any property changes, we walk up the parent hierarchy using BFS to check if the changed subject is a descendant of any tracked subject. **This works but causes unnecessary re-renders** - if a component reads `motor.Speed`, it will also re-render when `motor.Temperature` changes (even though Temperature isn't displayed).

## Describe the solution you'd like

**Primary proposal: Preserve ExecutionContext/AsyncLocal through RenderFragment invocations**

When Blazor invokes a RenderFragment (including deferred ones like DataGrid cell templates), it should capture and restore the ExecutionContext from when the RenderFragment was created/captured.

This aligns with how `async/await` preserves ExecutionContext and how other .NET APIs like `Task.Run` support ExecutionContext flow.

Conceptually:
```csharp
// When capturing a RenderFragment (e.g., in a DataGrid)
var capturedContext = ExecutionContext.Capture();

// Later, when invoking it (e.g., on scroll)
ExecutionContext.Run(capturedContext, _ => renderFragment(builder), null);
```

This would make `AsyncLocal<T>` "just work" for ambient context during rendering, which is the standard .NET pattern for flowing contextual data.

**Benefits:**
- **No new API surface** - behavioral change only
- **Aligns with .NET conventions** - AsyncLocal is the standard way to flow context
- **Enables many scenarios** - logging, tracing, diagnostics, dependency tracking, etc.
- **Already expected behavior** - developers expect AsyncLocal to flow through async operations

## Alternative solution

If preserving ExecutionContext is too broad or has performance concerns, a more targeted alternative:

**Ambient render data on RenderTreeBuilder:**

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

Usage in our library:
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

This is similar to React's Context or Vue's provide/inject, but accessible from non-component code (like property getters).

## Additional context

This would enable reactive UI patterns that are standard in other frameworks:
- **Vue.js**: Automatic dependency tracking via Proxy
- **MobX**: Observable state with automatic subscriptions
- **Solid.js**: Fine-grained reactivity
- **Svelte**: Compile-time reactivity

The primary proposal (ExecutionContext preservation) would benefit the broader .NET ecosystem, not just our library. Any code that uses `AsyncLocal` for ambient context (logging correlation IDs, transaction contexts, user identity, etc.) would work correctly during Blazor rendering.

---

**Library**: [Namotion.Interceptor](https://github.com/RicoSuter/Namotion.Interceptor)
**Detailed write-up**: [BlazorTrackingApi.md](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/docs/issues/BlazorTrackingApi.md)
