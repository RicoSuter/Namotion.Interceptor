using System.ComponentModel;
using System.Globalization;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using HomeBlaze.Abstractions.Sensors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Nuki.Client;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Nuki;

/// <summary>
/// A Nuki bridge with its smart locks and Openers, read through the HTTP API of the bridge.
/// </summary>
[Category("Security")]
[Description("Nuki bridge with its smart locks and Openers")]
[DisplayName("Nuki bridge")]
[SubjectIcon("Hub")]
[InterceptorSubject]
public partial class NukiBridge : BackgroundService,
    IConfigurable,
    IMonitoredService,
    IHubDevice,
    IConnectionState,
    IPowerSensor,
    INetworkHost,
    INetworkAdapter,
    ISoftwareState,
    IUptimeState,
    ILastUpdatedProvider,
    ITitleProvider,
    IIconProvider
{
    /// <summary>The default <see cref="PollingInterval"/>. It stays below 30 seconds, the time a ring shows at the bridge.</summary>
    public static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(10);

    /// <summary>The default <see cref="RetryInterval"/>.</summary>
    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The shortest <see cref="PollingInterval"/> and <see cref="RetryInterval"/>, in seconds; a shorter one is raised to it.
    /// </summary>
    public const int MinimumIntervalSeconds = 1;

    /// <summary>
    /// The longest <see cref="PollingInterval"/> and <see cref="RetryInterval"/>, in seconds; a longer one is lowered to it.
    /// </summary>
    public const int MaximumIntervalSeconds = 3600;

    internal const int MaxConsecutivePollFailures = 3;

    // /info changes slowly (uptime, versions), so it is read every this many polls only.
    internal const int InfoPollDivider = 6;

    private readonly IHttpClientFactory _httpClientFactory;

    public NukiBridge(IHttpClientFactory httpClientFactory, ILogger<NukiBridge> logger)
    {
        _httpClientFactory = httpClientFactory;
        Logger = logger;

        HostAddress = null;
        Token = null;
        TokenMode = NukiTokenMode.Encrypted;
        PollingInterval = DefaultPollingInterval;
        RetryInterval = DefaultRetryInterval;

        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        LastUpdated = null;
        BridgeType = null;
        HardwareId = null;
        ServerId = null;
        IsServerConnected = null;
        SoftwareVersion = null;
        WifiFirmwareVersion = null;
        Uptime = null;
        Locks = new Dictionary<string, NukiSmartLock>();
        Openers = new Dictionary<string, NukiOpener>();
    }

    internal ILogger<NukiBridge> Logger { get; }

    /// <summary>The delay before a request that the bridge answered with 503 is sent again.</summary>
    internal TimeSpan BusyRetryDelay { get; set; } = NukiBridgeClient.DefaultBusyRetryDelay;

    /// <summary>The IP address or host name of the bridge, optionally with a port (8080 by default).</summary>
    [Configuration]
    public partial string? HostAddress { get; set; }

    /// <summary>The API token of the bridge.</summary>
    [Configuration(IsSecret = true)]
    public partial string? Token { get; set; }

    /// <summary>How requests carry the token.</summary>
    [Configuration]
    public partial NukiTokenMode TokenMode { get; set; }

    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    [Configuration]
    public partial TimeSpan RetryInterval { get; set; }

    [State(IsDiscrete = true)]
    public partial bool IsConnected { get; internal set; }

    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    [State(Position = 950)]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [State(Position = 401)]
    [Category("Device")]
    public partial NukiBridgeType? BridgeType { get; internal set; }

    [State(Position = 402)]
    [Category("Device")]
    public partial long? HardwareId { get; internal set; }

    [State(Position = 403)]
    [Category("Device")]
    public partial long? ServerId { get; internal set; }

    /// <summary>Whether the bridge is connected to the Nuki servers.</summary>
    [State(Position = 404)]
    [Category("Device")]
    public partial bool? IsServerConnected { get; internal set; }

    public partial string? SoftwareVersion { get; internal set; }

    [State(Position = 852)]
    [Category("Device")]
    public partial string? WifiFirmwareVersion { get; internal set; }

    [Category("Device")]
    public partial TimeSpan? Uptime { get; internal set; }

    /// <summary>The smart locks and Smart Doors, keyed by their Nuki ID.</summary>
    [State]
    public partial Dictionary<string, NukiSmartLock> Locks { get; internal set; }

    /// <summary>The Openers, keyed by their Nuki ID.</summary>
    [State]
    public partial Dictionary<string, NukiOpener> Openers { get; internal set; }

    [Derived]
    public string? AvailableSoftwareUpdate => null;

    [Derived]
    public decimal? Power => IsConnected ? 1m : null;

    [Derived]
    public decimal? TotalConsumedEnergy => null;

    [Derived]
    public string? IpAddress => string.IsNullOrWhiteSpace(HostAddress) ? null : GetHost(NukiBridgeClient.NormalizeHostAddress(HostAddress));

    public string? MacAddress => null;

    public string? SubnetMask => null;

    public string? Gateway => null;

    public bool? IsWireless => null;

    public int? SignalStrength => null;

    [Derived]
    public string? Title => HardwareId is { } hardwareId
        ? "Nuki Bridge (" + hardwareId.ToString(CultureInfo.InvariantCulture) + ")"
        : "Nuki Bridge (" + (HostAddress ?? "?") + ")";

    [Derived]
    public string IconName => IsConnected ? "Hub" : "HubOutlined";

    [Derived]
    public string? IconColor => IsConnected ? "Success" : null;

    // A normalized address always carries a port.
    private static string GetHost(string normalizedHostAddress) =>
        normalizedHostAddress.AsSpan(0, normalizedHostAddress.IndexOf(':')).ToString();
}
