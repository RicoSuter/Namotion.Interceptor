---
title: C# Project Analyzer
navTitle: C# Project Analyzer
status: Planned
---

# C# Project Analyzer [Planned]

Discover [CodeSolutions](../subjects/code-solution.md), [CodeProjects](../subjects/code-project.md), supported runnable [Hosts](../subjects/host.md), and literal source/build references from supported solution and project files.

## Implementation readiness

**Delivery:** Can be implemented through existing subject and component extension points. The ArchDocs feature itself is not implemented.

**Prerequisites:** SourceRepository raw local access and analyzer registration; XML parsing needs no HomeBlaze platform extension.

## Input and output [Planned]

Input is a configured local source tree. Read basic XML properties and ProjectReference items. Capture literal values with source provenance, classify libraries or executable output only where supported input establishes it, and report unresolved references/conditions explicitly.

Read supported solution manifests as literal project lists. The initial proposed format is `.slnx`, demonstrated by the Commerce fixture. Resolve its project locations to the same repository-owned CodeProjects and expose inverse solution memberships. Multiple solutions can reference the same project, and a discovered project need not belong to any solution. Membership does not imply Service membership, a Host, or a deployment boundary. Support for `.sln` and other formats needs an explicit parser capability; this specification does not claim a complete solution evaluator.

## Limits [Planned]

Do not restore, build, run projects, or evaluate MSBuild. Imports, conditions, property expressions, generated references, and missing files can remain unknown with diagnostics. Exclude generated output, caches, and version-control internals according to explicit scan options. Exact exclusion and link policies remain to be settled.

A runnable output is not evidence of production deployment or continuous execution. Do not infer runtime service calls from project references. Public types/members and API Contracts are outside this analyzer; use the separate C# API analyzer capability.

## Verification criteria [Planned]

Cover literal solution memberships, shared projects, missing or unsupported entries, project properties and relative references, library versus runnable classification, duplicate names with distinct paths, unresolved input, stable refresh identity, and configured scan scope. The Commerce fixture supplies one `.slnx` solution, four projects, and three unambiguous executable candidates.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
