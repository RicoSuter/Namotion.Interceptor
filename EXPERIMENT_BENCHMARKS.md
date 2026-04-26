# Experiment Benchmarks — `experiment/minimal-properties-filter`

Per-step benchmark log for the registry attribute refactor experiment. The goal
was to land the original PR #268's API (`Properties` excludes attributes,
`property.Attributes`, distinct `RegisteredSubjectAttribute` type) while keeping
the `AddLotsOfPreviousCars` micro-benchmark regression under 2–3% of master —
the original PR regressed 9.2%.

**Machine:** Apple M4 Max, macOS 26.3.1, .NET 9.0.10, BenchmarkDotNet v0.15.5
**Filter:** `*AddLotsOfPreviousCars*`, `LaunchCount=3` on every run
**Base:** `master`

## Summary

| Step | Commit | Branch (ms) | Master (ms) | Δ vs master | Δ vs prev step | Branch (MB) | Δ alloc |
|---|---|---:|---:|---:|---:|---:|---:|
| 1. `Properties` filters attributes + `PropertiesAndAttributes` inclusive | `e4b5e889` | 33.29 | 33.05 | +0.7% | (baseline) | 22.83 | +0.17% |
| 2. Cold-path `IsAttribute` cleanup + `GetAllPropertiesAndAttributes` | `e2d8955e` | 33.16 | 33.26 | −0.3% | −0.13 ms | 22.83 | +0.17% |
| 3. Minimal sealed `RegisteredSubjectAttribute` subclass (no fields) | `98799f99` | 33.30 | 32.43 | +2.7% | +0.14 ms | 23.01 | +0.97% |
| 4. Typed fields + `GetAttributedProperty` on subclass | `aa3e5dc0` | 33.64 | 33.24 | +1.2% | +0.34 ms | 23.04 | +1.10% |
| 5. Hide `AttributeMetadata` (internal); type `Attributes` / `TryGetAttribute` | `6725d7f7` | 33.70 | 32.88 | +2.5% | +0.06 ms | 23.04 | +1.10% |
| 5. *(rerun)* | `6725d7f7` | 33.39 | 32.07 | +4.1% | −0.31 ms | 23.04 | +1.10% |

## Key observations

**Branch time is remarkably stable** across runs: **33.2–33.7 ms** regardless of
benchmark session. The measured absolute cost of all changes vs master is
**~0.5–1.3 ms per operation**.

**Master time drifted faster over the session** (33.26 → 32.07 ms), likely from
thermal / macOS environmental effects as the laptop warmed into a steady
state. This inflates the `Δ time` percentage even though branch performance
itself hasn't changed.

**Subclass existence is the real cost**: step 3 added only a zero-field sealed
subclass and immediately saw a measurable ~2.7% bump. Subsequent API-surface
changes (steps 4 and 5) added no additional runtime cost — they're compile-time
only. This matches the earlier isolated "minimal subclass" experiment that
confirmed `FrozenDictionary` values holding mixed concrete types costs ~2.7%
from JIT / devirtualization effects.

**Allocation cost is consistent**: +0.17% (snapshot field) growing to +1.1%
(extra fields on attribute subclass × 2 attributes per `Car` × 1000 attached
subjects per op). Under 1 MB additional on a 22.79 MB baseline.

**Gen1/Gen2 collections**: identical to master for steps 1–2, modest bumps for
steps 3–5 (Gen1 1375 → 1437, Gen2 437.5 → 437.5). Not the source of the time
delta.

## Step details

### Step 1 — Properties filters attributes (commit e4b5e889)

Renamed master's `Properties` accessor to `PropertiesAndAttributes` and added
a new lazy `Properties` that filters out attributes. Detach path (hot) uses
`PropertiesAndAttributes` to stay alloc-free. Updated 2 verified-test files
that assumed old inclusive semantics.

**Result: +0.7% time, +0.17% alloc.** Essentially noise. The lazy snapshot
stays `null` under attach-heavy workloads since `Properties` isn't read.

### Step 2 — Cold-path cleanups (commit e2d8955e)

Removed redundant `if (property.IsAttribute) continue;` filters from seven
cold-path iteration sites (`SubjectUpdateFactory`, MCP tools, OpcUa mappers,
`CustomNodeManager`) now that `Properties` excludes attributes. Added
`GetAllPropertiesAndAttributes` extension; refactored `GetAllProperties` to
share a walker.

