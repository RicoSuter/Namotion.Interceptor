---
title: C# Project Analyzer
navTitle: C# Project Analyzer
status: Planned
---

# C# Project Analyzer [Planned]

Discover CodeProjects, supported runnable Hosts, and literal project references from csproj files.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** SourceRepository raw local access and analyzer registration; XML parsing needs no HomeBlaze platform extension.

## Input and output [Planned]

Input is a configured local source tree. Read basic XML properties and ProjectReference items. Capture literal values with source provenance, classify libraries or executable output only where supported input establishes it, and report unresolved references/conditions explicitly.

## Limits [Planned]

Do not restore, build, run projects, or evaluate MSBuild. Imports, conditions, property expressions, generated references, and missing files can remain unknown with diagnostics. Exclude generated output, caches, and version-control internals according to explicit scan options. Exact exclusion and link policies remain to be settled.

A runnable output is not evidence of production deployment or continuous execution. Do not infer runtime service calls from project references. Public types/members and API Contracts are outside this analyzer; use the separate C# API analyzer capability.

## Verification criteria [Planned]

Cover literal project properties and relative references, library versus runnable classification, duplicate names with distinct paths, unresolved input, stable refresh identity, and configured scan scope. The Commerce fixture supplies four projects and three unambiguous executable candidates.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
