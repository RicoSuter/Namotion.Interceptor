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
}
