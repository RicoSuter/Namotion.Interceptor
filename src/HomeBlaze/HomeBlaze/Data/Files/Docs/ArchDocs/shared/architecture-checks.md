---
title: Architecture Checks
navTitle: Architecture Checks
status: Planned
---

# Architecture Checks [Planned]

Define file-authored rules over the discovered and authored graph, then evaluate the same rules in HomeBlaze and through a headless CLI in CI. Findings help humans and agents update the documentation correctly. Checks evaluate explicit policies; discovery does not silently create architectural assignments to make a rule pass.

## Implementation readiness

**Area:** ArchDocs. The rule evaluator, definitions, and diagnostic records are headless domain capabilities. UI results use existing component extension points; the CLI needs a separate entry point and file-backed loading.

**Delivery:** Planned shared evaluation with [CLI/CI execution](../integration/cli-and-ci.md) and an [ArchitectureCheckResults](../widgets/architecture-check-results.md) widget. No rule engine or command exists yet.

**Prerequisites:** Supported source analyzers, deterministic graph loading and publication, shared selectors, source provenance, metadata readers, and a complete evaluation scope. Missing required analysis must not become a successful check.

## Rule definitions [Proposed schema]

Proposed authoring uses an `architectureRules` collection in the existing [metadata envelope](metadata-and-relationships.md), owned by a folder or subject. Folder `.metadata.json`, JSON-subject sidecars, and planned Markdown frontmatter provide their normal canonical persistence. Generic metadata preserves these keys today; typed evaluation, validation, and UI are planned. A separate Rule or RuleSet subject is not selected by this proposal.

| Property | Meaning |
|---|---|
| `id` | Stable readable rule key within its metadata owner; combine owner identity and key when identifying a rule across scopes. |
| `title`, `message` | Human-readable purpose and explanation of a violation. |
| `kind` | Registered declarative check capability, such as the proposed `requireMembership`; unsupported kinds produce configuration errors. |
| `severity` | Proposed `error`, `warning`, or `info`; the runner's selected failure threshold is separate. |
| `remediation` | Actionable guidance for correcting authored configuration. It is advice, not an automatically applied edit. |
| `documentation` | Optional documentation URL or file link. Relative file links resolve from the owning definition file, independently of the working directory. |
| `subjectSources` | [PathQuery](path-queries.md) entries selecting the subjects to validate. |
| Kind-specific configuration | Additional selectors, relationship kinds, property references, and constraints required by that check. |

All names and record shapes below are proposed. Reuse the shared query syntax and property resolver instead of adding an unrelated query language. Check kinds are extensible registered capabilities; authored rule files are data and do not contain executable scripts. Exact registration, schema publication, and packaging remain open.

## Every Host belongs to a Service [Planned]

This is the first concrete policy example requested by the user. It is an opt-in rule for the chosen scope, not a new invariant imposed on every Host in the generic domain. Every selected [Host](../subjects/host.md) must occur in the resolved direct `hosts` collection of at least one selected [Service](../subjects/service.md). A Service's Host collection is the direct Host subset of its resolved `components`.

The proposed `requireMembership` kind is reusable for other membership policies. `containerSources` selects candidates, `containerType` filters compatible container subjects, `membersProperty` names the collection to inspect, and `minimum` gives the minimum number of distinct matching containers. Compatible targets and collection contents must be validated. A minimum of one allows overlapping membership and does not require exclusive ownership. Selecting a nested Service or a Resource does not implicitly assign its Hosts.

For a documentation mount containing `/Repository` and Service definitions below `/catalog`, put this in its root `.metadata.json`:

```json
{
  "architectureRules": [
    {
      "id": "hosts-have-service",
      "title": "Assign every Host to a Service",
      "kind": "requireMembership",
      "severity": "error",
      "message": "Every Host must be assigned to at least one Service.",
      "remediation": "Update a Service's componentSources to include the Host, adjust its membership rule, or define the missing Service.",
      "documentation": "./guides/assign-hosts.md",
      "subjectSources": [{ "path": "/Repository/hosts/*" }],
      "containerSources": [{ "path": "/catalog/**" }],
      "containerType": "Namotion.ArchDocs.Service",
      "membersProperty": "hosts",
      "minimum": 1
    }
  ]
}
```

The guide link assumes that file exists in the documentation mount. Merge this collection with existing metadata; preserve unrelated keys such as folder title and order. Subject paths address the mounted graph; the help link addresses a document relative to the metadata file. An explicit unresolved scope or invalid property is a configuration error. A complete valid scan finding no Hosts has zero violations, with the evaluated count reported; a separate minimum-count policy can require Hosts to exist. Zero selected Services with Hosts present produces unassigned-Host findings.

When a newly discovered Host is unmatched, report its graph path and source project, the rule's definition location, and the authored membership that needs attention. Suggest editing an existing Service's `componentSources` or defining a Service, with the linked guide explaining syntax. Do not invent which business Service owns it. A proposed edit must preserve source format and unrelated content and must be validated by rerunning the checks after it is applied.

## Additional useful checks [Planned]

| Policy | Required evidence and limits |
|---|---|
| Forbidden dependencies between layers or groups | Explicit source/target queries and edge kinds. Code references and runtime interactions are different evidence channels. |
| Dependency cycles | A selected graph and defined edge kinds; report an actual cycle path, not an unexplained boolean. |
| Unmatched membership rules or required coverage | Complete discovery plus explicit expected counts/coverage; ordinary empty selections remain valid unless a rule constrains them. |
| Unresolved architecture/document references | Supported reference parsing and target resolution; ordinary Markdown links establish documentation references, not runtime calls. |
| Required contracts or documentation | Explicit applicability and supported analysis. Missing capabilities produce incomplete evaluation rather than invented absence. |

Select initial check kinds during MVP planning. These policies reuse groups, selections, and existing subjects rather than introducing a mandatory Layer, Selection, or ArchitectureModel hierarchy. Built-in structural/configuration diagnostics remain distinct from policy violations.

## Findings and evaluation [Planned]

Each finding needs a stable rule identity, severity, readable message, affected subject identities/paths, relevant relationship evidence, and source locations. Keep the rule location, discovered source location, and editable documentation/configuration location distinct. Include a remediation message and a resolvable help link when available. Never fabricate line numbers or a particular target edit when the analyzer cannot establish them; report the available file and property/JSON pointer instead.

Evaluation consumes one complete coherent graph and its analysis diagnostics. Findings distinguish violations, invalid configuration, and checks that could not be evaluated. A last-successful graph remains useful in the UI with its age and failure state, but it cannot yield a clean current CI result after required analysis fails. Report evaluated subject counts and the coverage/capabilities used, including a genuinely empty scope.

Use the same evaluation and diagnostics in the UI and CLI. Presentation, command-line arguments, and report serialization are adapters. Deterministic ordering and stable finding keys make CI output reviewable. Time, revision, and freshness are explicit report context rather than hidden inputs to rule truth. Warning thresholds, exclusions, and any future suppressions must be explicit and inspectable; baselining/suppression syntax is deferred.

[CLI and CI](../integration/cli-and-ci.md) · [Results widget](../widgets/architecture-check-results.md) · [Validation scenario](../scenarios/architecture-validation.md)
