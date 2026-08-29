namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Shared deadlines for the write protocol acceptance repros, and the register of what each repro
/// pins and what would quietly stop it pinning anything.
/// </summary>
/// <remarks>
/// Every repro listed here fails on the current branch by design: the failure is the deliverable.
/// A repro that starts passing has either been fixed or has lost its instrument, and telling those
/// two apart is what this register is for. Each entry names the mechanism, the position its
/// instrument depends on, and what would turn it green without the defect being fixed.
///
/// CrossContextGateDeadlockTests, attach-callback half. Two lifecycle callbacks writing into each
/// other's contexts acquire two topology gates in opposite order, because the gate is entered before
/// the write chain resolves and the callback guard lives inside the chain. Depends on attach
/// callbacks running under the gate. It asserts the rejection and not only that both threads
/// finished, so a world where callbacks stop holding the gate fails on the missing rejection.
///
/// CrossContextGateDeadlockTests, downstream-interceptor half. The same acquisition order with no
/// callback involved, so no reentrancy guard is consulted at all. Depends on the gate being entered
/// around the whole write chain rather than inside the lifecycle. If gate entry moved inside the
/// lifecycle, an interceptor outside it would hold nothing and both writes would simply complete;
/// the asserted rejection is what catches that, and chain position alone does not.
///
/// TerminalStoreContractTests. Only the proposed value is claimed before the terminal runs, so a
/// foreign-context subject stored by a normalizing terminal is rejected after the backing field
/// already changed. Asserts a contract, that the write throws and the field is unchanged, rather
/// than a mechanism, so it stays valid wherever the rejection ends up being made. Lowest maintenance
/// risk here.
///
/// ReconcileRetentionWindowTests. A subject the new value still holds under a different key loses
/// its only support in the removal pass before the addition pass re-secures it. Depends on the keyed
/// removal pass walking occurrences in reverse, so the retained subject is released before the
/// parking one. A change to that order trips an explicit precondition instead of passing.
///
/// ReentrantStructuralWriteTests. A nested write from inside a scanned user value commits a baseline
/// the outer write then overwrites. Depends on the reconcile scanning the committed baseline after
/// the terminal stored and before the new baseline is committed. The re-entry is triggered by a
/// condition naming that phase rather than by a scan ordinal, and both halves of the window are
/// asserted, so caching baseline occurrences removes the window and trips those assertions rather
/// than silently disarming the test.
///
/// AttachResidueTests. A rejected explicit attach leaves the root claim and the explicit anchor
/// published. Depends on discovery invoking user code before the root is claimed, and on seeding
/// re-reading the property getter rather than reusing the discovery snapshot. Either change trips a
/// precondition.
///
/// DetachCallbackAdmissionTests. Property admission from a detach callback publishes an edge from an
/// owner the release already removed. Depends on the departing subject being scalar-only, which is
/// asserted, because a structural property on that model would divert the admission into its early
/// return and leave every other assertion satisfied.
///
/// AttachmentStateCoherenceTests. The attachment context and the anchor are published as two stores,
/// so a lock-free reader can observe a non-None anchor with no context. The only probabilistic repro
/// in the suite: it hammers until it observes the window or a deadline expires, so a change that
/// narrows the window without closing it would eventually read as a pass. The margin is currently
/// large, reproducing within a couple of transitions.
///
/// NormalizingSetterDerivedRaceTests. A derived recalculation on another thread convicts a subject
/// that a normalizing setter stored before the reconcile attached it. Parks in the authoritative
/// getter the lifecycle rereads between its own next and its reconcile, which the lifecycle invokes
/// itself, so no interceptor ordering can move it. Parking in the stored setter was measured and
/// rejected: that delegate runs under the terminal lock the reading thread also takes, so the reader
/// blocks instead of racing.
///
/// OwnershipChangeStreamTests is not a repro and passes today. It pins the ordered change stream for
/// the shapes the committed-edge validation governs. A failure there means the published order moved
/// and the new order needs reviewing, not that a defect appeared.
///
/// Three ways a repro here goes green without a fix, in the order they have actually happened.
/// First, instrument relocation: the park or interceptor sits where the design may stop running user
/// code, or may run it somewhere else. The answer is to trigger on a condition that names the phase
/// and to assert that phase, never on an ordinal or a chain position. Second, model-shape
/// preconditions: the repro reaches its arm only because a test model has, or lacks, a structural
/// property, so editing a model breaks the repro with no design change at all. The answer is to
/// assert the precondition. Third, probabilistic margin, which applies to one test only and is
/// recorded above.
/// </remarks>
internal static class WriteProtocolAcceptance
{
    /// <summary>How long a thread waits to meet another thread at a handoff before giving up.</summary>
    public static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long a bounded join waits before a stuck thread is reported as a failure.</summary>
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(20);
}
