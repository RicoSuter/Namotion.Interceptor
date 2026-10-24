---
title: CLI and CI Validation
navTitle: CLI and CI Validation
status: Planned
---

# CLI and CI Validation [Planned]

Run architecture validation from a local terminal or CI job by starting the same configured subject graph through the normal loading and lifecycle services in a headless host. SourceRepositories perform their normal analyzer-driven discovery. Humans and agents use the resulting errors to update Markdown, JSON, metadata, or membership rules and rerun the command. The host needs no web server, browser, or provider connection for local-source checks.

## Implementation readiness

**Area:** ArchDocs for the CLI adapter and shared rule evaluator. Shared graph bootstrap, storage loading, and headless lifecycle composition belong to HomeBlaze; reusable subject helpers belong to Namotion.Interceptor only where their dependencies fit.

**Delivery:** Planned headless command-line entry point. Exact executable/package names, argument syntax, distribution, and report schema are proposed. No runnable ArchDocs CLI exists in this PR.

**Prerequisites:** [Architecture checks](../shared/architecture-checks.md), normal subject/type registration, storage and metadata loading, shared graph lifecycle, a completion contract for required discovery/reconciliation, deterministic root mapping, and source-located diagnostics. UI component registration and rendering are not prerequisites.

## Execution contract [Planned]

1. Start the shared subject host with the normal type/module registrations, subject context, root configuration, storage containers, and metadata loading. Attach subjects through their ordinary lifecycle and start their required hosted services. Report unsupported authored features rather than skipping them.
2. Let each SourceRepository resolve its source root and invoke its selected shared analyzers through its normal discovery/reconciliation worker. Repositories can point outside the documentation mount. The CLI does not maintain another source scanner or directly reproduce repository behavior.
3. Wait for required storage loading, repository analysis, generated children, memberships, and relationships to settle for the current inputs. Registered singleton analyzers have the same configured graph paths and capabilities as in the UI. Completion must not depend on another running UI session.
4. Collect explicitly in-scope rule definitions and run the shared evaluator against that graph. Preserve original source locations through parsing and resolution so diagnostics can point back to authored files.
5. Emit the report and exit status, then stop and dispose the host through normal shutdown. Validation reads authored inputs and never changes them as a side effect. Basic static analyzers do not clone, fetch, restore, build, or execute the applications described by the graph.

The documentation mount, root configuration, selected rules, analyzer registry/configuration, source inputs, and supported capabilities must be reproducible locally and in CI. Given equivalent configuration and inputs, UI and CLI use the same subject types, ownership, paths, metadata, and discovery behavior, with separate runtime instances for their own process. A graph path such as `/Repository` is relative to the configured graph, not an operating-system path. Relative raw source locations retain their file-origin semantics. Exact host composition and analyzer publication remain design work; ordinary DI singleton registration alone does not publish an analyzer into the graph.

## Shared host and readiness [Planned]

Reuse normal loading and lifecycle services instead of implementing a CLI-specific Markdown/JSON model or project-analysis pipeline. The CLI supplies command arguments, waits for readiness, invokes validation, formats diagnostics, and chooses the exit code. Normal subjects and their workers own the graph and discovery. Optional web/UI services are composed separately from these shared services.

