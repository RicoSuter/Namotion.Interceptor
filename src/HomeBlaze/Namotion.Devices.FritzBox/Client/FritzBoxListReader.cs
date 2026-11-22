using System.Text.Json;
using System.Xml;
using Namotion.Devices.FritzBox.Parsing;
using Namotion.Devices.Upnp;

namespace Namotion.Devices.FritzBox.Client;

/// <summary>
/// Reads the lists of the router: hosts, mesh and filter profiles.
/// </summary>
internal static class FritzBoxListReader
{
    /// <summary>
    /// Reads the host list, which a poll requires. Throws <see cref="FritzBoxLoginException"/> when the router refuses
    /// it to this user, <see cref="FritzBoxFirmwareException"/> when it does not offer it, and any transport failure
    /// as is.
    /// </summary>
    internal static async Task<IReadOnlyList<HostEntry>> ReadHostsAsync(FritzBoxConnection connection, CancellationToken cancellationToken)
    {
        UpnpSoapResponse answer;
        try
        {
            answer = await connection.QueryAsync(Tr64Services.Hosts, "X_AVM-DE_GetHostListPath", null, cancellationToken);
        }
        catch (Exception exception) when (exception is FritzBoxServiceUnavailableException or UpnpFaultException { ErrorCode: 401 })
        {
            throw new FritzBoxFirmwareException(
                "The FRITZ!Box does not offer its host list (X_AVM-DE_GetHostListPath). Update FRITZ!OS.", exception);
        }
        catch (Exception exception) when (FritzBoxConnection.IsLoginRefusal(exception))
        {
            // The required list refused after a successful login means the password or permission changed since.
            throw new FritzBoxLoginException(exception);
        }

        var path = answer.GetString("NewX_AVM-DE_HostListPath")
            ?? throw new InvalidDataException("The FRITZ!Box did not return the path of its host list.");
        await using var content = await connection.DownloadAsync(path, FritzBoxConnection.MaximumListBytes, cancellationToken);
        return HostListParser.Parse(content);
    }

    /// <returns>The mesh topology, or null when the router does not offer the mesh list, or when its document is
    /// malformed, its path unsafe or its download fails with an HTTP status error.</returns>
    internal static async Task<MeshTopology?> ReadMeshAsync(FritzBoxConnection connection, CancellationToken cancellationToken)
    {
        var answer = await connection.ReadOrDefaultAsync(Tr64Services.Hosts, "X_AVM-DE_GetMeshListPath", null, cancellationToken);
        var path = answer?.GetString("NewX_AVM-DE_MeshListPath");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        // A malformed document, an unsafe path or an HTTP status error drops only the mesh; a connection failure,
        // a certificate problem, a timeout or cancellation fails the poll, so access points do not flicker for a cycle.
        try
        {
            await using var content = await connection.DownloadAsync(path, FritzBoxConnection.MaximumListBytes, cancellationToken);
            return MeshTopology.Build(MeshListParser.Parse(content));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or HttpRequestException { StatusCode: not null })
        {
            connection.LogUnavailable(Tr64Services.Hosts, "X_AVM-DE_GetMeshListPath", exception);
            return null;
        }
    }

    /// <returns>The profile names by ID, or null when the router does not offer the list, refuses it, or returns a
    /// malformed one; a refusal or a malformed list is logged once. Any other failure throws.</returns>
    internal static async Task<IReadOnlyDictionary<string, string>?> ReadFilterProfilesAsync(FritzBoxConnection connection, CancellationToken cancellationToken)
    {
        if (!connection.HasService(Tr64Services.HostFilter))
        {
            return null;
        }

        var answer = await connection.ReadOrDefaultAsync(Tr64Services.HostFilter, "GetFilterProfiles", null, cancellationToken);
        var list = answer?.GetString("NewFilterProfileList");
        if (string.IsNullOrEmpty(list))
        {
            return null;
        }

        try
        {
            return FilterProfileListParser.Parse(list);
        }
        catch (XmlException exception)
        {
            connection.LogUnavailable(Tr64Services.HostFilter, "GetFilterProfiles", exception);
            return null;
        }
    }
}
