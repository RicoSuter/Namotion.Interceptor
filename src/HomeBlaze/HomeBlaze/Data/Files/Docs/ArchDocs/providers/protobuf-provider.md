---
title: Protobuf Provider
navTitle: Protobuf Provider
status: Planned
---

# Protobuf Provider [Planned]

Populate a specialized Contract from explicitly selected or discovered protobuf source.

Expose parsing through a shared singleton [Provider subject](../subjects/provider.md). Repository selection references that subject; parsed state remains with the requesting repository/Contract owner. The exact invocation/selection API for standalone authored Contracts is still open.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** ProtobufContract, explicit source boundaries, and a source parser to be selected. Binary-descriptor input is a possible later capability.

## Input and output [Planned]

Read the initial `.proto` file selected by an authored [ProtobufContract.path](../subjects/protobuf-contract.md#configuration-properties-proposed-schema) or supplied by [contract discovery](contract-discovery.md). Relative descriptor paths use the defining file's local origin. Describe services, RPCs, messages, enums, and supported field information with source/revision provenance. A data-only schema can be a Contract without a network endpoint. Discovery and project association do not require structured parsing.

## Decisions before parsing [Proposed]

Import roots, supported revisions, dependency, parsed model, diagnostics, and any later binary-descriptor input. Compiler invocation and external dependency acquisition are separate choices. The shared source boundary also applies to imported definitions.

## Verification criteria [Planned]

Known imports, unresolved imports, unsupported constructs, data-only schemas, stable identities, and deterministic result publication for the selected parser scope.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
