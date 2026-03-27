---
title: AI Agents
navTitle: AI Agents
status: Planned
---

# Built-in AI Agents

**Status: Work in Progress**

**Prerequisites:**
- [Registry Attribute Migration](registry-attribute-migration.md) — HomeBlaze metadata provider reads from registry attributes instead of reflection
- [MCP Server](../../../../../docs/plans/mcp-server.md) — tool implementations reused by built-in agents as `AIFunction` objects

## Problem

HomeBlaze exposes the knowledge graph to external AI agents via MCP, but has no built-in agent capability. Operators must run external tools (Claude Code, Claude Desktop) to get AI-powered analysis and automation. Built-in agents would run inside the HomeBlaze process as subjects — visible in the graph, configurable via the UI, reactive to property changes.

## Package Structure

| Package | Contents |
|---|---|
| `HomeBlaze.AI.Abstractions` | `ILlmProvider`, `ILlmAgent` interfaces |
| `HomeBlaze.AI` | Provider subjects (`AnthropicProvider`, `OpenAiProvider`, `OllamaProvider`), `LlmAgent` subject, HomeBlaze MCP metadata provider (`HomeBlazeMcpMetadataProvider`, `StateAttributePathProvider`) |

## Design

### ILlmProvider — Shared LLM Configuration

A provider subject holds credentials and creates `IChatClient` instances. Multiple agents reference one provider by path. Each LLM service has its own subject type — no string-based switching, and each type only exposes the configuration fields it needs.

```csharp
// HomeBlaze.AI.Abstractions
public interface ILlmProvider
{
    string? Model { get; }
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
    string? Instructions { get; }
    string[]? WatchPaths { get; }
    string? Status { get; }
    string? LastAnalysis { get; }
    DateTime? LastRunTime { get; }
}
```

### LlmAgent — Generic Configurable Agent Subject

A single subject type that is instantiated per agent via configuration. No C# code needed per agent — operators create instances via the UI or JSON files.

```csharp
// HomeBlaze.AI
[InterceptorSubject]
public partial class LlmAgent : BackgroundService, ILlmAgent, ITitleProvider
{
    // Configuration
    [Configuration] public partial string? Name { get; set; }
    [Configuration] public partial string? ProviderPath { get; set; }
    [Configuration] public partial string? Instructions { get; set; }
    [Configuration] public partial string[]? WatchPaths { get; set; }
    [Configuration] public partial TimeSpan? PollInterval { get; set; }

    // State
    [State("Status")] public partial string? Status { get; set; }
    [State("Last Analysis")] public partial string? LastAnalysis { get; set; }
    [State("Last Run")] public partial DateTime? LastRunTime { get; set; }
    [State("Run Count")] public partial int RunCount { get; set; }

    // Operations
    [Operation(Title = "Run Now")]
    public Task RunAnalysisAsync() { ... }

    string? ITitleProvider.Title => Name;
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
    "providerPath": "Root.config[claude-provider.json]",
    "instructions": "You monitor temperature sensors. Alert via notification if any reading exceeds 80°C. Include the sensor path and current value in the alert.",
    "watchPaths": ["Root.sensors"],
    "pollInterval": "00:05:00"
}
```

## Observe-Think-Act Loop

When an agent runs (timer or property change trigger):

```
1. Resolve ILlmProvider from ProviderPath
2. Pre-fetch current state of WatchPaths via query tool
3. Build prompt:
   - System: Instructions
   - User: "Current state of watched paths:" + pre-fetched state
4. Send to LLM with available tools (via IChatClient + UseFunctionInvocation)
5. LLM responds:
   - May call read tools (query, get_property) to dig deeper
   - May call notify to send alerts
   - Returns analysis text
6. Store result in LastAnalysis, update Status, LastRunTime, RunCount
```

### Pre-fetching Watched State

The agent calls `query(path, depth=2, includeProperties=true)` for each watch path before invoking the LLM. This provides immediate context without requiring the LLM to make round-trips for basic state inspection. The LLM can still call tools to explore further.

## Tool Access — Safe by Default

Built-in agents reuse the MCP tool implementations as `AIFunction` objects (direct in-process calls, no MCP protocol overhead). Initially, write access is restricted:

| Tool | Available | Rationale |
|---|---|---|
| `query` | Yes | Browse the graph |
| `get_property` | Yes | Read any value |
| `set_property` | **No** | Unsafe without authorization |
| `list_types` | Yes | Discover capabilities |
| `list_methods` | Yes | See what's available |
| `invoke_method` | **Notify only** | Hardcoded to notification channel methods only |

