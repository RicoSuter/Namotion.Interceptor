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
/// </remarks>
internal static class WriteProtocolAcceptance
{
    /// <summary>How long a thread waits to meet another thread at a handoff before giving up.</summary>
    public static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long a bounded join waits before a stuck thread is reported as a failure.</summary>
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(20);
}
