# Namotion.Interceptor.Blazor

Automatic change tracking and re-rendering for Blazor applications using interceptor subjects.

## Getting Started

### Installation

Add the `Namotion.Interceptor.Blazor` package to your Blazor project and ensure your context is configured with property tracking and the read property recorder:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithReadPropertyRecorder();
```

There are two ways to render again on a change: [component tracking](#component-tracking), which tracks every component on its own, and [`TrackingScope`](#trackingscope), which tracks the content inside it as a whole.

## Component Tracking

Register component tracking once, and every component renders again when a property that its own render read changes:

```csharp
builder.Services.AddComponentTracking();
```

```razor
<p>@person.FirstName @person.LastName</p>
<MudText>@motor.Speed RPM</MudText>
```

No base class, wrapper or subscription is needed. A change renders only the components whose render read the changed property:

- **Content passed to a child component** is read in the render of the child that renders it, so `<MudText>@motor.Speed</MudText>` renders the `MudText` again, not its parent. This holds wherever and whenever the content renders: in a grid cell, in a virtualized row, in a tab panel that opens later or in a popover.
- **Content passed to a component that does not derive from `ComponentBase`**, such as `CascadingValue` or `SectionContent`, is recorded by the component that created it, which renders it again with the content.
- **A component whose `ShouldRender` returns false** still renders on a tracked change, because its own reasons not to render cannot know that a value its render read has changed.

### Guarantees

- **At-least-once render**: After a change of a property that a component's last render read, the component renders again, and that render reads values at least as new as the change. The property is subscribed before its value is read, so a change during the render renders once more.
- **Once per change and component**: Changes within `ThrottleMilliseconds` (default 33 ms) render each affected component once, and the components of one round render in one batch. A component that a parent renders in the same batch renders once.
- **Thread safety**: Changes can be written on any thread. Components render on their renderer's dispatcher.
- **Cost**: A property that no component reads costs a circuit nothing. A watched property costs one subscription per circuit, however many components read it. In steady state, neither a change nor a render allocates.

### What Is Not Tracked

- **Reads outside a render**: A value read in `OnInitialized`, `OnParametersSet`, after an `await` or in an event handler is not tracked, because a change renders the component again without running these methods. Read the values that the markup shows in the render.
- **Reads on another thread** during a render.
- **A template (`RenderFragment<T>`) passed to a component that does not derive from `ComponentBase`**: its reads are recorded only by an open `TrackingScope`. The components of the framework take no such template apart from `Router`.

### Options

```csharp
builder.Services.AddComponentTracking(options => options.ThrottleMilliseconds = 100);
```

| Option | Default | Description |
|--------|---------|-------------|
| `ThrottleMilliseconds` | 33 | Minimum time between two rounds of renders that changes cause. |

### Prerendering and Disposal

While prerendering, nothing is recorded or subscribed. The interactive render that follows subscribes what it reads. A component that the renderer disposes releases its subscriptions at most a second later, at the next render or change of the circuit, and the circuit releases all of them when it ends.

### Compatibility

Component tracking is installed through `IComponentActivator` and wraps the render of each `ComponentBase` through a private member of the framework. If a version of `Microsoft.AspNetCore.Components` changes that member, an error is logged once per circuit and components render as without component tracking. Component tracking registers its own `IComponentActivator`, so an activator that the application registers later replaces it.

## TrackingScope

Wrap your content in a `TrackingScope` to automatically re-render when tracked properties change:

```razor
@using Namotion.Interceptor.Blazor

<TrackingScope Context="@person.Context">
    <p>@person.FirstName @person.LastName</p>
    <p>Age: @person.Age</p>
</TrackingScope>
```

When any property accessed inside the scope changes, the scope renders its whole content again. With component tracking registered, a scope records only the reads that no component's render records.

### Debug Mode

Enable `ShowDebugInfo` to visualize tracking during development:

```razor
<TrackingScope Context="@context" ShowDebugInfo="true">
    @* Content shows green border and glow on updates *@
    @* Hover to see list of tracked properties *@
</TrackingScope>
```

### Features

#### Automatic Property Tracking

Properties read during render are automatically tracked. No manual subscription required.

```razor
<TrackingScope Context="@context">
    @* These properties are automatically tracked: *@
    <p>@motor.Speed RPM</p>
    <p>@motor.Temperature °C</p>
</TrackingScope>
```

#### Derived Property Support

Works seamlessly with `[Derived]` properties. When underlying dependencies change, derived properties update and trigger re-renders.

```csharp
[InterceptorSubject]
public partial class Motor
{
    public partial int Speed { get; set; }

    [Derived]
    public string Status => Speed > 0 ? "Running" : "Stopped";
}
```

#### Hierarchical Tracking

Child subjects are tracked automatically. Changes to nested properties trigger re-renders.

```razor
<TrackingScope Context="@context">
    @* Changes to any child motor property will trigger rerender *@
    @foreach (var motor in factory.Motors)
    {
        <p>@motor.Name: @motor.Speed RPM</p>
    }
