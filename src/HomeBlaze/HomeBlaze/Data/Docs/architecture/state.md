---
title: Implementation State
navTitle: State
position: 1
---

# HomeBlaze Implementation State

Tracks the implementation status of building blocks described in [Architecture Overview](overview.md).

## Core

| Building Block | Status | Notes |
|---|---|---|
| Subject graph + registry | Implemented | Namotion.Interceptor core |
| Property change tracking | Implemented | Including derived properties |
| Source generation | Implemented | `[InterceptorSubject]` + partial properties |

## Connectors

| Building Block | Status | Notes |
|---|---|---|
| WebSocket sync (SubjectUpdate) | Implemented | Including Welcome/Hello, structural sync |
| OPC UA client/server (core) | Implemented | `Namotion.Interceptor.OpcUa` |
| OPC UA server subject (HomeBlaze) | Implemented | `HomeBlaze.Servers.OpcUa` |
| OPC UA client subject (HomeBlaze) | Planned | Auto-discovery of remote subjects |
| MQTT client/server (core) | Implemented | `Namotion.Interceptor.Mqtt` |
| MQTT server subject (HomeBlaze) | Planned | |

## Knowledge Graph Extensions

| Building Block | Status | Notes |
|---|---|---|
| Operations (`[Operation]`/`[Query]`) | In Progress | Implemented in HomeBlaze, migrating to Namotion.Interceptor registry |
| Cross-instance operation proxying (RPC) | Planned | WebSocket message types 5-6 |
| Time-series history | Planned | `HomeBlaze.History.Abstractions` + sink model |
| Document store | Planned | Documents as subjects |
| Dynamic metadata / annotations | Planned | User-created attributes stored in config JSON |

## AI Integration

| Building Block | Status | Notes |
|---|---|---|
| MCP server (base tools) | In Progress | `Namotion.Interceptor.Mcp`, [PR #158](https://github.com/RicoSuter/Namotion.Interceptor/pull/158) |
| MCP server (HomeBlaze tools) | Planned | `HomeBlaze.Mcp` — query, methods, history |
| Built-in agents | Planned | Agent subjects with LLM integration |

## Platform

| Building Block | Status | Notes |
|---|---|---|
| Plugin system (build-time NuGet) | Implemented | Standard package references |
| Plugin system (runtime loading) | Planned | NuGet feed resolution at startup |
| Blazor operator UI | Implemented | Subject browser, dashboards, editors |
| Multi-instance topology | Implemented | Satellite/central via WebSocket |
| High availability (active-standby) | Planned | Failover with fencing |
| Authorization | In Progress | Graph-level access control, [PR #137](https://github.com/RicoSuter/Namotion.Interceptor/pull/137) |
| Observability (OpenTelemetry) | Planned | |
| Observability (health subjects) | Planned | |
| Audit trail | Planned | Change attribution |
| Alarms / events | Planned | |
