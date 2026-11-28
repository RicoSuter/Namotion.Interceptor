using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Namotion.Matter.Server;

/// <summary>Reads matterjs-server messages. Elements kept beyond the message are cloned by the caller first.</summary>
internal static class MatterServerJson
{
    public static MatterServerInfo ReadServerInfo(JsonElement element) => new(
        element.GetProperty("fabric_id").GetUInt64(),
        element.GetProperty("compressed_fabric_id").GetUInt64(),
        element.GetProperty("schema_version").GetInt32(),
        element.GetProperty("min_supported_schema_version").GetInt32(),
        element.GetProperty("sdk_version").GetString() ?? "",
        element.GetProperty("wifi_credentials_set").GetBoolean(),
        element.GetProperty("thread_credentials_set").GetBoolean(),
        element.GetProperty("bluetooth_enabled").GetBoolean());

    public static MatterException ReadError(JsonElement response)
    {
        var errorCode = (MatterErrorCode)response.GetProperty("error_code").GetInt32();
        var details = response.TryGetProperty("details", out var detailsElement) ? detailsElement.GetString() : null;
        var reason = response.TryGetProperty("reason", out var reasonElement) ? ReadCommissioningFailureReason(reasonElement.GetString()) : null;
        return new MatterException(
            errorCode,
            details ?? $"The Matter server answered with error {errorCode}.",
            ReadOptionalInt32(response, "status"),
            ReadOptionalInt32(response, "cluster_status"),
            reason);
    }

    /// <summary>Reads a node from a cloned element, so its attribute values stay valid.</summary>
    public static MatterNode ReadNode(JsonElement clonedNode)
    {
        var attributes = new Dictionary<AttributePath, MatterValue>();
        foreach (var attribute in clonedNode.GetProperty("attributes").EnumerateObject())
        {
            if (AttributePath.TryParse(attribute.Name, out var path))
            {
                attributes[path] = new MatterValue(attribute.Value);
            }
        }

        var endpointParents = new Dictionary<ushort, ushort>();
        if (clonedNode.TryGetProperty("endpoint_parents", out var parents) && parents.ValueKind == JsonValueKind.Object)
        {
            foreach (var parent in parents.EnumerateObject())
            {
                endpointParents[ushort.Parse(parent.Name, NumberStyles.None, CultureInfo.InvariantCulture)] = parent.Value.GetUInt16();
            }
        }

        return new MatterNode(
            clonedNode.GetProperty("node_id").GetUInt64(),
            clonedNode.GetProperty("available").GetBoolean(),
            clonedNode.TryGetProperty("is_bridge", out var isBridge) && isBridge.GetBoolean(),
            ReadOptionalTimestamp(clonedNode, "date_commissioned"),
            ReadOptionalTimestamp(clonedNode, "last_interview"),
            attributes,
            endpointParents);
    }

    public static MatterControllerEvent? ReadEvent(string name, JsonElement data) => name switch
    {
        "node_added" => new MatterNodeAddedEvent(ReadNode(data.Clone())),
        "node_updated" => new MatterNodeUpdatedEvent(ReadNode(data.Clone())),
        "node_removed" => new MatterNodeRemovedEvent(data.GetUInt64()),
        "endpoint_added" => new MatterEndpointAddedEvent(data.GetProperty("node_id").GetUInt64(), data.GetProperty("endpoint_id").GetUInt16()),
        "endpoint_removed" => new MatterEndpointRemovedEvent(data.GetProperty("node_id").GetUInt64(), data.GetProperty("endpoint_id").GetUInt16()),
        "attribute_updated" => new MatterAttributeChangedEvent(
            data[0].GetUInt64(),
            AttributePath.Parse(data[1].GetString()),
            new MatterValue(data[2].Clone())),
        "node_event" => new MatterNodeEventReceivedEvent(
            data.GetProperty("node_id").GetUInt64(),
            new EventPath(
                data.GetProperty("endpoint_id").GetUInt16(),
                data.GetProperty("cluster_id").GetUInt32(),
                data.GetProperty("event_id").GetUInt32()),
            data.GetProperty("event_number").GetUInt64(),
            data.GetProperty("priority").GetByte(),
            data.GetProperty("timestamp").GetInt64(),
            (MatterEventTimestampType)data.GetProperty("timestamp_type").GetInt32(),
            new MatterValue(data.GetProperty("data").Clone())),
        "server_info_updated" => new MatterServerInfoChangedEvent(ReadServerInfo(data)),
        _ => null
    };