</TrackingScope>
```

#### Session Isolation

Each browser session has isolated tracking. Server-side Blazor with multiple concurrent users works correctly.

### Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `Context` | `IInterceptorSubjectContext` | Required. The context to subscribe to for property changes. |
| `ThrottleMilliseconds` | `int` | Optional. Minimum time between rerenders in ms. Default: 33 (~30fps). |
| `ShowDebugInfo` | `bool` | Optional. Shows visual debugging (border, tracked properties tooltip). |
| `ChildContent` | `RenderFragment` | The content to render within the tracking scope. |

### Guarantees

- **At-Least-Once Rerender**: Any property change affecting tracked content triggers a re-render
- **Session Isolation**: No cross-talk between browser sessions in server-side Blazor
- **Thread Safety**: Safe for concurrent access from multiple threads

### Limitations

#### Excessive Re-renders for Child Subject Changes

Due to Blazor's architecture, `AsyncLocal` values are lost when RenderFragments are invoked in a different async context (e.g., virtualized DataGrid cells, deferred rendering).

To work around this, TrackingScope tracks **subjects** (not just properties) and re-renders when any descendant of a tracked subject changes. This means:

```razor
<TrackingScope Context="@context">
    @* Only Speed is displayed, but changes to Temperature also trigger rerender *@
    <p>@motor.Speed RPM</p>
</TrackingScope>
```

If `motor.Temperature` changes (even though it's not displayed), the component still re-renders because `motor` was accessed.

**Impact**: More re-renders than strictly necessary in some scenarios. The guarantee is "at-least-once" not "exactly-once".

#### Fine-Grained Tracking

[Component tracking](#component-tracking) records the reads of each component's own render, without relying on the async flow, so it tracks specific properties also in virtualized and deferred content.

---

# Implementation Details

For component tracking, see [Blazor Component Tracking](design/blazor-component-tracking.md). The rest of this section describes `TrackingScope`.

## Architecture Overview

The tracking system consists of three core components:

| Component | Purpose |
|-----------|---------|
| `TrackingScope` | Blazor component that wraps content and triggers re-renders |
| `ReadPropertyRecorder` | Interceptor that records property reads using `AsyncLocal<T>` |
| `ReadPropertyRecorderScope` | Disposable scope that collects property references during render |

## Data Flow

```
┌─────────────────────────────────────────────────────────────────┐
│                     Render Cycle                                 │
│                                                                 │
│  ShouldRender()              OnAfterRender()                    │
│       │                            │                            │
│       ▼                            ▼                            │
│  StartRecording()            StopRecording()                    │
│       │                            │                            │
│       ▼                            ▼                            │
│  AsyncLocal<Scopes>          Swap collecting ↔ active           │
│  push scope                  Extract tracked subjects           │
│       │                      Clear ancestor cache               │
│       ▼                            │                            │
│  Property reads ─────────────────► _collectingProperties        │
│  via interceptor                                                │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│                   Property Change Detection                      │
│                                                                 │
│  PropertyChangeInterceptor ─── Subscribe ──► TrackingScope      │
│        │                                          │             │
│        ▼                                          ▼             │
│   change.Property              1. Check _properties directly    │
│                                2. Check IsDescendantOfTracked   │
│                                          │                      │
│                                          ▼                      │
│                                   BFS Parent Walk               │
│                                   (all branches)                │
│                                          │                      │
│                                          ▼                      │
│                                   TriggerRerender()             │
│                                   (coalesced)                   │
└─────────────────────────────────────────────────────────────────┘
```

## Design Decisions

### AsyncLocal-Based Recording

**Problem**: Context-based storage (singleton) caused cross-client interference in server-side Blazor.

**Solution**: Use `AsyncLocal<T>` to maintain per-async-context isolation. Each browser session gets its own recording scope.

```csharp
private static readonly AsyncLocal<List<ReadPropertyRecorderScope>?> _activeScopes = new();
```

### Subject Descendant Tracking

**Problem**: Properties read during deferred RenderFragment invocation (e.g., DataGrid virtualization) aren't captured by AsyncLocal because the async context is lost.

**Solution**: Track subjects whose properties are read. When any property changes, check if the changed subject is a descendant of any tracked subject using BFS parent traversal.

### BFS Parent Walking

**Problem**: A subject can have multiple parents (multi-parent graphs). Following only one path misses ancestors via other branches.

**Solution**: Use BFS (breadth-first search) with a `Queue<T>` to explore ALL parent branches.

```csharp
var visited = new HashSet<IInterceptorSubject>();
var queue = new Queue<IInterceptorSubject>();
queue.Enqueue(subject);

while (queue.Count > 0)
{
    var current = queue.Dequeue();
    foreach (var parent in current.GetParents())
    {
        if (trackedSubjects.Contains(parent.Property.Subject))
            return true;  // Found tracked ancestor
        if (visited.Add(parent.Property.Subject))
            queue.Enqueue(parent.Property.Subject);  // Continue BFS
    }
}
```

### Conservative Caching

**Problem**: Caching false results leads to missed re-renders when the parent graph changes.

**Solution**: Only cache positive (true) results. False results are always recomputed.

```csharp
// Cache only positive results
if (found)
    _ancestorCache.TryAdd(subject, true);

// Never cache false - graph may change
return false;
```

### Rerender Coalescing

**Problem**: Rapid property changes can cause excessive re-renders.

**Solution**: Property changes are buffered within a configurable time window (`ThrottleMilliseconds`, default 33ms). All changes in the window are checked, and at most one rerender is triggered per window.

## Thread Safety

All shared state is protected:

- `Lock` for `_properties`, `_trackedSubjects` swaps
- `ConcurrentDictionary` for `_ancestorCache`
- `volatile` flags for `_disposed`, `_rerenderPending`
- Disposal guards to prevent callback exceptions during component teardown
