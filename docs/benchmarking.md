# Benchmarking

The benchmark suite lives in `src/Namotion.Interceptor.Benchmark` and runs on BenchmarkDotNet. Running it is easy; reading the result correctly is not, because the interception paths are short enough that ordinary measurement noise is the same size as the deltas people care about.

## The usual process

1. Work out which benchmarks can reach the code you changed. `dotnet run --project src/Namotion.Interceptor.Benchmark -c Release -- --filter "*" --list flat` lists every benchmark method. A class with `[Params]` turns each of its methods into several report rows, so the report is longer than that list.
2. Run those in one filtered comparison, together with something the change cannot reach, so the run carries its own noise reference. See below for what qualifies.
3. Only reach for the whole suite when the change is broad enough that step 1 cannot bound it. That costs hours, so propose it and get agreement before starting it rather than launching it and reporting back much later.

```
pwsh scripts/benchmark.ps1 -Filter "*RegistryBenchmark*","*ServiceOrderResolverBenchmark.LinearChain*" -LaunchCount 3
```

`-Filter` takes one pattern or several, matched as OR. Pass them as a PowerShell array, not as one comma separated string, and note the array form needs a PowerShell caller: from bash, `"a","b"` collapses into a single comma joined argument that matches nothing. Other flags: `-BaseBranch` (defaults to the local `master`), `-LocalOnly` for absolute numbers with no comparison arm, `-Stash`, `-Short`, `-LaunchCount` (defaults to 1) and `-MemoryRandomization:$false` to turn off heap randomization, which is otherwise on. The report lands in the working directory as `benchmark_<timestamp>.md`.

### Where the run goes wrong

The script checks the base branch out **in your working tree**, runs, then checks your branch back out. Two consequences:

- Run it from a git worktree, so your main checkout is left alone, and put that worktree **outside** the repository, because BenchmarkDotNet resolves the benchmark project by searching upward from the working directory and a worktree inside the repository gives it a second candidate.
- Git refuses to check out a branch that another worktree already holds (`fatal: '<branch>' is already used by worktree at ...`), so with the default `-BaseBranch master` the run dies at the first checkout whenever your main checkout is sitting on `master`. Passing `-BaseBranch origin/master` avoids it, because a remote-tracking ref is checked out detached.

The base ref is also resolved at that checkout, not when the script starts, so anything that moves it in between changes what you measured: a fetch if you passed a remote-tracking ref, or a pull, reset or commit from another worktree if you passed a local branch. Afterwards, confirm what each arm actually built from `$(git rev-parse --git-dir)/logs/HEAD`, and quote both commit hashes when reporting.

## Pin the CPU first

A CPU that changes frequency mid-run makes the two arms incomparable, by far more than anything being measured. Pin it to a fixed frequency, and make sure nothing else is running, including your own builds, test runs and leftover MSBuild worker nodes.

Do not use BenchmarkDotNet's header to check this; it reports a per-process view and has shown two different maximum frequencies for the two arms of a single pinned run. Check the operating system instead, on Linux via `scaling_governor`, `scaling_min_freq`, `scaling_max_freq` and `intel_pstate/no_turbo`.

Allocation columns hold up much better than timings, and for allocation focused work they are usually the whole answer. They are not immune, though: BenchmarkDotNet accounts allocations for the whole process, so a benchmark with a background thread, such as `PropertyChangeSubscriptionsBenchmark` or `SubjectSourceBenchmark`, absorbs whatever that thread allocated and can move with scheduling. The `Gen0`/`Gen1`/`Gen2` columns depend on heap state and are perturbed by heap randomization.

## Reading the results

**BenchmarkDotNet's statistics do not span the two arms.** Its comparison machinery, the `Ratio` column against a `[Benchmark(Baseline = true)]` method and the Mann-Whitney test behind `--statisticalTest`, works within one run, where BenchmarkDotNet builds and measures both sides itself. A branch comparison is two independent runs on two separately built binaries, concatenated by the script afterwards with nothing computed across them. That is why the two arms can disagree while both look precise.

**So read the noise off something the change cannot reach.** Whatever such a row does in that run is what the harness does to unchanged code, and that is the bar a real delta has to clear. Read it from your own run, since it moves between runs and machines, and remember that shorter benchmarks are more sensitive to code placement, so a slow reference understates the floor for fast rows.

