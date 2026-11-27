namespace Namotion.Interceptor.ConnectorTester.Engine.Verification;

/// <summary>
/// The write-durability check of one participant after a converged cycle.
/// </summary>
/// <param name="Participant">The participant name.</param>
/// <param name="Violations">One line per recorded write the participant's model no longer holds.</param>
/// <param name="DroppedWriteCount">The number of writes the participant's source dropped in the cycle.</param>
public sealed record WriteDurabilityResult(string Participant, IReadOnlyList<string> Violations, long DroppedWriteCount)
{
    /// <summary>
    /// Gets whether the violations may be writes the source dropped, which the write contract allows when its retry
    /// queue is full, so they neither fail nor pass the cycle.
    /// </summary>
    public bool IsInconclusive => Violations.Count > 0 && DroppedWriteCount != 0;

    /// <summary>Gets whether the violations fail the cycle.</summary>
    public bool IsFailure => Violations.Count > 0 && DroppedWriteCount == 0;
}
