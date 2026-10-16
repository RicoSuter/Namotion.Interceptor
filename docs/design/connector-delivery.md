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

## Why a server ranks against a different marker

The argument above rests on the source having produced its value before it saw our write. That holds for
a connector talking to something remote. It does not hold for a server, where a client's write is the
thing being applied, so a commit that predates it is genuinely older.

Ranking against the non-source marker there fails the same invariant from the other side. A local commit
that predates the client's write and reaches the write loop late is not superseded, so it is written out
after the client's value, leaving the clients on our older value while the subject holds theirs.

So which commits may supersede is a property of the sink, not of the change: `ChangeDeliveryRule`,
chosen at four construction sites: the three servers, and once in `SubjectSourceBase` for every client
source. The OPC UA server repeats the decision inside the node
manager lock, because a client write takes that same lock and can land between the batch being accepted
and written.

**Check the precondition, not the metaphor.** `SourceValuesAreSettled` is sound only if every commit the processor
skips as its own echo has already reached the destination when it is applied. The three servers satisfy
that differently, and the difference is load-bearing:

- **OPC UA** applies with `SetValueFromSource(this, ...)`, so the apply *is* echo-skipped. It is sound
  because the SDK wrote the node before `StateChanged` fired, so the value is already there. Without
  `SourceValuesAreSettled` the two stores diverge permanently, which is the failure this rule was added for.
- **MQTT and WebSocket** apply under a foreign source, `_mqttClientSource` and the originating connection,
  so nothing is echo-skipped and the precondition holds vacuously. Their failure without `SourceValuesAreSettled` is
  milder and different: within one flush the merger already picks by revision, so it only bites when the
  client's value and the straggler land in different flushes.

Unifying those conventions on `SetValueFromSource(this, ...)` would look like a tidy-up and would make
`SourceValuesAreSettled` unsound for MQTT immediately, because the broker does not distribute a client's
message itself: it handles the message and sets `ProcessPublish` to false, so the value reaches the other
clients only when the server relays it in order. Same for WebSocket, which has no store at all.

The OPC UA case was invisible until #425. Before it, the server applied its own node writes back to the
subject, which converged the two stores by accident while corrupting them in other ways.

## Why the batch survivor spans the batch

The survivor's old value comes from the lowest revision in the batch and its new value from the highest, rather than from whichever change happened to arrive first and last. Enqueuing happens after the commit and outside the subject lock, so a writer preempted between the two can present an older commit after a newer one. Under concurrent writers that inversion is real rather than theoretical, and taking the first and last arrivals would produce a survivor whose old value postdates its new one.

Everything else on the survivor, its `Revision`, `Origin` and both timestamps, comes from the highest-revision change, so a handler keying off `Origin.Source` sees the newest commit's origin rather than a mixture. Under the arrival-position fallback below the origin and timestamps come from the last arrival instead, and the survivor carries no revision at all.

## Why revision 0 is delivered rather than dropped

A change carrying revision 0 orders against nothing, so staleness is unprovable and it is delivered; a property with one in its batch collapses by arrival position instead, which is what a source saw before revisions existed. A redundant write costs one message, a wrong drop is permanent, so the guard errs toward delivering.

The survivor of such a batch is emitted carrying no revision too. It was chosen by arrival rather than by revision, so ranking it against the property marker could drop it while the higher-revision change whose value it carries has already been merged away in the same batch, leaving nothing to re-deliver.

No committed change arrives carrying revision 0: every published change comes from a write terminal and carries a revision, including derived recomputations. The batch collapses manufacture it deliberately, though, through `WithoutRevision`, which is the case the paragraph above describes, so this is a live path rather than a guard against something that cannot happen.

## Why the written-out mark is sticky and not per source

The mark that says a connector has written a property out never clears, and lives in the subject's property data rather than per source.

It cannot clear on an inbound event: nothing observable on this side proves that an earlier write of ours did not land on the source after a transaction's direct write, so clearing would be a bet against an ordering the client cannot see, and losing it silently strands a committed transaction value.

It is not per source because it decides only whether a confirmation is written back, and a confirmation carries the current value. The worst a foreign connector's mark can cost is one redundant write of the value the source is owed anyway. That is what lets it be a bare flag with no source reference to release. A property written only through transactions never sets it.

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

