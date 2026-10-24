---
title: Contract
navTitle: Contract
status: Planned
---

# Contract [Planned]

Describes a public software surface independently of networking, language, deployment, or a consumer.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Existing subject registration and source/document references. Parsing and public API extraction are optional analyzer capabilities.

## Data [Planned]

| Common information | Capability |
|---|---|
| Description, source/document references, optional version, analyzer provenance | Referenced by CodeProjects and Interfaces; raw and parsed presentation where supported |

Reference source documents through existing storage/file subjects or an explicit source descriptor where suitable. Authored and discovered Contracts have a defining file or analyzer owner; referencing a Contract does not clone it or transfer ownership. Exact source, revision, snapshot, and version identity are open.

## Specializations [Planned]

- [OpenApiContract](openapi-contract.md): HTTP API operations, parameters, and schemas.
- [ProtobufContract](protobuf-contract.md): services, RPCs, and message definitions.
- [CSharpApiContract](csharp-api-contract.md): public programming types, members, and signatures.

CodeProjects and Interfaces reference the base Contract capability. Additional types can be registered; no closed format enum is required. Proposed default: keep the base Contract usable for documents with no supported parser. Whether to make the base concrete is still a schema choice, not a claim that a parser exists.

## Invariants [Planned]

A library API needs no Host, network Interface, or Endpoint. A parsed representation is tied to its source/revision. Unknown or unsupported input is reported explicitly. Comparison and compatibility analysis are separate capabilities, not implied by having a version field.

## Usage examples

[Library contracts](../examples/library-contracts.md)

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
