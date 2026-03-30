---
title: AI Agents
navTitle: AI Agents
status: Planned
---

# Built-in AI Agents Plan

**Status: Planned**

**Prerequisites:**
- [MCP Server](../../../../../docs/plans/mcp-server.md) — core `Namotion.Interceptor.Mcp` package
- [HomeBlaze MCP Extensions](mcp-extensions.md) — HomeBlaze-specific tools, enrichers, type/path providers

## Problem

HomeBlaze exposes the knowledge graph to external AI agents via MCP, but has no built-in agent capability. Operators must run external tools (Claude Code, Claude Desktop) to get AI-powered analysis and automation. Built-in agents would run inside the HomeBlaze process as subjects — visible in the graph, configurable via the UI, reactive to property changes.

## Package Structure

| Package | Contents |
|---|---|
| `HomeBlaze.AI.Abstractions` | `ILlmProvider`, `ILlmAgent` interfaces |
| `HomeBlaze.AI` | Provider subjects, `LlmAgentBase`, `LlmAgent`, [MCP extensions](mcp-extensions.md) |

### Dependency Flow

```
HomeBlaze.AI → Namotion.Interceptor.Mcp → Namotion.Interceptor.Registry
             → Microsoft.Agents.AI → Microsoft.Extensions.AI
             → HomeBlaze.AI.Abstractions
```

## Design

### ILlmProvider — Shared LLM Configuration

A provider subject holds credentials and creates `IChatClient` instances. Multiple agents reference one provider by path. Each LLM service has its own subject type — no string-based switching, and each type only exposes the configuration fields it needs.

```csharp
// HomeBlaze.AI.Abstractions
public interface ILlmProvider
{
    IChatClient CreateChatClient();
}
```

Provider implementations use the existing `$type` polymorphism:

```csharp
// HomeBlaze.AI
[InterceptorSubject]
public partial class AnthropicProvider : ILlmProvider, ITitleProvider
{
    [Configuration] public partial string? Model { get; set; }
    [Configuration] public partial string? ApiKey { get; set; }

    string? ITitleProvider.Title => $"Anthropic ({Model})";

    public IChatClient CreateChatClient()
        => new AnthropicClient(ApiKey).Messages;
}

[InterceptorSubject]
public partial class OpenAiProvider : ILlmProvider, ITitleProvider
{
    [Configuration] public partial string? Model { get; set; }
    [Configuration] public partial string? ApiKey { get; set; }

    string? ITitleProvider.Title => $"OpenAI ({Model})";

    public IChatClient CreateChatClient()
        => new OpenAIClient(ApiKey).GetChatClient(Model).AsIChatClient();
}

[InterceptorSubject]
public partial class OllamaProvider : ILlmProvider, ITitleProvider
{
    [Configuration] public partial string? Model { get; set; }
    [Configuration] public partial string? Endpoint { get; set; }

    string? ITitleProvider.Title => $"Ollama ({Model})";

    public IChatClient CreateChatClient()
        => new OllamaChatClient(new Uri(Endpoint ?? "http://localhost:11434"), Model);
}
```

New providers (Azure OpenAI, AWS Bedrock, etc.) are added as new subject types — in `HomeBlaze.AI` or in third-party plugins.

### ILlmAgent — Agent Interface

```csharp
// HomeBlaze.AI.Abstractions
public interface ILlmAgent
{
    string? ProviderPath { get; }
    string? Status { get; }
    string? LastAnalysis { get; }
    DateTime? LastRunTime { get; }
}
```

### LlmAgentBase — Base Class

Base class providing MAF `ChatClientAgent` composition, run loop, queue-one concurrency, error handling, and state properties. Developers subclass this directly for specialized agents with custom tools and domain logic.

