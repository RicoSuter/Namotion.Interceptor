# Sonar analyzer policy

The shared configuration is in [src/.editorconfig](../src/.editorconfig). During the [incremental rollout](https://github.com/RicoSuter/Namotion.Interceptor/issues/545), projects opt in with a private `SonarAnalyzer.CSharp` package reference. The first PR adopts the core library and its tests. The final rollout PR moves the package reference to `src/Directory.Build.props` for the whole solution, including tests and HomeBlaze.

The policy retains the analyzer's default-enabled rules and explicitly enables S104, S107, S134, S138, S1067, S1200, S1541 and S3776 at their default thresholds. Warning severity is enforced by the existing warnings-as-errors build setting. S108 and S2699 remain enabled. Assertion helpers carry an emitted `AssertionMethodAttribute` so Sonar recognizes them even when called from another assembly.

## Disabled rules and reuse in other projects

These exclusions reflect this library's implementation patterns. Evaluate the reasons individually before copying the configuration to another project. Disabling a rule does not waive correctness, synchronization, or performance review.

| Rule | Why disabled here | Considerations for another project |
| --- | --- | --- |
| S3267: simplify loops with LINQ | The invalidation walk filters an array while updating a visited set and worklist. A `Where` replacement can add an iterator, delegate and closure allocation to a path that deliberately avoids them. Similar explicit loops are common in this library. | Keep the rule when LINQ readability is preferred and allocation-sensitive paths are rare, or suppress individual hot paths. An explicit loop is not inherently faster; assess the actual replacement. |
| S2743: static fields in generic types | `PropertyTypeIndex<TProperty>.Value` intentionally assigns a separate static index to every closed property type. This supports array lookup without a type-keyed dictionary on each intercepted access. Sharing the field across types would break the cache's identity scheme. | Keep the rule where generic static state is supposed to be shared across type arguments. Disable or suppress it where per-type caches, serializers, or dispatch tables are an intentional design. |
| S2696: instance methods writing static fields | Synchronous instance operations acquire and release thread-local traversal buffers, and disposing a scope restores its thread's previous ambient value. Retaining these patterns avoids per-operation scratch allocations and preserves scope semantics. Making the caller static does not itself provide synchronization. | Keep the rule where instance methods unexpectedly mutate process-wide state. This exclusion is also about ownership and scope, not only speed. Review ordinary shared statics for races and thread-local buffers for reentrancy; a thread-local buffer is not automatically safe across callbacks or asynchronous suspension. |
| S2326: unused type parameters | A generic argument can define type identity without appearing in any member signature or body. `PropertyTypeIndex<TProperty>` uses that identity for separate static cache slots and direct array lookup. These patterns must remain available for future optimizations without adding dummy uses or local analyzer exceptions. | Keep the rule where such type-identity patterns are uncommon and unnecessary generic parameters are a recurring maintenance problem. Removing a seemingly unused parameter from a per-type cache changes its semantics. |
| S2094: empty classes | Empty marker classes and test fixtures intentionally encode type identity or metadata. | Keep the rule where an empty class usually indicates unfinished implementation. |

## Test-only exclusions

S1144 (unused private types or members) is disabled only for C# files under directories ending in `.Tests`, including those beneath HomeBlaze. Fixture constructors or members can be reached through attributes, reflection, or generated code. We accept losing some dead-code detection in tests to reduce this noise. S1144 remains enabled in production. S2326 is disabled globally for the type-identity patterns described above.

The editorconfig override matches file paths, not MSBuild's `IsTestProject` property. Test projects with another directory naming convention, shared testing libraries, and benchmark projects do not receive these exclusions automatically. When reusing this policy elsewhere, adapt the path pattern to the repository's layout and decide whether its tests benefit more from unused-member detection or reduced fixture noise. S108 and S2699 remain enabled in tests.

## Inline TODO policy: to be decided

S1135 is disabled for now, so inline TODO comments are allowed in production and test code. The existing performance and executor-design notes remain at their original locations. Whether to enforce TODO tracking, require issue links, or leave this rule disabled permanently is **to be decided later**. This provisional choice does not change the requirement to record newly discovered problems in the main rollout issue.

## Local exceptions and follow-ups

Other exceptions stay at the affected member or statement, with a reason. Temporary complexity suppressions in the first adoption preserve established ordering and concurrency code while its decomposition is deferred. They are not a blanket exemption for new code.

Record every discovered bug, concern, and test gap in the [main rollout issue's body](https://github.com/RicoSuter/Namotion.Interceptor/issues/545), distinguishing reproduced defects from investigation hypotheses and completed fixes from deferred work. Keep each analyzer PR mostly mechanical and local. Document larger follow-ups in that PR and create separate issues only after it merges.

The shared editorconfig also preserves Verify snapshot encoding, line endings, and whitespace. These settings are required by the repository's Verify configuration checks when an editorconfig exists.
