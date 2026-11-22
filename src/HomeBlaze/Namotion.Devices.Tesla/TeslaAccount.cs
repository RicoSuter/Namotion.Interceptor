using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Tesla.Client;
using Namotion.Devices.Tesla.Polling;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// A Tesla account and its vehicles. Reads the vehicles without waking them; see the Tesla device documentation.
/// </summary>
[Category("Vehicles")]
[Description("Tesla account with its vehicles, read through the Tesla Owner API without waking the vehicles")]
[DisplayName("Tesla account")]
[SubjectIcon("DirectionsCar")]
[InterceptorSubject]
public partial class TeslaAccount : BackgroundService,
    IConfigurable,
    IMonitoredService,
    IAuthenticatedSubject,
    ITitleProvider,
    IIconProvider
{
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly TeslaAuthClient _authClient;
    private readonly TeslaOwnerApiClient _apiClient;

    // Serializes a poll and the Wake Up operation: the polling policies, the token manager field and the vehicle
    // dictionary are only touched while it is held.
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    public TeslaAccount(TimeProvider timeProvider, ILoggerFactory loggerFactory)
        : this(timeProvider, loggerFactory, TeslaHttp.CreateClient)
    {
    }

    internal TeslaAccount(TimeProvider timeProvider, ILoggerFactory loggerFactory, Func<HttpClient> createHttpClient)
    {
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TeslaAccount>();
        _httpClient = createHttpClient();
        _authClient = new TeslaAuthClient(_httpClient);
        _apiClient = new TeslaOwnerApiClient(_httpClient, timeProvider);

        Name = string.Empty;
        Provider = TeslaProvider.OwnerApi;
        RefreshToken = string.Empty;
        StatePollingInterval = TimeSpan.FromMinutes(1);
        ActivePollingInterval = TimeSpan.FromMinutes(1);
        IdlePollingInterval = TimeSpan.FromMinutes(5);
        IdleTimeout = TimeSpan.FromMinutes(15);
        SleepWindow = TimeSpan.FromMinutes(21);
        RetryInterval = TimeSpan.FromMinutes(1);

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        IsAuthenticated = false;
        IsReauthenticationRequired = false;
        AuthenticatedUser = null;
        AuthenticatedAt = null;
        Vehicles = new Dictionary<string, TeslaVehicle>();
    }

    // Configuration

    /// <summary>The display name of the account; empty to show "Tesla: " with the email of the account.</summary>
    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial TeslaProvider Provider { get; set; }

    /// <summary>The refresh token. Tesla rotates it with every refresh, and the rotated token is written back to the configuration.</summary>
    [Configuration(IsSecret = true)]
    public partial string RefreshToken { get; set; }

    /// <summary>How often the vehicle list with the state and the cached data of every vehicle is read.</summary>
    [Configuration]
    public partial TimeSpan StatePollingInterval { get; set; }

    /// <summary>How often the data of an awake vehicle is read while it charges, drives or has a user present.</summary>
    [Configuration]
    public partial TimeSpan ActivePollingInterval { get; set; }

    /// <summary>How often the data of an awake vehicle is read while it is not active.</summary>
    [Configuration]
    public partial TimeSpan IdlePollingInterval { get; set; }

    /// <summary>How long an awake vehicle that could sleep is read before reading pauses.</summary>
    [Configuration]
    public partial TimeSpan IdleTimeout { get; set; }

    /// <summary>How long reading pauses so that an idle vehicle can fall asleep.</summary>
    [Configuration]
    public partial TimeSpan SleepWindow { get; set; }

    /// <summary>How long to wait after a failed poll.</summary>
    [Configuration]
    public partial TimeSpan RetryInterval { get; set; }

    // State

    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    [State(IsDiscrete = true)]
    public partial bool IsAuthenticated { get; internal set; }

    /// <summary>
    /// Whether Tesla rejected the refresh token. Polling then waits until the refresh token is replaced; other failures
    /// such as a network outage retry by themselves.
    /// </summary>
    [State(IsDiscrete = true)]
    public partial bool IsReauthenticationRequired { get; internal set; }

    [State]
    public partial string? AuthenticatedUser { get; internal set; }

    [State]
    public partial DateTimeOffset? AuthenticatedAt { get; internal set; }

    /// <summary>The vehicles of the account, keyed by VIN.</summary>
    [State]
    public partial Dictionary<string, TeslaVehicle> Vehicles { get; internal set; }

    [Derived]
    public string? Title
    {
        get
        {
            if (!string.IsNullOrEmpty(Name))
            {
                return Name;
            }

            return AuthenticatedUser is { } user ? $"Tesla: {user}" : "Tesla";
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

    private TeslaPollingSettings PollingSettings => new(ActivePollingInterval, IdlePollingInterval, IdleTimeout, SleepWindow);

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        SignalConfigurationChanged();
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        // Cancels the stopping token first, so that the loop does not start another poll on the disposed client.
        // _pollLock is not disposed: it never creates a wait handle, and a poll still running when Dispose is called
        // without StopAsync releases it afterwards.
        base.Dispose();
        _httpClient.Dispose();
    }
}