```csharp
// HomeBlaze.AI
[InterceptorSubject]
public abstract partial class LlmAgentBase : BackgroundService, ILlmAgent, ITitleProvider
{
    // Configuration
    [Configuration] public partial string? ProviderPath { get; set; }

    // State
    [State("Status")] public partial string? Status { get; set; }
    [State("Last Analysis")] public partial string? LastAnalysis { get; set; }
    [State("Last Run")] public partial DateTime? LastRunTime { get; set; }
    [State("Run Count")] public partial int RunCount { get; set; }

    // Operations
    [Operation(Title = "Run Now")]
    public Task RunAnalysisAsync() { ... }

    // Subclass hooks
    protected abstract string? GetInstructions();
    protected abstract IEnumerable<string>? GetWatchPaths();
    protected virtual IEnumerable<AIFunction> GetAdditionalTools() => [];
}
```

### LlmAgent — Generic Configurable Agent

A config-driven subclass of `LlmAgentBase`. Operators create instances via the UI or JSON files — no C# code needed per agent.

```csharp
// HomeBlaze.AI
[InterceptorSubject]
public partial class LlmAgent : LlmAgentBase
{
    [Configuration] public partial string? Name { get; set; }
    [Configuration] public partial string? Instructions { get; set; }
    [Configuration] public partial string[]? WatchPaths { get; set; }
    [Configuration] public partial TimeSpan? PollInterval { get; set; }
    [Configuration] public partial string[]? FilterRules { get; set; }

    string? ITitleProvider.Title => Name;

    protected override string? GetInstructions() => Instructions;
    protected override IEnumerable<string>? GetWatchPaths() => WatchPaths;
}
```

### Storage Layout

```
Data/
├── config/
│   ├── claude-provider.json
│   └── openai-provider.json
├── agents/
│   ├── temp-monitor.json
│   └── energy-reporter.json
```

**Provider config:**
```json
{
    "$type": "HomeBlaze.AI.AnthropicProvider",
    "model": "claude-sonnet-4-20250514",
    "apiKey": "sk-..."
}
```

**Agent config:**
```json
{
    "$type": "HomeBlaze.AI.LlmAgent",
    "name": "Temperature Monitor",
    "providerPath": "/config/claude-provider",
    "instructions": "You monitor temperature sensors. Alert via notification if any reading exceeds 80°C. Include the sensor path and current value in the alert.",
    "watchPaths": ["/sensors"],
    "pollInterval": "00:05:00"
}
```

## Observe-Think-Act Loop

When an agent runs (timer or property change trigger):

```
1. Resolve ILlmProvider from ProviderPath → get IChatClient
2. Create fresh ChatClientAgent with IChatClient + registered AIFunction tools
3. Pre-fetch current state of WatchPaths via query tool
4. Build prompt:
   - System: Instructions (from GetInstructions())
   - User: "Current state of watched paths:" + pre-fetched state
5. Call ChatClientAgent.RunAsync() — MAF handles the tool-calling loop internally:
   - May call read tools (query, get_property) to dig deeper
   - May call invoke_method to send notifications
   - Returns AgentResponse with analysis text
6. Store result in LastAnalysis, update Status, LastRunTime, RunCount
```

### Tool Access

Built-in agents reuse `McpToolInfo` handlers (from core MCP + [HomeBlaze MCP extensions](mcp-extensions.md)) wrapped as `AIFunction` objects — direct in-process calls, no MCP protocol overhead.

Stage 1 restricts write access:

| Tool | Available | Scope |
|---|---|---|
| `query` | Yes | Browse the graph |
| `get_property` | Yes | Read any value |
| `set_property` | **No** | Deferred until authorization |
| `list_types` | Yes | Discover capabilities and concrete types |
| `list_methods` | Yes | Notification channels only |
| `invoke_method` | **Restricted** | Only on `INotificationChannel` subjects |

### Pre-fetching Watched State

The agent calls `query(path, depth=2, includeProperties=true)` for each watch path before invoking the LLM. This provides immediate context without requiring the LLM to make round-trips for basic state inspection. The LLM can still call tools to explore further.