`set_property` and unrestricted `invoke_method` are enabled when graph-level authorization (#137) is available. At that point, each agent gets a permission scope defining what it can read, write, and invoke.

## Notification Integration

The agent's only action channel (initially) is sending notifications via `INotificationChannel`. The `invoke_method` tool is restricted to methods on subjects implementing `INotificationChannel`:

```csharp
// Tool filter (pseudo-code)
bool IsMethodAllowed(string path, string method)
{
    var subject = ResolvePath(path);
    return subject is INotificationChannel;
}
```

This lets agents send alerts (email, push, webhook) without being able to modify the knowledge graph.

## Evolution Path

| Stage | Description | Configuration |
|---|---|---|
| **Stage 1: Generic agent** | `LlmAgent` subject — configurable via JSON/UI. Instructions, watch paths, poll interval. Read-only + notify | JSON files, UI editor |
| **Stage 2: Change-triggered** | Property change subscriptions with declarative filter rules (e.g., `temperature > 80`). Agent only runs when filter matches — avoids unnecessary LLM calls | Filter rules in agent config |
| **Stage 3: Write access** | `set_property` and `invoke_method` enabled with per-agent authorization scopes | Permission config per agent |
| **Stage 4: Specialized agents** | `LlmAgentBase` base class for C# agent subjects with domain-specific logic, custom tools, multi-step workflows | C# code in plugins |
| **Stage 5: Multi-agent** | MAF orchestration — agents hand off to each other, group chat, sequential workflows | Workflow config |

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Provider as separate subject | `LlmProvider` referenced by path | Centralized credentials, multiple agents share one provider |
| Generic agent first | Single `LlmAgent` type, config-driven instances | Operators create agents without C# code |
| Tool reuse | MCP tools as `AIFunction`, no protocol overhead | Same implementation, two delivery modes |
| Safe by default | Read-only + notify, write access gated on authorization | Prevents accidental graph modification |
| Pre-fetched context | Watch paths queried before LLM call | Reduces round-trips and API cost |
| LLM framework | `IChatClient` from `Microsoft.Extensions.AI` with `UseFunctionInvocation()` | Standard .NET AI abstraction, any provider works |

## Dependencies

- `Microsoft.Extensions.AI`: `IChatClient`, `AIFunction`, `UseFunctionInvocation()`
- `Anthropic` SDK (official): `IChatClient` implementation for Claude
- `Namotion.Interceptor.Mcp`: tool implementations reused as `AIFunction`
- `HomeBlaze.Abstractions`: `INotificationChannel`, `ITitleProvider`
- `HomeBlaze.Services`: `SubjectPathResolver` for provider/watch path resolution
- [Registry Attribute Migration](registry-attribute-migration.md): `HomeBlazeMcpMetadataProvider` and `StateAttributePathProvider` read from registry attributes
- [#137](https://github.com/RicoSuter/Namotion.Interceptor/pull/137) (Authorization): gating write access per agent

## HomeBlaze MCP Metadata Provider

`HomeBlaze.AI` also contains the HomeBlaze-specific MCP enrichment (referenced by the [MCP Server plan](../../../../../docs/plans/mcp-server.md)):

**`StateAttributePathProvider`** — determines which properties are exposed via MCP. Reads the `"state"` registry attribute (from the [Registry Attribute Migration](registry-attribute-migration.md)):

```csharp
public class StateAttributePathProvider : IPathProvider
{
    public bool IsPropertyIncluded(RegisteredSubjectProperty property)
        => property.TryGetAttribute(KnownAttributes.State) != null;

    public string? TryGetPropertySegment(RegisteredSubjectProperty property)
    {
        var metadata = property.TryGetAttribute(KnownAttributes.State)?.GetValue() as StateMetadata;
        return metadata?.Name ?? property.Name;
    }
}
```

**`HomeBlazeMcpMetadataProvider`** — enriches MCP responses with title, icon, units, and method discovery. All metadata reads go through registry attributes, ensuring it works identically for concrete and dynamic proxy subjects:

```csharp
public class HomeBlazeMcpMetadataProvider : IMcpMetadataProvider
{
    public void EnrichSubjectMetadata(RegisteredSubject subject, McpSubjectMetadata metadata)
    {
        if (subject.Subject is ITitleProvider titleProvider)
            metadata.Title = titleProvider.Title;
        if (subject.Subject is IIconProvider iconProvider)
            metadata.Icon = iconProvider.IconName;
    }

    public void EnrichPropertyMetadata(RegisteredSubjectProperty property, McpPropertyMetadata metadata)
    {
        var stateMetadata = property.TryGetAttribute(KnownAttributes.State)?.GetValue() as StateMetadata;
        if (stateMetadata is not null)
        {
            metadata.Unit = stateMetadata.Unit?.ToString();
            metadata.Position = stateMetadata.Position;
        }
    }

    public IEnumerable<McpMethodInfo> GetMethods(RegisteredSubject subject)
    {
        // Reads "operation" and "query" registry attributes from method properties
    }
}
```

## Open Questions

- **Conversation history**: Should agents maintain conversation context across runs, or start fresh each time? Fresh is simpler and cheaper; history enables multi-turn reasoning.
- **Cost controls**: Budget/rate limiting per agent, fallback when LLM API is unavailable.
- **Agent-to-agent**: Can one agent's analysis be input to another? Deferred to Stage 5 (MAF).
- **Streaming**: Should agent analysis stream to the UI in real time, or only show the final result?
- **Audit**: How to attribute agent actions in the audit trail (see [Audit](../architecture/design/audit.md)).
