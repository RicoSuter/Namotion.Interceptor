using System.Text.Json;
using System.Text.Json.Nodes;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Captures and compares participant snapshots for the ConnectorTester convergence check.
/// Produces a deterministic JSON representation per participant so equal source state
/// (modulo legitimate per-participant differences such as root subject IDs and
/// all timestamps) yields equal strings.
/// </summary>
public static class SnapshotComparer
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Builds a normalized snapshot JSON for the given root.
    /// The result is deterministic: root subject ID and all child subject IDs are
    /// replaced with stable positional IDs, all timestamps are stripped, and
    /// dictionary items are sorted by key so insertion order does not matter.
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

        // Build a stable ID map: traverse the subject graph in deterministic order
        // (BFS from root, properties visited in sorted name order, dictionary items
        // sorted by key, collection items in array order) and assign positional IDs.
        var idMap = BuildStableIdMap(rawRootId, subjects);

        // Rewrite every property in every subject using the stable ID map.
        foreach (var (_, subjectNode) in subjects)
        {
            if (subjectNode is JsonObject properties)
            {
                NormalizeProperties(properties, idMap);
            }
        }

        // Rebuild the subjects dictionary with stable keys and sorted property names.
        RebuildSubjects(root, subjects, idMap);

        root["root"] = idMap.TryGetValue(rawRootId, out var stableRoot) ? stableRoot : "ROOT";
    }

    /// <summary>
    /// Performs a BFS traversal of the subject graph in deterministic order and returns
    /// a map from raw subject ID to stable positional ID ("ROOT", "SUBJ_1", "SUBJ_2", …).
    /// </summary>
    private static Dictionary<string, string> BuildStableIdMap(string rawRootId, JsonObject subjects)
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        idMap[rawRootId] = "ROOT";
        queue.Enqueue(rawRootId);

        var counter = 1;

        while (queue.Count > 0)
        {
            var currentRawId = queue.Dequeue();

            if (subjects[currentRawId] is not JsonObject properties)
            {
                continue;
            }

            // Visit properties in sorted order so traversal is deterministic.
            foreach (var propertyName in properties.Select(kvp => kvp.Key).OrderBy(k => k, StringComparer.Ordinal))
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
                else if (kind == "Collection" || kind == "Dictionary")
                {
                    var items = property["items"] as JsonArray;
                    if (items is null)
                    {
                        continue;
                    }

                    // For dictionaries, visit items sorted by key so insertion order
                    // does not affect the traversal; collections keep their source order.
                    IEnumerable<JsonNode?> orderedItems = kind == "Dictionary"
                        ? items.OrderBy(item => item?["index"]?.GetValue<string>(), StringComparer.Ordinal)
                        : (IEnumerable<JsonNode?>)items;

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

            // Strip all timestamps: structural timestamps are local creation moments,
            // and Value timestamps differ across participants even for identical values
            // because each participant creates its context at a different wall-clock time.
            property.Remove("timestamp");

            if (kind == "Object")
            {
                var refId = property["id"]?.GetValue<string>();
                if (refId is not null && idMap.TryGetValue(refId, out var stableId))
                {
                    property["id"] = stableId;
                }
            }
            else if (kind == "Collection" || kind == "Dictionary")
            {
                if (property["items"] is JsonArray items)
                {
                    foreach (var itemNode in items)
                    {
                        if (itemNode is not JsonObject itemObject)
                        {
                            continue;
                        }

                        var refId = itemObject["id"]?.GetValue<string>();
                        if (refId is not null && idMap.TryGetValue(refId, out var stableId))
                        {
                            itemObject["id"] = stableId;
                        }
                    }

                    // Dictionary items have no defined order; sort by their "index" field
                    // (the dictionary key) for deterministic output.
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
    }

    private static void RebuildSubjects(JsonObject root, JsonObject subjects, Dictionary<string, string> idMap)
    {
        // Sort subjects by stable ID and property names for deterministic output.
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
