# Single-Context Lifecycle: Handover

State as of 2026-08-29. Supersedes every earlier plan and spec in this folder.

## What is done and merged

Four independent workstreams, each written test-first and each reviewed by an agent that did not write it. Every one of them had something found in review that its own green suite did not show.

| area | what | production lines |
|---|---|---|
| Generator | escaped identifiers (`@event`, `@class`) produced uncompilable code at four sites; chaining constructors dropped `[SetsRequiredMembers]` (CS9039) | +34 |
| Registry and scanner | reference equality for subject keys; dictionaries behind broadly-declared properties; restored the initial change notification of scalar dynamic properties | +57 |
| Derived ownership | master owned the subject held by a derived property and the rewrite threw; restored for the two shapes that genuinely store | 2 conditions |
| Connectors | provisional-anchor handling removed entirely: construct detached, assign, populate | **−56** |

Production sits at **+2787** over master across 102 files; public API delta is +77/−59.

**Property names still drop the `@` escape.** Ruled an edge case by the maintainer, deliberately not fixed.

## What is NOT done

**The write-protocol redesign is design-stage only. Nothing of it is implemented.** Four adversarial rounds returned NOT SOUND, on revisions 2 through 5. Eight instances of one class were found, one of them introduced by the fix for another.

The class: **an argument attributes information to a predicate that the predicate does not carry.**

Round 4 produced the artefact that makes this tractable: a systematic classification of every predicate the design relies on. Five unsound or partial, six sound. That table is the starting point for whoever picks this up, and it is in the campaign scratch alongside the design.

The one tension with no local fix: the capture skips subjects already attached to this context, and the design deletes the post-store getter read that currently rescues them. Either a getter read stays outside the capture, breaking the design's own mechanical grep audit, or those subjects are lost. Confirmed by probe in two reachable shapes, one needing no unusual precondition, and **none of the 628 existing tests catches either**.

## The acceptance suite is the asset

Branch `campaign/protocol-repros`, 15 commits, **zero production files touched**. Nine repros failing for their stated mechanisms, one parity guard, six mutation-verified characterization tests, and a register at `WriteProtocolAcceptance.cs` recording per repro what would turn it green **without** the defect being fixed.

Two repros needed re-arming during the campaign because a design change moved their instrument, and both times the guards caught it rather than passing silently. The general rule learned: a repro whose trigger counts protocol-internal events is coupled to the protocol it tests. Trigger on a condition naming the phase, never on an ordinal.

Before the campaign, **1534 existing tests did not catch `CommitsEdgeTo` returning `true` unconditionally**, a predicate that governs notification order and, in one shape, committed final state.

## Held back deliberately

`campaign/attachment-coherence` fixes a real data race: context and anchor were published as two stores, so a lock-free reader could observe a non-`None` anchor with a null context. Torn observations go from ~15,000-26,000 per run to **zero**.

**It is not merged, and should not be until benchmarked.** It costs +40 bytes per attachment transition where there were none, and adds one dependent load to every intercepted read, write and invoke. That is unmeasured, and decision-grade numbers need the CPU pinned. One countervailing data point: contended transitions per 10s went 9.4M to 18.2M, because the snapshot lets `TryGetAttachment` drop the monitor.

## Open items, ranked

1. **Write-protocol redesign.** Design plus predicate table in the campaign scratch; acceptance suite on its branch.
2. **Benchmark and decide `campaign/attachment-coherence`.**
3. **Dictionary same-key replace loses data.** The emitter produces `Insert@key, Remove@key` and applying it deletes the entry. Pre-existing, confirmed on master and branch. Not filed.
4. **`ImmutableDictionary` wrong-typed key throws** through the unguarded fast path. Pre-existing. Closing it means a tolerant lookup on a hot path, so it needs a measurement rather than a patch.
5. **MQTT `MqttServerLivenessTests` teardown ordering.** Disconnects clients after awaiting broker shutdown, so a socket error there replaces a genuine assertion failure.

## Nothing here needs re-deriving

The two ledgers (54 findings, 58 master-parity deltas with every public API snapshot line mapped), the connector probe, the reduction analysis and four rounds of adversarial findings all live in the campaign scratch. Read them before re-investigating anything above.
