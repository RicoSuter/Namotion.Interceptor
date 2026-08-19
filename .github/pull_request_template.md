<!-- Delete each comment as you fill its section in, so the description reads as prose rather than
     half boilerplate. Anything left here also reaches the tools that read this body later.
     Drop any heading that does not apply, except Breaking changes. -->

## Summary

<!-- What changed and why it matters to a consumer. A few sentences or bullets. -->

## Why

<!-- The problem this solves, when the summary does not already make it obvious. Drop this heading if it does. -->

## Breaking changes

<!-- Public API changes, renamed or removed members, and behavior changes a consumer must act on,
     each with the migration step that follows from it.

     Write "None" rather than deleting this heading, so an empty section is a decision rather than
     an oversight. This is the section the release notes most often need and most often lack.

     Check the public API snapshot diff before answering: a change in
     VerifyChecksTests.PublicApi.verified.txt that is not described here reaches consumers unannounced. -->

## Performance

<!-- Start with a paragraph in plain terms: what the change costs or saves, and where. State a known
     regression as readily as an improvement, and say when a cost was accepted on purpose.

     Then condense the numbers into the table, one row per benchmark the change can reach. Do not paste
     raw BenchmarkDotNet output for both arms; a reader cannot diff two 15-column tables by eye.

     Keep at least one row the change cannot reach, marked as the noise reference. A delta means nothing
     until it clears what that row did in the same run, and picking a reference is subtler than it looks,
     because [GlobalSetup] is per class. See docs/benchmarking.md.

     Drop this heading when nothing was measured; an unmeasured claim is worse than silence. -->

| Benchmark | Before | After | Delta | Allocated |
|---|---:|---:|---:|---|
|  |  |  |  |  |
| _noise reference_ |  |  |  |  |

## Diff composition

<!-- Generate with: pwsh scripts/diff-composition.ps1
     Add -PerProject to break production code down per project on a change that spans several. -->

| Area | Files | Added | Removed | Net |
|---|---:|---:|---:|---:|

## Verification

<!-- Tick what you ran, and give the count where a suite reports one. Strike an entry through with
     ~~two tildes~~ and a short reason when it does not apply, so an unticked box always means
     "not done yet" rather than "not relevant". Say plainly what you did not run and why.

     Unit tests:        dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
     Integration tests: per project, for connector or HomeBlaze UI changes
     Benchmarks:        docs/benchmarking.md, every benchmark the change can reach plus a noise reference
     Connector Tester:  docs/connector-tester.md, load and chaos profiles for risky connector work,
                        agreed while planning because they take hours -->

- [ ] Unit tests
- [ ] Integration tests
- [ ] Benchmarks
- [ ] Connector Tester load profile
- [ ] Connector Tester chaos profile
