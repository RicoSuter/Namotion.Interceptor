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
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Builds a normalized snapshot JSON for the given root.
    /// Subject IDs are remapped to stable positional IDs (root → "ROOT", then "SUBJ_1",
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
        var rawJson = JsonSerializer.Serialize(update, CompactJsonOptions);
        var node = JsonNode.Parse(rawJson)!.AsObject();
        Normalize(node);
        return node.ToJsonString(CompactJsonOptions);
    }

    private static void Normalize(JsonObject root)
    {
        var rawRootId = root["root"]?.GetValue<string>();
        if (rawRootId is null || root["subjects"] is not JsonObject subjects)
        {
            return;
        }

        var idMap = BuildStableIdMap(rawRootId, subjects);

        foreach (var (_, subjectNode) in subjects)
        {
            if (subjectNode is JsonObject properties)
            {
                NormalizeProperties(properties, idMap);
            }
        }

        RebuildSubjects(root, subjects, idMap);

        root["root"] = idMap.TryGetValue(rawRootId, out var stableRoot) ? stableRoot : "ROOT";
    }

    /// <summary>
    /// Traverses the subject graph from the root in deterministic order (BFS, properties
    /// visited in sorted name order, dictionary items sorted by index, collection items in
    /// source order) and assigns positional IDs ("ROOT", "SUBJ_1", "SUBJ_2", ...).
    /// This guarantees that two participants whose graphs are content-equal but whose
    /// internal subject IDs were assigned in different orders produce the same map.
    /// </summary>
    private static Dictionary<string, string> BuildStableIdMap(string rawRootId, JsonObject subjects)
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [rawRootId] = "ROOT"
        };
        var queue = new Queue<string>();
        queue.Enqueue(rawRootId);

        var counter = 1;

        while (queue.Count > 0)
        {
            var currentRawId = queue.Dequeue();

            if (subjects[currentRawId] is not JsonObject properties)
            {
                continue;
            }

            foreach (var propertyName in properties
                .Select(kvp => kvp.Key)
                .OrderBy(name => name, StringComparer.Ordinal))
            {
                if (properties[propertyName] is not JsonObject property)
                {
                    continue;
                }

                var kind = property["kind"]?.GetValue<string>();

                if (kind == "Object")
                {
                    var refId = property["id"]?.GetValue<string>();
                    if (refId is not null && !idMap.ContainsKey(refId))
                    {
                        idMap[refId] = $"SUBJ_{counter++}";
                        queue.Enqueue(refId);
                    }
                }
                else if ((kind == "Collection" || kind == "Dictionary") &&
                    property["items"] is JsonArray items)
                {
                    // Visit dictionary items sorted by key so insertion order does not
                    // affect ID assignment. Collection items keep source order.
                    IEnumerable<JsonNode?> orderedItems = kind == "Dictionary"
                        ? items.OrderBy(item => item?["index"]?.GetValue<string>(), StringComparer.Ordinal)
                        : items;

                    foreach (var itemNode in orderedItems)
                    {
                        if (itemNode is not JsonObject itemObject)
                        {
                            continue;
                        }

                        var refId = itemObject["id"]?.GetValue<string>();
                        if (refId is not null && !idMap.ContainsKey(refId))
                        {
                            idMap[refId] = $"SUBJ_{counter++}";
                            queue.Enqueue(refId);
                        }
                    }
                }
            }
        }

        return idMap;
    }

    private static void NormalizeProperties(JsonObject properties, Dictionary<string, string> idMap)
    {
        foreach (var (_, propertyNode) in properties)
        {
            if (propertyNode is not JsonObject property)
            {
                continue;
            }

            var kind = property["kind"]?.GetValue<string>();

            // Structural-property timestamps are local creation moments and never propagate
            // across the wire; strip them. Value-property timestamps DO propagate (e.g. via
            // OPC UA source timestamps) and must converge for participants to agree.
            if (kind != "Value")
            {
                property.Remove("timestamp");
            }

            if (kind == "Object")
            {
                var refId = property["id"]?.GetValue<string>();
                if (refId is not null && idMap.TryGetValue(refId, out var stableId))
                {
                    property["id"] = stableId;
                }
            }
            else if ((kind == "Collection" || kind == "Dictionary") &&
                property["items"] is JsonArray items)
            {
                foreach (var itemNode in items)
                {
                    if (itemNode is JsonObject itemObject)
                    {
                        var refId = itemObject["id"]?.GetValue<string>();
                        if (refId is not null && idMap.TryGetValue(refId, out var stableId))
                        {
                            itemObject["id"] = stableId;
                        }
                    }
                }

                // Dictionary items have no defined order; sort by their "index" field
                // (the dictionary key, serialized as a string). Collection items keep
                // their source order because order is part of equality.
                if (kind == "Dictionary")
                {
                    var sortedItems = items
                        .Select(item => item!.AsObject())
                        .OrderBy(item => item["index"]?.GetValue<string>(), StringComparer.Ordinal)
                        .Select(item => item.DeepClone())
                        .ToArray();

                    items.Clear();
                    foreach (var item in sortedItems)
                    {
                        items.Add(item);
                    }
                }
            }
        }
    }

    private static void RebuildSubjects(JsonObject root, JsonObject subjects, Dictionary<string, string> idMap)
    {
        // Sort subjects by stable ID and properties within each subject by name for deterministic output.
        var entries = subjects
            .Select(kvp => (
                StableKey: idMap.TryGetValue(kvp.Key, out var sid) ? sid : kvp.Key,
                Properties: kvp.Value!.AsObject()))
            .OrderBy(entry => entry.StableKey, StringComparer.Ordinal)
            .ToList();

        var sorted = new JsonObject();
        foreach (var (stableKey, properties) in entries)
        {
            var sortedProperties = new JsonObject();
            foreach (var propertyKey in properties
                .Select(kvp => kvp.Key)
                .OrderBy(name => name, StringComparer.Ordinal))
            {
                sortedProperties[propertyKey] = properties[propertyKey]!.DeepClone();
            }
            sorted[stableKey] = sortedProperties;
        }

        root["subjects"] = sorted;
    }
}
