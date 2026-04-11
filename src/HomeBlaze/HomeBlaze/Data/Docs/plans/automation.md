---
title: Automation and Workflows
navTitle: Automation
status: Planned
---

# Automation and Workflows Plan

**Status: Planned**

## Problem

HomeBlaze has a reactive knowledge graph with property change tracking, operations, and AI agent integration, but no way to define deterministic automation rules. Operators cannot express "if temperature exceeds 80, start the fan" without writing C# subject code or relying on external AI agents. The platform needs a built-in automation system that covers simple trigger-action rules, multi-step workflows, and full scripting, with a progressive UI that matches the user's skill level.

## Design Principles

1. **Unified model** -- A state machine is the canonical representation. Simple rules, sequences, and complex workflows are all state machines at different complexity levels. One JSON schema, one execution engine.
2. **Progressive UI** -- The same JSON renders as an IFTTT form (simple case), a visual state machine diagram (advanced case), or a code editor (power user). The UI auto-detects complexity and the user can switch views.
3. **Subject-based providers** -- Script engines follow the same provider pattern as LLM providers (`IScriptProvider` subjects referenced by path), keeping configuration visible in the knowledge graph.
4. **Multi-language extensibility** -- JavaScript, C#, Python, and future languages are plugins. The core automation engine is language-agnostic.
5. **Everything is a subject** -- Automations, scripts, and providers are all subjects with `[State]`, `[Configuration]`, and `[Operation]` properties.
6. **Generic and extensible** -- Prefer composable, typed extension points (operators, action types, trigger types) over hard-coded features. New capabilities should not require schema changes.
7. **Triggers and evaluation are independent** -- Triggers determine *when* to evaluate. Variables are resolved fresh from the knowledge graph on each evaluation (and re-resolved before each action). Conditions operate on current variable values. This clean separation means trigger combination (AND/OR) is unnecessary -- multiple triggers provide OR, conditions provide AND.

## Package Structure

| Package | Contents |
|---|---|
| `HomeBlaze.Automation.Abstractions` | `IScriptProvider`, `IScriptEngine`, action/trigger interfaces |
| `HomeBlaze.Automation` | Core engine, `Automation` subject, `Script` subject, built-in actions/operators, DynamicExpresso conditions |
| `HomeBlaze.Automation.Blazor` | IFTTT editor, state machine diagram editor, variable picker, action editor |
| `HomeBlaze.Automation.JavaScript` | Jint-based `IScriptProvider` |
| `HomeBlaze.Automation.CSharp` | Roslyn-based `IScriptProvider` |

### Dependency Flow

```
HomeBlaze.Automation.Blazor --> HomeBlaze.Automation --> HomeBlaze.Automation.Abstractions
                                                    --> HomeBlaze.Services
                                                    --> DynamicExpresso.Core

HomeBlaze.Automation.JavaScript --> HomeBlaze.Automation.Abstractions
                                --> Jint

HomeBlaze.Automation.CSharp --> HomeBlaze.Automation.Abstractions
                            --> Microsoft.CodeAnalysis.CSharp.Scripting
```

## Core Model

### Automation Subject

An `Automation` is a subject that defines a state machine with triggers, variables, states, and transitions.

```csharp
[InterceptorSubject]
public partial class Automation : BackgroundService, ITitleProvider, IIconProvider
{
    // Configuration
    [Configuration] public partial string? Name { get; set; }
    [Configuration] public partial bool Enabled { get; set; }
    [Configuration] public partial string Mode { get; set; } // "single", "restart", "queued", "parallel"
    [Configuration] public partial string OnError { get; set; } // "abort", "continue"
    [Configuration] public partial bool DryRun { get; set; }
    [Configuration] public partial List<TriggerDefinition>? Triggers { get; set; }
    [Configuration] public partial Dictionary<string, VariableBinding>? Variables { get; set; }
    [Configuration] public partial string? InitialState { get; set; }
    [Configuration] public partial Dictionary<string, StateDefinition>? States { get; set; }

    // Template support
    [Configuration] public partial string? BasePath { get; set; }
    [Configuration] public partial Dictionary<string, string>? Parameters { get; set; }
    [Configuration] public partial bool IsTemplate { get; set; }

    // Runtime state
    [State("Current State")] public partial string? CurrentState { get; set; }
    [State("Status")] public partial string? Status { get; set; }
    [State("Last Triggered")] public partial DateTime? LastTriggeredTime { get; set; }
    [State("Trigger Count")] public partial int TriggerCount { get; set; }
    [State("Last Error")] public partial string? LastError { get; set; }
    [State("Last Dry Run Result")] public partial string? LastDryRunResult { get; set; }

    // Operations
    [Operation(Title = "Enable")] public void Enable() { Enabled = true; }
    [Operation(Title = "Disable")] public void Disable() { Enabled = false; }
    [Operation(Title = "Reset")] public void Reset() { CurrentState = InitialState; }
    [Operation(Title = "Trigger Now")] public Task TriggerAsync(CancellationToken ct) { ... }

    string? ITitleProvider.Title => Name;
}
```

