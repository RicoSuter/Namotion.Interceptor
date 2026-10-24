---
title: Protobuf Analyzer
navTitle: Protobuf Analyzer
status: Planned
---

# Protobuf Analyzer [Planned]

Populate a specialized Contract from protobuf source or descriptor input.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** ProtobufContract, explicit source boundaries, and a parser/descriptor approach to be selected.

## Input and output [Planned]

Describe services, RPCs, messages, enums, and supported field information with source/revision provenance. A data-only schema can be a Contract without a network endpoint.

## Decisions before parsing [Proposed]

Source versus descriptor input, import roots, supported revisions, dependency, and diagnostics. Compiler invocation and external dependency acquisition are separate choices. The shared source boundary also applies to imported definitions.

## Verification criteria [Planned]

Known imports, unresolved imports, unsupported constructs, data-only schemas, stable identities, and deterministic result publication for the selected parser scope.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
