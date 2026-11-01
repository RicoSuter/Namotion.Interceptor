---
title: HostsAssignedToServicesAnalyzer
navTitle: HostsAssignedToServicesAnalyzer
status: Planned
---

# HostsAssignedToServicesAnalyzer [Planned]

## Class definition [Planned]

**HostsAssignedToServicesAnalyzer : [Analyzer](../subjects/analyzer.md)** is a predefined domain check requiring every applicable Host to appear in at least one Service's direct resolved Host collection. JSON uses `$type: Namotion.ArchDocs.HostsAssignedToServicesAnalyzer`. It can reuse membership-evaluation primitives without exposing a second dispatch field.

## Configuration properties [Proposed schema]

Inherit Analyzer's name, severity, message, remediation, and documentation properties. This initial predefined type requires no additional configuration: its invocation supplies the repository's discovered Hosts and Services in the explicit loaded architecture scope. Any future scope overrides must be typed documented properties.

## State properties [Proposed schema]

Inherit Analyzer's capabilities, definition location, and definition diagnostics. The invocation owner retains unassigned-Host findings and counts. This definition holds no global last-repository result.

## Behavior and readiness

**Area:** ArchDocs. **Delivery:** Planned concrete subject and evaluator. Current Host discovery, resolved Service memberships, and complete architecture context are prerequisites.

One or several matching Services pass. Selecting only a CodeProject, nested Service, or Resource does not implicitly assign its Hosts. The analyzer is an opt-in policy rather than a universal Host invariant. A genuinely complete zero-Host input reports zero evaluated subjects; failed required discovery or document loading yields incomplete validation.

Findings identify the Host/project, typed Analyzer definition/block, and remediation explaining how to update the intended Service's componentSources or define the missing logical Service. Use the containing Markdown explanation as help when available. Inherit common Analyzer presentation and optionally summarize assignment coverage.

[Predefined policy rationale](predefined-rules.md#every-host-belongs-to-a-service-planned) · [Generic MembershipAnalyzer](membership-analyzer.md) · [Authoring](authoring.md)
