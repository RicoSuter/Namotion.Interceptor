using System.Globalization;
using System.Text.Json;
using Namotion.Devices.FritzBox.Model;
using Namotion.Devices.FritzBox.Parsing;
using Namotion.Devices.Upnp;

namespace Namotion.Devices.FritzBox.Client;

/// <summary>
/// Reads everything one poll needs from the router: the device information alone, then the rest in parallel.
/// </summary>
internal static class FritzBoxPoller
{
    private static readonly Task<UpnpSoapResponse?> NoAnswer = Task.FromResult<UpnpSoapResponse?>(null);
    private static readonly (string Name, string Value)[] OnlineMonitorArguments = [("NewSyncGroupIndex", "0")];

    /// <summary>
    /// Reads one snapshot. Optional values the router does not offer are null. Throws
    /// <see cref="FritzBoxLoginException"/> when the router refuses the login or the host list to this user,
    /// <see cref="FritzBoxFirmwareException"/> when it offers no host list, and any transport failure as is.
    /// </summary>
    internal static async Task<FritzBoxSnapshot> PollAsync(FritzBoxConnection connection, CancellationToken cancellationToken)
    {
        // Alone first: a password changed since the last poll then costs one failed login, not one per parallel read.
        var deviceInfo = await connection.CheckLoginAsync(cancellationToken);

        var userInterfaceTask = connection.ReadOrDefaultAsync(Tr64Services.UserInterface, "GetInfo", null, cancellationToken);
        var internetTask = ReadInternetAsync(connection, cancellationToken);
        var trafficTask = ReadTrafficAsync(connection, cancellationToken);
        var wlansTask = ReadWlansAsync(connection, cancellationToken);
        var hostsTask = ReadHostsAsync(connection, cancellationToken);
        var meshTask = ReadMeshAsync(connection, cancellationToken);
        await Task.WhenAll(userInterfaceTask, internetTask, trafficTask, wlansTask, hostsTask, meshTask);

        return new FritzBoxSnapshot(
            ToRouterInfo(deviceInfo),
            ToAvailableSoftwareUpdate(await userInterfaceTask),
            await internetTask,
            await trafficTask,
            await wlansTask,
            await hostsTask,
            await meshTask);
    }

    /// <returns>The first value of a comma-separated series of the online monitor, the most recent one.</returns>
    internal static long? GetMostRecentSample(string? samples)
    {
        if (string.IsNullOrEmpty(samples))
        {
            return null;
        }

        var end = samples.IndexOf(',');
        var first = end < 0 ? samples.AsSpan() : samples.AsSpan(0, end);
        return long.TryParse(first.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sample) ? sample : null;
    }

    /// <summary>
    /// Names a WLAN. Of several WLAN services the last is the guest WLAN; the others are radios, named by the band
    /// the router reports or else by their position.
    /// </summary>
    internal static (string Role, string? Band) ClassifyWlan(int index, int count, string? frequencyBand)
    {
        var reportedBand = GetReportedBand(frequencyBand);
        if (count > 1 && index == count - 1)
        {
            return (FritzBoxWlanRoles.Guest, reportedBand);
        }

        var band = reportedBand ?? GetPositionalBand(index);
        return (band ?? $"WLAN{index + 1}", band);
    }

    private static string? GetReportedBand(string? frequencyBand) => frequencyBand?.Trim() switch
    {
        "2400" => FritzBoxWlanRoles.Band24,
        "5000" => FritzBoxWlanRoles.Band5,
        "6000" => FritzBoxWlanRoles.Band6,
        _ => null
    };

    private static string? GetPositionalBand(int index) => index switch
    {
        0 => FritzBoxWlanRoles.Band24,
        1 => FritzBoxWlanRoles.Band5,
        2 => FritzBoxWlanRoles.Band6,
        _ => null
    };

    private static RouterInfo ToRouterInfo(UpnpSoapResponse answer) =>
        new(
            StringValues.NullIfBlank(answer.GetString("NewModelName")),
            StringValues.NullIfBlank(answer.GetString("NewSerialNumber")),
            StringValues.NullIfBlank(answer.GetString("NewHardwareVersion")),
            StringValues.NullIfBlank(answer.GetString("NewSoftwareVersion")),
            ToTimeSpan(answer.GetInt64("NewUpTime")));

    private static string? ToAvailableSoftwareUpdate(UpnpSoapResponse? answer) =>
        answer?.GetBoolean("NewUpgradeAvailable") == true ? StringValues.NullIfBlank(answer.GetString("NewX_AVM-DE_Version")) : null;

