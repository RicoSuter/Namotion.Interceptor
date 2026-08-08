# Connector delivery: why the rules are what they are

Maintainer notes for the outbound delivery path. Consumer-facing behaviour is in
[connectors.md](../connectors.md); this covers the reasoning behind it, which is not recoverable from
the code and has been rediscovered more than once.

## The invariant

> A change may be dropped only if a later commit will carry the settled value in its place.

Everything else follows from that sentence, including the parts that look arbitrary.

## Why commit order and not value comparison

The obvious implementation asks "does this change still carry the property's current value?" and drops
it when the answer is no. It was implemented, shipped in a branch, and replaced. Three reasons:

1. **It cannot judge derived or runtime-registered properties.** The comparison is only meaningful when
   the getter returns what the write stored. A derived getter recomputes and can return a fresh instance
   that never compares equal; a runtime-registered property carries a caller-supplied getter that need
   not read stored state at all. Both were therefore exempted and delivered unconditionally, which is
   every property the OPC UA client loader creates.
2. **It boxes.** Comparing `object?` against `object?` allocated 48 bytes per delivered change on
   value-typed properties, on a path built to allocate nothing.
3. **It cannot see the difference that matters.** A value equal to the current one and a value that a
   later commit will re-deliver are indistinguishable by value, but only the second is safe to drop.

## Why source-originated commits do not advance the marker

This is the single most load-bearing line in the design, and it looks like a special case.

A commit that came from the source is skipped as an echo when that source's queue is drained. If it
counted as superseding, a change could be dropped against a commit that is then never delivered, which
breaks the invariant directly. The failure needs no concurrency: write A, it reaches the source, write
B, then the source's notification for A arrives late and commits locally at a higher revision than B.
B is dropped, the echo is skipped, nothing is sent, and both ends settle on A. The user's write is gone
with no error.

Issue #373 covers the general form: an echo's revision is stamped when we apply it, not when the source
produced it, so it cannot be ranked against local writes at all.

## Why the marker is read before FinalizeOrigin

`FinalizeOrigin` demotes a stamped origin to `Local` when the stored value differs from the sent value,
because the local model computed that value. Correct for publishing, wrong for the marker: the write
still originated at the source. Reading after the demotion made a property carrying a clamp or normalize
hook behave differently from one without, so whether a user's write survived depended on whether that
property happened to have a hook.

## Why Confirmed commits do advance it

They are echo-skipped like any other own-source change, so by the rule above they should not. They are
safe for a different reason: `SourceTransactionWriter` stamps `Confirmed` only after the source write
succeeded, so the source genuinely holds that value and needs nothing sent.

That reasoning lives in another assembly, which makes it fragile in both directions. Excluding
`Confirmed` would let an older local write overwrite a confirmed value; extending `Confirmed` stamping
to a path that has not written the source would silently lose writes. Transaction rollback is exactly
such a path and is tracked separately.

## Why the connect window reverses source-wins

At connect, a parked local write and the initial-state load cannot be ordered against each other, for
the same reason an echo cannot: the load carries the source's state as of the connect and says nothing
about whether it precedes or follows a write made moments earlier. The earlier rule resolved that
ambiguity by discarding the write, silently. It now resolves it the other way, and the source converges
to the local value.

Both directions keep the two ends in sync. What differs is whether a committed write can vanish without
an error.

## What actually guarantees convergence

Not the conflict rule. Two properties of the delivery path:

- The newest local commit is never dropped, since nothing supersedes it, so the source always receives
  the model's settled value.
- The source's notifications carry its own value back, so the model converges to what the source holds.

A source that neither reports values back nor answers reads is outside this, and no local mechanism can
close it. That is #373's territory: it needs a read-after-write fence or an in-band echo fence.

## The property index hysteresis

`ChangeMerger` trims its property index after a burst, but only once the narrow condition has held for
several consecutive flushes. Trimming on the first narrow batch measured as +17% allocation on the
delivery benchmark, because flush widths vary constantly under load and each narrow batch trimmed an
index the next wide one immediately regrew. A single batch cannot distinguish a working set from a burst
artifact; observing over time can.
