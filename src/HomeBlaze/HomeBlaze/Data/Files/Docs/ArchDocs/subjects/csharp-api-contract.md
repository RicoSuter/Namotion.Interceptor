---
title: CSharpApiContract
navTitle: CSharpApiContract
status: Planned
---

# CSharpApiContract [Planned]

A Contract specialization for the public C# programming surface.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** Contract; an optional C# public API analyzer or an explicitly supplied snapshot.

## Inheritance [Planned]

CSharpApiContract derives from Contract. It can describe public types, members, and signatures of a CodeProject without network Interfaces or Endpoints. A Contract may be authored as a supplied snapshot before extraction exists.

## Scope and open choices [Proposed]

Decide whether extraction uses source symbols or compiled metadata, which surface/revision and target framework it describes, and the snapshot format. The minimal csproj XML analyzer does not implement this. Generating a complete surface must report unresolved inputs rather than invent members. Extraction, comparison, and compatibility assessment are separate capabilities.

## Usage examples

[Library contracts](../examples/library-contracts.md)

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