**Result: −0.3% time, +0.17% alloc.** Branch slightly *faster* than master
(noise; the iteration-skip removal saves a handful of checks per op).

### Step 3 — Minimal sealed Attribute subclass (commit 98799f99)

Added `public sealed class RegisteredSubjectAttribute : RegisteredSubjectProperty`
with **zero new fields** — just a marker. Changed `_properties` construction to
instantiate this subclass when `PropertyAttributeAttribute` is present on the
reflection attributes. Everything else unchanged (same `IsAttribute`,
`AttributeMetadata`, same behavior).

**Result: +2.7% time, +0.97% alloc.** The mere existence of a second concrete
type in `_properties.Values` regresses perf — the JIT can't devirtualize as
aggressively when the dict holds mixed types, and the runtime path for
allocating a subclass differs slightly from the base.

### Step 4 — Typed fields on subclass (commit aa3e5dc0)

Added `AttributeName`, `PropertyName` properties on `RegisteredSubjectAttribute`
(populated from `PropertyAttributeAttribute` in its constructor). Moved
`GetAttributedProperty` from `Property` (where it threw if called on a non-
attribute) to the subclass (where it's always valid).

**Result: +1.2% time, +1.10% alloc.** Allocation up slightly from two extra
string refs per attribute (16 bytes × 2 attributes × 1000 Cars = 32 KB per op).
Time change from step 3 is within noise.

### Step 5 — Hide `AttributeMetadata`; type `Attributes` (commit 6725d7f7)

Changed `RegisteredSubjectProperty.AttributeMetadata` from `public` to
`internal`. Typed `Attributes` as `RegisteredSubjectAttribute[]` and
`TryGetAttribute` as `RegisteredSubjectAttribute?`. Made `BrowseName` virtual
with override in `RegisteredSubjectAttribute`. Updated external callers
(`SubjectUpdateFactory`, `SubjectUpdateBuilder`, `SubjectPropertyPanel.razor`)
to use typed accessors or pattern matching.

**Result: +2.5% → +4.1% time** (two runs), **+1.10% alloc.**
Compile-time only change — no runtime behavior difference from step 4. Branch
absolute time barely moved (33.64 → 33.70 / 33.39 ms). The reported Δ%
increase is entirely from master drifting faster between runs.

## Methodology note

Each benchmark uses `LaunchCount=3` (3 separate processes, aggregated). Error
bars are typically ±0.15–0.40 ms on this workload. Branch run-to-run spread is
~0.3 ms; master spread during this session was larger (1.2 ms) due to
session-level thermal/environmental drift.

For a PR decision this is not a flat statistical comparison — interpret the
**branch absolute time as stable at ~33.5 ms** and master as having real noise.
The honest verdict is "~2–4% regression vs master depending on the baseline
day", isolated to an extreme attach-heavy scenario.

---

## Laptop B (Intel Core Ultra 7 258V, .NET 9.0.15, BenchmarkDotNet v0.15.5)

Switched machines mid-experiment. Absolute ms numbers are not comparable to
laptop A (different CPU, OS, JIT codegen). Same-session within-day Δ% vs master
remains comparable.

Each row is a single `scripts/benchmark.ps1 -Filter "*AddLotsOfPreviousCars*"
-LaunchCount 3` invocation, which runs master and the branch back-to-back from
the same warm working tree.

| Step | Branch (ms) | Master (ms) | Δ vs master | Δ vs prev step | Branch (MB) | Δ alloc |
|---|---:|---:|---:|---:|---:|---:|
| 5 (re-baseline at `b7092806`) | 56.84 | 53.64 | **+5.97%** | (baseline) | 21.63 | +1.17% |
| 6 (`RegisteredSubjectMember` abstract base + `TryGetMember` + `GetAttributedMember`) | 58.79 | 53.61 | **+9.66%** | **+1.95 ms (+3.4%)** | 21.77 | +1.82% |
| 6 + experiment B (`[MethodImpl(AggressiveInlining)]` on `Member.ctor`) | 58.50 *(median)* | *master noisy: 64.51 mean / 54.77 median, StdDev 17 ms* | n/a | **~0 vs step 6** | 21.77 | 0% |

### Step 6 — Member abstract base + TryGetMember + GetAttributedMember

Hoisted `Parent`, `Name`, `BrowseName` (virtual), `ReflectionAttributes`,
`AttributesCache`, `Attributes`, `TryGetAttribute`, `AddAttribute`,
`AddDerivedAttribute` from `RegisteredSubjectProperty` to a new abstract
`RegisteredSubjectMember` base. `RegisteredSubjectProperty` now inherits from
it; `RegisteredSubjectAttribute` chain is unchanged. Added
`RegisteredSubject.TryGetMember(string)` returning `RegisteredSubjectMember?`
(today probes `_properties` only). Renamed
`RegisteredSubjectAttribute.GetAttributedProperty()` →
`GetAttributedMember()` returning `RegisteredSubjectMember`. Updated callers
in `SubjectUpdateFactory` (root-member walk) and
`SubjectRegistryJsonExtensions` (pattern-match back to property).

Rationale: prepare for the `[SubjectMethod]` follow-up (PR #264) so methods
can slot in as `RegisteredSubjectMethod : RegisteredSubjectMember` on a
separate `_methods` store, without restructuring connector code that walks
attribute chains via the common base. Two-store design avoids compounding the
JIT regression PR #268's unified `_members` dict would introduce.

**Result: +3.4% time, +0.65% alloc on top of step 5.** Master is rock-stable
between runs (53.64 → 53.61 ms), so the delta is hardware-real, not session
drift. Most plausible mechanism: extra constructor chain hop per
`RegisteredSubjectProperty` allocation (Member ctor → Property ctor) plus
field relocation; no new virtual dispatch was introduced at hot sites.

### Experiment B — `[MethodImpl(AggressiveInlining)]` on `Member.ctor`

Hypothesis: the JIT was skipping ctor inlining and the extra base-ctor frame
per allocation accounted for some of step 6's cost.

**Result: no measurable change.** Branch absolute time stayed at ~58.5 ms
(median) — within noise of step 6 without the attribute (58.79 ms). Run C's
master measurement is too noisy to use directly (StdDev 17 ms, mean 64.51 vs
median 54.77 — likely a background-process or thermal blip during the master
segment), so the Δ-vs-master percentage is not interpretable for run C.
Comparing branch absolute times across runs confirms the inlining attribute
did not help; the JIT was already inlining the ctor, or the regression has a
different root cause.

### Per-step regression vs master (laptop B)

Run A and Run B masters were essentially identical (53.64 / 53.61 ms),
giving a stable laptop B master baseline of **~53.6 ms**. Run C's master
measurement was contaminated (StdDev 17 ms, mean 64.51 vs median 54.77 — a
clear outlier session); comparison against the stable baseline is more
honest.

| Step / change | Branch (ms) | Δ vs master (ms) | Δ vs master (%) |
|---|---:|---:|---:|
| Step 5 baseline (subclass already in place from steps 3–5) | 56.84 | +3.20 | **+5.97%** |
| + step 6 (Member abstract base, `TryGetMember`, rename) | 58.79 | +5.18 | **+9.66%** |
| + experiment B (`[MethodImpl(AggressiveInlining)]` on `Member.ctor`) | 58.50 *(median)* | +4.90 | **+9.14%** |

Cumulative cost: subclass alone ~6%, + abstract base ~10%, +
AggressiveInlining tweak no recovery.

### Decision point

Step 6's pre-commitment said: re-benchmark; if step 6 stays in the step 5
band (≤1% delta) the abstract base is safe; if it grows by ≥1% prefer the
interface variant. **It grew by ~3.4%**, and experiment B confirmed the cost
is not from a non-inlined ctor.

Options:

1. **Keep the abstract base, accept ~10% total** on this synthetic micro. The
   typed `RegisteredSubjectMember` API enables the `[SubjectMethod]` PR to
   land cleanly with shared field-backed implementations.
2. **Replace the abstract base with `IRegisteredSubjectMember` interface** —
   field hoisting reverts; Property and (later) Method each declare their own
   `Parent`/`Name`/`AttributesCache` (≈10 lines duplication). Should restore
   step 5's ~6% baseline. `GetAttributedMember()` returns the interface; cold
   call sites pay interface dispatch instead of virtual dispatch.
3. **Accept ~6% (revert step 6 entirely)**, defer the methods-PR enablement
   until that work lands and prove necessity then.
4. **Try experiment A** (drop stored `Name`; abstract on Member, override on
   Property as `=> Reference.Name`). Trades the field for a virtual call;
   could regress further if `Name` reads dominate. Not yet measured.
