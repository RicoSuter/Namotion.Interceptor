# Blazor Component Tracking: Design

This document describes how component tracking in `Namotion.Interceptor.Blazor` works internally. For its use, see [Blazor](../blazor.md#component-tracking).

## Problem

`TrackingScope` records the reads of the async flow that renders it, for as long as its recording is open, and that spans the rest of the render batch. That window over-tracks: it also records the reads of every component that renders after the scope in the same batch, so a scope around a page renders the whole page for every change it tracks. It also under-tracks: a component that renders later in a batch of its own (after an await, in a virtualized grid, in a lazily rendered panel) renders outside the window.

Both come from the same cause: a scope around a fragment has no boundary of the components it renders. The render of each component is that boundary: the renderer invokes the component's render fragment synchronously, on one thread, for one component.

## Goals

1. A change renders only the components whose own render read the changed property, wherever and whenever they render.
2. At-least-once: after a change of a property that a component's last render read, the component renders again, and that render reads values at least as new as the change.
3. Once per change: changes within the throttle interval render each affected component once, in one batch.
4. Transparent: existing components, including those of component libraries, are tracked without changing them.
5. No allocation per change and none per render in steady state.

## The Extension Point

Blazor has no public hook around a component's render, neither in .NET 10 nor on the main branch of ASP.NET Core (.NET 11). The renderer logs `RenderingComponent` before each render, but only at debug level, without the instance and without an end. Both concrete renderers are internal. What it has:

- **`IComponentActivator`**, a public service that the renderer resolves from the circuit's service provider and that creates every component it instantiates, root components included.
- **`ComponentBase._renderFragment`**, the private delegate that `StateHasChanged` passes to `RenderHandle.Render` and that calls `BuildRenderTree`. A comment in `ComponentBase` says that the class invokes `BuildRenderTree` only through it. The field is unchanged since 2019 (.NET Core 3.0) up to the main branch.
- **`UnsafeAccessor`**, which reads and writes a private member without reflection, works with Native AOT and trimming, and costs nothing at run time.

`ComponentTrackingActivator` creates each component as the default activator does and replaces the `_renderFragment` of every `ComponentBase` with the `Render` method of a `ComponentReadTracker`, which sets itself as the read target of the thread, invokes the original fragment and restores the previous target.

The private members, all verified once by `ComponentBaseAccessors.IsSupported`:

| Member | Used for |
|---|---|
| `ComponentBase._renderFragment` | wrapping the render |
| `ComponentBase._renderHandle`, `_hasPendingQueuedRender` | rendering on a change, as `StateHasChanged` does without asking `ShouldRender` |
| `RenderHandle._renderer`, `Renderer.GetComponentState(IComponent)` | telling whether the renderer has disposed a component |
| `RenderTreeFrame.AttributeValueField` | taking ownership of content passed to components that are not a `ComponentBase` |

If one of them is missing, the activator logs an error and creates components unchanged.

From .NET 11 on, `IComponentPropertyActivator` (dotnet/aspnetcore#64595) is a second public service that receives every created instance. The wrapping can move there once the library targets .NET 11.

## Recording

`ReadPropertyRecorder.RecordOnCurrentThread(IReadPropertyRecorderTarget)` sets a `[ThreadStatic]` target until the returned `ref struct` is disposed. While a target is set, `ReadPropertyRecorder.ReadProperty` gives each read to it and not to the async-flow scopes of `ReadPropertyRecorder.Start`, which `TrackingScope` uses. A render is synchronous, so a thread static is exact and costs one thread-local read per intercepted read.

Content that a parent passes to a child, such as `<MudText>@motor.Speed</MudText>` or a grid's cell template, is invoked in the child's render and recorded by the child. On a change, only the child renders, and its render invokes the same content again.

### Rotating Sets

A tracker keeps two sets: the reads since the start of its last render (current), and those of the period before (previous). A property is acquired from the dispatcher while it is in either set. At the start of a render, the tracker releases what is in the previous set and not in the current one, and rotates. The first read of a property that the previous set does not hold acquires it **before** the value is read. Reads of content rendered after the component's own render, see below, therefore stay acquired until the next render reads them again, and a render that throws loses no acquired property. The sets are allocated on the first read, so a component that reads no subject costs one tracker and one delegate.

### Content of Components That Are Not a ComponentBase

`CascadingValue`, `DynamicComponent`, `LayoutView`, `RouteView`, `SectionContent` and `SectionOutlet` implement `IComponent` directly, so their renders are not wrapped. After a tracked render, `FragmentOwnership` walks the frames that the render added and wraps each `RenderFragment` attribute of such a component, so that the content records into the component that created it. That component renders again on a change, which renders the child with the content again: all of these render on every `SetParametersAsync`. The wrapped content walks its own frames as well, so nested cascading values work.

### Prerendering

While the renderer is not interactive (`RendererInfo.IsInteractive` is false), the tracker renders with a target that discards reads, so nothing is subscribed and no `TrackingScope` records them either.

## Change Dispatcher

`ComponentTrackingDispatcher` is a scoped service: one per circuit, and one per application on WebAssembly. Everything except a pending flag per property, a queue and the drain flag runs on the renderer's dispatcher, so the index needs no locks.

```
Render (renderer's dispatcher)            Write (any thread)                Drain (renderer's dispatcher)
 OnRead(P)                                 P = value                         every entry in the queue:
  └─ Acquire(P): entry for P,              └─ entry(P).OnChange               pending = 0
     SubscribeInline(P, entry) once            if pending 0 -> 1:             queue the trackers of the entry
     per circuit                                 enqueue entry               render the first tracker; its render
 read the value                                  schedule a drain             requests the others into the batch
```

- **Index:** `Dictionary<PropertyReference, Entry>`. An entry holds the inline subscription of its property and the trackers that read it, and is itself the `IPropertyChangeObserver`.
- **Change path:** `OnChange` sets the entry's pending flag and, when it was clear, enqueues the entry and schedules a drain. The queue holds each watched property at most once, so it is bounded by the number of watched properties however fast changes arrive or however long the circuit stalls.
- **Throttle:** a drain runs at once when the last one is older than `ThrottleMilliseconds`, otherwise at the end of the interval, through one reused timer, in a suppressed execution context so that no async-flow scope of the writer records the renders.
- **One batch per drain:** a render request outside a batch renders at once, one batch per component. The drain therefore renders the first tracker, and the start of its render requests the rest, which then join the batch in progress. A component that is requested twice, or that its parent renders in the same batch, renders once.
- **Rendering past `ShouldRender`:** a tracked change renders the component as `StateHasChanged` does, without asking `ShouldRender`. A component that renders only when its data changes cannot know that a value its render read has changed, and would otherwise show it stale.
- **Disposal:** the renderer tells no one when it disposes a component. At most once a second, at a render or a drain, the dispatcher releases the properties of trackers whose component the renderer no longer knows. A render request for a component disposed in between is skipped. When the circuit's scope ends, the dispatcher disposes every subscription.

### At-Least-Once

Per-property inline subscriptions deliver every write that commits after `SubscribeInline` returns. A property is subscribed before its value is read, so a write either commits before the read, and the render reads it, or after the subscription, and is delivered. The drain clears the pending flag before it routes, so a change during or after the drain enqueues the entry again. Routing and renders both run on the renderer's dispatcher, so a drain always sees the sets of completed renders.

### Cost

| Path | Steady state | When it changes |
|---|---|---|
| Component activation | one tracker and one delegate | |
| Read in a render | one thread-local read, two hash set operations | first read of a property in a circuit: entry and subscription |
| End of a render | one pass over its frames | content of a non-`ComponentBase` component: one delegate per fragment |
| Start of a render | one pass over the previous set | released properties: subscription disposal |
| Change of a watched property | one interlocked exchange, enqueue at most once per drain | |
| Change of an unwatched property | nothing for the circuit | |
| Drain | one queue pass and one `InvokeAsync` per interval | |

`TrackingScope` subscribes the context-wide observable per scope, so every change of the whole context costs every scope of every circuit a scheduled callback and buffer lists.

## Coexistence with TrackingScope

`TrackingScope` is unchanged. With component tracking registered, the scope's own fragment is the render of a `ComponentBase` and its content is owned like any other, so the scope records only reads that happen outside a render. Without the registration nothing changes.

## Alternatives

| | Activator (chosen) | `TrackedContent` wrapper | Tracked base class | One `TrackingScope` per component |
|---|---|---|---|---|
| Boundary | each component's own render | the wrapper's render | the component's render | until `OnAfterRender` of the batch |
| Transparent | yes | no: every component wraps its markup | no: every component inherits | no |
| Content of library components | owned exactly | unowned unless wrapped in every template | not reachable | over-tracked |
| Private members | six, verified at startup | none | reimplements `ComponentBase` | none |

A base class cannot wrap `BuildRenderTree` either: Razor overrides it, `ComponentBase` invokes it from a private fragment, and `IComponent.Attach` is implemented explicitly. Per-component scopes over-track with the square of the scopes in a batch. For the dispatcher, a subscription per component costs one batch per component and one subscription per component and property, and one context-wide observable per circuit puts every change of the context into every circuit's queue.

## Measurements

A server application with live dashboards, one browser, components counted from the render log (`RenderingComponent`, one event per component render) over 10 seconds, with a page-wide `TrackingScope` before and component tracking after. Hidden tabs were kept rendered before and are not after.

| Page | Components rendered/s before | after | Batches/s before | after |
|---|---:|---:|---:|---:|
| Dashboard (about 3,000 components) | 25,536 | 255 | 71.6 | 5.1 |
| Page with references and expressions | 2,342 | 10.7 | 9.5 | 1.7 |
| Page with inline expressions | 399 | 14.9 | 9.3 | 1.9 |
| Page without live values | 29 | 29 | 0.2 | 0.2 |

The same values changed on screen before and after on every page. Server CPU with the dashboard open fell from 14.2% to 2.0% of one core, against about 2% idle.

## Risks

- **Private members** may change in a framework update. The startup check then logs an error and components render without tracking; the test `WhenTheFrameworkIsReferenced_ThenThePrivateMembersAreFound` fails on such an update.
- **Reads outside a render** (lifecycle methods, after an await, event handlers) are not tracked. A component must read the values it shows in its render.
- **Bypassing `ShouldRender`** renders a component that chose not to render when a value its render read changed. A component that renders expensive content and reads live values in it renders for each of their changes.
