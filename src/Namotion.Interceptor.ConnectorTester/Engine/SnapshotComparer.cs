using System.Text.Json;
using System.Text.Json.Nodes;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Captures and compares participant snapshots for the ConnectorTester convergence check.
/// Produces a deterministic JSON representation per participant: equal source state yields
/// equal strings even when participants assigned different subject IDs internally.
/// </summary>
public static class SnapshotComparer
{
    private const string SubjectsKey = "subjects";
    internal const string TimestampKey = "timestamp";
    internal const string KindKey = "kind";
    internal const string ValueKey = "value";
    internal const string IdKey = "id";
    internal const string CountKey = "count";
    internal const string ItemsKey = "items";

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Builds a normalized snapshot JSON for the given root.
    /// Subject IDs are remapped to stable positional IDs (root -> "ROOT", then "SUBJ_1",
    /// "SUBJ_2", ... by deterministic graph traversal). Timestamps on structural
    /// (Object/Collection/Dictionary) properties are stripped: those reflect local graph
    /// creation moments, not synced wire events. Timestamps on Value properties are
    /// preserved: those propagate via source timestamps and must converge.
    /// Dictionary items are sorted by their index field (the dictionary key); Collection
    /// items keep their source order because order is part of equality.
    /// </summary>
    public static string Capture(TestNode root)
    {
        var update = SubjectUpdate.CreateCompleteUpdate(root, []);
        var idMap = BuildStableIdMap(update);

        // Verification-tooling invariant: every subject must be reachable from the root
        // via Object/Collection/Dictionary edges. Unreachable orphans would keep their
        // raw (per-participant) IDs after normalization and silently produce false
        // convergence failures. Fail fast so this surfaces as a tooling issue rather
        // than masquerading as a connector bug.
        if (update.Subjects.Count != idMap.Count)
        {
            var unreachable = update.Subjects.Keys.Where(id => !idMap.ContainsKey(id)).ToList();
            if (unreachable.Count > 0)
            {
                throw new InvalidOperationException(
                    $"SnapshotComparer: SubjectUpdate contains {unreachable.Count} subject(s) " +
                    $"not reachable from root '{update.Root}': {string.Join(", ", unreachable)}. " +
                    "This is a verification-tooling invariant violation (CreateCompleteUpdate is " +
                    "expected to emit only reachable subjects), not a connector convergence failure.");
            }
        }

        var normalized = NormalizeUpdate(update, idMap);
        return JsonSerializer.Serialize(normalized, CompactJsonOptions);
    }

    /// <summary>
    /// Compares two normalized snapshots produced by <see cref="Capture"/>.
    /// Falls back from string equality to a JSON-walking comparison that respects the
    /// architectural null-timestamp contract (see SubjectChangeContext NullTimestampSentinel):
    /// a null timestamp on either side matches any timestamp value. All other fields
    /// compare by strict JSON equality.
    /// </summary>
    public static bool SnapshotsMatch(string snapshotA, string snapshotB)
    {
        if (snapshotA == snapshotB)
        {
            return true;
        }

        return SubjectsMatch(ParseSubjects(snapshotA), ParseSubjects(snapshotB));
    }

    public static bool SnapshotsMatch(JsonObject? referenceSubjects, string snapshotB)
    {
        return SubjectsMatch(referenceSubjects, ParseSubjects(snapshotB));
    }

