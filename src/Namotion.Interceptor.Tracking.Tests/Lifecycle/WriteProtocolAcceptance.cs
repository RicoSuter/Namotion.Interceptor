namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Shared deadlines for the write protocol acceptance repros, and the register of what each repro
/// pins and what would quietly stop it pinning anything.
/// </summary>
/// <remarks>
/// The register covers eleven defect classes. Nine were reproduced concurrency defects, a tenth is
/// a documented contract boundary and an eleventh is a deferred best-effort gap. Each entry names
/// the file the pin lives in, the phase its instrument depends on, and what would turn it green
/// without the defect being fixed. An entry that says a repro fails today is a work item, not a
/// broken test: the assertion is the specification and the failure is the report.
///
/// Two repros in the campaign that produced these tests silently lost their instrument, and only
/// this register caught it. The same thing happened again in the port onto this branch, twice, and
/// both are recorded below: <c>SubstitutingDevice</c> can no longer substitute, and the park it
/// offers moved into the phase that was measured and rejected. Read the "instrument" sentence of an
/// entry before trusting that its test still measures anything.
///
/// ---
///
/// Defect 1, cross-context topology gate. <c>CrossContextGateDeadlockTests</c>, both halves. Two
/// lifecycle callbacks writing into each other's contexts acquire two topology gates in opposite
/// order, because the gate is entered before the write chain resolves and the callback guard lives
/// inside the chain; the downstream-interceptor half is the same acquisition order with no callback
/// involved, so no reentrancy guard is consulted at all. The first depends on attach callbacks
/// running under the gate, the second on the gate being entered around the whole write chain rather
/// than inside the lifecycle. If gate entry moved inside the lifecycle, an interceptor outside it
/// would hold nothing and both writes would simply complete; the asserted rejection is what catches
/// that, and chain position alone does not. Both halves assert the rejection and not only that both
/// threads finished, so a world where callbacks stop holding the gate fails on the missing
/// rejection. The exception type is pinned with <c>IsType</c> rather than <c>IsAssignableFrom</c>:
/// this branch had loosened both to <c>InvalidOperationException</c>, which also admits the bare
/// <c>InvalidOperationException</c> raised when a target context has no logical-context guard, and
/// that is a different rejection reaching the same assertion. The tight type was restored here
/// because the path does raise <c>LifecycleContractViolationException</c>; a design that rejects
/// with a different type makes this a one-line update rather than a finding. The sibling test that
/// drives a context with no real lifecycle keeps <c>IsAssignableFrom</c>, which is correct for it.
/// Instrument: the rendezvous counters, which fail the test if the two threads never met.
///
/// Defect 2, admission that releases its own subject. <c>Acceptance.AdmissionReleaseResidueAcceptanceTests</c>.
/// A property admission whose captured collection detaches the admitting subject mid-commit must
/// leave no snapshot entry behind, because the enumeration of a captured value is depth-zero user
/// code holding the topology gate reentrantly and can run the whole write protocol against the
/// subject being admitted. Depends on the admission enumerating the captured value at all. The
/// original repro fired its release from a second enumeration; this branch enumerates a captured
/// value exactly once, which its own <c>AddPropertiesLifecycleTests</c> asserts, so the release
/// fires from the only enumeration there is and the instrument is an enumeration-count guard rather
/// than an ordinal. Passes today. It goes green for the wrong reason if the admission stops
/// enumerating captured values inside the gate, so the guard asserts the collection was enumerated
/// at least once. The original repro's graph assertions were portable and were not ported when this
/// branch deleted it: nothing else asserts that no snapshot entry survives for a released subject.
///
/// Defect 3, reentrant structural write. <c>Acceptance.ReentrantStructuralWriteAcceptanceTests</c>,
/// same-property half. A structural write re-entered from user code the write itself invoked must
/// not leave the graph holding a subject the committed field no longer names. The re-entry point
/// moved on this branch: a committed value is never re-enumerated, which
/// <c>ReentrantStructuralWriteTests.WhenACommittedEnumerableIsReplaced_ThenItIsNotEnumeratedAgain</c>
/// asserts, so the original phase predicate (re-enter once the field no longer holds the committed
/// value) never fires and its test would fail on a window that does not exist rather than on a
/// defect. The instrument was moved to the capture of the incoming value, which is the only user
/// code left inside a structural write, and the assertion is stated as agreement between attachment
/// and the committed field rather than as one specific winner, because which value wins is a
/// property of where the re-entry lands. Passes today. It stops pinning anything if capture stops
/// invoking user code, so the guard asserts the re-entry actually ran and reports the enumeration
/// count when it did not.
///
/// Defect 3b, attach seeding re-entered. Same file, attach half. FAILS today. An explicit attach
/// whose seeding pass is re-entered by the enumerable it is seeding aborts the whole attach with
/// <c>LifecycleConflictException</c> and leaves the root, the seeded child and the late child all
/// unattached. The nested write itself raises nothing, so a caller cannot tell which of its two
/// operations was refused. This branch pins the abort as intended behaviour in
/// <c>ReentrantStructuralWriteTests.WhenAUserEnumerableWritesTheRootDuringCapture_ThenTheChangedSnapshotIsRejected</c>;
/// the two tests state opposite contracts on purpose, and the disagreement is the point. Depends on
/// seeding re-reading the property getter rather than reusing the discovery snapshot.
///
/// Defect 4, dictionary rekey. <c>DictionaryOccurrenceTests</c>. A dictionary rekey must move the
/// occurrence rather than release and rebuild the subject, because a removal pass that runs to
/// completion before the addition pass drops the subject's last support and lets another context
/// claim it in between. Carried on this branch unchanged from the campaign that wrote it, and kept
/// as the stronger copy because there is no other. Instrument: the attach and detach recorders,
/// which are what say the subject never left the graph rather than left and returned. Passes today.
/// It goes green for the wrong reason if the recorders stop being wired before the rename.
///
/// Defect 5, verdict on a mid-publication subject.
/// <c>Acceptance.MidPublicationVerdictAcceptanceTests</c>. FAILS today, all three. A derived getter
/// that exposes a subject this context does not own is reported, but a verdict reached while
/// another transaction is publishing must be withheld and retried once that transaction ends,
/// because a structural write between its terminal store and its reconcile leaves a subject legally
/// in a committed property while it is attached to nothing, and no number of retries converges that
/// away. This branch convicts on first sight instead: one evaluation, no retry, and the innocent
/// caller that ran into the race is failed. The no-transaction case pins that conviction comes out
/// of the bounded retry loop; the other two hold the gate open from an attach and from a structural
/// write respectively, so the pin does not depend on one entry point into the gate. Both park inside
/// the discovery scan, which is user code running under the topology gate, so the gate really is
/// held for the whole recalculation; each asserts it reached the park. The branch documents the
/// withholding contract in docs/design/tracking-lifecycle.md, so these three are the difference
/// between the documented contract and the implemented one.
///
/// Defect 5, race half. <c>NormalizingSetterDerivedRaceTests</c>. The instrument was lost on this
/// branch and this entry is the record of it. The defect is a derived recalculation convicting a
/// subject that a normalizing setter had stored before the reconcile attached it. The park must land
/// in the authoritative getter the lifecycle rereads between its own next and its reconcile, which
/// the lifecycle invokes itself, so no interceptor ordering can move it. Parking in the stored
/// setter was measured and rejected: that delegate runs under the terminal lock the reading thread
/// also takes, so the reader blocks instead of racing. On this branch <c>SubstitutingDevice</c> was
/// rewritten to be faithful, dropping both the substitution and the authoritative-getter hook, and
/// its remaining hook parks inside the raw store, which is the place that was measured and rejected.
/// The guard that asserted the backing field held the substituted subject when the park ran was
/// dropped with it, so a park landing outside the window now passes rather than fails. The premise
/// is gone too: a faithful setter stores the value the write already claimed, which is a strictly
/// easier window than the one the defect was about.
/// <c>WhenDirectAliasObservesTwoConsecutiveReservedValues_ThenItConvergesToTheLatest</c> is flaky,
/// failing at its rendezvous deadline in roughly half of isolated runs; a long-deadline variant
/// shows the second writer never starts because the projection is evaluated once and never
/// re-evaluated, so the flake is a missing re-evaluation and not a slow machine.
///
/// Defect 6, attach residue. <c>AttachResidueTests</c>, root and sibling halves. A rejected explicit
/// attach left residue behind. The root half asserts each kind of state the attach would have
/// written separately, so a partial rollback reports which part leaked; it depends on discovery
/// invoking user code before the root is claimed, and on seeding re-reading the property getter
/// rather than reusing the discovery snapshot, and its guard asserts the root is still unattached
/// when the scan parks. The sibling half is the residue a claim-only rollback cannot reach: a
/// subject the seed published before it threw was never in the claimed set, because the concurrent
/// write installed it after the scan. Both children arrive in one property value, so the seed
/// attaches them in that list's order rather than in whatever order the subject enumerates its
/// properties, which is what makes the published-then-rejected sequence deterministic. Both halves
/// are carried on this branch assertion for assertion and are kept as the stronger copy. Pass today.
///
/// Defect 6, rollback half. <c>Acceptance.AttachResidueAcceptanceTests</c>. FAILS today. A handler
/// that refuses the child's attach makes the seed throw, and one that then refuses the child's
/// detach makes the cleanup throw on top of it. The reason the attach failed must survive the
/// cleanup that ran after it, and the root must be left in a state the caller can still detach: a
/// leak the caller can clean up is strictly better than one it cannot. Deterministic and
/// single-threaded, because it does not need the discovery race. On this branch the first three
/// assertions hold, so the attach exception is not masked, but the explicit detach that is the
/// caller's remaining way out throws the refusing handler's own exception instead of completing, so
/// the residue is unreachable. This branch asserts the opposite contract in
/// <c>AttachResidueTests.WhenAttachAndDetachCallbacksThrow_ThenEachPreparedPublicationStillCommits</c>,
/// which is a deliberate inversion; the loss it does not replace is that nothing else asserts a
/// failing cleanup callback cannot mask the exception explaining why the attach was refused.
///
/// Defect 7, unfaithful terminal store. <c>Acceptance.TerminalStoreValidationAcceptanceTests</c>.
/// FAILS today, four of five. A structural write must validate the value the terminal actually
/// stored, not only the value proposed, so a rewriting setter cannot install a subject the write
/// never saw. Claiming the stored value is what puts the foreign-subject refusal ahead of every
/// graph mutation. On this branch the enforcement is a shape check rather than a validation: a
/// structural property that supplies no raw reader is refused outright, and one that supplies a raw
/// reader is trusted to store faithfully and never checked. Every repro here therefore goes through
/// the trusted shape, which is the shape every generated subject uses and the only one a real
/// consumer reaches; a repro written against the untrusted shape would be refused before the
/// terminal ran and would pin the shape check instead of the validation, which is the most likely
/// way this class goes green without being fixed. The foreign-subject repro is the serious one: no
/// exception, the graph commits an edge to the proposed subject and attaches it, and the field
/// exposes a foreign subject the graph knows nothing about. The reordering repro is the parity
/// guard, the only case whose stored value has to pass, so it is what stops an over-aggressive fix;
/// its stored list is a different instance from the proposed one, so a reference-equality short
/// circuit cannot hide the rewrite, and today it leaves the dropped subject attached with a phantom
/// edge. The stamped repro is the equality hole: a value type whose equality answers about a version
/// stamp must never be allowed to decide whether the stored value still needs claiming. The
/// immutable-array repro is the value-typed second claim and passes. The hand-written devices in
/// <c>Acceptance.AcceptanceSubjects</c> exist because this branch's <c>ReorderingDevice</c>,
/// <c>StoredValueClaimDevice</c> and <c>SubstitutingDevice</c> can no longer express the defect: the
/// first two reach the untrusted shape and are refused before their terminal runs, and the third was
/// rewritten to be faithful. All three are unreferenced by any test on this branch.
///
/// Defect 8, attachment publication atomicity. <c>AttachmentStateCoherenceTests</c>. The attached
/// context and the anchor must not be published as two stores, or a lock-free reader observes an
/// anchor with no context. Carried unchanged and kept as the stronger copy. Instruments: the
/// transition and read counters, both asserted non-zero, plus a revision bracket that discards
/// observations that were not torn, so a hammer that never raced fails rather than passes. Passes
/// today.
///
/// Defect 9, late lifecycle registration. <c>LateLifecycleRegistrationTests</c>. Registering a
/// lifecycle on a context that already has a subject attached leaves that subject attached but
/// unowned, which is a silent permanent corruption, so it is rejected at registration. Carried
/// unchanged and kept as the stronger copy. Instruments: the message pin and the negative controls
/// asserting the rejected registration really did not land, plus a positive control that the
/// registration still works when nothing is attached. Passes today.
///
/// Defect 10, the terminal-store field boundary.
/// <c>Acceptance.TerminalStoreFieldBoundaryAcceptanceTests</c>. FAILS today, on the graph half only.
/// A known contract boundary rather than a repro: when a raw writer stores something the write never
/// proposed, the framework cannot restore the backing field, because the only store it has is the
/// terminal the subject handed it and a terminal that ignores its argument re-stores the same value
/// when replayed. What it can and must do is leave the graph untouched. The assertion on the field
/// is deliberately in the negative, so a change that starts restoring it fails: the contract would
/// have moved and that should be seen rather than absorbed. On this branch the field half holds and
/// the graph half does not, so the unrestorable field is no longer the boundary. The boundary is
/// stated for consumers in the WriteProperty remarks and in docs/design/tracking-lifecycle.md, which
/// still claims the graph is left untouched, so this entry is the test-side reminder that the
/// documentation and the code disagree.
///
/// Defect 11, the deferred best-effort conviction.
/// <c>Acceptance.DeferredConvictionAcceptanceTests</c>. FAILS today, one of two. A conviction
/// reached while draining a withheld recalculation is traced rather than thrown, because the drain
/// runs from a thread that was only ending an unrelated transaction, and failing that thread for a
/// bug in someone else's derived getter is worse than reporting it. The verdict is then raised
/// against a caller on the next evaluation, if one occurs; nothing schedules that evaluation, so the
/// second half is best effort and not a guarantee, and closing it means making the conviction sticky
/// and raising from a read on a hot path. The half that must hold regardless, that the parked
/// unrelated thread is never failed, passes. The half that says withholding is not dropping fails on
/// its guard rather than on its verdict: nothing is ever withheld on this branch, so there is no
/// deferral to prove was not lost. That guard failing rather than the verdict is the signal that the
/// phase this instrument depends on is absent, not that the outcome is wrong. The campaign's
/// stronger version of this test constructed an outstanding booking directly through
/// <c>DerivedPropertyData.HasWithheldRecalculation</c>, which does not exist here, so the property
/// is asserted through observable behaviour instead and is weaker for it.
/// </remarks>
internal static class WriteProtocolAcceptance
{
    /// <summary>How long a thread waits to meet another thread at a handoff before giving up.</summary>
    public static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long a bounded join waits before a stuck thread is reported as a failure.</summary>
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(20);
}
