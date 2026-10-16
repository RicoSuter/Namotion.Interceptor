# Namotion.Interceptor.Blazor

Automatic change tracking and re-rendering for Blazor applications using interceptor subjects.

## Getting Started

### Installation

Add the `Namotion.Interceptor.Blazor` package to your Blazor project and ensure your context is configured with property tracking:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithReadPropertyRecorder();  // Required for TrackingScope
```

### Basic Usage

Wrap your content in a `TrackingScope` to automatically re-render when tracked properties change:

```razor
@using Namotion.Interceptor.Blazor

<TrackingScope Context="@person.Context">
    <p>@person.FirstName @person.LastName</p>
    <p>Age: @person.Age</p>
</TrackingScope>
```

When any property accessed inside the scope changes, the component automatically re-renders.

### Debug Mode

Enable `ShowDebugInfo` to visualize tracking during development:

```razor
<TrackingScope Context="@context" ShowDebugInfo="true">
    @* Content shows green border and glow on updates *@
    @* Hover to see list of tracked properties *@
</TrackingScope>
```

## Features

### Automatic Property Tracking

Properties read during render are automatically tracked. No manual subscription required.

```razor
<TrackingScope Context="@context">
    @* These properties are automatically tracked: *@
    <p>@motor.Speed RPM</p>
    <p>@motor.Temperature °C</p>
</TrackingScope>
```

### Derived Property Support

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

### Hierarchical Tracking

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

### Session Isolation

Each browser session has isolated tracking. Server-side Blazor with multiple concurrent users works correctly.

## Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `Context` | `IInterceptorSubjectContext` | Required. The context to subscribe to for property changes. |
| `ThrottleMilliseconds` | `int` | Optional. Minimum time between rerenders in ms. Default: 33 (~30fps). |
| `ShowDebugInfo` | `bool` | Optional. Shows visual debugging (border, tracked properties tooltip). |
| `ChildContent` | `RenderFragment` | The content to render within the tracking scope. |

## Guarantees

- **At-Least-Once Rerender**: Any property change affecting tracked content triggers a re-render
- **Session Isolation**: No cross-talk between browser sessions in server-side Blazor
- **Thread Safety**: Safe for concurrent access from multiple threads

## Limitations

### Reads Recorded While the Scope Is Open

Recording covers the property reads that happen while the scope is open: from the moment Blazor invokes the scope's render fragment until the scope's `OnAfterRender`. See [Data Flow](#data-flow) for exactly when that starts and ends. This covers the content directly inside `<TrackingScope>` and every descendant rendered in the same batch, but also the reads of other components that Blazor renders later in that batch, or otherwise before `OnAfterRender`, in the same flow. Those reads are attributed to the open scope as well: the scope over-tracks, which causes extra re-renders but never a missed one. Content that a component renders in a later batch of its own, for example a virtualized grid cell or content that renders only after a JavaScript callback, falls outside the scope. Reads inside such deferred content are recorded only the next time that content renders while the scope is open, not when it renders on its own.

To keep the guarantee "at least once" even for this deferred content, TrackingScope tracks the **subjects** whose properties were read, not just the individual properties, and re-renders when any descendant of a tracked subject changes:

```razor
<TrackingScope Context="@context">
    @* Only Speed is displayed, but changes to Temperature also trigger rerender *@
    <p>@motor.Speed RPM</p>
</TrackingScope>
```

If `motor.Temperature` changes, even though only `motor.Speed` was read, the component still re-renders because `motor` was read.

**Impact**: more re-renders than strictly necessary in some scenarios, and content that renders outside the scope is only ever captured by this subject-level fallback. A component that defers its content to a later render of its own can wrap that content in a nested `<TrackingScope>` to bring it back into a recorded pass.

---

# Implementation Details

## Architecture Overview

The tracking system consists of three core components:

| Component | Purpose |
|-----------|---------|
| `TrackingScope` | Blazor component that wraps content and triggers re-renders |
| `ReadPropertyRecorder` | Interceptor that records property reads using `AsyncLocal<T>` |
| `ReadPropertyRecorderScope` | Disposable scope that collects property references during render, and removes itself from the list it was added to on `Dispose`, even when that runs on a different async flow than `Start` |

## Data Flow

Recording starts inside the scope's own render fragment, not in a lifecycle method such as `OnParametersSet` or `ShouldRender`. Blazor can invoke a component's render fragment in a different async flow than the one that called `StateHasChanged`, and an `AsyncLocal` scope opened in a lifecycle method would not be visible to reads that happen in that other flow. Opening the scope inside the fragment itself guarantees the recording runs in the same flow as the reads it needs to capture.

Each time Blazor invokes the fragment, it closes any scope left open by an earlier invocation (so nothing is lost if the fragment runs again before `OnAfterRender`, whether later in the same batch or in a new one) and opens a new `ReadPropertyRecorderScope` before rendering `ChildContent`. Blazor renders every descendant that is part of this render pass before it calls `OnAfterRender`, so property reads anywhere in that pass, in the scope's own content or in a descendant component rendered as part of it, are captured. `OnAfterRender` then stops the scope, swaps the collected properties into the active set, rebuilds the tracked-subjects snapshot used for descendant matching, and clears the ancestor cache.

```
Render fragment (invoked by Blazor)              OnAfterRender()
       │                                                 │
       ▼                                                 ▼
 ReadPropertyRecorder.Start()                   scope.Dispose() (stop recording)
       │                                                 │
       ▼                                                 ▼
 property reads during this render pass        swap _collectingProperties ↔ _properties
 (ChildContent and its descendants) are         rebuild _trackedSubjects
 added to _collectingProperties                 clear the ancestor cache
```

```
Property Change Detection

 PropertyChangeInterceptor ── Subscribe ──► TrackingScope
        │
        ▼
  change.Property
        │
        ├─► 1. Is it one of _properties? ──────────────► TriggerRerender()
        │
        └─► 2. Is its subject a descendant of a
               tracked subject (BFS parent walk,
               all branches)? ────────────────────────► TriggerRerender()
```

## Design Decisions

### AsyncLocal-Based Recording

**Problem**: Context-based storage (singleton) caused cross-client interference in server-side Blazor.

**Solution**: Use `AsyncLocal<T>` to maintain per-async-context isolation. Each browser session gets its own recording scope.

```csharp
private static readonly AsyncLocal<List<ReadPropertyRecorderScope>?> _activeScopes = new();
```

### Subject Descendant Tracking

**Problem**: Content that a component renders later, in a render pass of its own (for example virtualized DataGrid cells), falls outside the scope's own render pass, so the reads it makes are not recorded by that pass. See [Limitations](#limitations) for when this applies in practice.

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
