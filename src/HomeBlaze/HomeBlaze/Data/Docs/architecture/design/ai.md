---
title: AI Integration
navTitle: AI
---

# AI Integration Design

## Overview

HomeBlaze treats AI as a first-class concern with two complementary modes: **built-in agents** running in-process as subjects, and **external agents** connecting via MCP (Model Context Protocol). Both modes access the knowledge graph through the same tool interface.

## Architecture

### Two Agent Modes

**Built-in agents** are `[InterceptorSubject]` classes that run inside the HomeBlaze process. They are visible in the knowledge graph like any other subject — operators can see their state, configuration, and activity. They use a compatible AI agent framework for LLM interaction, tool dispatch, and multi-provider support.

**External agents** connect via the MCP server. They use the standard MCP protocol and see the same tools. This is the integration point for Claude, ChatGPT, custom copilots, or any MCP-compatible client.

| Aspect | Built-in Agent | External Agent (MCP) |
|--------|---------------|---------------------|
| Runtime | In-process subject | Remote MCP client |
| Tool access | Direct method calls | MCP protocol |
| LLM config | Per-agent properties (provider, model, endpoint, key) | Client-side |
| Visibility | Visible as subject in knowledge graph | Not visible in graph |
| Interaction | Pull (poll) + push (change subscription) | Pull only (push TBD, depends on MCP evolution) |
| Use cases | Reactive automation, continuous monitoring | Operator copilot, ad-hoc queries, debugging |

### MCP Tool Layering

MCP tools are split across two packages:

| Package | Tools | Scope |
|---------|-------|-------|
| `Namotion.Interceptor.Mcp` | `get_property`, `set_property`, `list_types`, basic subject browse | Any Namotion.Interceptor application |
| `HomeBlaze.Mcp` | `query` (with rich metadata: `$type`, `$icon`, `$title`, `$methods`), `invoke_method`, `list_methods`, `get_history` | HomeBlaze-specific features |

This keeps the interceptor library independently usable. HomeBlaze adds richer tools on top.

See [PR #158](https://github.com/RicoSuter/Namotion.Interceptor/pull/158) for the MCP server design and tool specifications.

### Interaction Patterns

**Pull (poll/query):** Both built-in and external agents can query the knowledge graph on demand. Built-in agents typically poll on a configurable timer interval.

**Push (change subscription):** Built-in agents can subscribe to property changes via the reactive change streams already available in Namotion.Interceptor. Declarative filter rules determine when to trigger the agent (e.g., "notify when temperature > 80"). This avoids expensive LLM calls on every property change.

### Built-in Agent as Subject

A built-in agent is an `[InterceptorSubject]` with per-agent LLM configuration (provider, model, endpoint, key), agent configuration (instructions, watched paths, poll interval, filter rules), and observable state (status, last analysis, last run time). All of these are visible and editable in the operator UI.

Agent writes are local writes — they flow through the normal write path (local model -> UNS -> satellite -> device). No source tagging is needed because agents are not connectors — source tagging exists specifically to prevent feedback loops in bidirectional connector sync, not for attribution. Audit of agent actions is a separate concern (see [Audit](audit.md)).

## Evolution Path

| Stage | Description |
|-------|-------------|
| Stage 1: Hand-written agents | `[InterceptorSubject]` + AI agent framework + `BackgroundService`. Full flexibility, some boilerplate |
| Stage 2: Agent base class | Extract `AgentSubject` base: timer loop, watched paths, change subscriptions, prompt building, state management |
| Stage 3: Declarative agents | Config-driven (paths, prompt template, filter rules, allowed tools). Non-developers can create agents without C# |

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Agent runtime | Microsoft Agent Framework (MAF) as initial choice | Handles LLM loop, tool dispatch, multi-provider. Built on `Microsoft.Extensions.AI` |
| Interaction model | Pull + push (with declarative filters) for built-in; pull-only for external | Push avoids polling overhead; filters prevent unnecessary LLM calls |
| LLM config | Per-agent properties | Different agents may need different models. Config visible and editable in UI |
| Agent writes | Local writes, no source tagging | Source mechanism is for connector feedback-loop prevention, not attribution |

## Open Questions

- MCP push/subscription support for external agents (depends on protocol evolution)
- Agent audit trail — how to attribute changes to specific agents (see [Audit](audit.md))
- Authorization — which agents can access which subjects (see [Security](security.md))
- Agent-as-tool — exposing built-in agents as MCP tools for external copilots (deferred)
