# Sonar analyzer workflow

Use [src/.editorconfig](../src/.editorconfig) as the starting point when adopting this configuration elsewhere. It is the canonical source for rule descriptions, severities, exclusion reasons, and reuse considerations.

The [incremental rollout](https://github.com/RicoSuter/Namotion.Interceptor/issues/545) tracks adoption across the solution. Projects opt in with a private `SonarAnalyzer.CSharp` package reference until the final rollout PR centralizes that reference in `src/Directory.Build.props`. Configuration alone does not install the analyzer. The existing build settings enforce warnings as errors.

## Adoption and validation

Keep adoption PRs mostly mechanical and local. Inspect diagnostic locations before changing code, and follow the [agent approval policy](../AGENTS.md#analyzer-policy) when introducing or broadening an exception. Local suppressions should explain the intentional behavior or identify the deferred work.

Rebuild the adopted projects and run their relevant tests. Record diagnostic counts before and after, separating fixes from deliberate exclusions and temporary suppressions. For changes to shared configuration, verify both the intended scope and a control file outside that scope.

Assertion helpers must fail when their condition is unmet. When annotating one for analyzer recognition, verify it through a separately compiled caller and retain an assertion-free control that still produces a diagnostic.

Record every discovered bug, concern, and test gap in the main rollout issue's body, distinguishing reproduced defects from investigation hypotheses and completed fixes from deferred work. Document larger follow-ups in the adoption PR and create separate issues only after it merges.

## Open policy decisions

The long-term approach to inline TODO tracking is **to be decided later**: leave it unrestricted, require issue references, or enforce another tracking policy. The current setting and its provisional rationale live in `.editorconfig`.