## Why every load resynchronizes, and why the processor run is ended to do it

The reconcile used to run once per connection attempt of the base retry loop, before the connected processor started. Connector-internal reconnects (MQTT and WebSocket monitors, the OPC UA session manager) reload initial state while that processor keeps running, so a write parked during the outage stayed parked until some other owned write flushed it, unjudged, over a model that had meanwhile been replaced by the load (#362).

The reconcile cannot simply be called from the connector's reload: it restores values with `SetValue`, which re-enters the subscription the processor is draining, and it drains the retry queue the processor's flush competes for. Both need the processor to be out of the picture, and the processor's own buffer is the hard part: at the default 8 ms buffer time a write committed just before the load completes sits in that buffer, and a reconcile that runs beside the processor never sees it. A time-based barrier (wait a little, then reconcile) was tried and rejected: no fixed delay covers a transport write that stalls.

So the processor run is ended on every completed load, and that end is the capture barrier. The property writer exposes the generation of its latest `StartBuffering` and reports every completed, non-superseded load to the source. While that buffering generation is newer than the one the pump last resynchronized after, the processor's write handler parks into the retry queue instead of sending, its final flush included, and its completion handler skips the retry flush. The completed load cancels the running processor; its final flush parks whatever it still buffered; the pump then drains the subscription, reconciles, marks the generation, and starts the next processor. Everything the processor or the drain has seen before the reconcile is judged by it or a later one. Two windows remain, both resting on the source echoing its writes and both pre-existing in kind, since every write during a reload used to be sent unjudged: a send already in flight when the reload's `StartBuffering` lands can reach the source after the load's snapshot, and a write whose commit is visible before its change reaches the subscription (the enqueue happens after the terminal releases the subject lock) can miss the reconcile. They are listed under the unobservable-source limitation in [connectors.md](../connectors.md#known-limitations) and are not closed here.

Only `StartBuffering` opens the window, not `ReportConnectionLost`. The latter invalidates an in-flight load but promises no reload: an OPC UA keep-alive failure whose reconnect fails while the session stays usable is followed by nothing, and parking behind it would hold writes until some later reload. A write attempted in that state fails or succeeds as before, and a failure parks it for the next reconcile. The flip side is a contract on connectors: every `StartBuffering` has to be followed by a load or another `StartBuffering`.

Three consequences shape the rest. The pump has to be the subscription's only consumer from the first completed load on, so the per-attempt drain of the base loop runs only before it exists, which keeps a first connect that keeps failing bounded; it starts after the load and not after the listen because the WebSocket client claims ownership inside the load's apply, and a processor that dequeues a write before the claim discards it. The price is that each base-loop connection attempt's load joins its listen as an uncounted leg: until that load completes, writes wait in the unbounded subscription instead of being parked on a timer as before. A source with the retry queue disabled cannot park at all, so for it a completed load does not interrupt the processor and writes during a reload are sent as they are written, as before. A live send is cancelled by the stop alone, not by the end of a run, and the processor's five-second teardown bound counts from the stop rather than from the end of a run, so a run that ends for a load waits for its single in-flight write however long the transport takes: cancelling it would make the reconcile resend what the source already accepted, dropping it after five seconds would lose a committed write, and letting the reconcile proceed while it is in flight would reorder the property. The reconcile itself gets the same bound once the stop is requested, so a transport write that ignores cancellation cannot hold the stop forever; the drain has run by then, so abandoning it leaves the subscription with no second consumer, and retiring the queue settles the late write.

A load superseded by a later `StartBuffering` before its reconcile ran is deferred to the later load. Reconciling against a model that the next load is about to replace would send writes the next reload then overwrites locally; without an echo from the source the two ends would stay apart. Parked writes therefore wait for the generation that settles.

The retry queue collapses per property when it overflows, and only then. The pending window spans the whole outage, since connectors call `StartBuffering` when they detect the loss, so a property written at a modest rate for the length of an outage would otherwise take one slot per flush and evict the parked writes of every other property before any reconcile saw them. Collapsing on every enqueue was tried and rejected: it scanned the queue per parked change under the lock, and a collapsed write kept its earliest slot, so the property written most was the first evicted and lost its newest commit. On overflow each survivor takes the slot of its latest change, so the eviction that follows drops the properties written longest ago and every survivor carries its property's latest intent.

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
