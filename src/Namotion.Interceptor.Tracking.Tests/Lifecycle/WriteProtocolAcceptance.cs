namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Shared deadlines for the write protocol acceptance repros, and the register of what each repro
/// pins and what would quietly stop it pinning anything.
/// </summary>
/// <remarks>
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
/// NormalizingSetterDerivedRaceTests. A derived recalculation on another thread convicted a subject
/// that a normalizing setter had stored before the reconcile attached it. Parks in the authoritative
/// getter the lifecycle rereads between its own next and its reconcile, which the lifecycle invokes
/// itself, so no interceptor ordering can move it. Parking in the stored setter was measured and
/// rejected: that delegate runs under the terminal lock the reading thread also takes, so the reader
/// blocks instead of racing. The guard asserts the backing field held the substituted subject when
/// the park ran, so a park landing outside the window fails the test rather than passing it. Its
/// non-intercepted twin is the one that pins the mechanism: reading through a plain accessor records
/// no dependency, so no recalculation cascade can reach the probe and the value comes back only
/// through the booking the withholding recalculation made with the lifecycle. Both assert that the
/// re-evaluation happened rather than only that the final read looks right, because reading a
/// derived property re-invokes its getter and would answer correctly either way.
/// ConcurrentPublicationVerdictTests holds the other side, that a deferral is not an acquittal.
/// </remarks>
internal static class WriteProtocolAcceptance
{
    /// <summary>How long a thread waits to meet another thread at a handoff before giving up.</summary>
    public static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long a bounded join waits before a stuck thread is reported as a failure.</summary>
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(20);
}
