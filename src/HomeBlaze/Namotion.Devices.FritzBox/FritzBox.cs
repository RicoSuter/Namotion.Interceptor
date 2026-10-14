using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.FritzBox.Client;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.FritzBox;

/// <summary>
/// An AVM FRITZ!Box router reached over its local TR-064 interface: its network clients with presence and access
/// point, its mesh access points, internet connection, traffic rates and WLANs.
/// </summary>
[Category("Network")]
[Description("AVM FRITZ!Box router with its network clients, mesh access points, internet connection and WLANs")]
[DisplayName("FRITZ!Box")]
[SubjectIcon("Router")]
[InterceptorSubject]
public partial class FritzBox : BackgroundService,
    IConfigurable,
    IMonitoredService,
    IConnectionState,
    IHubDevice,
    IDeviceInfo,
    ISoftwareState,
    IUptimeState,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    /// <summary>
    /// The default <see cref="PollingInterval"/>, in seconds.
    /// </summary>
    public const int DefaultPollingIntervalSeconds = 15;

    /// <summary>
    /// The default <see cref="RetryInterval"/>, in seconds.
    /// </summary>
    public const int DefaultRetryIntervalSeconds = 30;

    /// <summary>
    /// The shortest <see cref="PollingInterval"/> and <see cref="RetryInterval"/>, in seconds; a shorter one is raised to it.
    /// </summary>
    public const int MinimumIntervalSeconds = 5;

    /// <summary>
    /// The longest <see cref="PollingInterval"/> and <see cref="RetryInterval"/>, in seconds; a longer one is lowered to it.
    /// </summary>
    public const int MaximumIntervalSeconds = 3600;

    private readonly ILogger<FritzBox> _logger;
    private readonly SemaphoreSlim _configurationChanged = new(0, 1);

    public FritzBox(ILogger<FritzBox> logger)
    {
        _logger = logger;

        Name = "FRITZ!Box";
        HostAddress = null;
        Username = null;
        Password = null;
        CertificatePin = null;
        PollingInterval = TimeSpan.FromSeconds(DefaultPollingIntervalSeconds);
        RetryInterval = TimeSpan.FromSeconds(DefaultRetryIntervalSeconds);

        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        LastUpdated = null;
        Model = null;
        SerialNumber = null;
        HardwareRevision = null;
        SoftwareVersion = null;
        AvailableSoftwareUpdate = null;
        Uptime = null;
        IsInternetConnected = null;
        ExternalIpAddress = null;
        ExternalIpv6Address = null;
        InternetConnectionUptime = null;
        MaximumUploadRate = null;
        MaximumDownloadRate = null;
        UploadRate = null;
        DownloadRate = null;
        PresentedCertificatePin = null;
        Clients = new Dictionary<string, FritzBoxClient>(StringComparer.Ordinal);
        AccessPoints = new Dictionary<string, FritzBoxAccessPoint>(StringComparer.Ordinal);
        Wlans = new Dictionary<string, FritzBoxWlan>(StringComparer.Ordinal);
    }

    [Configuration]
    public partial string Name { get; set; }

    /// <summary>
    /// The IP address or host name of the router. Empty stops the subject.
    /// </summary>
    [Configuration]
    public partial string? HostAddress { get; set; }

    /// <summary>
    /// A FRITZ!Box user with the "FRITZ!Box settings" permission.
    /// </summary>
    [Configuration]
    public partial string? Username { get; set; }

    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    /// <summary>
    /// The Base64 SHA-256 of the trusted certificate's public key. Empty trusts the key the router presents next
    /// and stores it here.
    /// </summary>
    [Configuration]
    public partial string? CertificatePin { get; set; }

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

    [Derived]
    public string? Manufacturer => "AVM";

    public partial string? Model { get; internal set; }

    public string? ProductCode => null;

    public partial string? SerialNumber { get; internal set; }

    public partial string? HardwareRevision { get; internal set; }

    public partial string? SoftwareVersion { get; internal set; }

    public partial string? AvailableSoftwareUpdate { get; internal set; }

    public partial TimeSpan? Uptime { get; internal set; }

    [State(Position = 110, IsDiscrete = true)]
    public partial bool? IsInternetConnected { get; internal set; }

    [State(Position = 111)]
    public partial string? ExternalIpAddress { get; internal set; }

    [State(Position = 112)]
    public partial string? ExternalIpv6Address { get; internal set; }

    [State(Position = 113)]
    public partial TimeSpan? InternetConnectionUptime { get; internal set; }

    [State(Position = 120, Unit = StateUnit.MegabitPerSecond)]
    public partial decimal? MaximumUploadRate { get; internal set; }

    [State(Position = 121, Unit = StateUnit.MegabitPerSecond)]
    public partial decimal? MaximumDownloadRate { get; internal set; }

    [State(Position = 122, Unit = StateUnit.KilobytePerSecond)]
    public partial decimal? UploadRate { get; internal set; }

    [State(Position = 123, Unit = StateUnit.KilobytePerSecond)]
    public partial decimal? DownloadRate { get; internal set; }

    /// <summary>
    /// Every device the router lists, keyed by MAC address. Owns the client subjects.
    /// </summary>
    [State]
    public partial Dictionary<string, FritzBoxClient> Clients { get; internal set; }

    /// <summary>
    /// The router itself and every meshed access point, keyed by MAC address.
    /// </summary>
    [State]
    public partial Dictionary<string, FritzBoxAccessPoint> AccessPoints { get; internal set; }

    /// <summary>
    /// The WLANs, keyed by <see cref="FritzBoxWlanRoles"/>.
    /// </summary>
    [State]
    public partial Dictionary<string, FritzBoxWlan> Wlans { get; internal set; }

    /// <summary>
    /// The key the router presents while it is not the trusted one; accepting it in the edit dialog trusts it.
    /// </summary>
    [State(Position = 940)]
    public partial string? PresentedCertificatePin { get; internal set; }

    [Derived]
    [State(Position = 130)]
    public int ClientCount => Clients.Count;

    [Derived]
    [State(Position = 131)]
    public int PresentClientCount
    {
        get
        {
            var count = 0;
            foreach (var client in Clients.Values)
            {
                if (client.IsPresent == true)
                {
                    count++;
                }
            }

            return count;
        }
    }

    [Derived]
    public string? Title => string.IsNullOrWhiteSpace(Name) ? Model ?? "FRITZ!Box" : Name;

    [Derived]
    public string IconName => "Router";

    [Derived]
    public string? IconColor => IsConnected ? "Success" : null;

    internal int DescriptionPort { get; set; } = Tr64Description.DescriptionPort;

    internal TimeSpan MinimumInterval { get; set; } = TimeSpan.FromSeconds(MinimumIntervalSeconds);

    internal TimeSpan InitialLoginRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    internal TimeSpan MaximumLoginRetryDelay { get; set; } = TimeSpan.FromHours(2);

    private TimeSpan EffectivePollingInterval => GetEffectiveInterval(PollingInterval, DefaultPollingIntervalSeconds, MinimumInterval);

    private TimeSpan EffectiveRetryInterval => GetEffectiveInterval(RetryInterval, DefaultRetryIntervalSeconds, MinimumInterval);

    /// <summary>
    /// Returns the configured interval clamped to <paramref name="minimum"/> through
    /// <see cref="MaximumIntervalSeconds"/>, or the default when it is zero or negative.
    /// </summary>
    internal static TimeSpan GetEffectiveInterval(TimeSpan configured, int defaultSeconds, TimeSpan minimum)
    {
        if (configured <= TimeSpan.Zero)
        {
            return TimeSpan.FromSeconds(defaultSeconds);
        }

        if (configured < minimum)
        {
            return minimum;
        }

        var maximum = TimeSpan.FromSeconds(MaximumIntervalSeconds);
        return configured > maximum ? maximum : configured;
    }

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            _configurationChanged.Release();
        }
        catch (SemaphoreFullException)
        {
            // A change is already pending.
        }

        return Task.CompletedTask;
    }

    /// <returns>True when the configuration changed within <paramref name="timeout"/>.</returns>
    private async Task<bool> WaitForConfigurationChangeAsync(TimeSpan timeout, CancellationToken stoppingToken)
    {
        try
        {
            return await _configurationChanged.WaitAsync(timeout, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
