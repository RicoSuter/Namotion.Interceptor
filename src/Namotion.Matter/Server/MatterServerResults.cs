using System.Globalization;
using System.Text.Json;

namespace Namotion.Matter.Server;

/// <summary>
/// Reads and checks the server's information and request results; one of an unexpected shape fails as
/// <see cref="MatterException"/> with the read error as inner exception.
/// </summary>
internal static class MatterServerResults
{
    /// <summary>The oldest server schema version this client works with.</summary>
    internal const int MinimumServerSchemaVersion = 14;

    /// <summary>The schema version this client implements.</summary>
    internal const int ClientSchemaVersion = 15;

    /// <summary>Reads the server information message.</summary>
    /// <exception cref="MatterException">The message could not be read.</exception>
    public static MatterServerInfo ReadServerInfo(ReadOnlyMemory<byte> message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            return MatterServerJson.ReadServerInfo(document.RootElement);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw new MatterException("The Matter server's information could not be read.", exception);
        }
    }

    /// <summary>Reads the cloned result of <c>start_listening</c>.</summary>
    public static IReadOnlyList<MatterNode> ReadNodes(JsonElement result) => Read("start_listening", result, MatterServerJson.ReadNodes);

    /// <summary>Reads the cloned result of <c>commission_with_code</c>.</summary>
    public static MatterNode ReadCommissionedNode(JsonElement result) => Read("commission_with_code", result, MatterServerJson.ReadNode);

    /// <summary>Reads the result of <c>parse_onboarding_payload</c>.</summary>
    public static IReadOnlyList<MatterOnboardingPayload> ReadOnboardingPayloads(JsonElement result) =>
        Read("parse_onboarding_payload", result, MatterServerJson.ReadOnboardingPayloads);

    /// <summary>Reads the result of <c>open_commissioning_window</c>.</summary>
    public static MatterCommissioningWindow ReadCommissioningWindow(JsonElement result) =>
        Read("open_commissioning_window", result, MatterServerJson.ReadCommissioningWindow);

    /// <summary>Gets whether an exception is one that parsing invalid JSON, or reading an element of an unexpected shape or range, throws.</summary>
    public static bool IsReadFailure(Exception exception) => exception switch
    {
        ObjectDisposedException => false,
        InvalidOperationException => true,
        JsonException => true,
        KeyNotFoundException => true,
        FormatException => true,
        OverflowException => true,
        IndexOutOfRangeException => true,
        _ => false
    };

    /// <summary>Throws when this client does not support the server's schema version.</summary>
    /// <exception cref="NotSupportedException">The schema version is not supported.</exception>
    public static void ThrowIfUnsupported(MatterServerInfo serverInfo)
    {
        if (serverInfo.SchemaVersion < MinimumServerSchemaVersion)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The Matter server has schema version {serverInfo.SchemaVersion}, older than the required {MinimumServerSchemaVersion}. Update the server."));
        }

        if (serverInfo.MinSupportedSchemaVersion > ClientSchemaVersion)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The Matter server requires client schema version {serverInfo.MinSupportedSchemaVersion} or newer, this client implements {ClientSchemaVersion}. Update the client."));
        }
    }

    /// <summary>
    /// Throws when a device answered a write with a status other than success; a result that is not a status list has
    /// no statuses.
    /// </summary>
    /// <exception cref="MatterException">The device rejected the write, or the result could not be read.</exception>
    public static void ThrowIfWriteFailed(JsonElement result, ulong nodeId, AttributePath path)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in result.EnumerateArray())
        {
            var status = Read("write_attribute", entry, static entry => entry.GetProperty("Status").GetInt32());
            if (status != 0)
            {
                throw new MatterException(
                    MatterErrorCode.Unknown,
                    string.Create(CultureInfo.InvariantCulture, $"Node {nodeId} rejected the write to {path} with status 0x{status:X2}."),
                    status,
                    MatterServerJson.ReadOptionalInt32(entry, "ClusterStatus"));
            }
        }
    }

    /// <summary>Reads a request's result.</summary>
    /// <exception cref="MatterException">The result could not be read.</exception>
    private static T Read<T>(string command, JsonElement result, Func<JsonElement, T> read)
    {
        try
        {
            return read(result);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw new MatterException($"The Matter server's answer to '{command}' could not be read.", exception);
        }
    }
}
