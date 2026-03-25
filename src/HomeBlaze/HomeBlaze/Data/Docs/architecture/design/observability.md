---
title: Observability
navTitle: Observability
---

# Observability Design

## Overview

HomeBlaze provides observability through two complementary mechanisms: **OpenTelemetry** for ops teams using standard monitoring infrastructure, and **health subjects** that expose system health within the knowledge graph itself.

## OpenTelemetry

.NET has built-in OpenTelemetry support. HomeBlaze exports traces, metrics, and logs via standard OTLP to any compatible backend (Prometheus, Grafana, Jaeger, etc.).

### Key Metrics

| Metric | Description |
|--------|-------------|
| Property changes per second | Throughput of the change pipeline |
| WebSocket connection state | Connected/disconnected per peer |
| Reconnection count | Stability indicator for inter-node links |
| LLM call duration | AI agent performance |
| Subject count | Knowledge graph size |
| Change queue depth | Backpressure indicator |

### Key Traces

| Trace | Description |
|-------|-------------|
| Property write path | End-to-end from consumer write to device confirmation |
| Operation invocation | Including cross-instance proxy hops |
| MCP tool call | External agent interaction |

## Health Subjects

Each instance exposes its own health as subjects in the knowledge graph. This means AI agents can monitor the system itself using the standard MCP tools — no separate monitoring integration needed.

Health subjects include node role, connection state, property count, changes per second, uptime, and last heartbeat. Derived properties compute overall status from these inputs.

Operators see health information in the Blazor UI alongside business subjects. AI agents can detect degradation and alert or take corrective action.

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Ops monitoring | OpenTelemetry (OTLP) | Standard, vendor-neutral, built into .NET |
| Self-monitoring | Health subjects in knowledge graph | AI agents and operators use the same tools for system and business data |
| No custom monitoring protocol | Reuse existing infrastructure | OpenTelemetry for ops, knowledge graph for self-monitoring |

## Open Questions

- Which metrics and traces are essential for v1
- Health subject schema standardization across instances
- Alerting integration (separate from alarms — see [Alarms](alarms.md))