Picking one needs care, because `[GlobalSetup]` is per class, not per method, and heap randomization re-runs it after every iteration. A row is only insulated if its **class** setup also avoids your change: `RegistryBenchmark.GenerateSubjectId` touches no subject itself, but its class setup builds a tracked `Car` and a thousand more, so it is no reference at all for a tracking or registry change. When in doubt use a different class.

`ServiceOrderResolverBenchmark` is the safe fallback: it is the only class producing rows that never touches an `[InterceptorSubject]`, in its benchmarks or its setup. One of its four rows is enough, `LinearChain` for instance; take the class when you want a spread rather than a single number.

**The Error column is not a significance test.** It is a confidence interval within one run, describing how precisely that run measured its own number, not whether the comparison would land in the same place tomorrow. A reference row can move many times its own error bar with its code provably unchanged. `-LaunchCount N` does not close this: it averages over process-level variation, but each arm keeps its own binary, so a difference caused by code placement reproduces on every launch instead of averaging out.

**Repeat before believing a small delta.** Running the same two commits twice and comparing the two runs measures the noise directly, because the inputs were identical. Expect rows to flip sign.

**Watch the timer floor.** A row whose per-operation cost approaches a nanosecond is at the edge of what the harness resolves, and its percentage swings wildly. That is a property of the measurement, not a rule about the code: benchmarks that deliberately measure such paths amortize them with `OperationsPerInvoke`, as `RegistryBenchmark.ReadParents` does over 256 calls, and those numbers are meaningful. Distrust a sub-nanosecond row that is not amortized.

## When a benchmark cannot answer the question

A benchmark can show "within noise". It cannot show "free". When the question is whether a guard, a cast or an extra call costs anything on a hot path, compare the machine code instead: it answers in minutes and is not subject to the noise floor. It is the way to settle whether a helper stayed inlined after gaining a branch, or whether a predicate that is constant per instantiation folded away entirely.

Build a small console application that drives the members in a loop and run it on both sides:

```
DOTNET_TieredCompilation=0 DOTNET_TieredPGO=0 DOTNET_ReadyToRun=0 \
DOTNET_JitDisasmDiffable=1 DOTNET_JitDisasm='SetPropertyValue WriteProperty' \
dotnet app.dll > jit.txt
```

`DOTNET_JitDisasmDiffable=1` is what makes two dumps comparable; without it addresses differ every run. Patterns are space separated, and a bare method name matches that method on every type declaring one, which is usually what you want for a chain. Explicit interface implementations need their fully qualified metadata name, `Namotion.Interceptor.IInterceptorSubject.get_Properties` rather than `get_Properties`, or nothing matches and it looks like the method was never compiled.

Diff the instruction lines only. The header carries a `; N single block inlinees` count that changes when a new inlinable helper appears even though the instructions are identical, so it reads as a difference when there is none.

For a generator change specifically, no runtime assembly moves at all. If `git diff --name-only <base>..<branch> -- src/ ':!*Tests*' ':!*Benchmark*' ':!*Generator*'` is empty, only generated code can differ, so emit it on both sides and diff that first. Force a rebuild, because an up-to-date build emits nothing and an empty output directory on both sides looks exactly like "nothing changed":

```
dotnet build <project> -c Debug -t:Rebuild -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=<dir>
```

Files appear under `<dir>/<generator-assembly>/<generator-type>/*.g.cs`.

## What a run costs

Rough wall clock for both arms with the full job and `-LaunchCount 3`, from one machine, for scoping only:

| Scope | Both arms |
|---|---|
| `*RegistryBenchmark*` | about 30 minutes |
| Whole suite | about 6 hours |

`SubjectSourceBenchmark` measures in milliseconds per operation, and `SubjectHierarchyBenchmark.ConstructThreeLevel` is allocation heavy and multimodal, so BenchmarkDotNet keeps iterating toward its ceiling instead of converging. Between them they dominate a whole-suite run. Judge `ConstructThreeLevel` on its allocation column rather than its timing.

A benchmark class that exists on only one branch runs on that arm and has nothing to compare against on the other, so exclude it from the filter unless you are deliberately measuring it. If you want a new benchmark compared properly, put it on the base branch first, which is what `-BaseBranch` is for.
