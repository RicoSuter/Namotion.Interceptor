using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text.Json;
using System.Text.Json.Nodes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.WebSocket.Client;
using Namotion.Interceptor.WebSocket.Server;
using Namotion.Interceptor.ConnectorTester.Logging;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Top-level orchestrator. Runs repeating mutate/converge cycles.
/// On convergence failure, exits the process with non-zero exit code.
/// </summary>
public class VerificationEngine : BackgroundService
{
    private static readonly TimeSpan SnapshotPollInterval = TimeSpan.FromSeconds(5);
    private const string MemoryLogPath = "logs/memory.log";

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly ConnectorTesterConfiguration _configuration;
    private readonly TestCycleCoordinator _coordinator;
    private readonly Dictionary<string, TestNode> _participants;
    private readonly List<MutationEngine> _mutationEngines;
    private readonly List<ChaosEngine> _chaosEngines;
    private readonly CycleLoggerProvider? _cycleLoggerProvider;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;

    private int _cycleNumber;
    private bool _failed;

    /// <summary>
    /// Whether the last cycle failed to converge. Used to set the process exit code.
    /// </summary>
    public bool Failed => _failed;

    public VerificationEngine(
        ConnectorTesterConfiguration configuration,
        TestCycleCoordinator coordinator,
        Dictionary<string, TestNode> participants,
        List<MutationEngine> mutationEngines,
        List<ChaosEngine> chaosEngines,
        CycleLoggerProvider? cycleLoggerProvider,
        IHostApplicationLifetime applicationLifetime,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        _configuration = configuration;
        _coordinator = coordinator;
        _participants = participants;
        _mutationEngines = mutationEngines;
        _chaosEngines = chaosEngines;
        _cycleLoggerProvider = cycleLoggerProvider;
        _applicationLifetime = applicationLifetime;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reset memory log for this run
        Directory.CreateDirectory(Path.GetDirectoryName(MemoryLogPath)!);
        if (File.Exists(MemoryLogPath))
        {
            File.Delete(MemoryLogPath);
        }

        // Brief startup delay to let connectors initialize
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        // Log configuration header
        _logger.LogInformation("""
            === Connector Tester Configuration ===
            Connector: {Connector}
            MutatePhaseDuration: {MutatePhaseDuration}
            ConvergenceTimeout: {ConvergenceTimeout}
            Participants: {Participants}
            """,
            _configuration.Connector,
            _configuration.MutatePhaseDuration,
            _configuration.ConvergenceTimeout,
            string.Join(", ", _participants.Select(p => p.Key)));
        foreach (var engine in _mutationEngines)
        {
            _logger.LogInformation("  {Name}: {Rate} value mutations/sec, {StructuralRate} structural mutations/sec",
                engine.Name, engine.ValueMutationRate, engine.StructuralMutationRate);
        }

        if (_configuration.ChaosProfiles.Count > 0)
        {
            _logger.LogInformation("  Chaos profiles ({Count}): {Profiles}",
                _configuration.ChaosProfiles.Count,
                string.Join(" -> ", _configuration.ChaosProfiles.Select(p => p.Name)));
        }

        // Validate chaos profile participant names
        foreach (var profile in _configuration.ChaosProfiles)
        {
            foreach (var participant in profile.Participants)
            {
                if (_chaosEngines.All(e => e.TargetName != participant))
                {
                    _logger.LogWarning(
                        "Chaos profile '{Profile}' references '{Participant}' which has no chaos engine (no Chaos config or not a known participant). It will be ignored.",
                        profile.Name, participant);
                }
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            _cycleNumber++;

            // Reset counters
            foreach (var engine in _mutationEngines)
                engine.ResetCounters();
            foreach (var engine in _chaosEngines)
                engine.ResetCounters();

            var activeProfileName = ApplyChaosProfile();

            _cycleLoggerProvider?.StartNewCycle(_cycleNumber);

            var cycleStopwatch = Stopwatch.StartNew();

            // 1. Mutate phase
            _coordinator.Resume();
            var profileLabel = activeProfileName != null ? $" [profile: {activeProfileName}]" : "";
            _logger.LogInformation("=== Cycle {Cycle}: Mutate phase started ({Duration}){Profile} ===",
                _cycleNumber, _configuration.MutatePhaseDuration, profileLabel);

            await Task.Delay(_configuration.MutatePhaseDuration, stoppingToken);

            // 2. Transition to converge
            _coordinator.Pause();
            _logger.LogInformation("=== Cycle {Cycle}: Converge phase started ===", _cycleNumber);

            // Recover all active chaos disruptions
            foreach (var chaosEngine in _chaosEngines)
            {
                await chaosEngine.RecoverActiveDisruptionAsync(stoppingToken);
            }

            // Grace period for server startup, port binding, and client reconnection.
            // OPC UA needs ~15-20s: server restart + port bind, client keep-alive
            // detection (up to 5s), reconnect handler (5s), session + subscription setup.
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

            // 3. Poll-compare snapshots
            var convergeStopwatch = Stopwatch.StartNew();
            var converged = false;
            var maxPolls = (int)(_configuration.ConvergenceTimeout / SnapshotPollInterval);

            for (var poll = 0; poll < maxPolls; poll++)
            {
                var snapshots = _participants
                    .Select(participant => (
                        Name: participant.Key,
                        Snapshot: CreateSnapshot(participant.Value)))
                    .ToList();

                if (snapshots.All(snapshot => SnapshotsMatch(snapshots[0].Snapshot, snapshot.Snapshot)))
                {
                    convergeStopwatch.Stop();
                    cycleStopwatch.Stop();

                    WriteStatistics(cycleStopwatch.Elapsed, convergeStopwatch.Elapsed, "PASS");
                    AppendMemoryLog(activeProfileName, "PASS");
                    _logger.LogInformation(
                        "=== Cycle {Cycle}: PASS (converged in {ConvergeTime:F1}s, cycle {CycleTime:F0}s) ===",
                        _cycleNumber, convergeStopwatch.Elapsed.TotalSeconds, cycleStopwatch.Elapsed.TotalSeconds);

                    _cycleLoggerProvider?.FinishCycle(_cycleNumber, true);
                    converged = true;
                    break;
                }

                await Task.Delay(SnapshotPollInterval, stoppingToken);
            }

            if (!converged)
            {
                cycleStopwatch.Stop();
                convergeStopwatch.Stop();

                // Log failure details
                var snapshots = _participants
                    .Select(participant => (
                        Name: participant.Key,
                        Snapshot: CreateSnapshot(participant.Value)))
                    .ToList();

                _logger.LogError("=== Cycle {Cycle}: FAIL (did not converge within {Timeout}) ===",
                    _cycleNumber, _configuration.ConvergenceTimeout);

                // Log snapshot diffs and write per-participant JSON files
                var referenceSnapshot = snapshots[0];
                foreach (var snapshot in snapshots.Skip(1))
                {
                    if (!SnapshotsMatch(referenceSnapshot.Snapshot, snapshot.Snapshot))
                    {
                        _logger.LogError("Mismatch between {Reference} and {Other}",
                            referenceSnapshot.Name, snapshot.Name);
                    }
                }

                // Write formatted JSON snapshots for easy diffing
                foreach (var snapshot in snapshots)
                {
                    var fileName = $"cycle{_cycleNumber:D3}-fail-{snapshot.Name}.json";
                    var filePath = Path.Combine(Path.GetDirectoryName(MemoryLogPath)!, fileName);
                    var formattedJson = FormatSnapshotJson(snapshot.Snapshot);
                    await File.WriteAllTextAsync(filePath, formattedJson, stoppingToken);
                    _logger.LogError("Snapshot [{Name}] written to {FilePath}", snapshot.Name, filePath);
                }

                // Detailed failure diagnostics
                LogPropertyDiffsWithTimestamps(snapshots);
                LogSequenceDiagnostics();
                LogReSyncCheck(snapshots);

                WriteStatistics(cycleStopwatch.Elapsed, convergeStopwatch.Elapsed, "FAIL");
                AppendMemoryLog(activeProfileName, "FAIL");
                _cycleLoggerProvider?.FinishCycle(_cycleNumber, false);

                _failed = true;
                _applicationLifetime.StopApplication();
                return;
            }
        }
    }

    /// <summary>
    /// Diffs the snapshots and logs write timestamps for each diverged property.
    /// Helps distinguish "never written" (null timestamp) from "overwritten with default".
    /// </summary>
    private void LogPropertyDiffsWithTimestamps(List<(string Name, string Snapshot)> snapshots)
    {
        try
        {
            var reference = JsonNode.Parse(snapshots[0].Snapshot)!["subjects"]!.AsObject();
            var referenceParticipant = _participants[snapshots[0].Name];

            for (var i = 1; i < snapshots.Count; i++)
            {
                var other = JsonNode.Parse(snapshots[i].Snapshot)!["subjects"]!.AsObject();
                var otherParticipant = _participants[snapshots[i].Name];

                foreach (var (subjectId, refSubjectNode) in reference)
                {
                    if (refSubjectNode is not JsonObject refProperties)
                        continue;

                    if (!other.ContainsKey(subjectId))
                    {
                        _logger.LogError("  Subject {SubjectId}: missing from {Participant}", subjectId, snapshots[i].Name);
                        continue;
                    }

                    var otherProperties = other[subjectId]!.AsObject();
                    foreach (var (propertyName, refValue) in refProperties)
                    {
                        var otherValue = otherProperties[propertyName];
                        if (refValue?.ToJsonString() != otherValue?.ToJsonString())
                        {
                            // Look up write timestamps from actual subjects
                            var refTimestamp = TryGetWriteTimestamp(referenceParticipant, subjectId, propertyName);
                            var otherTimestamp = TryGetWriteTimestamp(otherParticipant, subjectId, propertyName);

                            _logger.LogError(
                                "  {SubjectId}.{Property}: {Reference}={RefValue} (written {RefTimestamp}), {Other}={OtherValue} (written {OtherTimestamp})",
                                subjectId, propertyName,
                                snapshots[0].Name, refValue?.ToJsonString() ?? "null", refTimestamp ?? "never",
                                snapshots[i].Name, otherValue?.ToJsonString() ?? "null", otherTimestamp ?? "never");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log property diffs with timestamps");
        }
    }

    private static string? TryGetWriteTimestamp(TestNode root, string subjectId, string propertyName)
    {
        var registry = ((IInterceptorSubject)root).Context.TryGetService<ISubjectRegistry>();
        if (registry is null)
            return null;

        var idRegistry = ((IInterceptorSubject)root).Context.TryGetService<ISubjectIdRegistry>();
        if (idRegistry is null)
            return null;

        if (!idRegistry.TryGetSubjectById(subjectId, out var subject))
            return null;

        var timestamp = new PropertyReference(subject, propertyName).TryGetWriteTimestamp();
        return timestamp?.ToString("HH:mm:ss.fff");
    }

    /// <summary>
    /// Logs WebSocket sequence numbers on failure to detect delivery gaps.
    /// If server sequence > client's last received, updates were lost in transit.
    /// </summary>
    private void LogSequenceDiagnostics()
    {
        try
        {
            var hostedServices = _serviceProvider.GetServices<IHostedService>();

            foreach (var service in hostedServices)
            {
                if (service is WebSocketSubjectServer server)
                {
                    _logger.LogError("  Server broadcast sequence: {Sequence}", server.CurrentSequence);
                }
                else if (service is WebSocketSubjectClientSource client)
                {
                    _logger.LogError("  Client last received sequence: {Sequence} (root: {RootType})",
                        client.LastReceivedSequence, client.RootSubject.GetType().Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log sequence diagnostics");
        }
    }

    /// <summary>
    /// Re-sync check: creates a complete update from the reference (server), applies it to
    /// each diverged participant, then compares again. If they match, the failure was a
    /// transient delivery gap. If they still differ, it's a structural/applier bug.
    /// </summary>
    private void LogReSyncCheck(List<(string Name, string Snapshot)> snapshots)
    {
        try
        {
            var referenceNode = _participants[snapshots[0].Name];
            var completeUpdate = SubjectUpdate.CreateCompleteUpdate(referenceNode, []);

            for (var i = 1; i < snapshots.Count; i++)
            {
                if (SnapshotsMatch(snapshots[i].Snapshot, snapshots[0].Snapshot))
                    continue;

                var otherNode = _participants[snapshots[i].Name];
                otherNode.ApplySubjectUpdate(completeUpdate, DefaultSubjectFactory.Instance);

                var reSnapshotReference = CreateSnapshot(referenceNode);
                var reSnapshotOther = CreateSnapshot(otherNode);

                if (SnapshotsMatch(reSnapshotReference, reSnapshotOther))
                {
                    _logger.LogWarning(
                        "Re-sync check: {Participant} converged after applying server's complete update → transient delivery gap",
                        snapshots[i].Name);
                }
                else
                {
                    _logger.LogError(
                        "Re-sync check: {Participant} still diverged after applying server's complete update → structural/applier bug",
                        snapshots[i].Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to perform re-sync check");
        }
    }

    /// <summary>
    /// Compares two normalized snapshots by traversing the JSON tree field-by-field.
    /// Value property timestamps are compared only when both sides have a non-null timestamp.
    /// A null timestamp (property never explicitly written) matches any timestamp — this is
    /// legitimate after server rebuild or when EqualityCheck skips a redundant write.
    /// </summary>
    private static bool SnapshotsMatch(string snapshotA, string snapshotB)
    {
        if (snapshotA == snapshotB)
            return true;

        var a = JsonNode.Parse(snapshotA)!["subjects"]?.AsObject();
        var b = JsonNode.Parse(snapshotB)!["subjects"]?.AsObject();

        if (a is null || b is null)
            return a is null && b is null;

        if (a.Count != b.Count)
            return false;

        foreach (var (subjectId, subjectNodeA) in a)
        {
            if (b[subjectId] is not JsonObject propsB)
                return false;

            var propsA = subjectNodeA!.AsObject();
            if (propsA.Count != propsB.Count)
                return false;

            foreach (var (propertyName, propNodeA) in propsA)
            {
                if (propsB[propertyName] is not JsonObject propB)
                    return false;

                var propA = propNodeA!.AsObject();

                if (!PropertiesMatch(propA, propB))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Compares two property JSON objects by iterating all fields generically.
    /// The only special case is "timestamp": compared only when both sides are non-null
    /// (a null timestamp is legitimate after server rebuild or equality-check skip).
    /// All other fields are compared directly, so new fields added to SubjectPropertyUpdate
    /// are automatically included without code changes here.
    /// </summary>
    private static bool PropertiesMatch(JsonObject propA, JsonObject propB)
    {
        // Collect all keys from both sides
        var allKeys = new HashSet<string>(propA.Select(kvp => kvp.Key));
        allKeys.UnionWith(propB.Select(kvp => kvp.Key));

        foreach (var key in allKeys)
        {
            var valueA = propA[key];
            var valueB = propB[key];

            if (key == "timestamp")
            {
                // Only compare when both sides have a non-null timestamp
                if (valueA is not null && valueB is not null &&
                    !JsonValuesEqual(valueA, valueB))
                {
                    return false;
                }
            }
            else
            {
                if (!JsonValuesEqual(valueA, valueB))
                    return false;
            }
        }

        return true;
    }

    private static bool JsonValuesEqual(JsonNode? a, JsonNode? b)
    {
        if (a is null && b is null)
            return true;
        if (a is null || b is null)
            return false;

        return a.ToJsonString() == b.ToJsonString();
    }

    private static string CreateSnapshot(TestNode root)
    {
        var update = SubjectUpdate.CreateCompleteUpdate(root, []);

        // Serialize to JSON, then normalize the JSON tree for deterministic comparison.
        // This avoids mutating the SubjectPropertyUpdate objects from the update.
        var json = JsonSerializer.Serialize(update, SnapshotJsonOptions);
        var node = JsonNode.Parse(json)!.AsObject();

        // Normalize root subject ID: each participant generates its own root ID independently,
        // so replace it with a constant placeholder for comparison purposes.
        // See docs/connectors/subject-updates.md "Root ID Independence".
        var rootId = node["root"]?.GetValue<string>();

        var subjects = node["subjects"]?.AsObject();
        if (subjects is not null)
        {
            foreach (var (_, subjectNode) in subjects)
            {
                if (subjectNode is not JsonObject properties)
                    continue;

                foreach (var (_, propertyNode) in properties)
                {
                    if (propertyNode is not JsonObject property)
                        continue;

                    var kind = property["kind"]?.GetValue<string>();

                    // Strip timestamps from structural properties (Collection, Dictionary, Object).
                    // These are set during local graph creation and are inherently different per participant.
                    // Value timestamps are kept for cross-participant comparison via SnapshotsMatch,
                    // which treats null timestamps as matching any value (legitimate after server
                    // rebuild or when EqualityCheck skips a redundant write).
                    if (kind != "Value")
                    {
                        property.Remove("timestamp");
                    }

                    // Normalize references to the root subject ID. Each participant
                    // generates its own root ID independently (see "Root ID Independence"
                    // in subject-updates.md), so replace with a constant placeholder.
                    if (kind == "Object" && property["id"]?.GetValue<string>() == rootId)
                    {
                        property["id"] = "ROOT";
                    }

                    if ((kind == "Collection" || kind == "Dictionary") && property["items"] is JsonArray itemsToNormalize)
                    {
                        foreach (var itemNode in itemsToNormalize)
                        {
                            if (itemNode is JsonObject itemObj && itemObj["id"]?.GetValue<string>() == rootId)
                            {
                                itemObj["id"] = "ROOT";
                            }
                        }
                    }

                    // Sort dictionary items by key for order-independent comparison.
                    if (kind == "Dictionary" && property["items"] is JsonArray items)
                    {
                        var sorted = items
                            .Select(item => item!.AsObject())
                            .OrderBy(item => item["key"]?.GetValue<string>(), StringComparer.Ordinal)
                            .Select(item => item.DeepClone())
                            .ToArray();

                        items.Clear();
                        foreach (var item in sorted)
                        {
                            items.Add(item);
                        }
                    }
                }
            }
        }

        // Normalize keys first, then sort by normalized key for deterministic comparison.
        // Each participant generates its own root ID, so sorting must happen after normalization.
        var normalizedEntries = new List<(string Key, JsonObject Properties)>();
        if (subjects is not null)
        {
            foreach (var (subjectKey, subjectNode) in subjects)
            {
                var normalizedKey = subjectKey == rootId ? "ROOT" : subjectKey;
                var properties = subjectNode!.AsObject();
                var sortedProperties = new JsonObject();
                foreach (var propertyKey in properties.Select(kvp => kvp.Key).Order(StringComparer.Ordinal))
                {
                    sortedProperties[propertyKey] = properties[propertyKey]!.DeepClone();
                }
                normalizedEntries.Add((normalizedKey, sortedProperties));
            }
        }

        var sortedSubjects = new JsonObject();
        foreach (var (key, props) in normalizedEntries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            sortedSubjects[key] = props;
        }

        var result = new JsonObject
        {
            ["root"] = rootId != null ? "ROOT" : null,
            ["subjects"] = sortedSubjects
        };

        return result.ToJsonString(SnapshotJsonOptions);
    }

    private void WriteStatistics(TimeSpan cycleDuration, TimeSpan convergeDuration, string result)
    {
        var totalValueMutations = _mutationEngines.Sum(engine => engine.ValueMutationCount);
        var totalStructuralMutations = _mutationEngines.Sum(engine => engine.StructuralMutationCount);
        var totalChaos = _chaosEngines.Sum(engine => engine.ChaosEventCount);

        _logger.LogInformation("""
            --- Cycle {Cycle} Statistics ---
            Duration: {CycleDuration:F0}s (converged in {ConvergeDuration:F1}s)
            Total mutations: {TotalValueMutations:N0} value + {TotalStructuralMutations:N0} structural | Total chaos events: {TotalChaos}
            Result: {Result}
            """,
            _cycleNumber, cycleDuration.TotalSeconds, convergeDuration.TotalSeconds,
            totalValueMutations, totalStructuralMutations, totalChaos, result);

        // Per-participant breakdown
        foreach (var engine in _mutationEngines)
        {
            _logger.LogInformation("  {Name}: {Values:N0} value + {Structural:N0} structural mutations",
                engine.Name, engine.ValueMutationCount, engine.StructuralMutationCount);
        }

        // Chaos event timeline
        foreach (var engine in _chaosEngines)
        {
            foreach (var record in engine.EventHistory)
            {
                _logger.LogInformation("  {Name}: {FaultType} at {Time:HH:mm:ss} ({Duration:F1}s)",
                    engine.TargetName, record.FaultType, record.DisruptedAt.LocalDateTime, record.Duration.TotalSeconds);
            }
        }
    }

    private void AppendMemoryLog(string? profileName, string result)
    {
        // Compact LOH to prevent fragmentation from large temporary objects
        // created during server restart cycles (NodeSet XML, serialization buffers).
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        using var process = Process.GetCurrentProcess();
        var workingSetMb = process.WorkingSet64 / (1024.0 * 1024.0);
        var heapMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        var profile = profileName != null ? $"profile: {profileName}" : "no profile";

        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-ddTHH:mm:ss.fffZ}, Cycle {1}, {2}, {3}, ProcessMB: {4:F1}, HeapMB: {5:F1}",
            DateTimeOffset.UtcNow, _cycleNumber, profile, result, workingSetMb, heapMb);

        // Diagnostic: log registry vs reachable subject counts to distinguish leak from tree growth
        var registryInfo = string.Join(", ", _participants.Select(p =>
        {
            var registry = ((IInterceptorSubject)p.Value).Context.TryGetService<ISubjectRegistry>();
            var knownCount = registry?.KnownSubjects.Count ?? -1;
            var reachableUpdate = SubjectUpdate.CreateCompleteUpdate(p.Value, []);
            var reachable = reachableUpdate.Subjects.Count;

            if (registry is not null && knownCount > reachable)
            {
                var reachableIds = new HashSet<string>(reachableUpdate.Subjects.Keys);
                foreach (var kvp in registry.KnownSubjects)
                {
                    var id = kvp.Key.TryGetSubjectId();
                    if (id is not null && !reachableIds.Contains(id))
                    {
                        var refCount = kvp.Value.ReferenceCount;
                        var parents = kvp.Value.Parents;
                        var parts = new List<string>();
                        foreach (var parent in parents)
                        {
                            // Check what the parent's property actually contains
                            var actualValue = parent.Property.GetValue();
                            var actualContainsOrphan = false;
                            if (actualValue is System.Collections.IEnumerable enumerable and not string)
                            {
                                foreach (var item in enumerable)
                                {
                                    if (ReferenceEquals(item, kvp.Key)) { actualContainsOrphan = true; break; }
                                }
                            }
                            else
                            {
                                actualContainsOrphan = ReferenceEquals(actualValue, kvp.Key);
                            }

                            parts.Add($"{parent.Property.Name}[{parent.Index}]@{parent.Property.Subject.TryGetSubjectId() ?? "?"}" +
                                $"(actual:{(actualContainsOrphan ? "FOUND" : "MISSING")})");
                        }
                        _logger.LogWarning(
                            "LEAK: {Participant} orphaned {Id} refCount={RefCount} parents=[{Parents}]",
                            p.Key, id, refCount, parts.Count > 0 ? string.Join(", ", parts) : "no-parents");
                    }
                }
            }

            return $"{p.Key}={knownCount}/{reachable}";
        }));
        line += $", Subjects(registry/reachable): [{registryInfo}]";

        File.AppendAllText(MemoryLogPath, line + Environment.NewLine);
    }

    private static string FormatSnapshotJson(string compactJson)
    {
        using var document = JsonDocument.Parse(compactJson);
        return JsonSerializer.Serialize(document, IndentedJsonOptions);
    }

    private string? ApplyChaosProfile()
    {
        var profiles = _configuration.ChaosProfiles;
        if (profiles.Count == 0)
        {
            return null;
        }

        var profile = profiles[(_cycleNumber - 1) % profiles.Count];

        foreach (var engine in _chaosEngines)
        {
            engine.Enabled = profile.Participants.Contains(engine.TargetName);
        }

        return profile.Name;
    }
}
