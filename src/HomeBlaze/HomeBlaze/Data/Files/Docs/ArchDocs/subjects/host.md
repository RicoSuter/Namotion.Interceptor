---
title: Host
navTitle: Host
status: Planned
---

# Host [Planned]

Represents a runnable process definition, independently of a physical machine or running replica.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Manual configuration can start independently; discovered Hosts require SourceRepository and an applicable analyzer.

## Data [Planned]

| Authored or discovered | Referenced subjects |
|---|---|
| Name, runtime/packaging facts, extensible execution behavior and roles | Optional entry CodeProject and supporting projects, provided Resources, exposed Interfaces |

Execution can be continuous or run-to-completion. A worker, migration executable, local orchestration tool, or database process needs no dedicated platform-specific subtype. A managed resource can be modeled without any known Host.

## Relationships [Planned]

Hosts can provide multiple Resources; a Resource can have multiple providing Hosts. These are references. Services can select Hosts directly without an intermediate Resource. Discovery of an executable does not imply Service membership, deployment membership, an always-running role, or observed execution.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
