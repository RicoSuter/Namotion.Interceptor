using System.Globalization;
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
        var hostsTask = FritzBoxListReader.ReadHostsAsync(connection, cancellationToken);
        var meshTask = FritzBoxListReader.ReadMeshAsync(connection, cancellationToken);
        var filterProfilesTask = FritzBoxListReader.ReadFilterProfilesAsync(connection, cancellationToken);
        await Task.WhenAll(userInterfaceTask, internetTask, trafficTask, wlansTask, hostsTask, meshTask, filterProfilesTask);

        return new FritzBoxSnapshot(
            ToRouterInfo(deviceInfo),
            ToAvailableSoftwareUpdate(await userInterfaceTask),
            await internetTask,
            await trafficTask,
            await wlansTask,
            await hostsTask,
            await meshTask,
            await filterProfilesTask);
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
        var statusTask = ReadIfOfferedAsync(connection, serviceType, "GetStatusInfo", cancellationToken);
        var addressTask = ReadIfOfferedAsync(connection, serviceType, "GetExternalIPAddress", cancellationToken);
        var ipv6Task = ReadIfOfferedAsync(connection, Tr64Services.AppSetup, "GetAppRemoteInfo", cancellationToken);
        await Task.WhenAll(statusTask, addressTask, ipv6Task);

        return ToInternetInfo(await statusTask, await addressTask, await ipv6Task);
    }

    private static Task<UpnpSoapResponse?> ReadIfOfferedAsync(
        FritzBoxConnection connection, string? serviceType, string action, CancellationToken cancellationToken) =>
        serviceType is null || !connection.HasService(serviceType)
            ? NoAnswer
            : connection.ReadOrDefaultAsync(serviceType, action, null, cancellationToken);

    private static InternetInfo? ToInternetInfo(UpnpSoapResponse? status, UpnpSoapResponse? address, UpnpSoapResponse? ipv6)
    {
        if (status is null && address is null && ipv6 is null)
        {
            return null;
        }

        return new InternetInfo(
            GetIsConnected(status),
            StringValues.NullIfBlank(address?.GetString("NewExternalIPAddress")),
            StringValues.NullIfBlank(ipv6?.GetString("NewExternalIPv6Address")),
            ToTimeSpan(status?.GetInt64("NewUptime")));
    }

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

        // The online monitor reports bytes per second, also for the line speed. GetCommonLinkProperties is not used
        // for it: an Ethernet WAN reports its Layer1 rates there in kbit/s instead of bit/s.
        return new TrafficInfo(
            GetMostRecentSample(answer.GetString("Newus_current_bps")) / 1000m,
            GetMostRecentSample(answer.GetString("Newds_current_bps")) / 1000m,
            ToMegabitsPerSecond(answer.GetInt64("Newmax_us")),
            ToMegabitsPerSecond(answer.GetInt64("Newmax_ds")));
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

    private static TimeSpan? ToTimeSpan(long? seconds) => seconds is { } value ? TimeSpan.FromSeconds(value) : null;

    private static decimal? ToMegabitsPerSecond(long? bytesPerSecond) => bytesPerSecond * 8 / 1_000_000m;
}
