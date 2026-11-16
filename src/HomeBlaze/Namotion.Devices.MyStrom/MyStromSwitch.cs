using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using HomeBlaze.Abstractions.Sensors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.MyStrom.Model;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.MyStrom;

[Category("Switches")]
[Description("myStrom WiFi Switch with power metering, temperature sensing, and relay control")]
[DisplayName("myStrom switch")]
[SubjectIcon("Power")]
[InterceptorSubject]
public partial class MyStromSwitch :
    IConfigurable,
    IMonitoredService,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider,
    IConnectionState,
    IPowerRelay,
    IPowerMeter,
    ITemperatureSensor,
    INetworkAdapter,
    INetworkHost,
    IUptimeState,
    ISubjectHostedServiceFactory
{
    // Waited on by the poller. A subject nobody activated has no waiter.
    internal readonly SemaphoreSlim ConfigurationChanged = new(0, 1);

    private MyStromPoller? _activeService;

    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial string? HostAddress { get; set; }

    [Configuration]
    public partial bool AllowTurnOff { get; set; }

    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    [Configuration]
    public partial TimeSpan RetryInterval { get; set; }

    [State(IsDiscrete = true)]
    public partial bool IsConnected { get; internal set; }

    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    // Tracked, so that the derived properties that read it change when it is fetched or reset.
    internal partial MyStromSwitchInformation? Information { get; set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    [State(Position = 950)]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [State(IsDiscrete = true)]
    public partial bool? IsOn { get; internal set; }

    [State(Unit = StateUnit.Watt)]
    public partial decimal? MeasuredPower { get; internal set; }

    [State(Unit = StateUnit.WattHour, IsCumulative = true)]
    public partial decimal? TotalImportedEnergy { get; internal set; }

    [Derived]
    public decimal? TotalExportedEnergy => null;

    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; internal set; }

    [Category("Device")]
    public partial TimeSpan? Uptime { get; internal set; }

    [Derived]
    public string? Title => string.IsNullOrEmpty(Name) ? HostAddress : Name;

    [Derived]
    public string IconName => "Power";

    [Derived]
    public string? IconColor => IsOn switch
    {
        true => "Success",
        false => "Error",
        _ => null
    };

    [Derived]
    [State]
    public string? MacAddress => Information?.Mac;

    [Derived]
    [State]
    public string? IpAddress => Information?.Ip;

    [Derived]
    [State]
    public string? SubnetMask => Information?.Mask;

    [Derived]
    [State]
    public string? Gateway => Information?.Gateway;

    [Derived]
    [State(Position = 401)]
    [Category("Device")]
    public string? DeviceType => Information?.Type;

    [Derived]
    [State(Position = 402)]
    [Category("Device")]
    public string? FirmwareVersion => Information?.Version;

    public bool? IsWireless => true;
    public int? SignalStrength => null;

    [Derived]
    [PropertyAttribute("TurnOn", KnownAttributes.IsEnabled)]
    public bool TurnOn_IsEnabled => IsConnected && IsOn != true;

    [Derived]
    [PropertyAttribute("TurnOff", KnownAttributes.IsEnabled)]
    public bool TurnOff_IsEnabled => IsConnected && IsOn == true && AllowTurnOff;

    public MyStromSwitch()
    {
        Name = string.Empty;
        HostAddress = null;
        AllowTurnOff = true;
        PollingInterval = TimeSpan.FromSeconds(15);
        RetryInterval = TimeSpan.FromSeconds(30);

        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        LastUpdated = null;
        IsOn = null;
        MeasuredPower = null;
        TotalImportedEnergy = null;
        Temperature = null;
        Uptime = null;
    }

    [Operation(Title = "Turn On", Icon = "PowerSettingsNew", Position = 1)]
    public Task TurnOnAsync(CancellationToken cancellationToken)
        => GetActiveService().SetRelayAsync(true, cancellationToken);

    [Operation(Title = "Turn Off", Icon = "PowerOff", Position = 2)]
    public Task TurnOffAsync(CancellationToken cancellationToken)
        => AllowTurnOff ? GetActiveService().SetRelayAsync(false, cancellationToken) : Task.CompletedTask;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        try { ConfigurationChanged.Release(); }
        catch (SemaphoreFullException) { }

        return Task.CompletedTask;
    }

    IHostedService ISubjectHostedServiceFactory.CreateHostedService(IServiceProvider serviceProvider)
        => new MyStromPoller(this,
            serviceProvider.GetService<IHttpClientFactory>(),
            serviceProvider.GetService<ILogger<MyStromPoller>>());

    internal void SetActiveService(MyStromPoller service) => Volatile.Write(ref _activeService, service);

    // Compare and exchange, so a poller that finishes after its successor started does not clear it.
    internal void ClearActiveService(MyStromPoller service) => Interlocked.CompareExchange(ref _activeService, null, service);

    private MyStromPoller GetActiveService()
        => Volatile.Read(ref _activeService)
            ?? throw new InvalidOperationException(
                "The switch is not running. Start it, or register it with AddMyStromSwitch, before invoking an operation.");
}
