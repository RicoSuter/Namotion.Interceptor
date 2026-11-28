using System.Runtime.InteropServices;
using System.Text.Json;

namespace Namotion.Matter.Server;

/// <summary>Reads matterjs-server events; every element an event keeps is cloned.</summary>
internal static class MatterServerEvents
{
    /// <summary>Reads an event by its name element; returns null for an event this client does not know.</summary>
    public static MatterControllerEvent? ReadEvent(JsonElement name, JsonElement data)
    {
        // Attribute updates are by far the most frequent event, so they are checked first.
        if (name.ValueEquals("attribute_updated"u8))
        {
            return new MatterAttributeUpdatedEvent(data[0].GetUInt64(), ReadAttributePath(data[1]), new MatterValue(data[2].Clone()));
        }

        if (name.ValueEquals("node_event"u8))
        {
            return new MatterNodeEventReceivedEvent(
                data.GetProperty("node_id").GetUInt64(),
                new EventPath(
                    data.GetProperty("endpoint_id").GetUInt16(),
                    data.GetProperty("cluster_id").GetUInt32(),
                    data.GetProperty("event_id").GetUInt32()),
                data.GetProperty("event_number").GetUInt64(),
                data.GetProperty("priority").GetByte(),
                data.GetProperty("timestamp").GetInt64(),
                (MatterEventTimestampType)data.GetProperty("timestamp_type").GetInt32(),
                new MatterValue(data.GetProperty("data").Clone()));
        }

        if (name.ValueEquals("node_updated"u8))
        {
            return new MatterNodeUpdatedEvent(MatterServerJson.ReadNode(data.Clone()));
        }

        if (name.ValueEquals("node_added"u8))
        {
            return new MatterNodeAddedEvent(MatterServerJson.ReadNode(data.Clone()));
        }

        if (name.ValueEquals("node_removed"u8))
        {
            return new MatterNodeRemovedEvent(data.GetUInt64());
        }

        if (name.ValueEquals("endpoint_added"u8))
        {
            return new MatterEndpointAddedEvent(data.GetProperty("node_id").GetUInt64(), data.GetProperty("endpoint_id").GetUInt16());
        }

        if (name.ValueEquals("endpoint_removed"u8))
        {
            return new MatterEndpointRemovedEvent(data.GetProperty("node_id").GetUInt64(), data.GetProperty("endpoint_id").GetUInt16());
        }

        return name.ValueEquals("server_info_updated"u8) ? new MatterServerInfoChangedEvent(MatterServerJson.ReadServerInfo(data)) : null;
    }

    /// <summary>Reads an attribute path from its UTF-8 text without allocating, unless the text contains escapes.</summary>
    /// <exception cref="FormatException">The element is not a string with a valid attribute path.</exception>
    private static AttributePath ReadAttributePath(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            // The raw value includes the quotes and leaves escapes as they are; a path never needs one.
            var raw = JsonMarshal.GetRawUtf8Value(element);
            var text = raw[1..^1];
            if (text.IndexOf((byte)'\\') < 0
                ? AttributePath.TryParse(text, out var path)
                : AttributePath.TryParse(element.GetString(), out path))
            {
                return path;
            }
        }

        throw new FormatException($"'{element}' is not an attribute path of the form endpoint/cluster/attribute.");
    }
}