### Concurrency Modes

When a trigger fires while the automation is already executing (e.g., mid-delay):

| Mode | Behavior | Use Case |
|---|---|---|
| `single` (default) | Ignore new trigger while running | Safe default, no overlap |
| `restart` | Cancel current run, start from scratch | Timer-reset patterns (e.g., motion-activated light resets on new motion) |
| `queued` | Queue trigger, execute after current finishes | Ordered processing, sequential device commands |
| `parallel` | Allow concurrent runs | Independent actions (e.g., per-room notifications) |

### Error Handling

When an action fails during execution:

| Mode | Behavior |
|---|---|
| `abort` (default) | Stop action sequence, set `LastError`, stay in current state (don't transition). Wait for next trigger |
| `continue` | Log error, skip failed action, execute remaining actions, complete transition |

Configurable per automation via `OnError`.

### Dry Run Mode

When `DryRun` is true, the engine evaluates triggers, resolves variables, matches transitions, and walks the action sequence, but does not execute actions. Instead it logs what would happen to `LastDryRunResult`. Useful for testing automations before enabling them.

### State Definition

```csharp
public class StateDefinition
{
    public List<ActionDefinition>? OnEnter { get; set; }
    public List<ActionDefinition>? OnExit { get; set; }
    public List<TransitionDefinition>? Transitions { get; set; }

    // Visual editor metadata
    public double? X { get; set; }
    public double? Y { get; set; }
}
```

### Transition Definition

```csharp
public class TransitionDefinition
{
    public Dictionary<string, VariableBinding>? Variables { get; set; }
    public string? Condition { get; set; }
    public List<OperatorDefinition>? Operators { get; set; }
    public string? Target { get; set; }
    public List<ActionDefinition>? Actions { get; set; }
}
```

### Variable Binding

Variables bind subject property paths to short names used in conditions and scripts.

```csharp
public class VariableBinding
{
    public string? Path { get; set; }
}
```

Scope resolution: transition-level variables override automation-level variables. Conditions and actions within a transition see both scopes merged.

**Variable resolution timing**: Variables are resolved fresh from the knowledge graph before each action execution. This ensures actions in a sequence with delays always see current values, not stale trigger-time snapshots.

**Future extension**: `VariableBinding` is designed for extension with additional fields (e.g., `Aggregate`, `Duration`, `Since` for time-window aggregation once the time-series store is available). See [Future Enhancements](#future-enhancements).

### Trigger Definition

```csharp
public class TriggerDefinition
{
    public string? Type { get; set; }       // "propertyChanged", "cron"
    public string? Path { get; set; }       // subject property path (for propertyChanged)
    public string? Expression { get; set; } // cron expression (for cron)
    public List<OperatorDefinition>? Operators { get; set; }
}
```

| Trigger Type | Fires When |
|---|---|
| `propertyChanged` | A property at `path` changes value |
| `cron` | Cron schedule matches (e.g., `0 */5 * * * *` = every 5 minutes) |

Triggers determine *when* to evaluate, not *what* to decide. Multiple triggers on an automation act as OR -- any trigger firing causes evaluation. Edge detection (rising/falling, threshold crossing) is handled by conditions and state transitions, not triggers.

### Operators

Operators are composable temporal transforms applied to triggers and transitions. They control *when* and *how often* triggers fire or transitions match, without changing the core trigger/condition/action model. Operators are typed and extensible -- new operator types can be added without schema changes.

```csharp
public class OperatorDefinition
{
    public string? Type { get; set; }
    // Fields vary by type
}
```

#### Operators on Triggers

Transform the trigger event stream before evaluation:

```json
{
    "type": "propertyChanged",
    "path": "/Sensors/Temperature",
    "operators": [
        { "type": "debounce", "duration": "00:00:30" },
        { "type": "throttle", "maxFrequency": "00:01:00" }
    ]
}
```

#### Operators on Transitions

Transform how transition conditions are evaluated:

```json
{
    "condition": "temp > 80",
    "operators": [
        { "type": "for", "duration": "00:05:00" }
    ],
    "target": "alerting"
}
```

#### Built-in Operator Types

| Operator | On Trigger | On Transition | Meaning |
|---|---|---|---|
| `debounce` | Wait for settling after last change | Wait for condition to settle | "Stop changing for X before I fire" |
| `throttle` | Max once per interval | Max once per interval | "Don't fire more than once per X" |
| `for` | Property held value for X | Condition true for X continuously | "Must be stable for X" |
| `cooldown` | Ignore triggers for X after firing | Ignore matches for X after firing | "After acting, rest for X" |
| `count` | Fire after N trigger events | Fire after condition true N times | "Need N occurrences" |

Operators compose: a trigger with `[debounce, throttle]` first waits for the value to settle, then rate-limits the resulting events. Order matters.

### Action Definition

```csharp
public class ActionDefinition
{
    public string? Type { get; set; }
    // Fields vary by type -- see Built-in Action Types below
}
```

## Built-in Action Types

### Simple Actions

| Type | Fields | Description |
|---|---|---|
| `invokeMethod` | `path`, `method`, `arguments` | Invoke an `[Operation]` on any subject (including Script subjects) |
| `setProperty` | `path`, `value` | Set a property value on a subject |
| `delay` | `duration` (TimeSpan) | Pause execution before the next action |
| `notify` | `channel` (path), `title`, `message` | Send notification via `INotificationChannel` subject |
| `script` | `providerPath`, `code`, `variables` | Execute inline script via an `IScriptProvider` |

### Composite Actions (Flow Control)

Composite actions contain other actions, enabling branching, looping, and parallel execution within a single transition's action list. They nest naturally -- a `parallel` can contain `ifElse` actions, an `ifElse` can contain `repeat`, etc.

#### ifElse

Conditional branching within an action sequence:

```json
{
    "type": "ifElse",
    "condition": "hour >= 22",
    "then": [
        { "type": "setProperty", "path": "/Climate/TargetTemp", "value": 18 }
    ],
    "else": [
        { "type": "setProperty", "path": "/Climate/TargetTemp", "value": 22 }
    ]
}
```

The `condition` is evaluated using DynamicExpresso with the current variable scope (same as transition conditions). The `else` branch is optional.

#### repeat

Loop with a condition:

```json
{
    "type": "repeat",
    "while": "brightness < 100",
    "actions": [
        { "type": "setProperty", "path": "/Light/Brightness", "value": "brightness + 10" },
        { "type": "delay", "duration": "00:00:01" }
    ],
    "maxIterations": 100
}
```

Supports `while` (check before each iteration) or `until` (check after each iteration). `maxIterations` is a safety limit to prevent infinite loops.

#### parallel

Execute multiple actions simultaneously:

```json
{
    "type": "parallel",
    "actions": [
        { "type": "invokeMethod", "path": "/Light1", "method": "TurnOn" },
        { "type": "invokeMethod", "path": "/Light2", "method": "TurnOn" },
        { "type": "invokeMethod", "path": "/Blinds", "method": "Close" }
    ]
}
```

All actions start concurrently. The `parallel` action completes when all child actions complete (or when the first one fails, depending on `OnError` mode).

### Action Execution

Actions within a transition execute sequentially by default. A `delay` action pauses the sequence (the automation remains in its current state during the delay). If the process restarts during a delay, the sequence restarts from the beginning of the transition's action list (ephemeral execution -- acceptable for home/building automation timescales).

Variables are re-resolved fresh from the knowledge graph before each action, ensuring actions after delays always see current values.

## State Machine Execution

### Execution Flow

When a trigger fires:

1. If `Enabled` is false or `IsTemplate` is true, ignore.
2. Apply concurrency mode:
   - `single`: if already running, ignore this trigger.
   - `restart`: if already running, cancel current execution, proceed.
   - `queued`: if already running, enqueue this trigger.
   - `parallel`: proceed regardless.
3. Resolve the automation's effective definition (if `BasePath` is set, load template and substitute `Parameters`).
4. Resolve all automation-level variables (read current property values from the knowledge graph).
5. Look up the `CurrentState` in `States`.
6. Evaluate the state's transitions **in order** (first match wins):
   a. Resolve transition-level variables (merged with automation-level, transition overrides).
   b. Evaluate transition `Operators` (e.g., `for` -- has the condition been continuously true for the required duration?).
   c. Evaluate `Condition` using DynamicExpresso with resolved variables. No condition = always true.
   d. If operators pass and condition is true:
      - If `DryRun`: log what would happen to `LastDryRunResult`, do not execute.
      - Execute `OnExit` actions of the current state (if `Target` differs from current state).
      - Execute the transition's `Actions` sequentially (re-resolving variables before each action).
      - If `Target` is specified, move `CurrentState` to `Target`.
      - Execute `OnEnter` actions of the new state (if state changed).
      - **Stop** -- do not evaluate further transitions.
   e. If false: try next transition.
7. If no transition matched, do nothing.
8. Update `LastTriggeredTime`, increment `TriggerCount`.

### First Match Wins

Transitions are evaluated top to bottom. The first transition whose condition is true fires; the rest are skipped. This gives deterministic priority controlled by ordering.

### IFTTT as Degenerate State Machine

A rule with 1 state and self-transitions (no `target`) is an IFTTT rule:

```json
{
    "$type": "HomeBlaze.Automation.Automation",
    "name": "High Temp Alert",
    "enabled": true,
    "triggers": [
        { "type": "propertyChanged", "path": "/Sensors/Temperature" }
    ],
    "variables": {
        "temp": { "path": "/Sensors/Temperature" }
    },
    "initialState": "active",
    "states": {
        "active": {
            "transitions": [
                {
                    "condition": "temp > 80",
                    "actions": [
                        { "type": "invokeMethod", "path": "/Fan", "method": "Start" }
                    ]
                }
            ]
        }
    }
}
```

The IFTTT UI hides the state machine structure -- it just shows trigger, condition, and action fields.

### Stateful Automation Example

```json
{
    "$type": "HomeBlaze.Automation.Automation",
    "name": "HVAC Controller",
    "enabled": true,
    "mode": "single",
    "triggers": [
        { "type": "propertyChanged", "path": "/Sensors/Temperature" }
    ],
    "variables": {
        "temp": { "path": "/Sensors/Temperature" }
    },
    "initialState": "idle",
    "states": {
        "idle": {
            "onEnter": [
                { "type": "notify", "channel": "/Notifications/Log", "message": "HVAC idle" }
            ],
            "transitions": [
                {
                    "condition": "temp > 26",
                    "target": "cooling",
                    "actions": [
                        { "type": "invokeMethod", "path": "/AC", "method": "Start" }
                    ]
                },
                {
                    "condition": "temp < 18",
                    "target": "heating",
                    "actions": [
                        { "type": "invokeMethod", "path": "/Heater", "method": "Start" }
                    ]
                }
            ]
        },
        "cooling": {
            "transitions": [
                {
                    "condition": "temp < 23",
                    "target": "idle",
                    "actions": [
                        { "type": "invokeMethod", "path": "/AC", "method": "Stop" }
                    ]
                }
            ]
        },
        "heating": {
            "transitions": [
                {
                    "condition": "temp > 21",
                    "target": "idle",
                    "actions": [
                        { "type": "invokeMethod", "path": "/Heater", "method": "Stop" }
                    ]
                }
            ]
        }
    }
}
```

### Script Action Example

```json
{
    "$type": "HomeBlaze.Automation.Automation",
    "name": "Smart Charging",
    "enabled": true,
    "triggers": [
        { "type": "cron", "expression": "0 */5 * * * *" }
    ],
    "variables": {
        "solar": { "path": "/Sensors/SolarPower" },
        "battery": { "path": "/Sensors/BatteryLevel" }
    },
    "initialState": "active",
    "states": {
        "active": {
            "transitions": [
                {
                    "actions": [
                        {
                            "type": "script",
                            "providerPath": "/Providers/JavaScript",
                            "code": "if (solar > 3000 && battery > 80) { invoke('/Wallbox', 'SetCurrent', 16); } else { invoke('/Wallbox', 'SetCurrent', 6); }"
                        }
                    ]
                }
            ]
        }
    }
}
```

### Template Instantiation Example

Template definition (reusable motion-activated light pattern):

```json
{
    "$type": "HomeBlaze.Automation.Automation",
    "name": "Motion-Activated Light (Template)",
    "isTemplate": true,
    "triggers": [
        { "type": "propertyChanged", "path": "$motionSensor" }
    ],
    "variables": {
        "motion": { "path": "$motionSensor" }
    },
    "initialState": "off",
    "states": {
        "off": {
            "transitions": [
                {
                    "condition": "motion == true",
                    "target": "on",
                    "actions": [
                        { "type": "invokeMethod", "path": "$light", "method": "TurnOn" }
                    ]
                }
            ]
        },
        "on": {
            "transitions": [
                {
                    "condition": "motion == false",
                    "operators": [{ "type": "for", "duration": "$timeout" }],
                    "target": "off",
                    "actions": [
                        { "type": "invokeMethod", "path": "$light", "method": "TurnOff" }
                    ]
                }
            ]
        }
    }
}
```

Instance (binds the template to specific devices):

```json
{
    "$type": "HomeBlaze.Automation.Automation",
    "name": "Motion Light - Kitchen",
    "enabled": true,
    "basePath": "/Templates/MotionLight",
    "parameters": {
        "motionSensor": "/Sensors/Kitchen/Motion",
        "light": "/Lights/Kitchen",
        "timeout": "00:05:00"
    }
}
```

The engine resolves `BasePath`, loads the template, substitutes all `$parameter` references with the instance's `Parameters` values, and executes the resulting automation. The instance can override `Enabled`, `Mode`, `OnError`, and other top-level configuration.

## Script Providers

Script providers follow the same pattern as LLM providers in [AI Agents](ai-agents.md): a provider subject holds engine configuration and sandbox settings. Script actions and standalone Script subjects reference a provider by path.

### IScriptProvider Interface

```csharp
// HomeBlaze.Automation.Abstractions
public interface IScriptProvider
{
    string Language { get; }
    IScriptEngine CreateEngine();
}

public interface IScriptEngine : IDisposable
{
    Task<object?> EvaluateAsync(string expression, IReadOnlyDictionary<string, object?> variables, CancellationToken cancellationToken);
    Task ExecuteAsync(string script, ScriptExecutionContext context, CancellationToken cancellationToken);
}

public class ScriptExecutionContext
{
    public IReadOnlyDictionary<string, object?> Variables { get; init; }

    // Helper functions available in scripts
    public Func<string, string, object[]?, Task> InvokeMethodAsync { get; init; }
    public Func<string, object?, Task> SetPropertyAsync { get; init; }
    public Func<string, object?> GetProperty { get; init; }
}
```

### Provider Implementations

```csharp
// HomeBlaze.Automation.JavaScript
[InterceptorSubject]
public partial class JavaScriptScriptProvider : IScriptProvider, ITitleProvider
{
    [Configuration] public partial int MaxExecutionTimeMs { get; set; } = 5000;
    [Configuration] public partial int MaxMemoryMb { get; set; } = 64;
    [Configuration] public partial int MaxRecursionDepth { get; set; } = 100;

    string? ITitleProvider.Title => "JavaScript";
    public string Language => "javascript";

    public IScriptEngine CreateEngine()
        => new JintScriptEngine(MaxExecutionTimeMs, MaxMemoryMb, MaxRecursionDepth);
}

// HomeBlaze.Automation.CSharp
[InterceptorSubject]
public partial class CSharpScriptProvider : IScriptProvider, ITitleProvider
{
    [Configuration] public partial string[]? AdditionalAssemblies { get; set; }
    [Configuration] public partial int MaxExecutionTimeMs { get; set; } = 10000;

    string? ITitleProvider.Title => "C# Script";
    public string Language => "csharp";

    public IScriptEngine CreateEngine()
        => new RoslynScriptEngine(AdditionalAssemblies, MaxExecutionTimeMs);
}
```

### Provider Storage

```json
{
    "$type": "HomeBlaze.Automation.JavaScript.JavaScriptScriptProvider",
    "maxExecutionTimeMs": 5000,
    "maxMemoryMb": 64,
    "maxRecursionDepth": 100
}
```

## Standalone Script Subjects

A `Script` subject holds reusable code with a reference to a script provider. It can be invoked directly via its `[Operation]` or referenced from automation actions via `invokeMethod`.

```csharp
// HomeBlaze.Automation
[InterceptorSubject]
public partial class Script : ITitleProvider, IIconProvider
{
    // Configuration
    [Configuration] public partial string? Name { get; set; }
    [Configuration] public partial string? ProviderPath { get; set; }
    [Configuration] public partial Dictionary<string, VariableBinding>? Variables { get; set; }
    [Configuration] public partial string? Code { get; set; }

    // Runtime state
    [State("Last Result")] public partial object? LastResult { get; set; }
    [State("Last Error")] public partial string? LastError { get; set; }
    [State("Last Run")] public partial DateTime? LastRunTime { get; set; }
    [State("Run Count")] public partial int RunCount { get; set; }

    // Operations
    [Operation(Title = "Run")]
    public async Task RunAsync(CancellationToken cancellationToken) { ... }
}
```

```json
{
    "$type": "HomeBlaze.Automation.Script",
    "name": "Calculate Energy Balance",
    "providerPath": "/Providers/JavaScript",
    "variables": {
        "solar": { "path": "/Sensors/SolarPower" },
        "battery": { "path": "/Sensors/BatteryLevel" }
    },
    "code": "return solar - battery * 0.1;"
}
```

Referenced from an automation action:
```json
{ "type": "invokeMethod", "path": "/Scripts/EnergyBalance", "method": "Run" }
```

## Condition Evaluation

Conditions use **DynamicExpresso** (lightweight, C#-like expression evaluator, proven in HomeBlaze v1). No script provider is needed for conditions -- they are always evaluated by DynamicExpresso with resolved variables injected as parameters.

### Supported Expressions

```
temp > 80
temp > 26 && humidity < 80
status == "Running"
hour >= 22 || hour < 6
(solar - consumption) > 1000
```

### Built-in Variables

In addition to user-defined variables, the following are always available:

| Variable | Type | Description |
|---|---|---|
| `now` | `DateTime` | Current local time |
| `utcNow` | `DateTime` | Current UTC time |
| `hour` | `int` | Current hour (0-23) |
| `minute` | `int` | Current minute (0-59) |
| `dayOfWeek` | `DayOfWeek` | Current day of week |
| `timeInCurrentState` | `TimeSpan` | Duration in the current state |

## Progressive UI

The same JSON model renders in three UI modes. The UI auto-detects the appropriate default based on complexity, but the user can always switch. The automation edit component is registered as `[SubjectComponent(SubjectComponentType.Edit, typeof(Automation))]` and implements `ISubjectEditComponent` following the established HomeBlaze pattern.

### Overall Layout

```
+-------------------------------------------------------------+
|  [IFTTT] [Diagram] [Code]          [Enabled v] [Dry Run  ]  |
|  Name: [_________________________]  Mode: [Single v]         |
|  Triggers: [propertyChanged v] [/Sensors/Temp] [+ Add]      |
|  Variables: temp = [/Sensors/Temp] [+ Add]                   |
+-----------------------------------------+-------------------+
|                                         |                   |
|  Main area (view-dependent)             |  Edit panel       |
|                                         |  (diagram view    |
|  IFTTT:  full-width form                |   only)           |
|  Diagram: Excubo canvas                 |                   |
|  Code:   Monaco editor (full-width)     |  Shows selected   |
|                                         |  state or         |
|                                         |  transition       |
|                                         |  details          |
+-----------------------------------------+-------------------+
```

Top section (shared across all views): name, enabled/dry-run toggles, mode selector, trigger list, variable bindings. These use existing MudBlazor components (`MudTextField`, `MudSelect`, `MudSwitch`) and `SubjectPathField` for path selection.

The side edit panel only appears in diagram view. IFTTT and code views are full-width since they are self-contained editors.

### IFTTT View

Shown by default when the automation has 1 state with self-transitions only (no `target` fields). Full-width form below the shared header:

```
+-----------------------------------------+
| When:     [temp > 80                ]   |
|                                         |
| Then:                                   |
|   [Invoke Method v] [/Fan] -> [Start]   |
|   + Add action                          |
+-----------------------------------------+
```

Since there's only one state with self-transitions, the UI hides the state machine structure and shows condition + actions directly. The action list uses the ActionListEditor component (see below).

### State Machine View

Shown by default when the automation has multiple states. Split layout with Excubo.Blazor.Diagrams canvas on the left and an edit panel on the right.

**Diagram canvas (left ~60%)**:

Uses `Excubo.Blazor.Diagrams` (`<Diagram>`, `<Nodes>`, `<Links />`), following the same pattern as `CanvasLayoutWidget` in `HomeBlaze.Components`. States render as `<Node>` elements with state name, current-state indicator, and entry/exit action count. Transitions render as `<Link>` elements between nodes with condition text as labels.

```
+-------------------------------------+
|  [+ Add State]                      |
|                                     |
|   +--------+  temp > 26  +-------+  |
|   | *idle  | ----------> |cooling|  |
|   |        | <---------- |       |  |
|   +--------+  temp < 23  +-------+  |
|       |                             |
|       | temp < 18                   |
|       v                             |
|   +--------+                        |
|   |heating |                        |
|   |        |                        |
|   +--------+                        |
+-------------------------------------+
```

- `*` indicates the current state at runtime
- Click state node -> edit panel shows state details
- Click transition arrow -> edit panel shows transition details
- Drag from state edge -> create new transition
- Click canvas -> add new state at position
- Drag state -> reposition (with snap-to-grid, auto-persist)

**Edit panel (right ~40%)**:

Context-dependent based on selection:

*When a state is selected:*
```
+---------------------------+
| State: [idle          ]   |
|                           |
| On Enter:                 |
|   (ActionListEditor)      |
|   + Add action            |
|                           |
| On Exit:                  |
|   (ActionListEditor)      |
|   + Add action            |
|                           |
| Transitions:              |
|   1. temp > 26 -> cooling |
|   2. temp < 18 -> heating |
|   + Add transition        |
+---------------------------+
```

*When a transition is selected:*
```
+---------------------------+
| Condition: [temp > 26  ]  |
| Target: [cooling v]       |
|                           |
| Operators:                |
|   [for v] duration: 5m    |
|   + Add operator          |
|                           |
| Actions:                  |
|   (ActionListEditor)      |
|   + Add action            |
+---------------------------+
```

### Code View

Always available. Full-width Monaco editor (`BlazorMonaco.Editor.StandaloneCodeEditor`) showing the full automation JSON, following the same pattern as `JsonFileEditComponent` in `HomeBlaze.Storage.Blazor`. Theme: "vs-dark", automatic layout.

### View Switching

- View toggle buttons in the top bar. Active view is highlighted.
- IFTTT -> State Machine: User adds a second state or a transition with a `target`. UI suggests switching to diagram view.
- State Machine -> IFTTT: User simplifies to 1 state. UI suggests switching to IFTTT view.
- Code -> other views: JSON is parsed and validated before switching. Invalid JSON shows an error.
- Auto-detection on load: 1 state with no targets = IFTTT, otherwise = Diagram.

### ActionListEditor Component

Reusable recursive component for editing action lists. Used in IFTTT view, state edit panel, and transition edit panel.

Each action is a row with a type selector and type-specific inline fields. Composite actions (ifElse, repeat, parallel) expand to show nested `ActionListEditor` instances with visual indentation:

```
Actions:
+- [Invoke Method v]  /Fan -> Start              [drag] [x]
+- [If/Else v]        hour >= 22                 [drag] [x]
|  +- Then:
|  |  +- [Set Property v]  /Light/Brightness = 20  [drag] [x]
|  |  +- + Add action
|  +- Else:
|     +- [Set Property v]  /Light/Brightness = 100 [drag] [x]
|     +- + Add action
+- [Parallel v]                                  [drag] [x]
|  +- [Invoke Method v]  /Blind1 -> Close        [drag] [x]
|  +- [Invoke Method v]  /Blind2 -> Close        [drag] [x]
|  +- + Add action
+- + Add action
```

**Implementation**: Recursive Blazor component. `ActionListEditor` renders a list of `ActionEditor` instances. `ActionEditor` renders inline fields for simple action types and nested `ActionListEditor` for composite types. Type selector (`MudSelect`) determines which fields are shown. Subject paths use `SubjectPathField` (existing component). Drag handle for reordering. Nesting depth capped at 3-4 levels.

### Reused Existing Components

| Existing Component | Used For |
|---|---|
| `SubjectPathField` / `SubjectPathPicker` | Variable path binding, trigger paths, action target paths |
| `SubjectPropertyPanel` | Renders runtime [State] properties and [Operation] buttons automatically |
| `StandaloneCodeEditor` (BlazorMonaco) | Code view JSON editor |
| `Excubo.Blazor.Diagrams` | State machine diagram (nodes + links) |
| `MudDialog` / `SubjectEditDialog` | Template parameter dialog, confirmation dialogs |
| `PropertyEditor` | Inline editing of primitive action parameters |
| `SubjectComponentRegistry` | Auto-discovery of automation edit/page components |

## Storage Layout

```
Data/
+-- Providers/
|   +-- JavaScript.json          <-- IScriptProvider subject
|   +-- CSharpScript.json        <-- IScriptProvider subject
|   +-- ClaudeProvider.json      <-- ILlmProvider subject (AI agents)
+-- Templates/
|   +-- MotionLight.json         <-- Automation template (isTemplate: true)
|   +-- OffDelayTimer.json       <-- Standard timer template
+-- Scripts/
|   +-- EnergyBalance.json       <-- Standalone Script subject
|   +-- NotifyAdmin.json         <-- Standalone Script subject
+-- Automations/
|   +-- HighTempAlert.json       <-- Automation (IFTTT-simple)
|   +-- HvacController.json      <-- Automation (state machine)
|   +-- SmartCharging.json       <-- Automation (script-based)
|   +-- MotionLightKitchen.json  <-- Automation (template instance)
|   +-- MotionLightBedroom.json  <-- Automation (template instance)
```

## Key Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Unified model | State machine for all complexity levels | IFTTT rule is a 1-state machine, sequence is a chain, complex workflow is a graph. One engine, one JSON schema, progressive UI |
| Condition evaluator | DynamicExpresso (implicit, no provider) | Lightweight (~50KB), C#-like, proven in v1. Conditions are simple expressions, not scripts |
| Script engine abstraction | `IScriptProvider` subjects referenced by path | Consistent with `ILlmProvider` pattern. Configuration visible in graph. Languages are plugins |
| Multi-language | Separate NuGet packages per language | Core stays lightweight. Only pay for what you install |
| Transition evaluation | First match wins (ordered) | Deterministic, explicit priority, standard state machine semantics. No conflict resolution needed |
| On-entry/on-exit actions | Supported from the start | Avoids duplicating actions across transitions into/out of the same state |
| Workflow persistence | Ephemeral (restart from initial state on process restart) | Acceptable for home/building automation. Delay-based sequences restart from beginning. Avoids complex state persistence |
| Visual editor | MudBlazor native (IFTTT form + state machine diagram) | Consistent with HomeBlaze UI. No external designer dependency |
| State machine diagram | Excubo.Blazor.Diagrams or similar | Used in v1, MudBlazor-compatible, supports draggable nodes and connections |
| Standalone scripts | Script subjects with `[Operation] Run` | Reusable, invocable from automations via `invokeMethod`, testable independently |
| Variable scoping | Automation-level + transition-level override | Shared variables avoid duplication, transition-level allows local specialization |
| Variable resolution | Re-resolved fresh before each action | Ensures actions after delays see current values, not stale trigger-time snapshots |
| Built-in time variables | `now`, `hour`, `dayOfWeek`, `timeInCurrentState` | Common automation needs (time-of-day rules, state timeouts) without external dependencies |
| Temporal behavior | Composable operators on triggers and transitions | Generic, extensible pipeline (debounce, throttle, for, cooldown, count). New operators require no schema changes |
| Edge detection | Not needed as dedicated feature | Conditions + state transitions naturally handle rising/falling edges and threshold crossing |
| Action flow control | `ifElse`, `repeat`, `parallel` composite action types | Keeps operator-friendly flat action lists expressive without requiring script actions or extra states for simple branching |
| Concurrency modes | 4 modes: single (default), restart, queued, parallel | Adopted from Home Assistant. Proven model covering all concurrency scenarios |
| Error handling | Configurable per automation: abort (default) or continue | Safe default, with opt-in for partial execution when appropriate |
| Dry run mode | `DryRun` flag on Automation subject | Logs what would happen without executing. Enables safe testing of automations |
| Templates | `BasePath` + `Parameters` with `$parameter` substitution | Reusable automation patterns. Templates are regular automations with `IsTemplate: true`. Instances override only parameter bindings |
| Trigger combination | Not needed | Multiple triggers = OR, conditions = AND. Triggers and evaluation are independent concerns |
| Dataflow pipelines | Out of scope | Different paradigm (Node-RED style). Script actions cover in-automation data transformation |

## Dependencies

| Package | Dependency | Purpose |
|---|---|---|
| `HomeBlaze.Automation` | `DynamicExpresso.Core` | Condition expression evaluation |
| `HomeBlaze.Automation` | `Cronos` or similar | Cron trigger scheduling |
| `HomeBlaze.Automation.Blazor` | `Excubo.Blazor.Diagrams` (or similar) | State machine visual editor |
| `HomeBlaze.Automation.JavaScript` | `Jint` | JavaScript script engine |
| `HomeBlaze.Automation.CSharp` | `Microsoft.CodeAnalysis.CSharp.Scripting` | C# script engine |

## Evolution Path

| Stage | Description |
|---|---|
| **Stage 1** | Core engine: `Automation` subject, triggers (propertyChanged, cron), DynamicExpresso conditions, built-in simple actions (invokeMethod, setProperty, delay, notify), concurrency modes, error handling, dry run. IFTTT UI. |
| **Stage 2** | Composite actions (ifElse, repeat, parallel). Operators (debounce, throttle, for). State machine diagram UI (Excubo.Blazor.Diagrams). On-enter/on-exit actions. |
| **Stage 3** | `Script` subject. `IScriptProvider` abstraction. JavaScript provider (Jint). Inline script actions. |
| **Stage 4** | Template system (`BasePath`, `Parameters`, `IsTemplate`). Standard template library (timers, common patterns). |
| **Stage 5** | C# script provider (Roslyn). Code view (Monaco). Variable picker with subject path autocomplete. |
| **Stage 6** | Hierarchical states (composite states referencing templates via `basePath` on `StateDefinition`). Additional operator types. |

## Future Enhancements

The following are explicitly deferred but the design accommodates them without breaking changes:

### History Access in Conditions

`VariableBinding` is the extension point. Future fields like `Aggregate` (average, min, max, sum, count), `Duration` (rolling time window), and `Since` (startOfDay, startOfWeek, absolute timestamp) will enable time-window queries once the time-series store is implemented:

```json
{
    "avgTemp1h": { "path": "/Sensors/Temperature", "aggregate": "average", "duration": "01:00:00" },
    "energyToday": { "path": "/Sensors/EnergyTotal", "aggregate": "sum", "since": "startOfDay" }
}
```

### Scenes / State Snapshots

A `Scene` subject type that captures a set of property values and restores them atomically via `[Operation] Apply()`. Automations would activate scenes via `invokeMethod`. Not a fundamental gap -- multiple `setProperty` actions cover the same use case today.

### Hierarchical States

States within an automation can reference a template via `basePath` on `StateDefinition`, creating composite states with internal sub-state-machines. The parent sees the composite state as one state; internally the sub-machine runs. When the sub-machine reaches a terminal state, `onComplete` directs the parent to the next state. Parent-level transitions can interrupt the sub-machine.

### Additional Trigger Types

Event-based triggers (system events, message bus), startup triggers, and webhook triggers.

## Open Questions

- **Audit trail**: How to attribute automation-triggered changes in the knowledge graph. Relevant for [Audit](../architecture/design/audit.md).
- **Cross-instance**: Should automations on satellites reference subjects on the central instance (via proxied paths)? Likely yes -- paths resolve through the unified namespace naturally.