    private static bool SubjectsMatch(JsonObject? subjectsA, JsonObject? subjectsB)
    {
        if (subjectsA is null || subjectsB is null)
        {
            return subjectsA is null && subjectsB is null;
        }

        if (subjectsA.Count != subjectsB.Count)
        {
            return false;
        }

        foreach (var (subjectId, subjectNodeA) in subjectsA)
        {
            if (subjectsB[subjectId] is not JsonObject propertiesB)
            {
                return false;
            }

            var propertiesA = subjectNodeA!.AsObject();
            if (propertiesA.Count != propertiesB.Count)
            {
                return false;
            }

            foreach (var (propertyName, propertyNodeA) in propertiesA)
            {
                if (propertiesB[propertyName] is not JsonObject propertyB)
                {
                    return false;
                }

                if (!PropertiesMatch(propertyNodeA!.AsObject(), propertyB))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static JsonObject? ParseSubjects(string snapshot)
    {
        return JsonNode.Parse(snapshot)?[SubjectsKey]?.AsObject();
    }

    private static Dictionary<string, string> BuildStableIdMap(SubjectUpdate update)
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [update.Root] = "ROOT"
        };
        var queue = new Queue<string>();
        queue.Enqueue(update.Root);

        var counter = 1;

        while (queue.Count > 0)
        {
            var currentRawId = queue.Dequeue();

            if (!update.Subjects.TryGetValue(currentRawId, out var properties))
            {
                continue;
            }

            foreach (var propertyName in properties.Keys.OrderBy(name => name, StringComparer.Ordinal))
            {
                var property = properties[propertyName];

                if (property.Kind == SubjectPropertyUpdateKind.Object)
                {
                    if (property.Id is not null && !idMap.ContainsKey(property.Id))
                    {
                        idMap[property.Id] = $"SUBJ_{counter++}";
                        queue.Enqueue(property.Id);
                    }
                }
                else if (property.Kind is SubjectPropertyUpdateKind.Collection or SubjectPropertyUpdateKind.Dictionary
                    && property.Items is { } items)
                {
                    IEnumerable<SubjectPropertyItemUpdate> orderedItems =
                        property.Kind == SubjectPropertyUpdateKind.Dictionary
                            ? items.OrderBy(item => item.Index?.ToString(), StringComparer.Ordinal)
                            : items;

                    foreach (var item in orderedItems)
                    {
                        if (item.Id is not null && !idMap.ContainsKey(item.Id))
                        {
                            idMap[item.Id] = $"SUBJ_{counter++}";
                            queue.Enqueue(item.Id);
                        }
                    }
                }
            }
        }

        return idMap;
    }

    private static SubjectUpdate NormalizeUpdate(SubjectUpdate update, Dictionary<string, string> idMap)
    {
        string RemapId(string id) => idMap.GetValueOrDefault(id, id);

        var normalizedSubjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>();

        foreach (var (rawSubjectId, properties) in update.Subjects
            .OrderBy(kvp => RemapId(kvp.Key), StringComparer.Ordinal))
        {
            var normalizedProperties = new Dictionary<string, SubjectPropertyUpdate>();

            foreach (var (propertyName, property) in properties
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                var normalized = new SubjectPropertyUpdate
                {
                    Kind = property.Kind,
                    Value = property.Value,
                    Timestamp = property.Kind == SubjectPropertyUpdateKind.Value ? property.Timestamp : null,
                    Id = property.Id is not null ? RemapId(property.Id) : null,
                    Count = property.Count,
                    Operations = property.Operations,
                    Attributes = property.Attributes,
                    ExtensionData = property.ExtensionData,
                };

                if (property.Items is { } items)
                {
                    var normalizedItems = items
                        .Select(item => new SubjectPropertyItemUpdate
                        {
                            Index = item.Index,
                            Id = item.Id is not null ? RemapId(item.Id) : null,
                        })
                        .ToList();

                    if (property.Kind == SubjectPropertyUpdateKind.Dictionary)
                    {
                        normalizedItems.Sort((a, b) =>
                            string.Compare(a.Index?.ToString(), b.Index?.ToString(), StringComparison.Ordinal));
                    }

                    normalized.Items = normalizedItems;
                }

                normalizedProperties[propertyName] = normalized;
            }

            normalizedSubjects[RemapId(rawSubjectId)] = normalizedProperties;
        }

        return new SubjectUpdate
        {
            Root = "ROOT",
            Subjects = normalizedSubjects,
        };
    }

    /// <summary>
    /// Compares two snapshots and returns a list of properties where the null-timestamp
    /// rule forgave a mismatch (one side had a timestamp, the other did not).
    /// Returns an empty list when the snapshots are string-equal or when there are no
    /// forgiven mismatches. Returns null when the snapshots do not match at all.
    /// </summary>
    public static List<string>? CollectFindings(
        string nameA, string snapshotA,
        string nameB, string snapshotB)
    {
        if (snapshotA == snapshotB)
        {
            return [];
        }

        var subjectsA = ParseSubjects(snapshotA);
        var subjectsB = ParseSubjects(snapshotB);

        if (subjectsA is null || subjectsB is null)
        {
            return (subjectsA is null && subjectsB is null) ? [] : null;
        }

        if (subjectsA.Count != subjectsB.Count)
        {
            return null;
        }

        var findings = new List<string>();

        foreach (var (subjectId, subjectNodeA) in subjectsA)
        {
            if (subjectsB[subjectId] is not JsonObject propertiesB)
            {
                return null;
            }

            var propertiesA = subjectNodeA!.AsObject();
            if (propertiesA.Count != propertiesB.Count)
            {
                return null;
            }

            foreach (var (propertyName, propertyNodeA) in propertiesA)
            {
                if (propertiesB[propertyName] is not JsonObject propertyB)
                {
                    return null;
                }

                var propertyA = propertyNodeA!.AsObject();
                if (!PropertiesMatch(propertyA, propertyB))
                {
                    return null;
                }

                var tsA = propertyA[TimestampKey];
                var tsB = propertyB[TimestampKey];
                if ((tsA is null) != (tsB is null))
                {
                    var hasTimestamp = tsA is not null ? nameA : nameB;
                    var missingTimestamp = tsA is null ? nameA : nameB;
                    var timestampValue = (tsA ?? tsB)!.ToJsonString();
                    findings.Add($"{subjectId}.{propertyName}: {hasTimestamp} has timestamp {timestampValue}, {missingTimestamp} has none");
                }
            }
        }

        return findings;
    }

    internal static bool PropertiesMatch(JsonObject propertyA, JsonObject propertyB)
    {
        foreach (var (key, valueA) in propertyA)
        {
            if (!CompareField(key, valueA, propertyB[key]))
            {
                return false;
            }
        }

        foreach (var (key, valueB) in propertyB)
        {
            if (propertyA.ContainsKey(key))
            {
                continue;
            }

            if (!CompareField(key, null, valueB))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CompareField(string key, JsonNode? valueA, JsonNode? valueB)
    {
        if (key == TimestampKey)
        {
            // Architectural null-timestamp rule: null on either side is a legitimate
            // "no explicit write timestamp" state and matches any value. Only fail
            // when both sides are non-null and unequal.
            return valueA is null || valueB is null || JsonNode.DeepEquals(valueA, valueB);
        }

        return JsonNode.DeepEquals(valueA, valueB);
    }
}
