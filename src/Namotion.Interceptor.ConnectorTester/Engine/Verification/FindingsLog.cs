using Namotion.Interceptor.ConnectorTester.Snapshot;

namespace Namotion.Interceptor.ConnectorTester.Engine.Verification;

/// <summary>
/// Writes non-failure observations during passing cycles to findings.log.
/// Two finding types: slow-convergence (>10s with no chaos active) and null-timestamp
/// (one participant has a write timestamp the other doesn't, forgiven by the
/// null-timestamp rule).
/// </summary>
public sealed class FindingsLog
{
    private readonly string _filePath;
    private readonly Func<int> _cycleNumber;
    private readonly Func<bool> _chaosActive;
    private readonly ILogger _logger;

    public FindingsLog(string filePath, Func<int> cycleNumber, Func<bool> chaosActive, ILogger logger)
    {
        _filePath = filePath;
        _cycleNumber = cycleNumber;
        _chaosActive = chaosActive;
        _logger = logger;
    }

    public void AppendIfAny(IReadOnlyList<(string Name, string Snapshot)> snapshots, TimeSpan convergeTime)
    {
        try
        {
            var findings = new List<string>();

            if (convergeTime.TotalSeconds > 10 && !_chaosActive())
            {
                findings.Add($"slow-convergence: {convergeTime.TotalSeconds:F1}s with no chaos active");
            }

            for (var i = 1; i < snapshots.Count; i++)
            {
                var timestampFindings = SnapshotDiffer.CollectFindings(
                    snapshots[0].Name, snapshots[0].Snapshot,
                    snapshots[i].Name, snapshots[i].Snapshot);

                if (timestampFindings is { Count: > 0 })
                {
                    findings.AddRange(timestampFindings.Select(finding => $"null-timestamp: {finding}"));
                }
            }

            if (findings.Count == 0)
            {
                return;
            }

            var cycleNumber = _cycleNumber();
            _logger.LogWarning("Cycle {Cycle}: {Count} finding(s)", cycleNumber, findings.Count);

            var lines = new List<string>
            {
                $"[{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}] Cycle {cycleNumber}: {findings.Count} finding(s)"
            };
            lines.AddRange(findings.Select(finding => $"  {finding}"));
            lines.Add("");
            File.AppendAllLines(_filePath, lines);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to log findings");
        }
    }
}
