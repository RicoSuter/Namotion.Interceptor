---
title: Audit Trail
navTitle: Audit
---

# Audit Trail Design [Planned]

## Overview

The audit trail tracks who changed what and when across the knowledge graph. This is important for industrial compliance, debugging, and understanding AI agent behavior.

**This building block is planned but not yet designed.** The following captures known requirements and constraints.

## Requirements

- Track the identity of the actor (operator, AI agent, connector, external system) for every property write and operation invocation
- Record timestamp, previous value, new value, and actor identity
- Support querying audit history by subject, property, actor, or time range
- Integrate with the authorization system (log authorization decisions)

## Constraints

- Must not significantly impact write path performance
- Should work across instances (audit of cross-instance operation proxying)
- AI agent actions need particular visibility — operators must be able to review what agents did and why

## Open Questions

- Storage model (separate from time-series history, or extended history with actor metadata?)
- Retention and archival policy
- Integration with external audit/compliance systems
- How to attribute changes from built-in agents (agent subject identity) vs external agents (MCP session identity)