    private static async Task<InternetInfo?> ReadInternetAsync(FritzBoxConnection connection, CancellationToken cancellationToken)
    {
        var serviceType = connection.InternetServiceType;
        var linkTask = connection.ReadOrDefaultAsync(Tr64Services.WanCommonInterfaceConfig, "GetCommonLinkProperties", null, cancellationToken);
        var statusTask = ReadInternetServiceAnswerAsync(connection, serviceType, "GetStatusInfo", cancellationToken);
        var addressTask = ReadInternetServiceAnswerAsync(connection, serviceType, "GetExternalIPAddress", cancellationToken);
        var ipv6Task = connection.HasService(Tr64Services.AppSetup)
            ? connection.ReadOrDefaultAsync(Tr64Services.AppSetup, "GetAppRemoteInfo", null, cancellationToken)
            : NoAnswer;
        await Task.WhenAll(linkTask, statusTask, addressTask, ipv6Task);

        return ToInternetInfo(await linkTask, await statusTask, await addressTask, await ipv6Task);
    }

    private static Task<UpnpSoapResponse?> ReadInternetServiceAnswerAsync(
        FritzBoxConnection connection, string? serviceType, string action, CancellationToken cancellationToken) =>
        serviceType is null ? NoAnswer : connection.ReadOrDefaultAsync(serviceType, action, null, cancellationToken);

    private static InternetInfo? ToInternetInfo(
        UpnpSoapResponse? link, UpnpSoapResponse? status, UpnpSoapResponse? address, UpnpSoapResponse? ipv6)
    {
        if (IsEntirelyUnanswered(link, status, address, ipv6))
        {
            return null;
        }

        return new InternetInfo(
            GetIsConnected(status),
            StringValues.NullIfBlank(address?.GetString("NewExternalIPAddress")),
            StringValues.NullIfBlank(ipv6?.GetString("NewExternalIPv6Address")),
            ToTimeSpan(status?.GetInt64("NewUptime")),
            ToMegabitsPerSecond(link?.GetInt64("NewLayer1UpstreamMaxBitRate")),
            ToMegabitsPerSecond(link?.GetInt64("NewLayer1DownstreamMaxBitRate")));
    }

    private static bool IsEntirelyUnanswered(
        UpnpSoapResponse? link, UpnpSoapResponse? status, UpnpSoapResponse? address, UpnpSoapResponse? ipv6) =>
        link is null && status is null && address is null && ipv6 is null;

    private static bool? GetIsConnected(UpnpSoapResponse? status)
    {
        var connectionStatus = status?.GetString("NewConnectionStatus");
        return connectionStatus is null ? null : connectionStatus == "Connected";
    }

    private static async Task<TrafficInfo?> ReadTrafficAsync(FritzBoxConnection connection, CancellationToken cancellationToken)
    {
        var answer = await connection.ReadOrDefaultAsync(
            Tr64Services.WanCommonInterfaceConfig, "X_AVM-DE_GetOnlineMonitor", OnlineMonitorArguments, cancellationToken);
        if (answer is null)
        {
            return null;
        }

        // The online monitor reports bytes per second.
        return new TrafficInfo(
            GetMostRecentSample(answer.GetString("Newus_current_bps")) / 1000m,
            GetMostRecentSample(answer.GetString("Newds_current_bps")) / 1000m);
    }

    private static async Task<IReadOnlyList<WlanInfo>> ReadWlansAsync(FritzBoxConnection connection, CancellationToken cancellationToken)
    {
        var serviceTypes = connection.Description.WlanServiceTypes;
        var count = serviceTypes.Count;
        var tasks = new Task<UpnpSoapResponse?>[count * 2];
        for (var index = 0; index < count; index++)
        {
            tasks[index] = connection.ReadOrDefaultAsync(serviceTypes[index], "GetInfo", null, cancellationToken);
            tasks[count + index] = connection.ReadOrDefaultAsync(serviceTypes[index], "GetTotalAssociations", null, cancellationToken);
        }

        await Task.WhenAll(tasks);

        var wlans = new List<WlanInfo>(count);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var info = await tasks[index];
            var associations = await tasks[count + index];
            var (role, band) = ClassifyWlan(index, count, info?.GetString("NewX_AVM-DE_FrequencyBand"));
            if (!roles.Add(role))
            {
                role = $"{role}-{index + 1}";
                roles.Add(role);
            }

            wlans.Add(new WlanInfo(
                role,
                serviceTypes[index],
                info?.GetBoolean("NewEnable"),
                StringValues.NullIfBlank(info?.GetString("NewSSID")),
                band,
                info?.GetInt32("NewChannel"),
                associations?.GetInt32("NewTotalAssociations")));
        }

        return wlans;
    }

    private static async Task<IReadOnlyList<HostEntry>> ReadHostsAsync(FritzBoxConnection connection, CancellationToken cancellationToken)
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

    private static async Task<MeshTopology?> ReadMeshAsync(FritzBoxConnection connection, CancellationToken cancellationToken)
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

    private static TimeSpan? ToTimeSpan(long? seconds) => seconds is { } value ? TimeSpan.FromSeconds(value) : null;

    private static decimal? ToMegabitsPerSecond(long? bitsPerSecond) => bitsPerSecond / 1_000_000m;
}
