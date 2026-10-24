---
title: CLI and CI Validation
navTitle: CLI and CI Validation
status: Planned
---

# CLI and CI Validation [Planned]

Run architecture validation from a local terminal or CI job, without a running HomeBlaze server, browser, or provider connection. Humans and agents use the resulting errors to update Markdown, JSON, metadata, or membership rules and rerun the command. This is the same validation capability used by the UI.

## Implementation readiness

**Area:** ArchDocs for the CLI, graph-loading adapter, and shared rule evaluator. Generic storage/loading extraction, if needed, belongs to HomeBlaze; reusable subject helpers belong to Namotion.Interceptor only where their dependencies fit.

**Delivery:** Planned headless command-line entry point. Exact executable/package names, argument syntax, distribution, and report schema are proposed. No runnable ArchDocs CLI exists in this PR.

**Prerequisites:** [Architecture checks](../shared/architecture-checks.md), supported analyzers, file-backed subject/metadata loading, deterministic root mapping, and source-located diagnostics. UI component registration and rendering are not prerequisites.

## Execution contract [Planned]

1. Load a documentation directory as an explicit graph root, using the same supported type registrations, configuration, metadata, selectors, and analyzer capabilities as the UI. Report unsupported authored features rather than skipping them.
2. Resolve each SourceRepository's raw source root from its defining file and discover the current checkout. Repositories can point outside the mounted documentation directory. Do not clone, fetch, restore, build, or execute the described applications as part of basic static validation.
3. Resolve logical memberships and relationships and wait for a complete current graph. Registered singleton analyzers are available at documented headless graph locations matching their configured references; loading must not rely on another application's hidden root subjects.
4. Collect explicitly in-scope rule definitions and run the shared evaluator. Preserve original source locations through parsing and resolution so diagnostics can point back to authored files.
5. Emit a report and exit status suitable for CI. Validation reads authored inputs and never changes them as a side effect.

The documentation mount, selected rules, analyzer registry/configuration, source inputs, and supported capabilities must be reproducible locally and in CI. A graph path such as `/Repository` is relative to that mounted graph, not an operating-system path. Relative raw source locations retain their file-origin semantics. Exact loader/bootstrap configuration and how shared analyzers are published in a headless host remain design work; do not imply that an ordinary DI singleton is already sufficient.

## Command shape [Proposed]

Illustrative usage after a future CLI is installed and its supported analyzers are configured:

```shell
archdocs check --docs ./docs --format text
archdocs check --docs ./docs --format json --output ./artifacts/architecture-checks.json
```

`--docs` identifies the documentation mount. Rules are collected from supported metadata in that graph; rule/scope filters and the failure threshold need an explicit final CLI schema. `--output` writes the report only. No command shown here is implemented, and these examples are not an installation instruction or a complete CI workflow.

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