### Notification Integration

The agent's only action channel (stage 1) is sending notifications via `INotificationChannel`. The `invoke_method` tool is restricted to methods on subjects implementing `INotificationChannel`:

```csharp
// Tool filter (pseudo-code)
bool IsMethodAllowed(string path, string method)
{
    var subject = ResolvePath(path);
    return subject is INotificationChannel;
}
```

This lets agents send alerts (email, push, webhook) without being able to modify the knowledge graph.

### Concurrency — Queue One

A `_rerunRequested` flag handles concurrent triggers. If a trigger fires while the agent is already running, the flag is set. On completion, if the flag is set, clear it and run again with fresh state. No unbounded queue, no wasted LLM calls from cancellation, re-run always sees the latest state.

### Error Handling

On failure (LLM API down, rate limited, timeout), set `Status` to error message, log, and wait for the next trigger. No retry — the poll loop provides implicit retry.

## Evolution Path

| Stage | Description |
|---|---|
| **Stage 1** | `LlmAgentBase` + `LlmAgent`. Read-only + notify. Poll + change-triggered with filter rules. Queue-one concurrency. Fresh `ChatClientAgent` per run |
| **Stage 2** | Agent-to-agent calls via `invoke_method` on `ILlmAgent` (with recursion limits) |
| **Stage 3** | Full write access (`set_property`, unrestricted `invoke_method`) with per-agent authorization scopes |

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| LLM framework | MAF `ChatClientAgent` wrapping `IChatClient` | Built-in tool-calling loop, session management, standard .NET AI abstraction |
| Agent ↔ MAF | Composition — `LlmAgentBase` owns `ChatClientAgent` internally | Avoids fighting two frameworks' lifecycle models |
| Provider interface | `ILlmProvider` with `CreateChatClient()` only (no `Model`) | Consumer doesn't care which model — that's a provider config detail |
| Provider as subject | Referenced by path | Centralized credentials, multiple agents share one provider |
| Base class from day one | `LlmAgentBase` for specialized agents, `LlmAgent` for config-driven | Developers can subclass immediately, no waiting for later stages |
| Tool reuse | Transport-agnostic `McpToolInfo` (metadata + plain function), wrapped as `AIFunction` by agent | One implementation, any delivery mode. See [MCP Server](../../../../../docs/plans/mcp-server.md) |
| Safe by default | Read-only + notify, write access gated on authorization | Prevents accidental graph modification |
| Pre-fetched context | Watch paths queried before LLM call | Reduces round-trips and API cost |
| Per-run agent | Fresh `ChatClientAgent` per run, no session persistence | Simpler, cheaper, no conversation drift |
| Concurrency | Queue-one (`_rerunRequested` flag) | No triggers silently lost, no unbounded queue, re-run sees fresh state |
| Error handling | Log, set Status, wait for next trigger | Poll loop provides implicit retry |

## Dependencies

- `Microsoft.Agents.AI`: MAF `ChatClientAgent`, `AgentResponse`
- `Microsoft.Extensions.AI`: `IChatClient`, `AIFunction`, `AIFunctionFactory`
- `Anthropic` SDK (official): `IChatClient` implementation for Claude
- `Namotion.Interceptor.Mcp`: `McpToolInfo` handlers wrapped as `AIFunction` for built-in agents
- `HomeBlaze.Abstractions`: `INotificationChannel`, `ITitleProvider`
- `HomeBlaze.Services`: `SubjectPathResolver` for provider/watch path resolution
- Graph-level authorization: gating write access per agent

## Open Questions

- **Cost controls**: Budget/rate limiting per agent, fallback when LLM API is unavailable
- **Streaming**: Should agent analysis stream to the UI in real time, or only show the final result?
- **Audit**: How to attribute agent actions in the audit trail (see [Audit](../architecture/design/audit.md))
