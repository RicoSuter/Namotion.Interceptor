using Namotion.Interceptor.ConnectorTester.Configuration;

namespace Namotion.Interceptor.ConnectorTester.Hosting;

public static class RunModeSelector
{
    public static RunModeSelection Select(string[] args, ConnectorTesterConfiguration configuration)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--participant" && i + 1 < args.Length)
            {
                var participantName = args[i + 1];
                ValidateParticipantName(participantName, configuration);
                return new RunModeSelection(RunMode.Participant, participantName);
            }
        }

        return new RunModeSelection(RunMode.Verify, ParticipantName: null);
    }

    private static void ValidateParticipantName(string participantName, ConnectorTesterConfiguration configuration)
    {
        var knownNames = new List<string> { configuration.Server.Name };
        knownNames.AddRange(configuration.Clients.Select(client => client.Name));

        if (!knownNames.Contains(participantName, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Unknown participant '{participantName}'. Available participants: {string.Join(", ", knownNames)}.");
        }
    }
}
