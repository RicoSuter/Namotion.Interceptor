namespace Namotion.Interceptor.ConnectorTester.Engine.Verification;

/// <summary>
/// Records per-cycle bookend events. Implemented by CycleLoggerProvider, which opens a
/// pending cycle log file on <see cref="StartCycle"/> and flushes / renames / trims it
/// on <see cref="FinishCycle"/>. Decouples VerificationEngine from the file-rotation logic.
/// </summary>
public interface ICycleRecorder
{
    void StartCycle(int cycleNumber);
    void FinishCycle(int cycleNumber, CycleResult result);

    /// <summary>
    /// Returns the structural hash mismatch warnings logged during the current cycle, or an
    /// empty list if none were recorded. A mismatch means the connector detected a structural
    /// pipeline divergence and auto-healed it via reconnection, so final-state convergence alone
    /// would mask the underlying bug. VerificationEngine uses this to fail an otherwise converged
    /// cycle.
    /// </summary>
    IReadOnlyList<string> GetHashMismatchWarnings();
}
