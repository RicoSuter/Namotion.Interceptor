using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Hyundai.Client;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Hyundai;

/// <summary>
/// A Hyundai Bluelink account (Europe) and its electric vehicles. Polling reads only the status stored on Hyundai's
/// servers; only the Refresh operation and the commands reach a vehicle. See the Bluelink device documentation.
/// </summary>
[Category("Vehicles")]
[Description("Hyundai Bluelink account (Europe) with its electric vehicles, read from the status stored on Hyundai's servers")]
[DisplayName("Hyundai Bluelink account")]
[SubjectIcon("DirectionsCar")]
[InterceptorSubject]
public partial class BluelinkAccount : BackgroundService,
    IConfigurable,
    IMonitoredService,
    IAuthenticatedSubject,
    ITitleProvider,
    IIconProvider,
    IBluelinkVehicleCommands
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly BluelinkAuthClient _authClient;
    private readonly BluelinkVehicleApiClient _apiClient;
    private readonly BluelinkRequestBudget _budget;

    // Serializes polls, refreshes and commands: the token manager, the control token, the pause and the vehicle
    // dictionary are only touched while it is held.
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Budget changes on different threads can be reported out of order. Reading the current count under this lock
    // makes the last write to RequestsToday the current count.
    private readonly Lock _requestsTodayLock = new();

    public BluelinkAccount(TimeProvider timeProvider, ILoggerFactory loggerFactory)
        : this(timeProvider, loggerFactory, BluelinkHttp.CreateClient)
    {
    }

    internal BluelinkAccount(TimeProvider timeProvider, ILoggerFactory loggerFactory, Func<HttpClient> createHttpClient)
    {
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<BluelinkAccount>();
        _httpClient = createHttpClient();
        _authClient = new BluelinkAuthClient(_httpClient, timeProvider);
        _budget = new BluelinkRequestBudget(timeProvider, () => DailyRequestLimit);
        _budget.CountChanged += UpdateRequestsToday;
        _apiClient = new BluelinkVehicleApiClient(_httpClient, timeProvider, _budget);

        Name = string.Empty;
        Email = string.Empty;
        Password = string.Empty;
        Pin = string.Empty;
        Tokens = string.Empty;
        DeviceId = string.Empty;
        StatusPollingInterval = TimeSpan.FromMinutes(30);
        DailyRequestLimit = 150;
        MinimumAuxiliaryBatteryLevel = 0.65m;
        ClimateDuration = TimeSpan.FromMinutes(10);
        RetryInterval = TimeSpan.FromMinutes(5);

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        IsAuthenticated = false;
        IsReauthenticationRequired = false;
        AuthenticatedUser = null;
        AuthenticatedAt = null;
        RequestsToday = 0;
        Vehicles = new Dictionary<string, BluelinkVehicle>();
    }

    // Configuration

    /// <summary>The display name; empty to show "Bluelink: " with the email.</summary>
    [Configuration]
    public partial string Name { get; set; }

    /// <summary>The email of the Bluelink account.</summary>
    [Configuration]
    public partial string Email { get; set; }

    /// <summary>The password, used for one automatic sign in when Hyundai rejects a refresh; empty disables it.</summary>
    [Configuration(IsSecret = true)]
    public partial string Password { get; set; }

    /// <summary>The PIN of the Bluelink app; only charging and climate commands of CCS2 vehicles use it.</summary>
    [Configuration(IsSecret = true)]
    public partial string Pin { get; set; }

    /// <summary>The token set. Every refresh rotates it, and the rotated set is written back before it is used.</summary>
    [Configuration(IsSecret = true)]
    public partial string Tokens { get; set; }

    /// <summary>The device ID registered at setup.</summary>
    [Configuration]
    public partial string DeviceId { get; set; }

    /// <summary>How often the stored status of every vehicle is read; at least 15 minutes.</summary>
    [Configuration]
    public partial TimeSpan StatusPollingInterval { get; set; }

    /// <summary>The vehicle API calls allowed per local day, below Hyundai's undocumented limit of about 200.</summary>
    [Configuration]
    public partial int DailyRequestLimit { get; set; }

    /// <summary>The 12 V battery level (0..1) below which Refresh is refused.</summary>
    [Configuration]
    public partial decimal MinimumAuxiliaryBatteryLevel { get; set; }

    /// <summary>How long a started climate runs.</summary>
    [Configuration]
    public partial TimeSpan ClimateDuration { get; set; }

    /// <summary>How long to wait after a failed poll; at least one minute.</summary>
    [Configuration]
    public partial TimeSpan RetryInterval { get; set; }

    // State

    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    [State(IsDiscrete = true)]
    public partial bool IsAuthenticated { get; internal set; }

    /// <summary>Whether Hyundai rejected the tokens and the stored password; polling waits until the credentials are replaced.</summary>
    [State(IsDiscrete = true)]
    public partial bool IsReauthenticationRequired { get; internal set; }

    [State]
    public partial string? AuthenticatedUser { get; internal set; }

    [State]
    public partial DateTimeOffset? AuthenticatedAt { get; internal set; }

    /// <summary>The vehicle API calls of the current local day; resets at midnight and with a restart.</summary>
    [State]
    public partial int RequestsToday { get; internal set; }

    /// <summary>The electric vehicles of the account, keyed by VIN.</summary>
    [State]
    public partial Dictionary<string, BluelinkVehicle> Vehicles { get; internal set; }

    [Derived]
    public string? Title
    {
        get
        {
            if (!string.IsNullOrEmpty(Name))
            {
                return Name;
            }

            return !string.IsNullOrEmpty(Email) ? $"Bluelink: {Email}" : "Bluelink";
        }
    }

    [Derived]
    public string IconName => "DirectionsCar";

    [Derived]
    public string? IconColor => Status switch
    {
        ServiceStatus.Running when IsAuthenticated => "Success",
        ServiceStatus.Error => "Error",
        _ => null
    };

    private void UpdateRequestsToday()
    {
        lock (_requestsTodayLock)
        {
            RequestsToday = _budget.Count;
        }
    }

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        SignalConfigurationChanged();
        return Task.CompletedTask;
    }

    /// <summary>Stops polling and waits until a refresh or command in progress has persisted rotated tokens.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            // A command running on another thread may have rotated the tokens; disposing the client afterwards would lose them.
            await _lock.WaitAsync(cancellationToken);
            _lock.Release();
        }
        catch (OperationCanceledException)
        {
            // The host gave up waiting.
        }
    }

    public override void Dispose()
    {
        // _lock is not disposed: it never creates a wait handle, and a command still running releases it afterwards.
        base.Dispose();
        _httpClient.Dispose();
    }
}
