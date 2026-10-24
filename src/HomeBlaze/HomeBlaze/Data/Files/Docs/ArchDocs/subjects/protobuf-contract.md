---
title: ProtobufContract
navTitle: ProtobufContract
status: Planned
---

# ProtobufContract [Planned]

A Contract specialization for protobuf definitions.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Contract; an optional protobuf analyzer and specialized presentation.

## Inheritance [Planned]

ProtobufContract derives from Contract. Specialized data can describe services, RPCs, messages, fields, and enums. Imported definitions need explicit source scope and diagnostics. Protobuf data contracts do not require a deployed gRPC endpoint.

## Scope and open choices [Proposed]

Choose source-text versus descriptor input, import resolution, supported language revisions, parsed model shape, and dependency. Running a compiler or obtaining external imports is not automatically authorized by selecting a contract file.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
