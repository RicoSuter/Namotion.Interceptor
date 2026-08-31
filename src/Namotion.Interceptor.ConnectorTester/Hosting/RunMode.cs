namespace Namotion.Interceptor.ConnectorTester.Hosting;

public enum RunMode
{
    Verify,
    Participant
}

public sealed record RunModeSelection(RunMode Mode, string? ParticipantName);
