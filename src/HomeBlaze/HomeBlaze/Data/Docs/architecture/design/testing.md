---
title: System Testing
navTitle: Testing
---

# System Testing Design [Planned]

## Overview

Individual connectors are tested via the `ConnectorTester` and integration tests at the Namotion.Interceptor layer. This document covers system-level testing of HomeBlaze deployments — verifying that full topologies (single node through multi-node HA) behave correctly under normal operation and failure conditions.

**This building block is planned but not yet implemented.**

## Scope

| Level | Tested By | Covered Here? |
|-------|-----------|---------------|
| Unit tests | xUnit, per project | No — standard .NET testing |
| Connector resilience | `ConnectorTester` + `IChaosTarget` in Namotion.Interceptor | No — already tested at connector layer |
| Plugin / subject testing | Test helpers for plugin authors (see development docs) | No |
| **System topology testing** | **HomeBlaze system tester** | **Yes** |
| **Chaos testing** | **Fault injection across full topologies** | **Yes** |
| **Performance / load testing** | **Synthetic graph generator + sustained load** | **Yes** |

## HomeBlaze System Tester

Similar to how `ConnectorTester` tests individual connectors by running them through connection, disconnection, and reconnection scenarios, the system tester would run full HomeBlaze topologies through their expected lifecycle and failure modes.

### Supported Topologies

The tester should support every deployment stage described in the [architecture overview](../overview.md):

| Topology | Nodes | What's Tested |
|----------|-------|---------------|
| Single instance | 1 | Startup, configuration loading, device connection, UI serving, graceful shutdown |
| Single instance + standby | 2 | State replication, failover with fencing, promotion, recovery after primary returns |
| Satellite + central | 2+ | State sync upward, write path downward, Welcome snapshot on reconnect, operation proxying |
| Satellite + central + standby | 3+ | Combined HA + multi-instance, failover at each level |
| Multi-primary (peer sync) | 2+ | Bidirectional state sync, write propagation, conflict resolution |

### Test Scenarios

#### Normal Operation

| Scenario | Description |
|----------|-------------|
| Cold start | All nodes start from configuration, connect to devices/peers, reach steady state |
| Steady-state sync | Property changes on satellite propagate to central within expected latency |
| Write path | Operator writes on central, change flows to satellite, device confirms, confirmation flows back |
| Operation proxying | Operation invoked on central, proxied to satellite, result returned |
| Event distribution | Event published on satellite, received on central via message broker |
| Configuration change | Subject added/removed via UI, persisted, survives restart |

#### Failure and Recovery

| Scenario | Description |
|----------|-------------|
| Satellite disconnect | Satellite loses connection to central. Central detects loss. Satellite reconnects, sends Welcome snapshot, state converges |
| Central restart | Central process restarts. All satellites reconnect, send Welcome snapshots. Central recovers full state |
| Primary crash | Primary becomes unreachable. Standby detects, performs fencing check, promotes, activates device connections, resumes sync |
| Primary returns | After failover, original primary returns. It should join as standby (not cause split-brain) |
| Network partition | Satellite reachable by devices but not by central. Satellite continues operating locally. Reconnects when partition heals |
| Storage failure | Storage backend becomes unavailable. Instance continues operating (live state unaffected). Configuration changes cannot be persisted. Alert raised |
| Message broker down | Event distribution interrupted. Instances continue operating. Events buffered or dropped (depending on broker QoS). Recovery on broker return |

#### Chaos Testing

Inspired by the `ConnectorTester` pattern — inject faults continuously while verifying system invariants:

| Fault | Injection |
|-------|-----------|
| Kill node | Hard process kill (SIGKILL equivalent) — no graceful shutdown |
| Disconnect node | Network-level disconnection (transport break, not process kill) |
| Pause node | Freeze process (simulate GC pause, VM suspension, or CPU starvation) |
| Storage unavailable | Storage backend returns errors on write |
| Broker unavailable | Message broker unreachable |
| Slow network | Introduce latency on inter-node connections |
| Clock skew | Shift system clock on one node |

### System Invariants

During and after chaos, the tester verifies:

| Invariant | Description |
|-----------|-------------|
| State convergence | After fault recovery, all instances converge to the same state (verified by comparing property values across nodes) |
| No data loss (state) | Property values are not lost — they recover from source of truth (devices, peers) |
| Single primary | At most one primary per HA pair is active at any time (verified by checking device connection ownership) |
| Event delivery | Events are delivered at least once to all subscribers (after broker recovery) |
| Configuration consistency | Configuration files are not corrupted after faults |

### Implementation Approach

The system tester runs HomeBlaze instances as separate processes (not in-process) to realistically test process crashes, restarts, and network partitions. Each node is started from the same binary with different configuration files.

| Component | Responsibility |
|-----------|---------------|
| Topology builder | Creates configuration files for each node in the topology, starts processes |
| Simulated devices | In-process or mock devices that respond to OPC UA / MQTT connections, providing a controlled source of truth |
| Fault injector | Kills, disconnects, pauses, or degrades individual nodes or network links |
| State verifier | Queries each node's knowledge graph (via MCP or direct API) and compares property values across instances |
| Test orchestrator | Sequences: setup topology → run scenario → inject faults → wait for recovery → verify invariants |

## Performance and Load Testing

In addition to functional correctness, the system tester should validate performance characteristics at increasing scale. This uses a synthetic graph generator that produces subject graphs of configurable size and shape.

### Synthetic Graph Generator

| Parameter | Description |
|-----------|-------------|
| `totalSubjects` | Number of subjects in the graph |
| `propertiesPerSubject` | Value properties per subject (mix of int, decimal, string) |
| `derivedPerSubject` | Derived properties per subject (depend on 1-3 value properties) |
| `collectionDepth` | Nesting depth of subject collections |
| `changeRatePerSecond` | Target property change throughput |

### Scale Tiers

| Tier | Subjects | Properties | Target Change Rate |
|------|----------|-----------|-------------------|
| Small | 100 | 1,000 | 100/sec |
| Medium | 5,000 | 50,000 | 1,000/sec |
| Large | 50,000 | 500,000 | 5,000/sec |
| Very Large | 100,000 | 1,000,000 | 10,000/sec |

### Performance Scenarios

| Scenario | Description |
|----------|-------------|
| Graph construction | Measure time and memory to construct the full subject graph with tracking, registry, and lifecycle enabled |
| Steady-state throughput | Sustain target change rate, measure achieved throughput and latency distribution |
| Derived property cascade | Create dependency chains of varying depth, measure recalculation time per source write |
| Welcome snapshot | Generate a complete SubjectUpdate for the full graph, measure serialization time and payload size |
| WebSocket sync under load | Satellite generates changes at target rate, measure sync latency and reconnect recovery time |
| Multi-connector independence | Attach multiple connectors under load, verify a slow connector does not block others |
| Registry and path resolution | Measure `GetAllProperties()`, MCP `query`, and path segment lookup at each scale tier |

Concrete performance targets should be established after initial baseline measurements — the goal of the first round is to understand where the actual limits are, not to hit predetermined numbers. See [Scalability](scalability.md) for known bottlenecks to validate.

## Open Questions

- Should the system tester be part of the HomeBlaze solution or a separate test project?
- How to simulate network partitions portably across OS platforms?
- Should simulated devices be shared (all nodes see same device) or per-satellite?
- How long to wait for convergence after fault recovery (fixed timeout vs. polling with adaptive timeout)?
- Should the tester produce a report or just pass/fail?
- Integration with CI — can multi-node tests run in GitHub Actions (multiple processes, network simulation)?
