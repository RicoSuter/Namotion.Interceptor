using Namotion.Interceptor.ConnectorTester.Configuration;

namespace Namotion.Interceptor.ConnectorTester.Engine.Chaos;

/// <summary>
/// Picks one chaos profile per cycle (round-robin) and toggles each ChaosEngine.Enabled
/// based on the active profile's participant list. Returns the active profile name (or null
/// when no profiles are configured) so VerificationEngine can include it in cycle logs.
/// </summary>
public sealed class ChaosProfileRotator
{
    private readonly List<ChaosProfileConfiguration> _profiles;
    private readonly List<ChaosEngine> _chaosEngines;
    private readonly ILogger _logger;
    private bool _warned;

    public ChaosProfileRotator(
        List<ChaosProfileConfiguration> profiles,
        List<ChaosEngine> chaosEngines,
        ILogger logger)
    {
        _profiles = profiles;
        _chaosEngines = chaosEngines;
        _logger = logger;
    }

    public string? ApplyForCycle(int cycleNumber)
    {
        if (_profiles.Count == 0)
        {
            return null;
        }

        var profile = _profiles[(cycleNumber - 1) % _profiles.Count];

        if (!_warned)
        {
            WarnAboutUnknownParticipants();
            _warned = true;
        }

        foreach (var engine in _chaosEngines)
        {
            engine.Enabled = profile.Participants.Contains(engine.TargetName);
        }

        return profile.Name;
    }

    private void WarnAboutUnknownParticipants()
    {
        foreach (var profile in _profiles)
        {
            foreach (var participant in profile.Participants)
            {
                if (_chaosEngines.All(engine => engine.TargetName != participant))
                {
                    _logger.LogWarning(
                        "Chaos profile '{Profile}' references '{Participant}' which has no chaos engine (no Chaos config or not a known participant). It will be ignored.",
                        profile.Name, participant);
                }
            }
        }
    }
}