Existing RootManager loads root configuration and attaches the subject context; its `RootLoaded` task marks that root attachment, not completion of downstream storage scans and future analyzer passes. Existing FluentStorageContainer runs loading as a hosted subject. These are useful bootstrap primitives, but the full ArchDocs completion barrier is not implemented. See the [readiness source evidence](../implementation-readiness.md#source-evidence-implemented).

The shared host needs an explicit way to await successful completion of the finite work required for the current input generation. Root existence, service startup, a fixed delay, or an empty work queue observed once is insufficient. If inputs change while discovery or membership resolution is running, await the resulting current generation before validating. Background watchers and timers need not terminate for the graph to become ready. Cancellation, timeout, or required-work failure produces incomplete validation; stop outstanding work through the normal lifecycle and do not emit a clean pass. Exact readiness interfaces, generation coordination, and timeout configuration remain open.

## Command shape [Proposed]

Illustrative usage after a future CLI is installed and its supported analyzers are configured:

```shell
archdocs check --docs ./docs --format text
archdocs check --docs ./docs --format json --output ./artifacts/architecture-checks.json
archdocs check --config ./Root.json --format text
```

Proposed `--config` starts the normal graph from an explicit root configuration. `--docs` is a convenience for a minimal normal host configuration mounting that directory, with the required analyzer subjects published through the same registration mechanism; it does not use a second loader. The graph paths of that mount must be documented, and it must not be silently remounted when running equivalent UI configuration. Rules are collected from supported metadata in the graph; scope filters and the failure threshold need a final CLI schema. `--output` writes the report only. No command shown here is implemented, and these examples are not an installation instruction or a complete CI workflow.

Proposed exit contract:

| Exit code | Meaning |
|---|---|
| `0` | Current validation completed with no findings at or above the configured failure threshold. |
| `1` | Validation completed and found policy violations at or above the threshold. |
| `2` | Validation could not complete because of invalid configuration, unavailable capabilities, failed analysis, or another execution error. Partial findings may still be reported. |

Cancellation must produce an unsuccessful result with an explicit reason; its exact code is left to the final CLI contract. A stale graph or skipped required check cannot return a clean pass. Default failure threshold is proposed as `error`; warning-only results stay visible. Sorting, report versioning, and path normalization must be documented so agents can consume reports without scraping terminal styling.

## Human-readable errors [Planned]

For the [Host membership policy](../shared/architecture-checks.md#every-host-belongs-to-a-service-planned), a diagnostic can read:

```text
ERROR hosts-have-service: Host Orders.Worker is not assigned to a Service.
Subject: /Repository/hosts/Orders.Worker
Source: ../src/Orders.Worker/Orders.Worker.csproj
Rule: .metadata.json /architectureRules/0
Fix: Update a Service's componentSources to include this Host, adjust its
     membership rule, or define the missing Service.
Help: guides/assign-hosts.md
```

Source and rule locations identify different things. If no unique Service can be inferred, provide the applicable documentation/configuration candidates without pretending there is one guaranteed edit. The guide explains the supported file syntax. Plain output must remain useful in an ordinary terminal and CI log.

## Agent-readable errors [Proposed schema]

JSON output should carry structured fields with equivalent information. This illustrative report describes one completed evaluation with a policy violation:

```json
{
  "schemaVersion": 1,
  "status": "violations",
  "summary": { "errors": 1, "warnings": 0, "incompleteChecks": 0 },
  "findings": [
    {
      "ruleId": "hosts-have-service",
      "ruleOwnerPath": "/",
      "severity": "error",
      "message": "Host Orders.Worker is not assigned to a Service.",
      "subjectPath": "/Repository/hosts/Orders.Worker",
      "sourceLocation": { "path": "../src/Orders.Worker/Orders.Worker.csproj" },
      "ruleLocation": { "path": ".metadata.json", "pointer": "/architectureRules/0" },
      "remediation": {
        "message": "Update a Service's componentSources to include the Host, adjust its membership rule, or define the missing Service.",
        "documentationPath": "guides/assign-hosts.md"
      }
    }
  ]
}
```

Final reports also need input/analysis context, evaluated counts, configuration diagnostics, and a documented base for each path. Proposed file paths above are relative to the documentation mount; graph paths use the graph root. Include precise line/column spans only when known. Broken help links are authoring diagnostics; missing help must not hide the underlying violation. JSON output must remain valid even when validation fails. Additional formats such as SARIF or CI annotations are optional later adapters, not prerequisites.

## Agent and CI workflow [Planned]

A person or agent runs the check, follows the finding to its rule and guide, edits the canonical authored file, and runs the same command again. The runner does not decide business ownership or rewrite prose automatically. Semantic fix suggestions can become richer later while retaining focused source edits and explicit validation of the result.

CI runs against its checked-out revision and retains the report as an artifact if desired. Checks based on local static source do not need GitHub credentials. Provider observations, trace-based rules, and other remote capabilities must be explicitly selected and are outside the basic local validation path. Parallel runs must not depend on another UI session's mutable graph or cached stale state.

[Architecture checks](../shared/architecture-checks.md) · [Validation scenario](../scenarios/architecture-validation.md) · [Readiness](../implementation-readiness.md)
