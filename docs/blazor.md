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

- **At-Least-Once Rerender**: When a property that the scope recorded changes, or any property of a subject below a subject whose property a completed render recorded, the scope renders again after the change. A change that arrives while a render is in progress, or before the client has acknowledged it, renders once more as well, so the last render always reads values at least as new as the last change the scope tracks. This holds for the reads the scope records, see [Reads Recorded While the Scope Is Open](#reads-recorded-while-the-scope-is-open).
- **Session Isolation**: No cross-talk between browser sessions in server-side Blazor
- **Thread Safety**: Safe for concurrent access from multiple threads

## Limitations

### Reads Recorded While the Scope Is Open

Recording covers the property reads that happen while the scope is open: from the moment Blazor invokes the scope's render fragment until the scope's `OnAfterRender`. See [Data Flow](#data-flow) for exactly when that starts and ends. This covers the content directly inside `<TrackingScope>` and every descendant rendered in the same batch. After its first render the scope renders once more, which also records content that a descendant renders in a batch of its own right after its first render, for example after it has measured its element, as long as the descendant renders again when the scope does, as a component with a parameter of a subject type does.

Content that a component renders later on its own, for example after awaiting a JavaScript call or a timer, or a virtualized grid cell, falls outside the scope. Reads inside such content are recorded only the next time that content renders while the scope is open. To keep the guarantee "at least once" for this content as far as possible, TrackingScope tracks the **subjects** whose properties were read, not just the individual properties, and re-renders when any descendant of a tracked subject changes:

```razor
<TrackingScope Context="@context">
    @* Only Speed is displayed, but changes to Temperature also trigger rerender *@
    <p>@motor.Speed RPM</p>
</TrackingScope>
```

If `motor.Temperature` changes, even though only `motor.Speed` was read, the component still re-renders because `motor` was read. The tracked subjects are taken over when a render completes, in `OnAfterRender`, so a change below a subject that only the render still in progress has read is not matched. A read in deferred content of a subject that no completed render read is not covered: wrap such content in a nested `<TrackingScope>` of its own, which records it when it renders.

### Over-Tracking Within a Batch

Every scope that is open in the async flow records every read in that flow, not only the reads of its own content. A scope stays open until its `OnAfterRender`, which runs after the whole batch has rendered, so it also records the reads of the components that Blazor renders after it in the same batch:

- **Sibling scopes**: When a parent renders 20 sibling scopes in one batch, the first scope records the reads of all 20, the second those of 19, and so on. A change that only the last one reads then renders all 20 once. Each scope that renders on its own afterwards records only its own reads again. The render after the first render is one of these on WebAssembly, so siblings start out tracking their own reads there. On a server, a render request waits for the client only when the client falls 10 batches behind, and the requests that wait then render together in one batch, so the scopes in that batch over-track each other again.
- **Nested scopes**: The outer scope records everything that the inner scope records, so a change read inside the inner scope renders the outer scope as well.
- **Unacknowledged batches**: On a server, `OnAfterRender` runs once the client acknowledges the batch, so until then a scope also records the reads of later batches that render in the same async flow. `OnAfterRender` and the renders requested from it run in the flow that rendered the batch, so a nested scope can, for example, record the reads of the outer scope's render after the first render.
- **Cost of a read**: Each read is added to every open scope in the flow, so a read costs time in proportion to the number of open scopes.

Over-tracking causes extra renders of the scope that over-tracks. It does not change what the other scopes record.

### Prerendering

When a page is prerendered, `OnAfterRender` never runs, so a scope records from its render fragment until it is disposed at the end of prerendering. The recorded set holds each property read in that flow at most once, and disposal removes the scope from the flow. The interactive render that follows starts a new scope with its own recording.

### Scope Size

A scope that renders again renders its whole content, and Blazor renders every component inside it again whose parameters it cannot compare, such as subjects or render fragments. A scope around a whole page therefore renders the whole page for each change it tracks. Scopes around the parts that change keep these renders small, at the cost of the over-tracking described above when many of them render in one batch.

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

Each time Blazor invokes the fragment, it first clears the pending render flag, see [Rerender Coalescing](#rerender-coalescing). It then closes any scope left open by an earlier invocation (so nothing is lost if the fragment runs again before `OnAfterRender`, whether later in the same batch or in a new one) and opens a new `ReadPropertyRecorderScope` before rendering `ChildContent`. Blazor renders every descendant that is part of this render pass before it calls `OnAfterRender`, so property reads anywhere in that pass, in the scope's own content or in a descendant component rendered as part of it, are captured. The recorder adds a property to the open scopes before it reads the value, so a change written after the read finds the property recorded. `OnAfterRender` then swaps the collected properties into the active set, rebuilds the tracked-subjects snapshot used for descendant matching, clears the ancestor cache, and only then stops the scope. After the first render, the scope renders once more, posted from outside the dispatcher so that it follows the `OnAfterRender` calls of its descendants.

```
Render fragment (invoked by Blazor)              OnAfterRender()
       │                                                 │
       ▼                                                 ▼
 reset the pending render flag                  swap _collectingProperties ↔ _properties
 ReadPropertyRecorder.Start()                   rebuild _trackedSubjects
       │                                         clear the ancestor cache
       ▼                                        scope.Dispose() (stop recording)
 property reads during this render pass
 (ChildContent and its descendants) are
 added to _collectingProperties
```

```
Property Change Detection

 PropertyChangeInterceptor ── Subscribe ──► TrackingScope
        │
        ▼
  change.Property
        │
        ├─► 1. Is it one of _properties, or, while a
        │      render records, of _collectingProperties? ─► TriggerRerender()
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

**Solution**: Property changes are buffered within a configurable time window (`ThrottleMilliseconds`, default 33ms). All changes in the window are checked, and a render is requested only if none is pending yet. The pending flag is cleared when the render fragment starts, not when the render completes: a change that arrives after the fragment has started may have been read with its old value, so it requests another render instead of being coalesced into the current one.

## Thread Safety

All shared state is protected:

- `Lock` for the swap of `_properties` and `_collectingProperties` and for `_trackedSubjects`; the change callback reads both sets and the recording flag under the same lock. The flag is written outside the lock. The render sets it with a full fence (`Interlocked.Exchange`) before it reads anything, the recorder adds each property to the concurrent set before it reads the value, and the change path passes through locks before the callback runs, so a callback for a change written after a read sees the flag set. `OnAfterRender` clears the flag only after the swap, so a callback that sees it cleared finds the reads in `_properties`
- `ConcurrentDictionary` for the property sets and `_ancestorCache`
- `volatile` flags for `_disposed`, `_rerenderPending`, `_isRecording`; the pending and recording flags are set with a full fence before the render reads anything
- Disposal guards to prevent callback exceptions during component teardown
