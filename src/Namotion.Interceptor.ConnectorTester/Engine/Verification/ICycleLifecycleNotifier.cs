namespace Namotion.Interceptor.ConnectorTester.Engine.Verification;

/// <summary>
/// Receives notifications when a verification cycle starts and finishes.
/// Implemented by CycleLoggerProvider so the cycle log file is opened/closed/renamed
/// per cycle. Decouples VerificationEngine from the file-rotation logic.
/// </summary>
public interface ICycleLifecycleNotifier
{
    void StartCycle(int cycleNumber);
    void FinishCycle(int cycleNumber, CycleResult result);
}
