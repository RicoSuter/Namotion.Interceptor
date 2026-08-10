# Benchmarking

The benchmark suite lives in `src/Namotion.Interceptor.Benchmark` and runs on BenchmarkDotNet. Running it is easy; reading the result correctly is not, because the interception paths are short enough that ordinary measurement noise is the same size as the deltas people care about.

## The usual process

1. Work out which benchmarks can reach the code you changed. `dotnet run --project src/Namotion.Interceptor.Benchmark -c Release -- --filter "*" --list flat` prints every row.
2. Run those in one filtered comparison, and check the filter also catches at least one row the change cannot reach, to read the noise off. Often it already does. If it does not, add a single stable row such as `*ServiceOrderResolverBenchmark.LinearChain*`.
3. Only reach for the whole suite when the change is broad enough that step 1 cannot bound it. That costs hours, so propose it and get agreement before starting it rather than launching it and reporting back much later.

```
pwsh scripts/benchmark.ps1 -Filter "*RegistryBenchmark*","*ServiceOrderResolver*" -LaunchCount 3
```

`-Filter` takes one pattern or several, matched as OR. Pass them as a PowerShell array, not as one comma separated string. Other flags worth knowing: `-BaseBranch` (defaults to `master`), `-LocalOnly` for absolute numbers without a comparison arm, `-Stash`, and `-Short`. Heap layout randomization is on by default. The report lands in the working directory as `benchmark_<timestamp>.md`.

Two things that have burned us: `-BaseBranch` is resolved when that arm is checked out, not when the script starts, so a fetch during the run can move it silently, and BenchmarkDotNet searches the working directory for a project file, so run from a worktree outside the repository. Afterwards, confirm what each arm actually built from `.git/worktrees/<name>/logs/HEAD` and quote both commit hashes when reporting.

## Pin the CPU first

A CPU that changes frequency mid-run makes the two arms incomparable, by far more than anything being measured. Pin it to a fixed frequency, and make sure nothing else is running, including your own builds, test runs and leftover MSBuild worker nodes.

Do not use BenchmarkDotNet's header to check this. It has printed two different maximum frequencies for the two arms of a single correctly pinned run. Check the operating system instead, on Linux via `scaling_governor`, `scaling_min_freq`, `scaling_max_freq` and `intel_pstate/no_turbo`.

Allocation columns are unaffected by any of this. They are deterministic counts, so they stay trustworthy even when the timings are not, and for allocation focused work they are the whole answer.

## Reading the results

**BenchmarkDotNet's statistics do not span the two arms.** Its comparison machinery, the `Ratio` column against a `[Baseline]` and the Mann-Whitney test behind `--statisticalTest`, all works within one run, where BenchmarkDotNet builds and measures both things itself. A branch comparison is two independent runs on two separately built binaries, stapled together by the script afterwards, so none of it applies. Every figure in the report is computed inside its own arm, which is why the two arms can disagree while both look precise.

**So read the noise off a row the change cannot reach.** Most filtered runs already contain one: `RegistryBenchmark.GenerateSubjectId`, for instance, is a static method that never touches a subject. Whatever such rows do in that run is what the harness does to an unchanged code path, and that is the bar a real delta has to clear. Read it from your own run, since it moves between runs and machines, and note that shorter benchmarks are more sensitive to code placement, so a slow control understates the floor for fast rows.

`ServiceOrderResolverBenchmark` is the fallback when the filter has nothing unreachable in it. It is the only class in the suite that never touches an `[InterceptorSubject]` at all. Every other class constructs subjects, including `SourcePathProviderBenchmark` and `SubjectUpdateBenchmark`, which are easy to mistake for controls. One of its four rows is enough; take the whole class only when you want a spread rather than a single number.

**The Error column is not a significance test.** It is a confidence interval within one run, describing how precisely that run measured its own number, not whether the comparison would land in the same place tomorrow. A control row can move many times its own error bar with its code provably unchanged. `-LaunchCount N` does not close this: it averages over process-level variation, but each arm keeps its own binary, so a difference caused by code placement reproduces on every launch instead of averaging out.

**Repeat before believing a small delta.** Running the same two commits twice and comparing the two runs measures the noise directly, because the inputs were identical. Expect rows to flip sign.

**Ignore rows below about a nanosecond.** They produce percentages that look dramatic and mean nothing. Judge those on allocations, or not at all.

## When a benchmark cannot answer the question

A benchmark can show "within noise". It cannot show "free". When the question is whether a guard, a cast or an extra call costs anything on a hot path, compare the machine code instead: it answers in minutes and is not subject to the noise floor. It is the way to settle whether a helper stayed inlined after gaining a branch (several types on the read and write paths carry `AggressiveInlining`, which the JIT can decline once a method grows), or whether a predicate that is constant per instantiation folded away entirely.

Build a small console application that drives the members in a loop and run it on both sides:

```
DOTNET_TieredCompilation=0 DOTNET_TieredPGO=0 DOTNET_ReadyToRun=0 \
DOTNET_JitDisasmDiffable=1 DOTNET_JitDisasm='SetPropertyValue WriteProperty' \
dotnet app.dll > jit.txt
```

`DOTNET_JitDisasmDiffable=1` is what makes two dumps comparable; without it addresses differ every run. Patterns are space separated, and a bare method name matches that method on every type declaring one, which is usually what you want for a chain. Explicit interface implementations need their full name, `Namotion.Interceptor.IInterceptorSubject.get_Properties` rather than `get_Properties`, or nothing matches and it looks like the method was never compiled. Strip comment lines before diffing (`grep -vE '^;|^$'`): the header carries a `; N single block inlinees` count that legitimately changes when a new inlinable helper appears, even though the instructions are identical.

For a generator change specifically, no runtime assembly moves at all. If `git diff --name-only <base>..<branch> -- src/ ':!*Tests*' ':!*Benchmark*' ':!*Generator*'` is empty, only generated code can differ, so emit it on both sides with `-p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=<dir>` and diff that before reaching for the disassembler.

## What a run costs

Rough wall clock for both arms with the full job and `-LaunchCount 3`, from one machine, for scoping only:

| Scope | Both arms |
|---|---|
| `*RegistryBenchmark*` | about 30 minutes |
| Whole suite | about 6 hours |

`SubjectSourceBenchmark` measures in milliseconds per operation, and `SubjectHierarchyBenchmark.ConstructThreeLevel` is allocation heavy and bimodal, so BenchmarkDotNet never converges and runs to its iteration ceiling on every launch. Between them they dominate a whole-suite run. Read that row's allocation column rather than its timing.

A benchmark class that exists on only one side produces no comparison row, so exclude it from the filter rather than paying for it on both arms.
