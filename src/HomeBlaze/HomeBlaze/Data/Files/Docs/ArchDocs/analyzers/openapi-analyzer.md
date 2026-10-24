---
title: OpenAPI Analyzer
navTitle: OpenAPI Analyzer
status: Planned
---

# OpenAPI Analyzer [Planned]

Populate a specialized Contract from an OpenAPI document.

Expose parsing through a shared singleton [Analyzer subject](../subjects/analyzer.md). Repository selection references that subject; parsed state remains with the requesting repository/Contract owner. The exact invocation/selection API for standalone authored Contracts is still open.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** OpenApiContract, explicit document sources, and a parser dependency to be selected.

## Input and output [Planned]

Read the `.json`, `.yaml`, or `.yml` source selected by an authored [OpenApiContract.path](../subjects/openapi-contract.md#configuration-properties-proposed-schema) or supplied by [contract discovery](contract-discovery.md). Relative descriptor paths use the defining file's local origin. Preserve source/revision provenance. Where supported, expose operations, parameters, request/response definitions, and schemas. Report unsupported or unresolved input distinctly from an empty API. File discovery and project association do not require structured API parsing.

## Decisions before parsing [Proposed]

Supported specification revisions, local/external reference resolution, source limits, parser dependency, and refresh policy. Remote fetching, endpoint probing, and health checks are separate opt-in capabilities, not implied by a document URL.

## Verification criteria [Planned]

Valid and invalid documents, selected version coverage, unresolved references, deterministic identity, and last-success behavior. Do not claim a complete resolved contract when required references are unavailable.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
