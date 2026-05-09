using System.Text.Json;
using System.Text.Json.Nodes;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Engine;

/// <summary>
/// Captures and compares participant snapshots for the ConnectorTester convergence check.
/// Produces a deterministic JSON representation per participant so equal source state
/// (modulo legitimate per-participant differences such as root subject IDs and
/// structural-property timestamps) yields equal strings.
/// </summary>
public static class SnapshotComparer
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Builds a normalized snapshot JSON for the given root.
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
        var rootId = root["root"]?.GetValue<string>();

        if (root["subjects"] is JsonObject subjects)
        {
            foreach (var (_, subjectNode) in subjects)
            {
                if (subjectNode is JsonObject properties)
                {
                    NormalizeProperties(properties, rootId);
                }
            }

            ReplaceSubjects(root, subjects, rootId);
        }

        if (rootId is not null)
        {
            root["root"] = "ROOT";
        }
    }

    private static void NormalizeProperties(JsonObject properties, string? rootId)
    {
        foreach (var (_, propertyNode) in properties)
        {
            if (propertyNode is not JsonObject property)
            {
                continue;
            }

            var kind = property["kind"]?.GetValue<string>();

            // Strip timestamps from non-Value properties: structural-property timestamps
            // are local creation moments and never propagate across the wire.
            if (kind != "Value")
            {
                property.Remove("timestamp");
            }

            // Replace per-participant root ID references with "ROOT".
            if (kind == "Object" && property["id"]?.GetValue<string>() == rootId)
            {
                property["id"] = "ROOT";
            }

            if ((kind == "Collection" || kind == "Dictionary") &&
                property["items"] is JsonArray items)
            {
                foreach (var itemNode in items)
                {
                    if (itemNode is JsonObject itemObject &&
                        itemObject["id"]?.GetValue<string>() == rootId)
                    {
                        itemObject["id"] = "ROOT";
                    }
                }

                // Dictionary items have no defined order; sort by their "index" field
                // (the dictionary key, serialized as a string). Collection items keep
                // their source order (order is part of equality).
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

    private static void ReplaceSubjects(JsonObject root, JsonObject subjects, string? rootId)
    {
        // Sort subject IDs (with root renamed) and property keys for deterministic output.
        var entries = subjects
            .Select(kvp => (
                Key: kvp.Key == rootId ? "ROOT" : kvp.Key,
                Properties: kvp.Value!.AsObject()))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToList();

        var sorted = new JsonObject();
        foreach (var (key, properties) in entries)
        {
            var sortedProperties = new JsonObject();
            foreach (var propertyKey in properties
                .Select(kvp => kvp.Key)
                .OrderBy(name => name, StringComparer.Ordinal))
            {
                sortedProperties[propertyKey] = properties[propertyKey]!.DeepClone();
            }
            sorted[key] = sortedProperties;
        }

        root["subjects"] = sorted;
    }
}