    /// <summary>Reads the nodes of a cloned <c>start_listening</c> result.</summary>
    public static IReadOnlyList<MatterNode> ReadNodes(JsonElement clonedNodes)
    {
        var nodes = new List<MatterNode>(clonedNodes.GetArrayLength());
        foreach (var node in clonedNodes.EnumerateArray())
        {
            nodes.Add(ReadNode(node));
        }

        return nodes;
    }

    /// <summary>Throws when a device answered a write with a status other than success; a group write has no statuses.</summary>
    public static void ThrowIfWriteFailed(JsonElement result, ulong nodeId, AttributePath path)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in result.EnumerateArray())
        {
            var status = entry.GetProperty("Status").GetInt32();
            if (status != 0)
            {
                throw new MatterException(
                    MatterErrorCode.Unknown,
                    string.Create(CultureInfo.InvariantCulture, $"Node {nodeId} rejected the write to {path} with status 0x{status:X2}."),
                    status,
                    ReadOptionalInt32(entry, "ClusterStatus"));
            }
        }
    }

    public static MatterCommissioningWindow ReadCommissioningWindow(JsonElement result) => new(
        result.GetProperty("setup_pin_code").GetUInt32(),
        result.GetProperty("setup_manual_code").GetString() ?? "",
        result.GetProperty("setup_qr_code").GetString() ?? "");

    public static IReadOnlyList<MatterOnboardingPayload> ReadOnboardingPayloads(JsonElement result)
    {
        if (result.GetProperty("type").GetString() == "manual")
        {
            return [ReadOnboardingPayload(result)];
        }

        var payloads = new List<MatterOnboardingPayload>();
        foreach (var payload in result.GetProperty("payloads").EnumerateArray())
        {
            payloads.Add(ReadOnboardingPayload(payload));
        }

        return payloads;
    }

    private static MatterOnboardingPayload ReadOnboardingPayload(JsonElement payload) => new(
        payload.GetProperty("setup_pin_code").GetUInt32(),
        payload.TryGetProperty("discriminator", out var discriminator) ? discriminator.GetUInt16() : null,
        payload.TryGetProperty("short_discriminator", out var shortDiscriminator) ? shortDiscriminator.GetByte() : null,
        payload.TryGetProperty("vendor_id", out var vendorId) ? vendorId.GetUInt16() : null,
        payload.TryGetProperty("product_id", out var productId) ? productId.GetUInt16() : null,
        payload.TryGetProperty("discovery_capabilities", out var capabilities) ? (MatterDiscoveryCapabilities)capabilities.GetInt32() : null);

    private static MatterCommissioningFailureReason? ReadCommissioningFailureReason(string? reason) => reason switch
    {
        null => null,
        "not-discovered" => MatterCommissioningFailureReason.NotDiscovered,
        "passcode-mismatch" => MatterCommissioningFailureReason.PasscodeMismatch,
        "invalid-payload" => MatterCommissioningFailureReason.InvalidPayload,
        "attestation-rejected" => MatterCommissioningFailureReason.AttestationRejected,
        "timeout" => MatterCommissioningFailureReason.Timeout,
        _ => MatterCommissioningFailureReason.Other
    };

    private static int? ReadOptionalInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static DateTimeOffset? ReadOptionalTimestamp(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
            ? timestamp
            : null;

    /// <summary>Throws when this client does not support the server's schema version.</summary>
    /// <exception cref="NotSupportedException">The schema version is not supported.</exception>
    public static void ThrowIfUnsupported(MatterServerInfo serverInfo)
    {
        if (serverInfo.SchemaVersion < MatterServerController.MinimumServerSchemaVersion)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The Matter server has schema version {serverInfo.SchemaVersion}, older than the required {MatterServerController.MinimumServerSchemaVersion}. Update the server."));
        }

        if (serverInfo.MinSupportedSchemaVersion > MatterServerController.ClientSchemaVersion)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The Matter server requires client schema version {serverInfo.MinSupportedSchemaVersion} or newer, this client implements {MatterServerController.ClientSchemaVersion}. Update the client."));
        }
    }

    /// <summary>Writes a request message.</summary>
    public static ReadOnlyMemory<byte> WriteRequest(string messageId, string command, Action<Utf8JsonWriter>? writeArguments)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("message_id", messageId);
        writer.WriteString("command", command);
        if (writeArguments is not null)
        {
            writer.WritePropertyName("args");
            writer.WriteStartObject();
            writeArguments(writer);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenMemory;
    }
}
