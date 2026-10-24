---
title: CodeProject
navTitle: CodeProject
status: Planned
---

# CodeProject [Planned]

Represents a source/build project independently of a runnable process.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** SourceRepository and a project analyzer; optional authored Contract references use metadata or capability-specific configuration to be finalized.

## Data [Planned]

| Discovered state | Optional enrichment |
|---|---|
| Stable repository-relative key, name, source path, language/classification, literal project references, provenance | Description, relationships, and Contract references |

A CodeProject can contribute to multiple Hosts and Services. Libraries remain CodeProjects. A public programming API can have one or more Contracts without a Host, network Interface, or Endpoint. Basic project XML discovery does not extract public types or members; that is a separate analyzer capability.

## Invariants [Planned]

Project references establish code dependencies and do not assert runtime calls. Repository ownership and subject identity survive architectural selection and refresh. Tests and executable projects need explicit classification where supported; runnable output alone does not establish a production role.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
