---
title: AI Integration
navTitle: AI
status: Planned
---

# AI Integration Design

## Overview [Planned]

HomeBlaze treats AI as a first-class concern with two complementary modes: **built-in agents** running in-process as subjects, and **external agents** connecting via MCP (Model Context Protocol). Both modes access the knowledge graph through the same tool interface.

## Architecture

### Two Agent Modes

**Built-in agents** are `[InterceptorSubject]` classes that run inside the HomeBlaze process. They are visible in the knowledge graph like any other subject — operators can see their state, configuration, and activity. They use MAF (`ChatClientAgent`) for LLM interaction and tool dispatch, with `IChatClient` from `Microsoft.Extensions.AI` for multi-provider support.

**External agents** connect via the MCP server. They use the standard MCP protocol and see the same tools. This is the integration point for Claude, ChatGPT, custom copilots, or any MCP-compatible client.

| Aspect | Built-in Agent | External Agent (MCP) |
|--------|---------------|---------------------|
| Runtime | In-process subject | Remote MCP client |
| Tool access | `AIFunction` (direct in-process calls) | MCP protocol |
| LLM config | Provider subject referenced by path | Client-side |
| Visibility | Visible as subject in knowledge graph | Not visible in graph |
| Interaction | Pull (poll) + push (change subscription with filter rules) | Pull only (push TBD, depends on MCP evolution) |
| Use cases | Reactive automation, continuous monitoring | Operator copilot, ad-hoc queries, debugging |

### MCP Tool Layering [Implemented]

MCP tools are split across packages, configured via a single `McpServerConfiguration` object:

| Package | Tools | Scope |
|---------|-------|-------|
| `Namotion.Interceptor.Mcp` | `query`, `get_property`, `set_property`, `list_types` | Any Namotion.Interceptor application |
| `HomeBlaze.AI` | `list_methods`, `invoke_method` (via `IMcpToolProvider`) + subject enrichment (`$type`, `$icon`, `$title` via `IMcpSubjectEnricher`) + concrete type discovery (via `IMcpTypeProvider`) | HomeBlaze-specific features |
| `HomeBlaze.History.Mcp` | `get_property_history`, and the planned `get_event_history` and `get_command_history` (see [Messages](messages.md)) | Ships with the history packages |

`list_methods` lists each operation and query of a subject with its name, kind, title, description and return type. Its parameters use the `McpMethodParameter` shape of the core library: JSON Schema `type`, `format`, `pattern`, `enum` names and `nullable`. HomeBlaze adds the unit of an `[OperationParameter(Unit = ...)]` as the `description`; a `Percent` parameter reads "fraction where 1 = 100% (0.2 = 20%)", since agents pass the fraction and only the operation dialog takes 0 to 100.

`invoke_method` takes the arguments by parameter name in a `parameters` object and converts them with `McpValueConverter`, so an enum accepts its name (ignoring case) or a defined number, and a `TimeSpan` its `pattern` string. An unknown parameter, a missing non-nullable one or a value that does not convert returns an error that names the expected parameters, and the method is not invoked; a missing nullable parameter is passed as null. When the method itself rejects an argument with an `ArgumentException`, its message is returned, for example the list of known values; any other failure returns a generic error and is logged. In read-only mode only queries can be invoked.

This keeps the interceptor library independently usable. Method tools are HomeBlaze-specific because method discovery uses `MethodMetadata` with `[Operation]`/`[Query]` registry attributes — a HomeBlaze convention, not a core interceptor concept. Property-level metadata (units, position) is already in the registry as `StateMetadata` attributes — included directly in `query` responses when `includeAttributes=true`.

Tool implementations are transport-agnostic `McpToolInfo` instances (metadata + plain function). Consumers wrap them as MCP tools (for external agents) or `AIFunction` objects (for built-in agents). One implementation, any delivery mode.

HomeBlaze MCP uses slash (`/`) separator with `[InlinePaths]` flattening (e.g., `/Servers/OpcUaServer/Port`). See [MCP Server docs](../../../../../../../../docs/mcp.md) for the core MCP server design and [AI Agents plan](../../plans/ai-agents.md) for the built-in agent design.

### Interaction Patterns

**Pull (poll/query):** Both built-in and external agents can query the knowledge graph on demand. Built-in agents typically poll on a configurable timer interval.

**Push (change subscription):** Built-in agents can subscribe to property changes via the reactive change streams already available in Namotion.Interceptor. Declarative filter rules determine when to trigger the agent (e.g., "temperature > 80"). This avoids expensive LLM calls on every property change.

### Built-in Agent as Subject

A built-in agent is an `[InterceptorSubject]` subclassing `LlmAgentBase`. It has a provider reference (path to an `ILlmProvider` subject), agent configuration (instructions, watched paths, poll interval, filter rules), and observable state (status, last analysis, last run time). All visible and editable in the operator UI.

`LlmAgentBase` provides the MAF composition, run loop, queue-one concurrency, and error handling. `LlmAgent` is the generic config-driven subclass — operators create instances via UI/JSON. Developers subclass `LlmAgentBase` directly for specialized agents with custom tools.

Agent writes are local writes — they flow through the normal write path (local model -> UNS -> satellite -> device). No source tagging is needed because agents are not connectors — source tagging exists specifically to prevent feedback loops in bidirectional connector sync, not for attribution. Audit of agent actions is a separate concern (see [Audit](audit.md)).

## Evolution Path

| Stage | Description |
|-------|-------------|
| Stage 1 | `LlmAgentBase` + `LlmAgent`. Read-only + notify. Poll + change-triggered with filter rules. Queue-one concurrency |
| Stage 2 | Agent-to-agent calls via `invoke_method` on `ILlmAgent` (with recursion limits) |
| Stage 3 | Full write access (`set_property`, unrestricted `invoke_method`) with per-agent authorization scopes |

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Agent runtime | MAF `ChatClientAgent` via composition | Built-in tool-calling loop, session management. Built on `Microsoft.Extensions.AI` |
| Provider model | `ILlmProvider` subjects referenced by path, `CreateChatClient()` only | Centralized credentials, consumer doesn't care which model |
| Interaction model | Pull + push (with declarative filters) for built-in; pull-only for external | Push avoids polling overhead; filters prevent unnecessary LLM calls |
| Tool split | Core 4 tools in `Namotion.Interceptor.Mcp`, method tools in `HomeBlaze.AI`, history tool in `HomeBlaze.History.Mcp` | Methods are a HomeBlaze convention, and a tool ships with the feature it exposes |
| Agent writes | Local writes, no source tagging | Source mechanism is for connector feedback-loop prevention, not attribution |

## Open Questions

- MCP push/subscription support for external agents (depends on protocol evolution)
- Agent audit trail — how to attribute changes to specific agents (see [Audit](audit.md))
- Authorization — which agents can access which subjects (see [Security](security.md))
- **Capacity planning** — many built-in agents watching many paths means aggregate LLM API cost and rate limits. Budget/rate limiting per agent, queueing when multiple agents trigger simultaneously, and fallback behavior when the LLM API is unavailable all need design
