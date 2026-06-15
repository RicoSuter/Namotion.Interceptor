using System.Text.Json;
using System.Text.Json.Nodes;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Snapshot;

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
        var idMap = SnapshotIdMap.Build(update);

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

    /// <summary>
    /// Returns (subjects, properties) totals for the snapshot, used on PASS to log a summary
    /// of what was actually compared. A non-zero, expected count confirms the comparer ran
    /// over real data; the absence of a diff alone could otherwise hide a regression that
    /// skipped properties.
    /// </summary>
    public static (int Subjects, int Properties) CountSubjectsAndProperties(string snapshot)
    {
        var subjects = ParseSubjects(snapshot);
        if (subjects is null) return (0, 0);

        var properties = 0;
        foreach (var (_, subjectNode) in subjects)
        {
            if (subjectNode is JsonObject subjectObject)
            {
                properties += subjectObject.Count;
            }
        }
        return (subjects.Count, properties);
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
                    Attributes = property.Attributes,
                    ExtensionData = property.ExtensionData,
                };

                if (property.Items is { } items)
                {
                    var normalizedItems = items
                        .Select(item => new SubjectPropertyItemUpdate
                        {
                            Id = RemapId(item.Id),
                            Key = item.Key,
                        })
                        .ToList();

                    if (property.Kind == SubjectPropertyUpdateKind.Dictionary)
                    {
                        // Dictionary ordering is keyed (not positional), so sort by key for a
                        // deterministic, order-independent comparison. Collections keep source
                        // order because array position is part of equality.
                        normalizedItems.Sort((a, b) =>
                            string.Compare(a.Key, b.Key, StringComparison.Ordinal));
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
