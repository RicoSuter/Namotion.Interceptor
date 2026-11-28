using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Namotion.Matter.Server;

/// <summary>Reads and writes matterjs-server messages. Elements kept beyond the message are cloned by the caller first.</summary>
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

    /// <summary>Reads an attribute path from its UTF-8 text without allocating; a path with escapes is malformed.</summary>
    /// <exception cref="FormatException">The element is not a string with a valid attribute path.</exception>
    public static AttributePath ReadAttributePath(JsonElement element)
    {
        // The raw value includes the quotes.
        if (element.ValueKind == JsonValueKind.String && AttributePath.TryParse(JsonMarshal.GetRawUtf8Value(element)[1..^1], out var path))
        {
            return path;
        }

        throw new FormatException($"'{element}' is not an attribute path of the form endpoint/cluster/attribute.");
    }

    /// <summary>Reads a node from a cloned element, so its attribute values stay valid.</summary>
    public static MatterNode ReadNode(JsonElement clonedNode)
    {
        var attributes = new Dictionary<AttributePath, MatterValue>();
        foreach (var attribute in clonedNode.GetProperty("attributes").EnumerateObject())
        {
            if (AttributePath.TryParse(JsonMarshal.GetRawUtf8PropertyName(attribute), out var path))
            {
                attributes[path] = new MatterValue(attribute.Value);
            }
        }

        var endpointParents = new Dictionary<ushort, ushort>();
        if (clonedNode.TryGetProperty("endpoint_parents", out var parents) && parents.ValueKind == JsonValueKind.Object)
        {
            foreach (var parent in parents.EnumerateObject())
            {
                if (ushort.TryParse(parent.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var endpoint) &&
                    parent.Value.ValueKind == JsonValueKind.Number && parent.Value.TryGetUInt16(out var parentEndpoint))
                {
                    endpointParents[endpoint] = parentEndpoint;
                }
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

    public static int? ReadOptionalInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static DateTimeOffset? ReadOptionalTimestamp(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
            ? timestamp
            : null;

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
